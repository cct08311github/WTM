#nullable enable
// #727 regression tests — audit of the #721 IDataContext→NullContext DI-resolution gap.
//
// AddWtmContext only ever registers services.TryAddScoped<IDataContext, NullContext>() as a
// placeholder default. Before #727, WorkflowEngine and WorkflowTimerExecutor were registered
// with plain `services.AddScoped<T>()`, which let ASP.NET Core's automatic constructor
// injection resolve their IDataContext parameter straight off the DI container — i.e.
// NullContext in every real deployment — throwing InvalidCastException ("Unable to cast object
// of type 'NullContext' to type 'DbContext'") the very first time either type was constructed
// via a real DI container wired the way demo/WalkingTec.Mvvm.Demo/Startup.cs wires it
// (AddWtmWorkFlow / AddWtmWorkFlowTimers + AddWtmContext's IDataContext placeholder).
//
// This was masked because no prior test constructed either type through a real ASP.NET Core DI
// container: EngineTests.cs uses the internal direct-DbContext test constructor, and
// ControllerTests.cs mocks IWorkflowEngine entirely — the broken production DI registration
// itself was never exercised by any existing test.
//
// These tests build a DI container the same way AddWtmContext + AddWtmWorkFlow +
// AddWtmWorkFlowTimers do in a real app (IDataContext -> NullContext placeholder,
// IWtmDataContextFactory -> a real SQLite-backed factory) and assert IWorkflowEngine /
// WorkflowTimerExecutor resolve successfully AND genuinely round-trip to the database (not a
// NullContext no-op) — the same shape of proof #721 used for TokenService.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

/// <summary>
/// Real <see cref="DbContext"/> + <see cref="IDataContext"/> combined type (mirrors production
/// consumer <c>DataContext</c> classes) — extends <see cref="EmptyContext"/> (the same base
/// production callers use, per <c>ProcessDefinitionPublisher</c>'s remarks) and layers on the
/// WorkFlow schema via <see cref="WorkFlowDbContextExtensions.ApplyWorkFlowModels"/>, exactly
/// like a real consumer's <c>DataContext.OnModelCreating</c> would. Backed by SQLite
/// shared-memory (never EF InMemory — spec §7.6 / #119 / #162).
/// </summary>
internal sealed class WfProdDiTestContext : EmptyContext
{
    public WfProdDiTestContext(string dbName) : base(dbName, DBTypeEnum.SQLite) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSqlite(SqliteSharedMemoryFixture.BuildConnectionString(CSName));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // #899: pass `this` so this fixture's doc comment ("exactly like a real consumer's
        // DataContext.OnModelCreating would") is actually true -- the zero-arg overload this
        // used to call can never apply the ITenant/soft-delete filter. Every seeded row and
        // context in this file uses TenantCode == null (single-tenant scenario), and EF's
        // null-safe `==` translation matches NULL == NULL, so this is behaviour-preserving for
        // every existing test here while also now exercising the real production filter wiring.
        modelBuilder.ApplyWorkFlowModels(this);
    }
}

/// <summary>
/// Fake <see cref="IWtmDataContextFactory"/> that mirrors what
/// <c>WtmDataContextFactory.CreateDC()</c> does in a real app: hand back a fresh, real,
/// DbContext-backed <see cref="IDataContext"/> on every call.
/// </summary>
internal sealed class ProdDiReproDataContextFactory : IWtmDataContextFactory
{
    private readonly string _dbName;

    public ProdDiReproDataContextFactory(string dbName) => _dbName = dbName;

    public IDataContext? CreateDC(
        string? currentCs = null,
        string? currentTenant = null,
        string? refererDomain = null,
        string? userCode = null,
        bool isLog = false,
        string? cskey = null,
        bool logerror = true)
        => new WfProdDiTestContext(_dbName);
}

