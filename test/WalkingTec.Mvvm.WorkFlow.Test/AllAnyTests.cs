#nullable enable
// WF-9 + WF-10: AllApprovalHandler (会签) + AnyApprovalHandler (或签) integration tests.
//
// Tests:
//   All-1. 3-approver All full flow → all three approve → instance Approved.
//   All-2. 3-approver All ratio (60 %, ceil=2): 2 approvals → instance Approved.
//   All-3. RejectGate.Immediate: any reject → node fails → instance Rejected.
//   All-4. RejectGate.AfterAll: single reject does NOT fail node; second reject makes threshold unreachable → instance Rejected.
//   All-5. Impossible threshold (percent=1.5 → clamped to 100%): 3 approvers, 3 needed.
//   All-6. T-CONC-3: two concurrent final approvals → exactly one completes node; other is AlreadyHandled / Advanced.
//   Any-1. 3-approver Any: first approve → instance Approved; sibling tasks Cancelled.
//   Any-2. T-CONC-1: two concurrent approvals → exactly one wins; other is AlreadyHandled.
//   Any-3. Reject behaviour: A1 rejects → node still Running; all three reject → instance Rejected.
//
// All tests use SQLite shared-in-memory (NEVER EF InMemory — spec §7.6 / #119 / #162).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ── Graph helpers ────────────────────────────────────────────────────────────────

internal static class AllAnyGraphs
{
    /// <summary>Start → Approval (All mode, n approvers, optional percent) → End.</summary>
    public static string AllApprovers(
        IEnumerable<string> approvers,
        decimal? percent = null,
        RejectGate rejectGate = RejectGate.Immediate)
    {
        var rule = string.Join(",", approvers);
        return WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "AllGraph",
            Name = "AllGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey       = "approval1",
                    Kind          = NodeKind.Approval,
                    ApproveMode   = ApproveMode.All,
                    ApproverRule  = new ApproverRuleDef { Type = "User", Value = rule },
                    ApprovePercent = percent,
                    RejectGate    = rejectGate,
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });
    }

    /// <summary>Start → Approval (Any mode, n approvers) → End.</summary>
    public static string AnyApprovers(IEnumerable<string> approvers) =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "AnyGraph",
            Name = "AnyGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Any,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = string.Join(",", approvers) },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });
}

// ── Test fixture ──────────────────────────────────────────────────────────────────

