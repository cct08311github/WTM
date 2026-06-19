#nullable enable
// Regression tests for three WorkFlow transaction-safety fixes:
//   #357 — StartAsync atomic handoff (NodeInstance mint + Draft→Running flip in ONE tx)
//   #358 — WithdrawAsync canonical lock-order + tx-wrap (Task cancels BEFORE ProcessInstance flip)
//   #361 — mid-chain InitiatorAutoApprove bounded loop (consume consecutive AutoApproved slots)
//
// Tests:
//   T_357_01: happy-path — StartAsync produces Running instance with a NodeInstance
//   T_357_02: crash-window invariant — Running-without-node must not be possible
//   T_358_01: structural lock-order — Task writes must precede ProcessInstance flip in WithdrawAsync
//   T_358_02: happy-path — WithdrawAsync cancels tasks + flips instance atomically
//   T_358_03: CAS-loser on Withdrawn — CannotWithdrawAlreadyFinal returned correctly
//   T_361_01: mid-chain AutoApprove — slot N+1 auto (initiator == approver), N+2 real → chain reaches Pending
//   T_361_02: all-auto chain — all slots auto-approved → instance Approved
//   T_361_03: #361 loop CAS-loser — no orphan-Pending task at nextPtr after concurrent epoch bump
//
// All tests use SQLite shared-in-memory (never EF InMemory — spec §7.6 / #119 / #162).
// Uses WfAbbaTestContext (defined in AbbaFixTests.cs, same assembly) which supports
// EF interceptors and covers the full schema including DelegationRule and WorkflowTimer.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ─── Write-order interceptor for lock-order assertions (#358) ────────────────

/// <summary>
/// DbCommandInterceptor that records which tables receive non-query writes (UPDATE/ExecuteUpdate).
/// Used by T_358_01 to assert canonical lock order: ApprovalTask before ProcessInstance.
/// </summary>
internal sealed class Wf358WriteOrderInterceptor : DbCommandInterceptor
{
    private readonly List<string> _writes = new();
    private bool _capturing;

    public IReadOnlyList<string> TableWrites => _writes;

    public void StartCapture() { _writes.Clear(); _capturing = true; }
    public void StopCapture() { _capturing = false; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (_capturing)
        {
            var lower = (command.CommandText ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("wf_processinstance"))
                _writes.Add("ProcessInstance");
            else if (lower.Contains("wf_approvaltask"))
                _writes.Add("ApprovalTask");
            else if (lower.Contains("wf_nodeinstance"))
                _writes.Add("NodeInstance");
            else if (lower.Contains("wf_workflowtimer"))
                _writes.Add("WorkflowTimer");
        }
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

// ─── Shared base ─────────────────────────────────────────────────────────────

/// <summary>
/// Shared test base for #357/#358/#361 tests.
/// Uses <see cref="WfAbbaTestContext"/> (defined in AbbaFixTests.cs, same assembly),
/// which supports EF interceptors and has the complete schema.
/// </summary>
public abstract class Wf357358361TestBase : IDisposable
{
    protected SqliteConnection _keepAlive = null!;
    protected string _dbName = null!;

    protected void BaseSetup(string prefix)
    {
        _dbName = $"{prefix}_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        using var db = new WfAbbaTestContext(_dbName);
        db.Database.EnsureCreated();
    }

    protected void BaseCleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => BaseCleanup();

    internal WfAbbaTestContext MakeContext() => new(_dbName);

    internal WfAbbaTestContext MakeContext(params IInterceptor[] interceptors) =>
        new(_dbName, interceptors);

    internal static WorkflowEngine MakeEngine(WfAbbaTestContext ctx, WorkFlowOptions? opts = null)
    {
        var options = opts ?? new WorkFlowOptions();
        var resolver = new DefaultApproverResolverExposed(options, ctx);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, options);
        return WorkflowEngine_Exposed.CreateWithOptions(ctx, dispatcher, options, NullLogger.Instance);
    }

    internal async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfAbbaTestContext ctx, string graphJson)
    {
        var ver = new ProcessDefinitionVersion
        {
            ID = Guid.NewGuid(),
            DefinitionId = Guid.NewGuid(),
            GraphJson = graphJson,
            ContentHash = $"hash-{Guid.NewGuid():N}",
            VersionNo = 1,
            IsValid = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(ver);
        await ctx.SaveChangesAsync();
        return ver;
    }

    /// <summary>Simple single-approver Sequential graph for StartAsync/WithdrawAsync tests.</summary>
    protected static string SingleApproverGraph(string approver = "alice") =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "SingleApproverGraph",
            Name = "SingleApproverGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "nodeA",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = approver },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "nodeA" },
                new() { From = "nodeA", To = "end"   },
            },
        });
}