[TestClass]
public class ProdDiReproTests
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfProdDi_{Guid.NewGuid():N}";
        _keepAlive = SqliteSharedMemoryFixture.OpenKeepAliveWithBusyTimeout(_dbName);
        using var ctx = new WfProdDiTestContext(_dbName);
        ctx.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    /// <summary>
    /// Builds a DI container the same way a real WTM app wires it: AddWtmContext's
    /// IDataContext -> NullContext placeholder, plus AddWtmWorkFlow / AddWtmWorkFlowTimers, plus
    /// a real IWtmDataContextFactory (in production this is WtmDataContextFactory, wired by
    /// AddWtmContext; here it is ProdDiReproDataContextFactory backed by SQLite).
    /// </summary>
    private IServiceProvider BuildProdLikeProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        // Mirrors AddWtmContext (Core IServiceExtension.cs:35 / Mvc FrameworkServiceExtension.cs:418).
        services.TryAddScoped<IDataContext, NullContext>();
        // Mirrors AddWtmContext registering IWtmDataContextFactory (IServiceExtension.cs:68 /
        // FrameworkServiceExtension.cs:510) — a real factory that hands back a real DbContext.
        services.AddScoped<IWtmDataContextFactory>(_ => new ProdDiReproDataContextFactory(_dbName));
        services.AddWtmWorkFlow();
        services.AddWtmWorkFlowTimers();
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task IWorkflowEngine_ResolvedFromProdLikeDI_NoLongerThrowsAndReachesRealDatabase()
    {
        var sp = BuildProdLikeProvider();
        using var scope = sp.CreateScope();

        // Resolution itself must not throw InvalidCastException (the #727 bug).
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        Assert.IsNotNull(engine);

        // Prove the engine is backed by a REAL database, not a NullContext no-op: StartAsync
        // against a nonexistent definition must reach the SQL query and return the engine's own
        // domain-level "not found" error — not an infra-level InvalidCastException/
        // NotImplementedException/"requires IDataContext to be a DbContext subclass" error.
        var missingId = Guid.NewGuid();
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            engine.StartAsync(missingId, null, "tester", null));
        StringAssert.Contains(ex.Message, "not found or not valid");
    }

    [TestMethod]
    public async Task WorkflowTimerExecutor_ResolvedFromProdLikeDI_NoLongerThrowsAndReachesRealDatabase()
    {
        var sp = BuildProdLikeProvider();
        using var scope = sp.CreateScope();

        // Resolution itself must not throw (the #727 bug affected this type identically to
        // IWorkflowEngine — both took IDataContext via plain constructor injection).
        var executor = scope.ServiceProvider.GetRequiredService<WorkflowTimerExecutor>();
        Assert.IsNotNull(executor);

        // Prove RunTickAsync reaches real SQL instead of throwing
        // "WorkflowTimerExecutor requires IDataContext to be a DbContext subclass." — an empty
        // due-timer scan against a real (empty) table completes without throwing.
        await executor.RunTickAsync(DateTime.UtcNow, default);
    }

    // ── #727-followup regression tests ─────────────────────────────────────────
    //
    // The naive #727 fix let IWorkflowEngine's and WorkflowTimerExecutor's DI factories each
    // independently call ResolveDataContext(sp) -> IWtmDataContextFactory.CreateDC() (which has
    // no per-scope caching), minting TWO separate DbContext/DB-connection instances within one
    // scope — breaking WorkflowTimerExecutor.Fire.cs's IN-TXN-claim / POST-COMMIT-continuation
    // atomicity contract for AutoApprove/AutoReject. These tests prove the fix: both services
    // resolved from the SAME scope now share one DbContext, and an end-to-end AutoApprove drive
    // through the prod-DI-resolved WorkflowTimerExecutor.RunTickAsync actually completes the
    // instance (a path the original #727 ProdDiReproTests never exercised — they only proved an
    // EMPTY tick resolves without throwing).

    private static readonly FieldInfo EngineDcField = typeof(WorkflowEngine)
        .GetField("_dc", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("WorkflowEngine._dc field not found — signature changed?");

    private static readonly FieldInfo ExecutorDcField = typeof(WorkflowTimerExecutor)
        .GetField("_dc", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("WorkflowTimerExecutor._dc field not found — signature changed?");

    [TestMethod]
    public void IWorkflowEngine_And_WorkflowTimerExecutor_ResolvedFromSameScope_ShareOneDataContext()
    {
        var sp = BuildProdLikeProvider();
        using var scope = sp.CreateScope();

        // Mirrors the production order in WorkflowTimerHostedService.TickAsync: the executor is
        // resolved first; its factory resolves IWorkflowEngine internally as an optional dependency.
        var executor = scope.ServiceProvider.GetRequiredService<WorkflowTimerExecutor>();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        var executorDc = ExecutorDcField.GetValue(executor);
        var engineDc = EngineDcField.GetValue(engine);

        Assert.IsNotNull(executorDc, "WorkflowTimerExecutor._dc must be non-null in the prod DI path");
        Assert.IsNotNull(engineDc, "WorkflowEngine._dc must be non-null in the prod DI path");
        Assert.IsTrue(ReferenceEquals(executorDc, engineDc),
            "#727-followup: WorkflowTimerExecutor and IWorkflowEngine resolved from the SAME DI " +
            "scope must share the exact same IDataContext/DbContext instance — otherwise the " +
            "executor's fire transaction and the engine's SystemClaimTaskAsync writes run on two " +
            "different DB connections, breaking the documented IN-TXN atomicity contract.");
    }

    /// <summary>
    /// Seeds a minimal Start → Approval(Any) → End graph version.
    /// </summary>
    private static async Task<Guid> SeedAnyModeVersionAsync(WfProdDiTestContext db, string nodeKey = "approval")
    {
        var graph = new WorkflowGraph
        {
            Key = "ProdDiGraph_" + Guid.NewGuid().ToString("N")[..6],
            Name = "ProdDiGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey = nodeKey,
                    Kind = NodeKind.Approval,
                    ApproveMode = ApproveMode.Any,
                    ApproverRule = new ApproverRuleDef { Type = "Static", Value = "alice" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = nodeKey },
                new() { From = nodeKey, To = "end" },
            },
        };

        // ProcessDefinitionVersion.DefinitionId is a real FK (Restrict) to ProcessDefinition —
        // WfProdDiTestContext (unlike the lighter WfTestContext/WfEngineTestContext test doubles
        // used elsewhere) enforces it, so the parent row must exist first.
        var definitionId = Guid.NewGuid();
        db.Set<ProcessDefinition>().Add(new ProcessDefinition
        {
            ID = definitionId,
            Code = "ProdDiDef_" + Guid.NewGuid().ToString("N")[..6],
            Name = "ProdDiDef",
            IsEnabled = true,
        });

        var version = new ProcessDefinitionVersion
        {
            ID = Guid.NewGuid(),
            DefinitionId = definitionId,
            VersionNo = 1,
            SchemaVersion = 1,
            GraphJson = WorkflowGraphSerializer.Serialize(graph),
            ContentHash = "hash-" + Guid.NewGuid().ToString("N"),
            PublishedAt = DateTime.UtcNow,
            PublishedBy = "test",
            IsValid = true,
        };
        db.Set<ProcessDefinitionVersion>().Add(version);
        await db.SaveChangesAsync();
        return version.ID;
    }

    [TestMethod]
    public async Task WorkflowTimerExecutor_AutoApprove_ViaProdLikeDI_CompletesInstanceAtomically()
    {
        // ── Seed: a Running instance blocked at an Any-mode Approval node, alice's Pending
        //    task, and an Armed AutoApprove timer scoped to that task — all via a throwaway
        //    context (the prod-like DI container's IWtmDataContextFactory hands back fresh
        //    instances backed by the same SQLite shared-memory database).
        Guid instanceId, nodeId, taskId;
        await using (var seed = new WfProdDiTestContext(_dbName))
        {
            var versionId = await SeedAnyModeVersionAsync(seed);

            instanceId = Guid.NewGuid();
            nodeId = Guid.NewGuid();
            taskId = Guid.NewGuid();

            seed.Set<ProcessInstance>().Add(new ProcessInstance
            {
                ID = instanceId,
                State = InstanceState.Running,
                RowVer = 0,
                InitiatorITCode = "initiator",
                DefinitionVersionId = versionId,
                IsValid = true,
                Generation = 0,
            });
            seed.Set<NodeInstance>().Add(new NodeInstance
            {
                ID = nodeId,
                State = NodeState.Activated,
                RowVer = 0,
                NodeKey = "approval",
                InstanceId = instanceId,
                TenantCode = null,
                TotalRequired = 1,
                ApproveMode = ApproveMode.Any,
                Generation = 0,
            });
            seed.Set<ApprovalTask>().Add(new ApprovalTask
            {
                ID = taskId,
                State = TaskState.Pending,
                RowVer = 0,
                AssigneeITCode = "alice",
                NodeInstanceId = nodeId,
                IsValid = true,
                Generation = 0,
                SequenceOrder = 0,
            });
            seed.Set<WorkflowTimer>().Add(new WorkflowTimer
            {
                ID = Guid.NewGuid(),
                Status = TimerStatus.Armed,
                RowVer = 0,
                NodeInstanceId = nodeId,
                ApprovalTaskId = taskId,
                IdempotencyKey = "prod-di-autoapprove-" + Guid.NewGuid().ToString("N"),
                FireAtUtc = DateTime.UtcNow.AddHours(-1),
                Action = TimerAction.AutoApprove,
                Generation = 0,
                RemindCount = 0,
            });
            await seed.SaveChangesAsync();
        }

        // ── Build a prod-like DI container with AllowTimerAutoAction=true and run one tick
        //    through the DI-resolved WorkflowTimerExecutor — exactly the
        //    WorkflowTimerHostedService.TickAsync production path.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.TryAddScoped<IDataContext, NullContext>();
        services.AddScoped<IWtmDataContextFactory>(_ => new ProdDiReproDataContextFactory(_dbName));
        services.AddWtmWorkFlow(o => o.AllowTimerAutoAction = true);
        services.AddWtmWorkFlowTimers();
        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<WorkflowTimerExecutor>();
            await executor.RunTickAsync(DateTime.UtcNow.AddSeconds(1), default);
        }

        // ── Assert: the fire transaction's task-claim CAS AND the engine's post-commit
        //    node-completion continuation both actually happened — proving the executor's
        //    txn-scoped writes and the engine's SystemClaimTaskAsync/SystemContinueTaskAsync
        //    writes landed correctly instead of racing on two disjoint DB connections.
        await using var verify = new WfProdDiTestContext(_dbName);

        var task = await verify.Set<ApprovalTask>().AsNoTracking().SingleAsync(t => t.ID == taskId);
        Assert.AreEqual(TaskState.AutoApproved, task.State,
            "#727-followup: alice's task must be AutoApproved after the DI-resolved tick");

        var instance = await verify.Set<ProcessInstance>().AsNoTracking().SingleAsync(i => i.ID == instanceId);
        Assert.AreEqual(InstanceState.Approved, instance.State,
            "#727-followup: instance must reach Approved — proves SystemContinueTaskAsync's " +
            "post-commit AdvanceAsync continuation ran against the same, correctly-committed " +
            "state as the executor's fire transaction (not a stranded/orphaned claim on a " +
            "separate, uncommitted connection).");
    }
}
