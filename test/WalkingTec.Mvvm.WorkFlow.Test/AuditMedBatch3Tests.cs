#nullable enable
// AuditMedBatch3Tests.cs — Regression tests for audit medium-severity batch 3 fixes.
//
// C13: AtAction sweep collision pre-check — principal already participant → FailClosed audit,
//      no duplicate-key exception, no infinite per-tick retry.
// C14: DelegationResolvingDecorator dedupe inverted logic — direct-always-wins provenance.
//
// DB: SQLite shared-in-memory.
//   C13 tests use WfTestContext (from ConcurrencyConformanceTests.cs) — has the UNIQUE index
//   on (NodeInstanceId, AssigneeITCode, Generation) that was causing the collision exception.
//   C14 tests use WfDelegationTestContext (from DelegationTests.cs) — has DelegationRule table.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ─────────────────────────────────────────────────────────────────────────────
// C13 + C14 Tests
// ─────────────────────────────────────────────────────────────────────────────

[TestClass]
public class AuditMedBatch3Tests : IDisposable
{
    // ── C13 fixture (uses WfTestContext which has the UNIQUE index on tasks) ──
    private SqliteConnection _keepAliveC13 = null!;
    private string _dbNameC13 = null!;

    // ── C14 fixture (uses WfDelegationTestContext which has DelegationRule) ───
    private SqliteConnection _keepAliveC14 = null!;
    private string _dbNameC14 = null!;

    [TestInitialize]
    public void Setup()
    {
        // C13 DB
        _dbNameC13 = $"WfAuditMedBatch3_C13_{Guid.NewGuid():N}";
        _keepAliveC13 = new SqliteConnection($"DataSource={_dbNameC13}?mode=memory&cache=shared");
        _keepAliveC13.Open();
        using var c13Ctx = new WfTestContext(_dbNameC13);
        c13Ctx.Database.EnsureCreated();

        // C14 DB
        _dbNameC14 = $"WfAuditMedBatch3_C14_{Guid.NewGuid():N}";
        _keepAliveC14 = new SqliteConnection($"DataSource={_dbNameC14}?mode=memory&cache=shared");
        _keepAliveC14.Open();
        using var c14Ctx = new WfDelegationTestContext(_dbNameC14);
        c14Ctx.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAliveC13?.Close();
        _keepAliveC13?.Dispose();
        _keepAliveC14?.Close();
        _keepAliveC14?.Dispose();
    }

    public void Dispose() => Cleanup();

    private WfTestContext MakeC13Context() => new(_dbNameC13);
    private WfDelegationTestContext MakeC14Context() => new(_dbNameC14);

    // ── C13 seed helpers ──────────────────────────────────────────────────────

    private async Task<(Guid instanceId, Guid nodeId)> SeedRunningInstanceAndNodeC13Async(
        WfTestContext db,
        uint generation = 0)
    {
        var instanceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        db.Set<ProcessInstance>().Add(new ProcessInstance
        {
            ID                  = instanceId,
            State               = InstanceState.Running,
            RowVer              = 0,
            InitiatorITCode     = "tester",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid             = true,
            Generation          = generation,
            NextSeq             = 1,
        });
        db.Set<NodeInstance>().Add(new NodeInstance
        {
            ID         = nodeId,
            State      = NodeState.Activated,
            RowVer     = 0,
            NodeKey    = "approval1",
            InstanceId = instanceId,
            Generation = generation,
        });
        await db.SaveChangesAsync();
        return (instanceId, nodeId);
    }

    private static WorkflowTimerExecutor MakeExecutorC13(
        WfTestContext db,
        WorkFlowOptions? opts = null)
    {
        var options = Options.Create(opts ?? new WorkFlowOptions());
        return new WorkflowTimerExecutor(
            db, options,
            NullLogger<WorkflowTimerExecutor>.Instance,
            engine: null,
            notifier: null);
    }

    // ── C14 executor helper ───────────────────────────────────────────────────

