#nullable enable
// #667 completion — T-667-LEGALITY: the primary regression test the adversarial review
// flagged as MISSING from the first #667 attempt (MEDIUM finding): every existing
// deadlock-retry test drives the classifier loop via SaveChangesInterceptor/DbCommandInterceptor
// on the DEFAULT SQLite NonRetryingExecutionStrategy — none of them ever configured a DbContext
// whose execution strategy has RetriesOnFailure == true, so none of them could ever have caught
// the bug #667 exists to fix: EF Core throws InvalidOperationException("...does not support
// user-initiated transactions...") when Database.BeginTransactionAsync() is called directly
// while the configured execution strategy is a RETRYING one (the exact effect of a host calling
// options.EnableRetryOnFailure() for SqlServer/Npgsql/MySql — a commonly-recommended cloud
// database setting).
//
// SQLite has no built-in retrying strategy (no EnableRetryOnFailure option), so this suite
// registers a minimal custom IExecutionStrategy (TestRetryingExecutionStrategy, RetriesOnFailure
// == true via the ExecutionStrategy base class default, never actually retrying since
// ShouldRetryOn always returns false) via DbContextOptionsBuilder's ExecutionStrategy(...) hook.
// This reproduces, on the CI-default provider, the exact legality condition #667 fixes —
// without needing a live SqlServer/Postgres instance.
//
// HONEST VERIFICATION (see #667 completion notes): this test file was manually verified to
// FAIL with InvalidOperationException against the pre-completion code (bare
// Db.Database.BeginTransactionAsync() on the Return-path / WorkflowTimerExecutor /
// ProcessDefinitionPublisher / WorkflowEventLogWriter call sites, before they were routed
// through the execution-strategy wrap) and to PASS after the fix.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class RetryingExecutionStrategyLegalityTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfLegality_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        using var db = new WfAbbaTestContext(_dbName);
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    private static async Task<ProcessDefinitionVersion> SeedVersionAsync(WfAbbaTestContext ctx)
    {
        var graph = WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "LegalityOneNode",
            Name = "LegalityOneNode",
            Nodes = new System.Collections.Generic.List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "nodeA",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "alice" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new System.Collections.Generic.List<TransitionDef>
            {
                new() { From = "start", To = "nodeA" },
                new() { From = "nodeA", To = "end"   },
            },
            FieldWhitelist = new System.Collections.Generic.List<FieldWhitelistEntry>(),
        });
        var ver = new ProcessDefinitionVersion
        {
            ID          = Guid.NewGuid(),
            GraphJson   = graph,
            ContentHash = "legality-hash",
            VersionNo   = 1,
            TenantCode  = "T1",
            IsValid     = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(ver);
        await ctx.SaveChangesAsync();
        return ver;
    }

    // ── T-667-LEGALITY-01: the core mechanism — StartAsync under a retrying strategy ──

    /// <summary>
    /// T-667-LEGALITY-01: <c>WorkflowEngine.StartAsync</c> — routed through
    /// <c>ExecuteInTransactionAsync</c> — must SUCCEED against a DbContext whose execution
    /// strategy has <c>RetriesOnFailure == true</c>. Before #667's execution-strategy wrap, the
    /// underlying bare <c>Db.Database.BeginTransactionAsync()</c> call would have thrown
    /// <see cref="InvalidOperationException"/> with EF Core's "does not support user-initiated
    /// transactions" message under this exact configuration.
    /// </summary>
    [TestMethod]
    public async Task T_667_LEGALITY_01_StartAsync_Succeeds_Under_RetryingExecutionStrategy()
    {
        await using var ctx = new WfAbbaTestContext(_dbName, useRetryingExecutionStrategy: true);

        var opts = new WorkFlowOptions { DeadlockRetryAttempts = 3, DeadlockRetryBaseDelay = TimeSpan.Zero };
        var resolver   = new DefaultApproverResolverExposed(opts, ctx);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
        var engine     = WorkflowEngine_Exposed.CreateWithOptions(ctx, dispatcher, opts, NullLogger.Instance);

        var version = await SeedVersionAsync(ctx);

        // Must not throw. Pre-#667-completion (bare BeginTransactionAsync in the start-handoff
        // tx) this line throws InvalidOperationException under a retrying execution strategy.
        var inst = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, inst.State,
            "T-667-LEGALITY-01: StartAsync must succeed (not throw) under a retrying execution strategy.");
    }

    // ── T-667-LEGALITY-02: the specific Return-path fix (previously excluded from BOTH concerns) ──

    /// <summary>
    /// T-667-LEGALITY-02: <c>WorkflowEngine.ReturnToInitiatorAsync</c>'s txReturn — one of the
    /// three Return-path sites the adversarial review found still on a bare, un-strategy-wrapped
    /// <c>BeginTransactionAsync()</c> (the first #667 attempt excluded the Return paths from BOTH
    /// the deadlock-retry loop AND the legality wrap, when only the retry-loop exclusion was ever
    /// justified) — must now SUCCEED under a retrying execution strategy.
    /// </summary>
    [TestMethod]
    public async Task T_667_LEGALITY_02_ReturnToInitiatorAsync_Succeeds_Under_RetryingExecutionStrategy()
    {
        await using var ctx = new WfAbbaTestContext(_dbName, useRetryingExecutionStrategy: true);

        var opts = new WorkFlowOptions { DeadlockRetryAttempts = 3, DeadlockRetryBaseDelay = TimeSpan.Zero };
        var resolver   = new DefaultApproverResolverExposed(opts, ctx);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
        var engine     = WorkflowEngine_Exposed.CreateWithOptions(ctx, dispatcher, opts, NullLogger.Instance);

        var version = await SeedVersionAsync(ctx);
        await engine.StartAsync(version.ID, null, "initiator", null);

        await using var readCtx = new WfAbbaTestContext(_dbName);
        var taskA = await readCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.State == TaskState.Pending);

        // Must not throw. Pre-#667-completion, txReturn's bare BeginTransactionAsync throws
        // InvalidOperationException under a retrying execution strategy — the exact HIGH bug
        // the adversarial review flagged (Return paths stranded, no compensating action, on any
        // host that had opted into EnableRetryOnFailure).
        var result = await engine.ReturnToInitiatorAsync(taskA.ID, "alice", reason: "T-667-LEGALITY-02");

        Assert.AreEqual(WorkflowActionCode.ReturnedToInitiator, result.Code,
            "T-667-LEGALITY-02: ReturnToInitiatorAsync must succeed (not throw) under a retrying execution strategy.");
    }
}

/// <summary>
/// Minimal custom <see cref="ExecutionStrategy"/> used only to make
/// <c>Database.CreateExecutionStrategy().RetriesOnFailure</c> report <c>true</c> on SQLite (which
/// has no built-in retrying strategy / EnableRetryOnFailure option). <c>ShouldRetryOn</c> always
/// returns <c>false</c> — this strategy never actually retries anything; it exists purely to
/// reproduce the EF Core transaction-legality gate that a real host's
/// <c>options.EnableRetryOnFailure()</c> configuration trips.
/// </summary>
internal sealed class TestRetryingExecutionStrategy : ExecutionStrategy
{
    public TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : base(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
    }

    protected override bool ShouldRetryOn(Exception exception) => false;
}