// ─── #357 StartAsync atomic handoff tests ────────────────────────────────────

[TestClass]
public class Wf357StartAtomicTests : Wf357358361TestBase
{
    [TestInitialize]
    public void Setup() => BaseSetup("Wf357");

    [TestCleanup]
    public void Cleanup() => BaseCleanup();

    /// <summary>
    /// T_357_01: happy-path StartAsync.
    /// StartAsync must produce a Running ProcessInstance with at least one NodeInstance,
    /// and exactly one Submit event log entry.
    /// </summary>
    [TestMethod]
    public async Task T_357_01_StartAsync_HappyPath_RunningWithStartNode()
    {
        await using var ctx = MakeContext();
        var version = await SeedVersionAsync(ctx, SingleApproverGraph());
        var engine = MakeEngine(ctx);

        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        // Instance must be Running.
        Assert.AreEqual(InstanceState.Running, instance.State,
            "T_357_01: StartAsync must produce a Running instance.");

        // At least one NodeInstance must exist.
        await using var verify = MakeContext();
        var nodes = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .Where(n => n.InstanceId == instance.ID)
            .ToListAsync();
        Assert.IsTrue(nodes.Count > 0,
            "T_357_01: Running instance must have at least one NodeInstance (#357 atomic handoff).");

        // Exactly one Submit event log entry must exist.
        var logs = await verify.Set<WorkflowEventLog>()
            .AsNoTracking()
            .Where(l => l.InstanceId == instance.ID && l.Action == EventAction.Submit)
            .ToListAsync();
        Assert.AreEqual(1, logs.Count,
            "T_357_01: exactly one Submit event log entry must exist after StartAsync.");
    }

    /// <summary>
    /// T_357_02: crash-window invariant — Running-without-node must not be possible.
    ///
    /// Runs 5 independent StartAsync calls and asserts that every Running instance
    /// has at least one associated NodeInstance. This proves the #357 tx sealed the
    /// crash window: Draft INSERT (outside tx) + NodeInstance mint + Running flip
    /// (inside single tx). If Running → NodeInstance must exist.
    /// </summary>
    [TestMethod]
    public async Task T_357_02_StartAsync_EveryRunningInstance_HasNodeInstance()
    {
        for (int i = 0; i < 5; i++)
        {
            var dbName = $"Wf357_inv_{i}_{Guid.NewGuid():N}";
            await using var keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
            keepAlive.Open();
            await using var initCtx = new WfAbbaTestContext(dbName);
            initCtx.Database.EnsureCreated();

            var graphJson = SingleApproverGraph("alice");
            var ver = new ProcessDefinitionVersion
            {
                ID = Guid.NewGuid(),
                DefinitionId = Guid.NewGuid(),
                GraphJson = graphJson,
                ContentHash = $"hash-inv-{i}",
                VersionNo = 1,
                IsValid = true,
            };
            initCtx.Set<ProcessDefinitionVersion>().Add(ver);
            await initCtx.SaveChangesAsync();

            await using var runCtx = new WfAbbaTestContext(dbName);
            var engine = MakeEngine(runCtx);
            var instance = await engine.StartAsync(ver.ID, null, "initiator", null);

            // Invariant: if Running, must have a NodeInstance.
            if (instance.State == InstanceState.Running)
            {
                await using var checkCtx = new WfAbbaTestContext(dbName);
                var nodeCount = await checkCtx.Set<NodeInstance>()
                    .AsNoTracking()
                    .CountAsync(n => n.InstanceId == instance.ID);
                Assert.IsTrue(nodeCount > 0,
                    $"T_357_02 round {i}: Running ProcessInstance {instance.ID} has no NodeInstance — " +
                    "this is the stranded state that #357 must prevent.");
            }
        }
    }
}

// ─── #358 WithdrawAsync lock-order + tx-wrap tests ───────────────────────────

[TestClass]
public class Wf358WithdrawAtomicTests : Wf357358361TestBase
{
    [TestInitialize]
    public void Setup() => BaseSetup("Wf358");

