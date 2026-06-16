#nullable enable
// W-ALL-CLAIM-TO-INCREMENT (T5 fix): regression tests for the atomic claim+increment tx.
//
// These tests verify that after each approval in All/Any mode:
//   1. ApprovedCount is durably written to the DB (not lost if engine crashes between claim and advance).
//   2. The count is never double-incremented when incrementAlreadyDone=true is passed to
//      ExecuteApproveCompletionAsync.
//   3. CAS-loser attempts do not corrupt the count.
//
// All tests use SQLite shared-in-memory (NEVER EF InMemory — spec §7.6 / #119 / #162).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class Wf320AllAnyClaimTxTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfClaimTx_{Guid.NewGuid():N}";
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
        var ctx        = MakeContext();
        var opts       = options ?? new WorkFlowOptions();
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

    // ── T5-All-1: 3-approver All — ApprovedCount increments durably after each approval ──

    /// <summary>
    /// 会签 (All mode): 3 approvers, approve all 3 in sequence.
    /// After each approval, ApprovedCount in DB must equal the number of approvals so far.
    /// This verifies the T5 fix — the count is committed atomically with the claim.
    /// </summary>
    [TestMethod]
    public async Task T5_All_ThreeApprovers_HappyPath_ApprovedCountIncrements()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, AllAnyGraphs.AllApprovers(new[] { A1, A2, A3 }));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        // Read the approval node and its three tasks.
        await using var read0 = MakeContext();
        var nodeInst = await read0.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var tasks = await read0.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        var t0 = tasks.Single(t => t.AssigneeITCode == A1);
        var t1 = tasks.Single(t => t.AssigneeITCode == A2);
        var t2 = tasks.Single(t => t.AssigneeITCode == A3);

        // First approval — must return Advanced, ApprovedCount must be 1.
        var r1 = await engine.ApproveTaskAsync(t0.ID, A1);
        Assert.AreEqual(WorkflowActionCode.Advanced, r1.Code,
            $"1st approval (1/3) must return Advanced, got {r1.Code}.");

        await using var readCtx1 = MakeContext();
        var freshNode1 = await readCtx1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(1, freshNode1.ApprovedCount,
            "ApprovedCount must be 1 after first approval (T5: committed in claim+increment tx).");

        // Second approval — must return Advanced, ApprovedCount must be 2.
        var r2 = await engine.ApproveTaskAsync(t1.ID, A2);
        Assert.AreEqual(WorkflowActionCode.Advanced, r2.Code,
            $"2nd approval (2/3) must return Advanced, got {r2.Code}.");

        await using var readCtx2 = MakeContext();
        var freshNode2 = await readCtx2.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(2, freshNode2.ApprovedCount,
            "ApprovedCount must be 2 after second approval.");

        // Third approval — threshold reached → InstanceApproved, ApprovedCount must be 3.
        var r3 = await engine.ApproveTaskAsync(t2.ID, A3);
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, r3.Code,
            $"3rd approval (last) must return InstanceApproved, got {r3.Code}.");

        await using var readFinal = MakeContext();
        var freshNodeFinal = await readFinal.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(3, freshNodeFinal.ApprovedCount,
            "ApprovedCount must be 3 after all three approvals.");

        var finalInst = await readFinal.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State,
            "Instance must be Approved after all three approvers approve.");
    }

    // ── T5-Any-1: 3-approver Any — first approve completes node, ApprovedCount == 1 ──

    /// <summary>
    /// 或签 (Any mode): 3 approvers, first approve must complete the node.
    /// ApprovedCount must be exactly 1 and instance must be Approved.
    /// </summary>
    [TestMethod]
    public async Task T5_Any_FirstApprove_CompletesNode_ApprovedCountOne()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, AllAnyGraphs.AnyApprovers(new[] { A1, A2, A3 }));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read0 = MakeContext();
        var nodeInst = await read0.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var tasks = await read0.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        var t0 = tasks.Single(t => t.AssigneeITCode == A1);

        // First approve in Any mode → should complete the instance.
        var r1 = await engine.ApproveTaskAsync(t0.ID, A1);
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, r1.Code,
            $"Any mode: first approve must return InstanceApproved, got {r1.Code}.");

        await using var readFinal = MakeContext();
        var freshNodeFinal = await readFinal.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(1, freshNodeFinal.ApprovedCount,
            "Any mode: ApprovedCount must be exactly 1 after first approval.");

        var finalInst = await readFinal.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State,
            "Any mode: instance must be Approved after first approval.");
    }

    // ── T5-All-2: non-final approval — ApprovedCount is durable even when threshold not met ──

    /// <summary>
    /// 会签 (All mode, 3 approvers, threshold=3): first approval must not complete the node.
    /// The result is Advanced, but ApprovedCount must already be 1 in the DB (not 0).
    /// This is the core T5 regression test: count is committed atomically with the claim.
    /// </summary>
    [TestMethod]
    public async Task T5_All_NonFinalApprove_ApprovedCountDurableBeforeAdvanced()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, AllAnyGraphs.AllApprovers(new[] { A1, A2, A3 }));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read0 = MakeContext();
        var nodeInst = await read0.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var tasks = await read0.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        var t0 = tasks.Single(t => t.AssigneeITCode == A1);

        // First approval — threshold=3, so should return Advanced.
        var r1 = await engine.ApproveTaskAsync(t0.ID, A1);
        Assert.AreEqual(WorkflowActionCode.Advanced, r1.Code,
            $"Non-final approval in 3-approver All must return Advanced, got {r1.Code}.");

        // ApprovedCount must be 1 in DB immediately — durable, not delayed.
        await using var readCtx = MakeContext();
        var freshNode = await readCtx.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(1, freshNode.ApprovedCount,
            "T5 regression: ApprovedCount must be 1 in DB even though threshold not yet met. " +
            "The count is committed atomically with the claim, before AdvanceWithActorAsync.");

        // The task must be in Approved state.
        await using var readTask = MakeContext();
        var freshTask = await readTask.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.ID == t0.ID);
        Assert.AreEqual(TaskState.Approved, freshTask.State,
            "Task must be in Approved state after approve.");

        // Instance must still be Running (not yet at threshold).
        var midInst = await readCtx.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Running, midInst.State,
            "Instance must still be Running after non-final approval.");
    }

    // ── T5-All-3: CAS-loser — second attempt on same task does not increment count ──

    /// <summary>
    /// 会签 (All mode): approve task t0 successfully (count → 1), then try to approve
    /// the same task again. The second call must return AlreadyHandled.
    /// ApprovedCount must remain 1 (no double-increment).
    /// </summary>
    [TestMethod]
    public async Task T5_All_CasLoser_LeavesApprovedCountUnchanged()
    {
        const string A1 = "alice"; const string A2 = "bob"; const string A3 = "carol";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, AllAnyGraphs.AllApprovers(new[] { A1, A2, A3 }));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read0 = MakeContext();
        var nodeInst = await read0.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var tasks = await read0.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        var t0 = tasks.Single(t => t.AssigneeITCode == A1);

        // First approval succeeds — Advanced (1/3).
        var r1 = await engine.ApproveTaskAsync(t0.ID, A1);
        Assert.AreEqual(WorkflowActionCode.Advanced, r1.Code,
            $"First approval must return Advanced, got {r1.Code}.");

        // Verify count is 1.
        await using var readCtx1 = MakeContext();
        var freshNode1 = await readCtx1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(1, freshNode1.ApprovedCount, "ApprovedCount must be 1 after first approval.");

        // Second attempt on the SAME task (simulating CAS-loser / duplicate call).
        var r2 = await engine.ApproveTaskAsync(t0.ID, A1);
        Assert.AreEqual(WorkflowActionCode.AlreadyHandled, r2.Code,
            $"Second attempt on already-claimed task must return AlreadyHandled, got {r2.Code}.");

        // Count must NOT have been incremented again.
        await using var readCtx2 = MakeContext();
        var freshNode2 = await readCtx2.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(1, freshNode2.ApprovedCount,
            "T5 regression: ApprovedCount must still be 1 after a CAS-loser attempt (no double-increment).");
    }
}
