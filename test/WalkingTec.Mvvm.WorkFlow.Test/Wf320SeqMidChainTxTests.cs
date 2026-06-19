#nullable enable
// PR C regression tests — W-SEQ-MIDCHAIN atomicity (Issue #320).
//
// Three tests:
//   1. Happy-path-still-works (3-step Sequential regression guard)
//   2. InitiatorAutoApprove preserved after Task→Node reorder
//   3. Atomicity invariant: CAS-loser leaves no half-activated state
//
// All tests use SQLite shared-in-memory (never EF InMemory — spec §7.6 / #119 / #162).
// This file reuses WfSequentialTestContext and SeqTestGraphs defined in SequentialTests.cs
// (same namespace, same assembly).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class Wf320SeqMidChainTxTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfSeqMidChain_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        using var ctx = MakeContext();
        ctx.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    private WfSequentialTestContext MakeContext() => new(_dbName);

    private (IWorkflowEngine engine, WfSequentialTestContext ctx) MakeEngine()
    {
        var ctx = MakeContext();
        var opts = new WorkFlowOptions();
        var resolver = new DefaultApproverResolverExposed(opts, ctx);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
        var engine = WorkflowEngine_Exposed.Create(ctx, dispatcher, NullLogger.Instance);
        return (engine, ctx);
    }

    private async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfSequentialTestContext ctx,
        string graphJson)
    {
        var version = new ProcessDefinitionVersion
        {
            ID = Guid.NewGuid(),
            DefinitionId = Guid.NewGuid(),
            VersionNo = 1,
            SchemaVersion = 1,
            GraphJson = graphJson,
            ContentHash = "test-hash-" + Guid.NewGuid().ToString("N"),
            PublishedAt = DateTime.UtcNow,
            PublishedBy = "test",
            IsValid = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }

    private static string ThreeApproversGraph(string a1, string a2, string a3) =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "ThreeApproverGraph",
            Name = "ThreeApproverGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey     = "approval1",
                    Kind        = NodeKind.Approval,
                    ApproveMode = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = $"{a1},{a2},{a3}" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end" },
            },
        });

    // ── Test 1: Happy-path regression guard (3-step Sequential) ──────────────────

    /// <summary>
    /// Regression guard: approving step 0 of a 3-step Sequential node MUST atomically:
    /// (a) advance the pointer to 1, AND (b) activate the step-1 task to Pending.
    /// Both assertions must hold simultaneously — not just one or the other.
    /// Full flow through all 3 steps must reach InstanceApproved.
    /// EventLog Seq must be monotonic from 1 with no gaps.
    /// </summary>
    [TestMethod]
    public async Task SeqMidChain_HappyPath_ThreeStep_PointerAndTaskBothAdvance_AtomicReturnsApproved()
    {
        const string A1 = "alice_seq3";
        const string A2 = "bob_seq3";
        const string A3 = "carol_seq3";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, ThreeApproversGraph(A1, A2, A3));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read0 = MakeContext();
        var nodeInst = await read0.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(0, nodeInst.SequencePointer);
        Assert.AreEqual(3, nodeInst.TotalRequired);

        var task0 = await read0.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);
        Assert.AreEqual(TaskState.Pending, task0.State);

        // Approve step 0 → should advance pointer to 1 AND activate step-1 task.
        var r0 = await engine.ApproveTaskAsync(task0.ID, A1, "step 0 ok");
        Assert.AreEqual(WorkflowActionCode.Advanced, r0.Code,
            $"Step 0 approve must return Advanced (steps remain), got {r0.Code}.");

        // ATOMIC ASSERTION: both pointer and task must have advanced together.
        await using var read1 = MakeContext();
        var nodeAfter0 = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);
        Assert.AreEqual(1, nodeAfter0.SequencePointer,
            "SequencePointer must be 1 after step 0 approved.");

        var task1 = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 1);
        Assert.AreEqual(TaskState.Pending, task1.State,
            "Step-1 task must be Pending after step 0 approved (atomic with pointer advance).");

        // Approve step 1.
        var r1 = await engine.ApproveTaskAsync(task1.ID, A2, "step 1 ok");
        Assert.AreEqual(WorkflowActionCode.Advanced, r1.Code,
            $"Step 1 approve must return Advanced (step 2 remains), got {r1.Code}.");

        // Step 2 must be Pending.
        await using var read2 = MakeContext();
        var task2 = await read2.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 2);
        Assert.AreEqual(TaskState.Pending, task2.State,
            "Step-2 task must be Pending after step 1 approved.");

        // Approve step 2 (last) → InstanceApproved.
        var r2 = await engine.ApproveTaskAsync(task2.ID, A3, "step 2 ok");
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, r2.Code,
            $"Step 2 approve (last) must return InstanceApproved, got {r2.Code}.");

        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State);

        // EventLog Seq monotonic.
        var events = await readFinal.Set<WorkflowEventLog>()
            .AsNoTracking()
            .Where(e => e.InstanceId == instance.ID)
            .OrderBy(e => e.Seq)
            .ToListAsync();
        Assert.IsTrue(events.Count >= 4, $"Expected ≥4 log entries, got {events.Count}.");
        for (int i = 0; i < events.Count; i++)
            Assert.AreEqual(i + 1, events[i].Seq, $"EventLog Seq[{i}] must be {i + 1} (monotonic).");
    }

    // ── Test 2: InitiatorAutoApprove disambiguation preserved after Task→Node reorder ──

    /// <summary>
    /// The pointer CAS now comes AFTER the activate CAS. The AutoApprove disambiguation
    /// (activateRows==0 when nextTask.State==AutoApproved) must still fire post-commit.
    ///
    /// Setup: 3-step Sequential where step 1 is manually set to AutoApproved before step 0 is approved,
    /// and step 2 remains NotYetActive. Approving step 0 must:
    ///   (a) commit the transaction (pointer→1, activate-rows==0 for step-1)
    ///   (b) post-commit: detect step-1 is AutoApproved → call AdvanceAsync (the #361 bounded loop)
    ///   (c) #361 bounded loop: pointer=1, step-1 is AutoApproved → advance pointer to 2, activate step-2 → Pending
    ///
    /// Post-#361 invariants:
    ///   - pointer advances to 2 (the #361 loop drained the AutoApproved slot at pointer=1)
    ///   - step-1 stays AutoApproved (the activate CAS correctly skipped it)
    ///   - step-2 is Pending (the #361 bounded loop activated it)
    ///   - instance stays Running
    ///   - result.Code is NOT AlreadyHandled
    ///
    /// This proves the post-commit #361 bounded-loop code path fires correctly after the Task→Node reorder.
    /// </summary>
    [TestMethod]
    public async Task SeqMidChain_InitiatorAutoApprove_Disambiguation_StillFires_AfterReorder()
    {
        const string A1 = "alice_auto3";
        const string A2 = "auto_approver3";
        const string A3 = "carol_auto3";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, ThreeApproversGraph(A1, A2, A3));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var setup = MakeContext();
        var nodeInst = await setup.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        // Manually set step-1 task to AutoApproved (simulating InitiatorAutoApprove for step 1).
        // Step 2 stays NotYetActive — step 1 is skipped by the engine (activateRows==0 for AutoApproved).
        await setup.Set<ApprovalTask>()
            .Where(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, TaskState.AutoApproved));

        // Verify starting state: step-0 Pending, step-1 AutoApproved, step-2 NotYetActive.
        var task0 = await setup.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);
        Assert.AreEqual(TaskState.Pending, task0.State);

        // Approve step 0.
        // With the new atomic transaction + #361 bounded loop:
        //   - activateRows == 0 (step-1 is AutoApproved, not NotYetActive/AddedPending → CAS misses)
        //   - advanceRows == 1 (pointer CAS succeeds → pointer→1)
        //   - commit succeeds
        //   - post-commit: activateRows==0 → read nextTask(order=1) → State==AutoApproved → #361 bounded loop fires
        //   - #361 bounded loop: detects step-1 AutoApproved → advances pointer to 2, activates step-2 (→ Pending)
        //   - engine returns WorkflowActionResult reflecting the #361 loop outcome
        var result = await engine.ApproveTaskAsync(task0.ID, A1, "step 0 ok");

        // The key invariant: engine must NOT return AlreadyHandled — the transaction committed,
        // the pointer advanced, and the AutoApprove disambiguation fired (returning Blocked or Advanced).
        Assert.AreNotEqual(WorkflowActionCode.AlreadyHandled, result.Code,
            "AutoApprove disambiguation must fire — engine must not treat step 0 as AlreadyHandled.");

        // Verify the pointer advanced to 2: txn committed (→1) then the #361 bounded loop drained
        // the AutoApproved slot at pointer=1 and advanced to pointer=2.
        await using var readAfter = MakeContext();
        var nodeAfter = await readAfter.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);
        Assert.AreEqual(2, nodeAfter.SequencePointer,
            "Pointer must have advanced to 2 — #361 bounded loop drained the AutoApproved slot at pointer=1.");

        // Step-1 must still be AutoApproved (the activate CAS correctly skipped it).
        var step1After = await readAfter.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 1);
        Assert.AreEqual(TaskState.AutoApproved, step1After.State,
            "Step-1 must remain AutoApproved — the activate CAS only targets NotYetActive/AddedPending.");

        // Step-2 must be Pending — the #361 bounded loop activated it after draining the
        // AutoApproved step-1 slot and advancing the pointer to 2.
        var step2After = await readAfter.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 2);
        Assert.AreEqual(TaskState.Pending, step2After.State,
            "Step-2 must be Pending — the #361 bounded loop activated it after advancing past the AutoApproved slot.");

        // Instance is still Running — step-2 is now Pending, waiting for approval.
        var instanceAfter = await readAfter.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Running, instanceAfter.State,
            "Instance must still be Running — step-2 Pending task needs to be approved.");
    }

    // ── Test 3: Atomicity invariant — CAS-loser leaves no half-activated state ───

    /// <summary>
    /// Simulates the concurrent-loser scenario: two concurrent ApproveTaskAsync calls on
    /// the same Pending task. Only one wins the task CAS. The key invariant:
    /// after the race, there must be NO state where SequencePointer==1 but the step-1
    /// task is still NotYetActive (the old half-activated strand scenario).
    /// If pointer==1 then step-1 task MUST be Pending.
    /// If pointer==0 then step-1 task MUST NOT be Pending (no orphan activation).
    /// </summary>
    [TestMethod]
    public async Task SeqMidChain_ConcurrentLoser_NoHalfActivatedState()
    {
        const int Rounds = 5;

        for (int round = 0; round < Rounds; round++)
        {
            var dbName = $"WfSeqMidChainConc_{round}_{Guid.NewGuid():N}";
            await using var keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
            keepAlive.Open();

            await using var initCtx = new WfSequentialTestContext(dbName);
            initCtx.Database.EnsureCreated();

            const string A1 = "alice_conc";
            const string A2 = "bob_conc";
            const string A3 = "carol_conc";
            var graphJson = ThreeApproversGraph(A1, A2, A3);

            var versionId = Guid.NewGuid();
            initCtx.Set<ProcessDefinitionVersion>().Add(new ProcessDefinitionVersion
            {
                ID = versionId,
                DefinitionId = Guid.NewGuid(),
                VersionNo = 1,
                SchemaVersion = 1,
                GraphJson = graphJson,
                ContentHash = "conc-mid-" + versionId.ToString("N"),
                PublishedAt = DateTime.UtcNow,
                PublishedBy = "test",
                IsValid = true,
            });
            await initCtx.SaveChangesAsync();

            // Start instance.
            await using var startCtx = new WfSequentialTestContext(dbName);
            var startOpts = new WorkFlowOptions();
            var startResolver = new DefaultApproverResolverExposed(startOpts, startCtx);
            var startDispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(startResolver, startOpts);
            var startEngine = WorkflowEngine_Exposed.Create(startCtx, startDispatcher, NullLogger.Instance);
            var instance = await startEngine.StartAsync(versionId, null, "initiator", null);

            await using var readCtx = new WfSequentialTestContext(dbName);
            var nodeInst = await readCtx.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
            var task0 = await readCtx.Set<ApprovalTask>()
                .AsNoTracking()
                .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0 && t.State == TaskState.Pending);

            // Race two approve calls on the same task0.
            var barrier = new SemaphoreSlim(0, 2);
            var taskId = task0.ID;
            var nodeInstId = nodeInst.ID;

            Task<WorkflowActionResult> MakeApproveTask(string actor) => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                var raceOpts = new WorkFlowOptions();
                await using var raceCtx = new WfSequentialTestContext(dbName);
                var raceResolver = new DefaultApproverResolverExposed(raceOpts, raceCtx);
                var raceDispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(raceResolver, raceOpts);
                var raceEngine = WorkflowEngine_Exposed.Create(raceCtx, raceDispatcher, NullLogger.Instance);
                return await raceEngine.ApproveTaskAsync(taskId, actor);
            });

            var t1 = MakeApproveTask(A1);
            var t2 = MakeApproveTask(A1);
            barrier.Release(2);
            var results = await Task.WhenAll(t1, t2);

            // Exactly one must advance; the other must lose gracefully.
            int advanced = results.Count(r => r.Code == WorkflowActionCode.Advanced);
            int lost = results.Count(r =>
                r.Code == WorkflowActionCode.AlreadyHandled ||
                r.Code == WorkflowActionCode.TaskNotActive ||
                r.Code == WorkflowActionCode.NodeClosed);

            Assert.AreEqual(1, advanced,
                $"Round {round}: exactly 1 result must be Advanced (mid-chain), got [{results[0].Code},{results[1].Code}].");
            Assert.AreEqual(1, lost,
                $"Round {round}: exactly 1 result must be AlreadyHandled/TaskNotActive/NodeClosed, got [{results[0].Code},{results[1].Code}].");

            // KEY INVARIANT: no half-activated state.
            await using var verifyCtx = new WfSequentialTestContext(dbName);
            var freshNode = await verifyCtx.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInstId);
            var step1Task = await verifyCtx.Set<ApprovalTask>()
                .AsNoTracking()
                .SingleAsync(t => t.NodeInstanceId == nodeInstId && t.SequenceOrder == 1);

            if (freshNode.SequencePointer == 1)
            {
                Assert.AreEqual(TaskState.Pending, step1Task.State,
                    $"Round {round}: SequencePointer==1 but step-1 task is {step1Task.State} — half-activated strand! " +
                    "The tx must have committed both pointer advance AND task activation together.");
            }
            else
            {
                // pointer still 0: step-1 task must NOT be Pending (no orphan activation without pointer advance).
                Assert.AreNotEqual(TaskState.Pending, step1Task.State,
                    $"Round {round}: SequencePointer==0 but step-1 task is Pending — orphan activation! " +
                    "The task was activated without the pointer advancing (half-activated in reverse).");
            }
        }
    }
}