    [TestCleanup]
    public void Cleanup() => BaseCleanup();

    /// <summary>
    /// T_358_01: structural lock-order assertion.
    /// Within WithdrawAsync, ApprovalTask writes must come BEFORE the ProcessInstance flip.
    /// Uses Wf358WriteOrderInterceptor to verify the write sequence (#358 canonical order).
    /// </summary>
    [TestMethod]
    public async Task T_358_01_WithdrawAsync_TaskWritesBeforeInstanceFlip_CanonicalLockOrder()
    {
        // Seed a Running instance with a Pending task.
        await using var setupCtx = MakeContext();
        var version = await SeedVersionAsync(setupCtx, SingleApproverGraph("alice"));
        var setupEngine = MakeEngine(setupCtx);
        var instance = await setupEngine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State,
            "T_358_01: Setup: StartAsync must produce Running instance.");

        // Verify Pending task exists.
        await using var readCtx = MakeContext();
        var pendingCount = await readCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .CountAsync(t => t.State == TaskState.Pending);
        Assert.IsTrue(pendingCount > 0,
            "T_358_01: Setup: at least one Pending task must exist before Withdraw.");

        // Build an interceptor-instrumented context using WfAbbaTestContext (supports interceptors).
        var interceptor = new Wf358WriteOrderInterceptor();
        await using var iCtx = MakeContext(interceptor);
        var engine = MakeEngine(iCtx);

        // Capture writes during WithdrawAsync only.
        interceptor.StartCapture();
        var result = await engine.WithdrawAsync(instance.ID, "initiator");
        interceptor.StopCapture();

        Assert.AreEqual(WorkflowActionCode.Withdrawn, result.Code,
            $"T_358_01: WithdrawAsync must succeed. Got {result.Code}.");

        // Assert lock order: first ApprovalTask write must precede first ProcessInstance write.
        var writes = interceptor.TableWrites;

        var firstTaskIdx = writes
            .Select((t, i) => (t, i))
            .Where(x => x.t == "ApprovalTask")
            .Select(x => x.i)
            .DefaultIfEmpty(-1)
            .Min();

        var firstPiIdx = writes
            .Select((t, i) => (t, i))
            .Where(x => x.t == "ProcessInstance")
            .Select(x => x.i)
            .DefaultIfEmpty(-1)
            .Min();

        Assert.IsTrue(firstTaskIdx >= 0,
            $"T_358_01: at least one ApprovalTask write must occur. Writes: [{string.Join(", ", writes)}]");
        Assert.IsTrue(firstPiIdx >= 0,
            $"T_358_01: at least one ProcessInstance write must occur. Writes: [{string.Join(", ", writes)}]");
        Assert.IsTrue(firstTaskIdx < firstPiIdx,
            $"T_358_01: ApprovalTask write (idx {firstTaskIdx}) must precede ProcessInstance flip " +
            $"(idx {firstPiIdx}). Canonical lock order violated (#358). Writes: [{string.Join(", ", writes)}]");
    }

    /// <summary>
    /// T_358_02: happy-path — WithdrawAsync cancels tasks + flips instance to Withdrawn atomically.
    /// </summary>
    [TestMethod]
    public async Task T_358_02_WithdrawAsync_HappyPath_TasksCancelledInstanceWithdrawn()
    {
        await using var ctx = MakeContext();
        var version = await SeedVersionAsync(ctx, SingleApproverGraph("alice"));
        var engine = MakeEngine(ctx);

        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State,
            "T_358_02: Setup: StartAsync must produce Running instance.");

        var result = await engine.WithdrawAsync(instance.ID, "initiator");
        Assert.AreEqual(WorkflowActionCode.Withdrawn, result.Code,
            $"T_358_02: WithdrawAsync must succeed. Got {result.Code}.");

        await using var verify = MakeContext();

        // Instance must be Withdrawn.
        var afterInst = await verify.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Withdrawn, afterInst.State,
            "T_358_02: ProcessInstance must be Withdrawn after WithdrawAsync.");

        // All tasks must be Cancelled (no Pending/NotYetActive remaining).
        var nodeIds = await verify.Set<NodeInstance>()
            .Where(n => n.InstanceId == instance.ID)
            .Select(n => n.ID)
            .ToListAsync();
        var pendingTasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => nodeIds.Contains(t.NodeInstanceId)
                         && (t.State == TaskState.Pending || t.State == TaskState.NotYetActive))
            .CountAsync();
        Assert.AreEqual(0, pendingTasks,
            "T_358_02: All tasks must be Cancelled after WithdrawAsync.");
    }

    /// <summary>
    /// T_358_03: CAS-loser — if the instance was already finalized (simulated by manually
    /// setting state to Approved), WithdrawAsync must return CannotWithdrawAlreadyFinal.
    /// </summary>
    [TestMethod]
    public async Task T_358_03_WithdrawAsync_AlreadyFinalized_ReturnsCannotWithdraw()
    {
        await using var ctx = MakeContext();
        var version = await SeedVersionAsync(ctx, SingleApproverGraph("alice"));
        var engine = MakeEngine(ctx);

        // Start instance.
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        // Simulate a concurrent finalization: directly flip instance to Approved.
        await ctx.Set<ProcessInstance>()
            .Where(p => p.ID == instance.ID)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, InstanceState.Approved)
                                      .SetProperty(p => p.RowVer, x => x.RowVer + 1));

        // WithdrawAsync on an Approved instance must return CannotWithdrawAlreadyFinal.
        await using var withdrawCtx = MakeContext();
        var wEngine = MakeEngine(withdrawCtx);

        var result = await wEngine.WithdrawAsync(instance.ID, "initiator");

        Assert.AreEqual(WorkflowActionCode.CannotWithdrawAlreadyFinal, result.Code,
            $"T_358_03: WithdrawAsync on a finalized instance must return CannotWithdrawAlreadyFinal. Got {result.Code}.");
    }
}