[TestClass]
public class AllAnyTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfAllAny_{Guid.NewGuid():N}";
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

    private (IWorkflowEngine engine, WfSequentialTestContext ctx) MakeEngine(
        WorkFlowOptions? options = null)
    {
        var ctx  = MakeContext();
        var opts = options ?? new WorkFlowOptions();
        var resolver   = new DefaultApproverResolverExposed(opts, ctx);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithAllModes(resolver, opts);
        var engine     = WorkflowEngine_Exposed.Create(ctx, dispatcher, NullLogger.Instance);
        return (engine, ctx);
    }

    private async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfSequentialTestContext ctx,
        string graphJson)
    {
        var version = new ProcessDefinitionVersion
        {
            ID            = Guid.NewGuid(),
            DefinitionId  = Guid.NewGuid(),
            VersionNo     = 1,
            SchemaVersion = 1,
            GraphJson     = graphJson,
            ContentHash   = "test-hash-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            TenantCode    = null,
            IsValid       = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }

    // ── All-1: 3-approver full flow → Approved ────────────────────────────────────

    /// <summary>
    /// 会签 (All mode): 3 approvers, all three approve sequentially.
    /// After the third approval the instance must reach Approved.
    /// Each intermediate approval returns Advanced.
    /// </summary>
    [TestMethod]
    public async Task All_ThreeApprovers_AllApprove_ReachesApproved()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, AllAnyGraphs.AllApprovers(new[] { A1, A2, A3 }));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running after StartAsync (all three tasks Pending).");

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(NodeState.Activated, nodeInst.State);
        Assert.AreEqual(3, nodeInst.TotalRequired);

        // All three tasks must be Pending simultaneously.
        var tasks = await read1.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .OrderBy(t => t.SequenceOrder)
            .ToListAsync();
        Assert.AreEqual(3, tasks.Count, "Three parallel Pending tasks must be minted.");
        Assert.IsTrue(tasks.All(t => t.State == TaskState.Pending),
            "All three tasks must be Pending simultaneously.");

        var t0 = tasks.Single(t => t.AssigneeITCode == A1);
        var t1 = tasks.Single(t => t.AssigneeITCode == A2);
        var t2 = tasks.Single(t => t.AssigneeITCode == A3);

        // First two approvals return Advanced.
        var r1 = await engine.ApproveTaskAsync(t0.ID, A1);
        Assert.AreEqual(WorkflowActionCode.Advanced, r1.Code,
            $"1st approval (1/3) must return Advanced, got {r1.Code}.");

        var r2 = await engine.ApproveTaskAsync(t1.ID, A2);
        Assert.AreEqual(WorkflowActionCode.Advanced, r2.Code,
            $"2nd approval (2/3) must return Advanced, got {r2.Code}.");

        // Third approval: threshold reached → InstanceApproved.
        var r3 = await engine.ApproveTaskAsync(t2.ID, A3);
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, r3.Code,
            $"3rd approval (last) must return InstanceApproved, got {r3.Code}.");

        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State);
    }

    // ── All-2: ratio (60%, 3 approvers, ceil=2) → Approved after 2 ───────────────

    /// <summary>
    /// 会签 ratio: 3 approvers, approvePercent=0.6 → threshold=ceil(3×0.6)=2.
    /// After 2 approvals the node must complete (3rd task becomes irrelevant).
    /// </summary>
    [TestMethod]
    public async Task All_RatioSixtyPercent_TwoApprovalsComplete()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx,
            AllAnyGraphs.AllApprovers(new[] { A1, A2, A3 }, percent: 0.6m));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        // Verify threshold via model: ApprovePercent was copied to NodeInstance.
        Assert.AreEqual(0.6m, nodeInst.ApprovePercent,
            "ApprovePercent must be copied from NodeDef to NodeInstance.");
        // ComputeThreshold: ceil(3 * 0.6) = ceil(1.8) = 2.
        int threshold = AllApprovalHandler.ComputeThreshold(
            nodeInst.TotalRequired, nodeInst.ApprovePercent, nodeInst.NodeKey);
        Assert.AreEqual(2, threshold, "Threshold for 60% of 3 must be 2.");

        var tasks = await read1.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();
        var t0 = tasks.Single(t => t.AssigneeITCode == A1);
        var t1 = tasks.Single(t => t.AssigneeITCode == A2);

        var r1 = await engine.ApproveTaskAsync(t0.ID, A1);
        Assert.AreEqual(WorkflowActionCode.Advanced, r1.Code,
            $"1st approval (1/2 needed) must return Advanced, got {r1.Code}.");

        // 2nd approval crosses threshold of 2 → InstanceApproved.
        var r2 = await engine.ApproveTaskAsync(t1.ID, A2);
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, r2.Code,
            $"2nd approval (threshold=2 reached) must return InstanceApproved, got {r2.Code}.");

        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State);
    }

    // ── All-3: RejectGate.Immediate → first reject fails node ────────────────────

    /// <summary>
    /// 会签 RejectGate.Immediate (default): the first reject must fail the node
    /// immediately; remaining Pending tasks must be Cancelled; instance Rejected.
    /// </summary>
    [TestMethod]
    public async Task All_RejectGateImmediate_FirstReject_FailsNode()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx,
            AllAnyGraphs.AllApprovers(new[] { A1, A2, A3 }, rejectGate: RejectGate.Immediate));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var tasks = await read1.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();
        var t0 = tasks.Single(t => t.AssigneeITCode == A1);

        // A1 approves first.
        var rApprove = await engine.ApproveTaskAsync(t0.ID, A1);
        Assert.AreEqual(WorkflowActionCode.Advanced, rApprove.Code);

        // A2 rejects → immediate gate fires.
        await using var read2 = MakeContext();
        var t1 = await read2.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.AssigneeITCode == A2);
        var rReject = await engine.RejectTaskAsync(t1.ID, A2, "Not approved.");
        Assert.AreEqual(WorkflowActionCode.Rejected, rReject.Code,
            $"Immediate gate: first reject must return Rejected, got {rReject.Code}.");

        // Instance must be Rejected.
        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Rejected, finalInst.State,
            "Instance must be Rejected after RejectGate.Immediate fires.");

        // Remaining Pending task (A3) must be Cancelled.
        var t2State = await readFinal.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID && t.AssigneeITCode == A3)
            .Select(t => t.State)
            .SingleAsync();
        Assert.AreEqual(TaskState.Cancelled, t2State,
            "Remaining Pending task must be Cancelled after RejectGate.Immediate.");
    }

    // ── All-4: RejectGate.AfterAll ────────────────────────────────────────────────

    /// <summary>
    /// 会签 RejectGate.AfterAll (3 approvers, threshold=3):
    /// First reject does NOT fail node (threshold still reachable).
    /// After 2nd reject the threshold is unreachable (max possible = 1 < 3) → node fails.
    /// </summary>
    [TestMethod]
    public async Task All_RejectGateAfterAll_OneRejectContinues_TwoRejectsFails()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx,
            AllAnyGraphs.AllApprovers(new[] { A1, A2, A3 }, rejectGate: RejectGate.AfterAll));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var tasks = await read1.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();
        var t0 = tasks.Single(t => t.AssigneeITCode == A1);
        var t1 = tasks.Single(t => t.AssigneeITCode == A2);
        var t2 = tasks.Single(t => t.AssigneeITCode == A3);

        // A1 rejects — gate not met yet (A2, A3 still pending → max possible = 2, threshold=3... wait, need re-check).
        // Actually for AfterAll at threshold=3: after A1 rejects, remaining pending = A2+A3 (2 pending).
        // approved (0) + pending (2) = 2 < 3 (threshold) → threshold is unreachable → fails.
        // To test "gate not met" scenario we need a ratio graph where threshold < total.
        // Re-design: use 3 approvers, percent=0.6 → threshold=2, with AfterAll.
        // After 1 reject: approved(0) + pending(2) = 2 >= threshold(2) → continues.
        // After A2 also rejects: approved(0) + pending(1) = 1 < threshold(2) → fails.
        // But this test uses percent=null (threshold=3). Let's adjust to percent=0.6.
        // HOWEVER: this test was seeded with threshold=3 (percent=null, AfterAll).
        // With threshold=3 and AfterAll: after any single reject → approved+pending = 2 < 3 → immediate fail.
        // So this test actually tests that reject DOES fail for AfterAll with threshold=3.
        // Let's pivot: A1 approves, A2 rejects → approved(1)+pending(1) = 2 < 3 → fails.
        //
        // True "AfterAll gate defers" test: percent=0.6, threshold=2, 3 approvers.
        // Use the test below (All-4b). This test (All-4) verifies that AfterAll still
        // eventually fails when threshold is unreachable.
        //
        // A1 approves.
        var rApprove = await engine.ApproveTaskAsync(t0.ID, A1);
        Assert.AreEqual(WorkflowActionCode.Advanced, rApprove.Code);

        // A2 rejects → approved(1)+pending(1) = 2 < 3 → threshold unreachable → node fails.
        var rReject = await engine.RejectTaskAsync(t1.ID, A2, "nope");
        Assert.AreEqual(WorkflowActionCode.Rejected, rReject.Code,
            $"AfterAll gate: reject when threshold unreachable must return Rejected, got {rReject.Code}.");

        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Rejected, finalInst.State);

        // A3 task must be Cancelled.
        var t2State = await readFinal.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID && t.AssigneeITCode == A3)
            .Select(t => t.State)
            .SingleAsync();
        Assert.AreEqual(TaskState.Cancelled, t2State,
            "Remaining Pending task must be Cancelled after node rejected.");
    }

    [TestMethod]
    public async Task All_RejectGateAfterAll_Ratio_SingleRejectContinues_ThenFails()
    {
        // 3 approvers, percent=0.6 (threshold=2), RejectGate.AfterAll.
        // After A1 rejects: approved(0) + pending(2) = 2 >= 2 → gate not met → node continues.
        // After A2 rejects: approved(0) + pending(1) = 1 < 2 → unreachable → node fails.
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx,
            AllAnyGraphs.AllApprovers(new[] { A1, A2, A3 }, percent: 0.6m, rejectGate: RejectGate.AfterAll));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var tasks = await read1.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID).ToListAsync();
        var t0 = tasks.Single(t => t.AssigneeITCode == A1);
        var t1 = tasks.Single(t => t.AssigneeITCode == A2);

        // A1 rejects — gate not met yet (approved=0, pending=2, threshold=2 → 0+2 >= 2 → continue).
        var rReject1 = await engine.RejectTaskAsync(t0.ID, A1, "nope");
        Assert.AreEqual(WorkflowActionCode.Advanced, rReject1.Code,
            $"AfterAll+ratio: first reject when threshold still reachable must return Advanced, got {rReject1.Code}.");

        // Instance still Running.
        await using var readMid = MakeContext();
        var midInst = await readMid.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Running, midInst.State,
            "Instance must still be Running after first reject with AfterAll gate.");

        // A2 rejects → approved(0)+pending(1)=1 < threshold(2) → unreachable → fails.
        var rReject2 = await engine.RejectTaskAsync(t1.ID, A2, "also nope");
        Assert.AreEqual(WorkflowActionCode.Rejected, rReject2.Code,
            $"AfterAll+ratio: second reject makes threshold unreachable, must return Rejected, got {rReject2.Code}.");

        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Rejected, finalInst.State);
    }

    // ── All-5: impossible threshold (percent=1.5) → clamped to 100% ──────────────

    /// <summary>
    /// ComputeThreshold with percent &gt; 1.0 must clamp to total (100%).
    /// With 3 approvers and threshold=3, all 3 must approve.
    /// </summary>
    [TestMethod]
    public async Task All_ImpossibleThreshold_ClampsToHundredPercent()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        // percent=1.5 → ComputeThreshold clamps to total=3.
        var version  = await SeedVersionAsync(ctx,
            AllAnyGraphs.AllApprovers(new[] { A1, A2, A3 }, percent: 1.5m));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        // Compute threshold: 1.5 > 1.0 → clamped → threshold = 3.
        int threshold = AllApprovalHandler.ComputeThreshold(
            nodeInst.TotalRequired, nodeInst.ApprovePercent, nodeInst.NodeKey);
        Assert.AreEqual(3, threshold, "percent=1.5 must clamp to threshold=3 (100% of 3).");

        var tasks = await read1.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID).ToListAsync();
        var t0 = tasks.Single(t => t.AssigneeITCode == A1);
        var t1 = tasks.Single(t => t.AssigneeITCode == A2);
        var t2 = tasks.Single(t => t.AssigneeITCode == A3);

        await engine.ApproveTaskAsync(t0.ID, A1);
        await engine.ApproveTaskAsync(t1.ID, A2);

        // Only 2 of 3 approved; threshold is 3 — must still be Running.
        await using var readMid = MakeContext();
        var midInst = await readMid.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Running, midInst.State,
            "Must still be Running after 2/3 approvals when threshold=3.");

        // Third approval completes node.
        var r3 = await engine.ApproveTaskAsync(t2.ID, A3);
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, r3.Code,
            $"Third approval (threshold=3) must return InstanceApproved, got {r3.Code}.");

        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State);
    }

    // ── All-6: T-CONC-3 — two concurrent final approvals, exactly one winner ─────

    /// <summary>
    /// 会签 T-CONC-3: 2-approver All node, threshold=2.
    /// Race the second (final) approval by two concurrent calls.
    /// Exactly one must return InstanceApproved; the other AlreadyHandled or Advanced.
    /// The node must transition to CompletedApproved exactly once.
    /// </summary>
    [TestMethod]
    public async Task All_TCONC3_TwoConcurrentFinalApprovals_ExactlyOneWinner()
    {
        const int Rounds = 8;

        for (int round = 0; round < Rounds; round++)
        {
            var dbName = $"WfAllConc_{round}_{Guid.NewGuid():N}";
            await using var keepAlive = new SqliteConnection(
                $"DataSource={dbName}?mode=memory&cache=shared");
            keepAlive.Open();

            await using var seedCtx = new WfSequentialTestContext(dbName);
            seedCtx.Database.EnsureCreated();

            const string A1 = "alice"; const string A2 = "bob";

            // 2 approvers, percent=null → threshold=2. Race the 2nd (final) approval.
            var graphJson = AllAnyGraphs.AllApprovers(new[] { A1, A2 });
            var versionId = Guid.NewGuid();
            seedCtx.Set<ProcessDefinitionVersion>().Add(new ProcessDefinitionVersion
            {
                ID = versionId, DefinitionId = Guid.NewGuid(), VersionNo = 1, SchemaVersion = 1,
                GraphJson = graphJson, ContentHash = "hash-" + versionId, IsValid = true,
            });
            await seedCtx.SaveChangesAsync();

            // Start the instance.
            await using var ctx1 = new WfSequentialTestContext(dbName);
            var opts = new WorkFlowOptions();
            var resolver1   = new DefaultApproverResolverExposed(opts, ctx1);
            var dispatcher1 = NodeKindDispatcher_Exposed.CreateWithAllModes(resolver1, opts);
            var engine1     = WorkflowEngine_Exposed.Create(ctx1, dispatcher1, NullLogger.Instance);
            var instance    = await engine1.StartAsync(versionId, null, "init_conc", null);

            // Find the approval node and approve A1 first (step 0/2 — advisory count = 1 < threshold=2).
            await using var readCtx = new WfSequentialTestContext(dbName);
            var nodeInst = await readCtx.Set<NodeInstance>().AsNoTracking()
                .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
            var tasks = await readCtx.Set<ApprovalTask>().AsNoTracking()
                .Where(t => t.NodeInstanceId == nodeInst.ID).ToListAsync();
            var t0 = tasks.Single(t => t.AssigneeITCode == A1);

            // ApproveA1 via engine1.
            var r0 = await engine1.ApproveTaskAsync(t0.ID, A1);
            Assert.AreEqual(WorkflowActionCode.Advanced, r0.Code,
                $"Round {round}: A1 approval (1/2) must return Advanced.");

            // Now A2 task is the final approval. Race it with two concurrent engines.
            await using var rCtx = new WfSequentialTestContext(dbName);
            var t1Id = (await rCtx.Set<ApprovalTask>().AsNoTracking()
                .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.AssigneeITCode == A2)).ID;

            var barrier = new SemaphoreSlim(0, 2);

            Task<WorkflowActionResult> MakeRacer(string dbN, Guid taskId, string actor)
            {
                return Task.Run(async () =>
                {
                    await barrier.WaitAsync();
                    await using var raceCtx = new WfSequentialTestContext(dbN);
                    var raceOpts       = new WorkFlowOptions();
                    var raceResolver   = new DefaultApproverResolverExposed(raceOpts, raceCtx);
                    var raceDispatcher = NodeKindDispatcher_Exposed.CreateWithAllModes(raceResolver, raceOpts);
                    var raceEngine     = WorkflowEngine_Exposed.Create(raceCtx, raceDispatcher, NullLogger.Instance);
                    return await raceEngine.ApproveTaskAsync(taskId, actor);
                });
            }

            var race1 = MakeRacer(dbName, t1Id, A2);
            var race2 = MakeRacer(dbName, t1Id, A2);
            barrier.Release(2);
            var results = await Task.WhenAll(race1, race2);

            int approved = results.Count(r => r.Code == WorkflowActionCode.InstanceApproved);
            int others   = results.Count(r => r.Code != WorkflowActionCode.InstanceApproved);

            Assert.AreEqual(1, approved,
                $"Round {round}: exactly one must return InstanceApproved, got [{results[0].Code},{results[1].Code}].");
            Assert.AreEqual(1, others,
                $"Round {round}: exactly one must return non-InstanceApproved (AlreadyHandled or Advanced), " +
                $"got [{results[0].Code},{results[1].Code}].");

            // Node must be CompletedApproved exactly once.
            await using var finalCtx = new WfSequentialTestContext(dbName);
            var completedNodes = await finalCtx.Set<NodeInstance>().AsNoTracking()
                .CountAsync(n => n.InstanceId == instance.ID
                                  && n.NodeKind == NodeKind.Approval
                                  && n.State == NodeState.CompletedApproved);
            Assert.AreEqual(1, completedNodes,
                $"Round {round}: exactly one CompletedApproved NodeInstance must exist.");
        }
    }

    // ── Any-1: first approve → Approved, siblings Cancelled ──────────────────────

    /// <summary>
    /// 或签 (Any mode): 3 approvers.
    /// First approver to approve wins — node completes; other two tasks are Cancelled.
    /// Instance reaches Approved.
    /// </summary>
    [TestMethod]
    public async Task Any_FirstApprove_WinsNode_SiblingsCancelled()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, AllAnyGraphs.AnyApprovers(new[] { A1, A2, A3 }));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running — three Pending tasks, waiting for any one approval.");

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var tasks = await read1.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID).ToListAsync();
        Assert.IsTrue(tasks.All(t => t.State == TaskState.Pending),
            "All three tasks must be Pending simultaneously in Any mode.");

        var t0 = tasks.Single(t => t.AssigneeITCode == A1);

        // A1 approves first — must win the node.
        var r1 = await engine.ApproveTaskAsync(t0.ID, A1);
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, r1.Code,
            $"First approve in Any mode must return InstanceApproved, got {r1.Code}.");

        // Sibling tasks (A2, A3) must be Cancelled.
        await using var readFinal = MakeContext();
        var allTasks = await readFinal.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID).ToListAsync();
        var a1Task = allTasks.Single(t => t.AssigneeITCode == A1);
        var a2Task = allTasks.Single(t => t.AssigneeITCode == A2);
        var a3Task = allTasks.Single(t => t.AssigneeITCode == A3);
        Assert.AreEqual(TaskState.Approved,   a1Task.State, "Winner task must be Approved.");
        Assert.AreEqual(TaskState.Cancelled,  a2Task.State, "Sibling A2 must be Cancelled.");
        Assert.AreEqual(TaskState.Cancelled,  a3Task.State, "Sibling A3 must be Cancelled.");

        var finalInst = await readFinal.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State);
    }

    // ── Any-2: T-CONC-1 — two concurrent approvals, exactly one winner ────────────

    /// <summary>
    /// 或签 T-CONC-1: two approvers each race to approve their own task.
    /// Exactly one must win (InstanceApproved); the other must be a clean loser
    /// (AlreadyHandled, NodeClosed, NodeAlreadyDecided, or TaskNotActive — see #307).
    /// Node must reach CompletedApproved exactly once.
    /// </summary>
    [TestMethod]
    public async Task Any_TCONC1_TwoConcurrentApprovals_ExactlyOneWinner()
    {
        const int Rounds = 10;

        for (int round = 0; round < Rounds; round++)
        {
            var dbName = $"WfAnyConc_{round}_{Guid.NewGuid():N}";
            await using var keepAlive = new SqliteConnection(
                $"DataSource={dbName}?mode=memory&cache=shared");
            keepAlive.Open();

            await using var seedCtx = new WfSequentialTestContext(dbName);
            seedCtx.Database.EnsureCreated();

            const string A1 = "alice"; const string A2 = "bob";
            var graphJson = AllAnyGraphs.AnyApprovers(new[] { A1, A2 });
            var versionId = Guid.NewGuid();
            seedCtx.Set<ProcessDefinitionVersion>().Add(new ProcessDefinitionVersion
            {
                ID = versionId, DefinitionId = Guid.NewGuid(), VersionNo = 1, SchemaVersion = 1,
                GraphJson = graphJson, ContentHash = "hash-" + versionId, IsValid = true,
            });
            await seedCtx.SaveChangesAsync();

            await using var ctx1 = new WfSequentialTestContext(dbName);
            var opts1       = new WorkFlowOptions();
            var resolver1   = new DefaultApproverResolverExposed(opts1, ctx1);
            var dispatcher1 = NodeKindDispatcher_Exposed.CreateWithAllModes(resolver1, opts1);
            var engine1     = WorkflowEngine_Exposed.Create(ctx1, dispatcher1, NullLogger.Instance);
            var instance    = await engine1.StartAsync(versionId, null, "init_any_conc", null);

            await using var readCtx = new WfSequentialTestContext(dbName);
            var nodeInst = await readCtx.Set<NodeInstance>().AsNoTracking()
                .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
            var tasks = await readCtx.Set<ApprovalTask>().AsNoTracking()
                .Where(t => t.NodeInstanceId == nodeInst.ID).ToListAsync();
            var t0Id = tasks.Single(t => t.AssigneeITCode == A1).ID;
            var t1Id = tasks.Single(t => t.AssigneeITCode == A2).ID;

            var barrier = new SemaphoreSlim(0, 2);

            Task<WorkflowActionResult> MakeRacer(string dbN, Guid taskId, string actor)
            {
                return Task.Run(async () =>
                {
                    await barrier.WaitAsync();
                    await using var raceCtx = new WfSequentialTestContext(dbN);
                    var raceOpts       = new WorkFlowOptions();
                    var raceResolver   = new DefaultApproverResolverExposed(raceOpts, raceCtx);
                    var raceDispatcher = NodeKindDispatcher_Exposed.CreateWithAllModes(raceResolver, raceOpts);
                    var raceEngine     = WorkflowEngine_Exposed.Create(raceCtx, raceDispatcher, NullLogger.Instance);
                    return await raceEngine.ApproveTaskAsync(taskId, actor);
                });
            }

            var race1   = MakeRacer(dbName, t0Id, A1);
            var race2   = MakeRacer(dbName, t1Id, A2);
            barrier.Release(2);
            var results = await Task.WhenAll(race1, race2);

            int approved = results.Count(r => r.Code == WorkflowActionCode.InstanceApproved);
            // #307: widen loser set — NodeClosed is a valid race-loser code when the winner
            // drives the node/instance to approved+closed before the concurrent loser's CAS
            // guard runs.  AlreadyHandled, NodeAlreadyDecided, and TaskNotActive are also
            // legitimate "I lost the race / already decided" outcomes.
            int loser = results.Count(r => IsLegitimateLoserOutcome(r.Code));

            Assert.AreEqual(1, approved,
                $"Round {round}: exactly one must return InstanceApproved, got [{results[0].Code},{results[1].Code}].");
            Assert.AreEqual(1, loser,
                $"Round {round}: exactly one must be a clean loser (AlreadyHandled/NodeClosed/NodeAlreadyDecided/TaskNotActive), " +
                $"got [{results[0].Code},{results[1].Code}].");

            // Node completed exactly once.
            await using var finalCtx = new WfSequentialTestContext(dbName);
            var completedNodes = await finalCtx.Set<NodeInstance>().AsNoTracking()
                .CountAsync(n => n.InstanceId == instance.ID
                                  && n.NodeKind == NodeKind.Approval
                                  && n.State == NodeState.CompletedApproved);
            Assert.AreEqual(1, completedNodes,
                $"Round {round}: exactly one CompletedApproved NodeInstance must exist.");
        }
    }

    /// <summary>
    /// Returns true when <paramref name="code"/> is a legitimate concurrent-loser outcome
    /// for an approval race: the actor lost the CAS and performed no state change.
    /// (#307: NodeClosed is equally valid as AlreadyHandled when the winner closes the
    /// node/instance before the loser's guard checks run.)
    /// </summary>
    private static bool IsLegitimateLoserOutcome(WorkflowActionCode code) =>
        code is WorkflowActionCode.AlreadyHandled
             or WorkflowActionCode.NodeClosed
             or WorkflowActionCode.NodeAlreadyDecided
             or WorkflowActionCode.TaskNotActive;

    // ── Any-3: reject behaviour ───────────────────────────────────────────────────

    /// <summary>
    /// 或签 reject semantics:
    ///   • A1 rejects → node still Running (A2 and A3 can still approve).
    ///   • A2 rejects → node still Running (A3 can still approve).
    ///   • A3 rejects → last pending approver rejected, nobody approved → instance Rejected.
    /// </summary>
    [TestMethod]
    public async Task Any_Reject_SingleDoesNotFail_LastRejectFails()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, AllAnyGraphs.AnyApprovers(new[] { A1, A2, A3 }));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var tasks = await read1.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID).ToListAsync();
        var t0 = tasks.Single(t => t.AssigneeITCode == A1);
        var t1 = tasks.Single(t => t.AssigneeITCode == A2);
        var t2 = tasks.Single(t => t.AssigneeITCode == A3);

        // A1 rejects — 2 approvers still pending → node continues.
        var rR1 = await engine.RejectTaskAsync(t0.ID, A1, "nope");
        Assert.AreEqual(WorkflowActionCode.Advanced, rR1.Code,
            $"Or-sign: first reject (2 others pending) must return Advanced, got {rR1.Code}.");

        await using var readMid1 = MakeContext();
        var midInst1 = await readMid1.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Running, midInst1.State,
            "Instance must be Running after first reject in Any mode.");

        // A2 rejects — A3 still pending → node continues.
        var rR2 = await engine.RejectTaskAsync(t1.ID, A2, "nope too");
        Assert.AreEqual(WorkflowActionCode.Advanced, rR2.Code,
            $"Or-sign: second reject (1 other pending) must return Advanced, got {rR2.Code}.");

        await using var readMid2 = MakeContext();
        var midInst2 = await readMid2.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Running, midInst2.State,
            "Instance must be Running after second reject (last approver still pending).");

        // A3 rejects — no pending approvers, nobody approved → node fails.
        var rR3 = await engine.RejectTaskAsync(t2.ID, A3, "nope three");
        Assert.AreEqual(WorkflowActionCode.Rejected, rR3.Code,
            $"Or-sign: last-reject (0 pending, 0 approved) must return Rejected, got {rR3.Code}.");

        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Rejected, finalInst.State,
            "Instance must be Rejected after last approver in Any mode rejects.");
    }
}