    private DelegationResolvingDecorator MakeDecoratorC14(WorkFlowOptions? opts = null)
    {
        var options  = opts ?? new WorkFlowOptions();
        var inner    = new DefaultApproverResolverExposed(options, MakeC14Context());
        var optWrap  = Options.Create(options);
        var logger   = NullLogger<DelegationResolvingDecorator>.Instance;
        return new DelegationResolvingDecorator(inner, optWrap, logger);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C13 TESTS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// C13: When the expired delegated task's principal ALREADY holds a task row on the
    /// same (NodeInstanceId, Generation), the sweep must NOT flip the delegated row
    /// (which would violate the UNIQUE index), must NOT throw, and must emit a FailClosed
    /// event for operator visibility.
    /// </summary>
    [TestMethod]
    public async Task C13_AtActionSweep_CollidesWithPrincipalParticipant_NoException_LeavesExpiredSlot_EmitsFailClosed()
    {
        await using var seed = MakeC13Context();
        var (instanceId, nodeId) = await SeedRunningInstanceAndNodeC13Async(seed);

        // Principal already has a normal (non-delegated) task on the same node/gen.
        seed.Set<ApprovalTask>().Add(new ApprovalTask
        {
            ID               = Guid.NewGuid(),
            State            = TaskState.Pending,
            RowVer           = 0,
            AssigneeITCode   = "principal",
            DelegatedFromITCode = null,
            DelegationRuleId    = null,
            DelegationExpiresUtc = null,
            NodeInstanceId   = nodeId,
            IsValid          = true,
            Generation       = 0,
            SequenceOrder    = 0,
        });

        // Expired delegated task: delegatee="delegatee", principal="principal", expired 2 hours ago.
        var delegatedTaskId = Guid.NewGuid();
        seed.Set<ApprovalTask>().Add(new ApprovalTask
        {
            ID               = delegatedTaskId,
            State            = TaskState.Pending,
            RowVer           = 0,
            AssigneeITCode   = "delegatee",
            DelegatedFromITCode  = "principal",
            DelegationRuleId     = Guid.NewGuid(),
            DelegationExpiresUtc = DateTime.UtcNow.AddHours(-2),
            NodeInstanceId   = nodeId,
            IsValid          = true,
            Generation       = 0,
            SequenceOrder    = 1,
        });
        await seed.SaveChangesAsync();

        var opts = new WorkFlowOptions
        {
            DelegationWindowMode   = DelegationWindowMode.AtAction,
            DelegationExpiredSweep = DelegationExpiredSweep.RevertToPrincipal,
        };
        var executor = MakeExecutorC13(MakeC13Context(), opts);

        // (a) Must not throw — the UNIQUE index collision is now pre-empted.
        await executor.RunTickAsync(DateTime.UtcNow, CancellationToken.None);

        await using var verify = MakeC13Context();

        // (b) The expired delegated task still has AssigneeITCode == "delegatee" and State == Pending.
        var delegatedTask = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == delegatedTaskId);
        Assert.AreEqual("delegatee", delegatedTask.AssigneeITCode,
            "C13: expired delegated task must NOT be flipped to principal when collision detected");
        Assert.AreEqual(TaskState.Pending, delegatedTask.State,
            "C13: expired delegated task must remain Pending when collision detected");

        // (c) A FailClosed event must have been written.
        var failClosedEvents = await verify.Set<WorkflowEventLog>()
            .AsNoTracking()
            .Where(e => e.InstanceId == instanceId && e.Action == EventAction.FailClosed)
            .ToListAsync();
        Assert.IsTrue(failClosedEvents.Any(),
            "C13: FailClosed event must be written when collision is detected");
        Assert.IsTrue(
            failClosedEvents.Any(e => e.Reason != null &&
                e.Reason.Contains("collision", StringComparison.OrdinalIgnoreCase)),
            "C13: FailClosed event Reason must mention 'collision'");

        // (d) No duplicate (nodeId, "principal", gen=0) row — principal's original task untouched.
        var principalRows = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeId
                         && t.AssigneeITCode == "principal"
                         && t.Generation == 0)
            .ToListAsync();
        Assert.AreEqual(1, principalRows.Count,
            "C13: principal must have exactly one task row (no duplicate inserted)");
    }

    /// <summary>
    /// C13 positive-control: when the principal has NO existing task row on (NodeInstanceId,
    /// Generation), the expired delegation IS reverted normally to the principal.
    /// </summary>
    [TestMethod]
    public async Task C13_AtActionSweep_NonColliding_Reverts_ToPrincipal()
    {
        await using var seed = MakeC13Context();
        var (instanceId, nodeId) = await SeedRunningInstanceAndNodeC13Async(seed);

        // No existing task for "principal" on this node — only the delegated task exists.
        var delegatedTaskId = Guid.NewGuid();
        seed.Set<ApprovalTask>().Add(new ApprovalTask
        {
            ID               = delegatedTaskId,
            State            = TaskState.Pending,
            RowVer           = 0,
            AssigneeITCode   = "delegatee",
            DelegatedFromITCode  = "principal",
            DelegationRuleId     = Guid.NewGuid(),
            DelegationExpiresUtc = DateTime.UtcNow.AddHours(-1),
            NodeInstanceId   = nodeId,
            IsValid          = true,
            Generation       = 0,
            SequenceOrder    = 0,
        });
        await seed.SaveChangesAsync();

        var opts = new WorkFlowOptions
        {
            DelegationWindowMode   = DelegationWindowMode.AtAction,
            DelegationExpiredSweep = DelegationExpiredSweep.RevertToPrincipal,
        };
        var executor = MakeExecutorC13(MakeC13Context(), opts);
        await executor.RunTickAsync(DateTime.UtcNow, CancellationToken.None);

        await using var verify = MakeC13Context();
        var task = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == delegatedTaskId);

        // The expired delegation MUST be reverted to the principal.
        Assert.AreEqual("principal", task.AssigneeITCode,
            "C13 positive-control: expired delegation must be reverted to principal when no collision");
        Assert.IsNull(task.DelegationRuleId,
            "C13 positive-control: DelegationRuleId must be cleared after revert");
        Assert.IsNull(task.DelegatedFromITCode,
            "C13 positive-control: DelegatedFromITCode must be cleared after revert");

        // DelegationExpiredReverted event must be written.
        var events = await verify.Set<WorkflowEventLog>()
            .AsNoTracking()
            .Where(e => e.InstanceId == instanceId && e.Action == EventAction.DelegationExpiredReverted)
            .ToListAsync();
        Assert.IsTrue(events.Any(),
            "C13 positive-control: DelegationExpiredReverted event must be written on successful revert");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C14 TESTS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// C14: Dedupe order-independence — direct always wins.
    /// Order A: [C (direct), A (delegates to C)].
    /// C is processed first (direct, ruleId==null).
    /// Then A's chain resolves to C → dedupe hit; incoming is delegated → existing direct kept.
    /// C's provenance must be direct (RuleId == null).
    /// </summary>
    [TestMethod]
    public async Task C14_Dedupe_DirectFirst_DirectProvenance_Wins()
    {
        const string A = "alice"; const string C = "carol";

        await using var ctx = MakeC14Context();

        // Seed: A delegates to C. C has no outgoing rule (direct approver).
        var ruleId = Guid.NewGuid();
        ctx.Set<DelegationRule>().Add(new DelegationRule
        {
            ID               = ruleId,
            PrincipalITCode  = A,
            DelegateeITCode  = C,
            StartUtc         = DateTime.UtcNow.AddHours(-1),
            EndUtc           = DateTime.UtcNow.AddHours(+8),
            IsValid          = true,
        });
        await ctx.SaveChangesAsync();

        var decorator = MakeDecoratorC14();

        // Order A: C appears BEFORE A in the inner resolver's list.
        // We wrap the inner with a fixed-list resolver returning [C, A].
        var innerFixed = new FixedListApproverResolver(new[] { C, A });
        var opts   = Options.Create(new WorkFlowOptions());
        var logger = NullLogger<DelegationResolvingDecorator>.Instance;
        var dec    = new DelegationResolvingDecorator(innerFixed, opts, logger);

        var nodeInstance = new NodeInstance
        {
            ID             = Guid.NewGuid(),
            NodeKey        = "approval1",
            DefinitionCode = null, // no scope filter
            TenantCode     = null,
        };
        var rule = new WalkingTec.Mvvm.WorkFlow.Definition.ApproverRuleDef
        {
            Type  = "User",
            Value = $"{C},{A}",
        };

        await dec.ResolveAsync(ctx, rule, nodeInstance, "initiator", CancellationToken.None);

        var provenance = ((IDelegationContextProvider)dec).LastResolutionProvenance;
        Assert.IsNotNull(provenance, "C14: provenance must be set after ResolveAsync");
        Assert.IsTrue(provenance.ContainsKey(C),
            "C14: provenance must contain C (the terminal approver)");

        var cProv = provenance[C];
        Assert.IsNull(cProv.RuleId,
            $"C14 DirectFirst: C's RuleId must be null (direct wins over delegated). Got {cProv.RuleId}");
        Assert.AreEqual(C, cProv.OriginalPrincipalITCode,
            "C14 DirectFirst: C's OriginalPrincipalITCode must be C (self, direct slot)");
    }

    /// <summary>
    /// C14: Dedupe order-independence — direct always wins.
    /// Order B: [A (delegates to C), C (direct)].
    /// A's chain resolves to C first (delegated, ruleId != null) → C added with delegated provenance.
    /// Then C itself appears → dedupe hit; incoming is direct (ruleId==null) → overwrites delegated entry.
    /// C's provenance must be direct (RuleId == null).
    /// </summary>
    [TestMethod]
    public async Task C14_Dedupe_DelegatedFirst_DirectProvenance_StillWins()
    {
        const string A = "alice"; const string C = "carol";

        await using var ctx = MakeC14Context();

        // Seed: A delegates to C. C has no outgoing rule.
        var ruleId = Guid.NewGuid();
        ctx.Set<DelegationRule>().Add(new DelegationRule
        {
            ID               = ruleId,
            PrincipalITCode  = A,
            DelegateeITCode  = C,
            StartUtc         = DateTime.UtcNow.AddHours(-1),
            EndUtc           = DateTime.UtcNow.AddHours(+8),
            IsValid          = true,
        });
        await ctx.SaveChangesAsync();

        // Order B: A appears BEFORE C in the inner resolver's list.
        // A's chain delegates to C (ruleId != null), so C is added with delegated provenance first.
        // Then C itself is processed (direct, ruleId == null) → must overwrite.
        var innerFixed = new FixedListApproverResolver(new[] { A, C });
        var opts   = Options.Create(new WorkFlowOptions());
        var logger = NullLogger<DelegationResolvingDecorator>.Instance;
        var dec    = new DelegationResolvingDecorator(innerFixed, opts, logger);

        var nodeInstance = new NodeInstance
        {
            ID             = Guid.NewGuid(),
            NodeKey        = "approval1",
            DefinitionCode = null,
            TenantCode     = null,
        };
        var rule = new WalkingTec.Mvvm.WorkFlow.Definition.ApproverRuleDef
        {
            Type  = "User",
            Value = $"{A},{C}",
        };

        await dec.ResolveAsync(ctx, rule, nodeInstance, "initiator", CancellationToken.None);

        var provenance = ((IDelegationContextProvider)dec).LastResolutionProvenance;
        Assert.IsNotNull(provenance, "C14: provenance must be set after ResolveAsync");
        Assert.IsTrue(provenance.ContainsKey(C),
            "C14: provenance must contain C (the terminal approver)");

        var cProv = provenance[C];
        Assert.IsNull(cProv.RuleId,
            $"C14 DelegatedFirst: C's RuleId must be null (direct wins over delegated). Got {cProv.RuleId}. " +
            "Before fix, the C14 bug kept the FIRST-seen delegated provenance when direct was processed second — " +
            "but the original code was actually inverted (it overwrote direct with delegated). " +
            "After fix, direct always wins regardless of order.");
        Assert.AreEqual(C, cProv.OriginalPrincipalITCode,
            "C14 DelegatedFirst: C's OriginalPrincipalITCode must be C (self, direct slot)");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test helper: IApproverResolver that returns a fixed ordered list of ITCodes.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class FixedListApproverResolver : IApproverResolver
{
    private readonly string[] _approvers;

    public FixedListApproverResolver(string[] approvers)
    {
        _approvers = approvers;
    }

    public Task<ApproverResolution> ResolveAsync(
        DbContext db,
        WalkingTec.Mvvm.WorkFlow.Definition.ApproverRuleDef rule,
        NodeInstance nodeInstance,
        string initiatorITCode,
        CancellationToken ct = default)
        => Task.FromResult(ApproverResolution.Success(_approvers.ToList()));
}