// ─── #361 mid-chain InitiatorAutoApprove bounded loop tests ──────────────────

[TestClass]
public class Wf361MidChainAutoApproveTests : Wf357358361TestBase
{
    [TestInitialize]
    public void Setup() => BaseSetup("Wf361");

    [TestCleanup]
    public void Cleanup() => BaseCleanup();

    /// <summary>
    /// Build a 3-approver Sequential graph where "initiator" is at step 1 (middle).
    /// With InitiatorAutoApprove=true: step 0 = alice (human), step 1 = initiator (auto), step 2 = carol (human).
    /// The comma-separated Value mirrors how ThreeApproversGraph works in Wf320SeqMidChainTxTests.
    /// </summary>
    private static string MidChainAutoGraph() =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "Wf361MidChainGraph",
            Name = "Wf361MidChainGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "nodeA",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    // alice (step 0) → initiator (step 1, auto-approved) → carol (step 2, human)
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "alice,initiator,carol" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "nodeA" },
                new() { From = "nodeA", To = "end"   },
            },
        });

    /// <summary>
    /// Build a 2-approver Sequential graph where both approvers are the initiator.
    /// With InitiatorAutoApprove=true, all steps become AutoApproved → instance Approved immediately.
    /// </summary>
    private static string AllAutoGraph() =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "Wf361AllAutoGraph",
            Name = "Wf361AllAutoGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "nodeA",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    // Both steps are "initiator" → all auto-approved.
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "initiator,initiator" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "nodeA" },
                new() { From = "nodeA", To = "end"   },
            },
        });

    /// <summary>
    /// T_361_01: mid-chain InitiatorAutoApprove — step N+1 is auto-approved, step N+2 is a real approver.
    ///
    /// Setup: 3-step Sequential (alice → initiator[auto] → carol).
    /// After alice (step 0) approves:
    ///   - The #361 bounded loop must advance past the AutoApproved slot (step 1).
    ///   - carol (step 2) must have a Pending task.
    ///   - Instance must still be Running (carol hasn't approved yet).
    ///
    /// Pre-#361: the single AdvanceAsync call returned Blocked without activating carol's task
    /// → step 2 stayed NotYetActive and the instance would hang with no human able to proceed.
    /// Post-#361: the bounded loop consumes step 1 and activates step 2.
    /// </summary>
    [TestMethod]
    public async Task T_361_01_MidChain_AutoApprove_Slot1_ChainReachesCarolPending()
    {
        await using var ctx = MakeContext();
        var version = await SeedVersionAsync(ctx, MidChainAutoGraph());
        var opts = new WorkFlowOptions { InitiatorAutoApprove = true };
        var engine = MakeEngine(ctx, opts);

        // Start — "initiator" is the process starter. alice gets step 0's Pending task.
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State,
            "T_361_01: StartAsync must produce Running instance.");

        // Verify alice has a Pending task at step 0, and step 1 is AutoApproved.
        await using var readCtx = MakeContext();
        var aliceTask = await readCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.State == TaskState.Pending && t.AssigneeITCode == "alice");
        Assert.IsNotNull(aliceTask,
            "T_361_01: alice must have a Pending task at step 0 after StartAsync.");

        var nodeInst = await readCtx.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var autoTask = await readCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 1);
        Assert.AreEqual(TaskState.AutoApproved, autoTask.State,
            "T_361_01: step 1 (initiator) must be AutoApproved at start.");

        // Act: alice approves step 0.
        // The #361 bounded loop must advance past the AutoApproved step 1 and activate carol's step 2.
        await using var approveCtx = MakeContext();
        var approveEngine = MakeEngine(approveCtx, opts);
        var approveResult = await approveEngine.ApproveTaskAsync(aliceTask.ID, "alice");

        // Any non-error code is acceptable (Blocked/Advanced/InstanceApproved/AlreadyHandled).
        Assert.IsTrue(
            approveResult.Code is WorkflowActionCode.Blocked
                or WorkflowActionCode.Advanced
                or WorkflowActionCode.InstanceApproved
                or WorkflowActionCode.AlreadyHandled,
            $"T_361_01: ApproveTaskAsync must not return an error code. Got {approveResult.Code}.");

        // CORE ASSERTION: carol must now have a Pending task (step 2 activated by the #361 loop).
        await using var verifyCtx = MakeContext();
        var carolTask = await verifyCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.State == TaskState.Pending && t.AssigneeITCode == "carol");
        Assert.IsNotNull(carolTask,
            "T_361_01: carol must have a Pending task after alice approves — " +
            "the #361 bounded loop must advance past the mid-chain AutoApproved slot and activate step 2.");

        // Instance must still be Running (carol hasn't approved yet).
        var freshInst = await verifyCtx.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Running, freshInst.State,
            "T_361_01: instance must still be Running — carol's step 2 is Pending but not yet approved.");
    }

    /// <summary>
    /// T_361_02: all-auto chain — all Sequential slots auto-approved via InitiatorAutoApprove.
    ///
    /// Setup: 2-step Sequential where both approvers are "initiator".
    /// SequentialApprovalHandler already advances SequencePointer to TotalRequired when all
    /// steps are auto-approved at OnEnterAsync (lines 191-206). StartAsync drives to Approved.
    ///
    /// This test verifies: no human Pending task is left, instance reaches Approved.
    /// </summary>
    [TestMethod]
    public async Task T_361_02_AllAutoChain_NoHumanPendingTask_InstanceApproved()
    {
        await using var ctx = MakeContext();
        var version = await SeedVersionAsync(ctx, AllAutoGraph());
        var opts = new WorkFlowOptions { InitiatorAutoApprove = true };
        var engine = MakeEngine(ctx, opts);

        // Start — both steps auto-approved; engine should drive to Approved immediately.
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        // No human Pending task should exist.
        await using var verify = MakeContext();
        var pendingCount = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .CountAsync(t => t.State == TaskState.Pending);
        Assert.AreEqual(0, pendingCount,
            "T_361_02: all-auto chain must leave no human Pending task after StartAsync.");

        // Instance should be Approved.
        var afterInst = await verify.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, afterInst.State,
            "T_361_02: all-auto chain must reach Approved state immediately after StartAsync.");
    }

    /// <summary>
    /// T_361_03: #361 loop CAS-loser — no orphan-Pending task at nextPtr after concurrent epoch bump.
    ///
    /// Races two concurrent ApproveTaskAsync calls on alice's step-0 task.  One wins and drives the
    /// #361 loop to activate carol's step-2 task; the loser must return AlreadyHandled/TaskNotActive.
    /// KEY INVARIANT: no orphan-Pending task at step 2 when the pointer is still at step 1 —
    /// i.e. the per-iteration tx rolled back the step-2 activate when the pointer CAS was lost.
    ///
    /// Mirrors Wf320SeqMidChainTxTests.SeqMidChain_ConcurrentLoser_NoHalfActivatedState but
    /// exercises the #361 bounded-loop path (AutoApproved mid-chain slot at step 1).
    /// </summary>
    [TestMethod]
    public async Task T_361_03_Loop_CasLoser_NoOrphanPendingTask()
    {
        const int Rounds = 5;

        for (int round = 0; round < Rounds; round++)
        {
            var dbName = $"Wf361Conc_{round}_{Guid.NewGuid():N}";
            await using var keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
            keepAlive.Open();

            await using var initCtx = new WfAbbaTestContext(dbName);
            initCtx.Database.EnsureCreated();

            var opts = new WorkFlowOptions { InitiatorAutoApprove = true };

            // Seed the mid-chain graph.
            var ver = new ProcessDefinitionVersion
            {
                ID = Guid.NewGuid(),
                DefinitionId = Guid.NewGuid(),
                GraphJson = MidChainAutoGraph(),
                ContentHash = $"hash-{Guid.NewGuid():N}",
                VersionNo = 1,
                IsValid = true,
            };
            initCtx.Set<ProcessDefinitionVersion>().Add(ver);
            await initCtx.SaveChangesAsync();

            // Start instance ("initiator" is the process submitter → step 1 is AutoApproved).
            await using var startCtx = new WfAbbaTestContext(dbName);
            var startEngine = MakeEngine(startCtx, opts);
            var instance = await startEngine.StartAsync(ver.ID, null, "initiator", null);

            // Read alice's step-0 Pending task.
            await using var readCtx = new WfAbbaTestContext(dbName);
            var nodeInst = await readCtx.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
            var aliceTask = await readCtx.Set<ApprovalTask>()
                .AsNoTracking()
                .SingleAsync(t => t.NodeInstanceId == nodeInst.ID
                                   && t.SequenceOrder == 0
                                   && t.State == TaskState.Pending);

            var taskId = aliceTask.ID;
            var nodeInstId = nodeInst.ID;

            // Race two concurrent approve calls on alice's task.
            var barrier = new SemaphoreSlim(0, 2);

            Task<WorkflowActionResult> MakeApproveTask(string actor) => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var raceCtx = new WfAbbaTestContext(dbName);
                var raceEngine = MakeEngine(raceCtx, opts);
                return await raceEngine.ApproveTaskAsync(taskId, actor);
            });

            var t1 = MakeApproveTask("alice");
            var t2 = MakeApproveTask("alice");
            barrier.Release(2);
            var results = await Task.WhenAll(t1, t2);

            // One must win (Advanced/Blocked/InstanceApproved), one must lose gracefully.
            int won = results.Count(r =>
                r.Code is WorkflowActionCode.Advanced
                       or WorkflowActionCode.Blocked
                       or WorkflowActionCode.InstanceApproved);
            int lost = results.Count(r =>
                r.Code is WorkflowActionCode.AlreadyHandled
                       or WorkflowActionCode.TaskNotActive
                       or WorkflowActionCode.NodeClosed);
            Assert.AreEqual(1, won,
                $"Round {round}: exactly 1 result must be Advanced/Blocked/InstanceApproved, got [{results[0].Code},{results[1].Code}].");
            Assert.AreEqual(1, lost,
                $"Round {round}: exactly 1 result must be AlreadyHandled/TaskNotActive/NodeClosed, got [{results[0].Code},{results[1].Code}].");

            // KEY INVARIANT: no orphan-Pending task at step 2 while pointer is still at step 1.
            await using var verifyCtx = new WfAbbaTestContext(dbName);
            var freshNode = await verifyCtx.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInstId);
            var step2Task = await verifyCtx.Set<ApprovalTask>()
                .AsNoTracking()
                .SingleAsync(t => t.NodeInstanceId == nodeInstId && t.SequenceOrder == 2);

            if (freshNode.SequencePointer >= 2)
            {
                // Pointer reached step 2 — step-2 task must be Pending (winner activated it correctly).
                Assert.AreEqual(TaskState.Pending, step2Task.State,
                    $"Round {round}: SequencePointer=={freshNode.SequencePointer} but step-2 task is {step2Task.State} — pointer advanced without activating carol's task.");
            }
            else
            {
                // Pointer is still at step 1 — step-2 task must NOT be Pending (no orphan activation).
                Assert.AreNotEqual(TaskState.Pending, step2Task.State,
                    $"Round {round}: SequencePointer=={freshNode.SequencePointer} (step 1) but step-2 task is Pending — " +
                    "orphan-Pending orphan! The per-iteration tx must roll back the activate when the CAS is lost (#361 R2).");
            }
        }
    }
}
