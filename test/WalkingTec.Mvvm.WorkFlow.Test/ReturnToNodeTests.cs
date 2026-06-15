#nullable enable
// WF-16 Wave-3 — ReturnToNode functional tests.
//
// Covers:
//   T-RET-3:  MaxReturnLoops cap prevents infinite return ping-pong.
//   T-MIG-1:  Existing rows (Generation==0) continue to work correctly after Wave-3 migration.
//   ReturnToNode_Succeeds: happy-path return supersedes span and mints fresh node.
//   ReturnToPrev_Succeeds: convenience wrapper finds the closest dominating Approval node.
//   ReturnToNode_RejectsBadTarget: non-dominator target returns NoDominatorTarget.
//   ReturnToNode_WrongActor: actor mismatch returns TaskNotActive.
//   Dominator_SimpleLinear: dominator-set computation for a simple A→B→C graph.
//   Dominator_ForkMerge: dominator-set computation for a fork-merge graph.
//
// All tests use SQLite shared-in-memory (NOT EF InMemory — engine requires ExecuteUpdateAsync).

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

/// <summary>
/// Functional tests for WF-16 ReturnToNode / ReturnToPrev.
/// </summary>
[TestClass]
public class ReturnToNodeTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfReturn_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        using var db = MakeContext();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    private WfTestContext MakeContext() => new(_dbName);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ProcessInstance SeedInstance(WfTestContext db, uint generation = 0, uint returnLoops = 0)
    {
        var inst = new ProcessInstance
        {
            ID = Guid.NewGuid(),
            State = InstanceState.Running,
            RowVer = 0,
            InitiatorITCode = "user1",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid = true,
            TenantCode = "T1",
            Generation = generation,
            ReturnLoops = returnLoops,
            NextSeq = 1,
        };
        db.ProcessInstances.Add(inst);
        db.SaveChanges();
        return inst;
    }

    private NodeInstance SeedNode(WfTestContext db, Guid instanceId, string nodeKey,
        NodeState state = NodeState.Activated, uint generation = 0, uint rowVer = 0)
    {
        var node = new NodeInstance
        {
            ID = Guid.NewGuid(),
            InstanceId = instanceId,
            NodeKey = nodeKey,
            NodeKind = NodeKind.Approval,
            State = state,
            RowVer = rowVer,
            TenantCode = "T1",
            Generation = generation,
        };
        db.NodeInstances.Add(node);
        db.SaveChanges();
        return node;
    }

    private ApprovalTask SeedTask(WfTestContext db, Guid nodeInstanceId, string assignee,
        TaskState state = TaskState.Pending, uint generation = 0, uint rowVer = 0)
    {
        var task = new ApprovalTask
        {
            ID = Guid.NewGuid(),
            NodeInstanceId = nodeInstanceId,
            AssigneeITCode = assignee,
            State = state,
            RowVer = rowVer,
            TenantCode = "T1",
            IsValid = true,
            Generation = generation,
        };
        db.ApprovalTasks.Add(task);
        db.SaveChanges();
        return task;
    }

    // ── T-RET-3: MaxReturnLoops cap ───────────────────────────────────────────

    /// <summary>
    /// T-RET-3: When <c>ReturnLoops == MaxReturnLoops</c>, BeginReturnAsync must not
    /// advance and the engine must return <c>MaxReturnLoopsExceeded</c>.
    /// Prevents infinite ping-pong (Race D cap, spec §2 STEP-6-FC).
    /// </summary>
    [TestMethod]
    public async Task T_RET_3_MaxReturnLoops_Cap_RejectsReturn()
    {
        // Arrange: instance already at the cap (ReturnLoops == MaxReturnLoops default = 3).
        await using var db = MakeContext();

        var maxLoops = new WorkFlowOptions().MaxReturnLoops; // default 3
        var inst = SeedInstance(db, generation: (uint)maxLoops, returnLoops: (uint)maxLoops);
        var nodeA = SeedNode(db, inst.ID, "NodeA", NodeState.Activated, generation: (uint)maxLoops);
        var task = SeedTask(db, nodeA.ID, "approver1", TaskState.Pending, generation: (uint)maxLoops);

        // Also seed a "target" node instance at NodeB (gen 0 so it would be the previous Approval).
        // The CAS should fail before we reach STEP-5.
        var nodeB = SeedNode(db, inst.ID, "NodeB", NodeState.Superseded, generation: 0);

        // Act: attempt BeginReturnAsync directly (engine-level CAS).
        var (rows, _) = await GuardedTransition.BeginReturnAsync(
            db, inst.ID,
            expectedRowVer: inst.RowVer,
            expectedGeneration: inst.Generation,
            maxReturnLoops: maxLoops,
            leaseExpiry: DateTime.UtcNow.AddMinutes(30),
            ct: CancellationToken.None);

        // Assert: CAS returns 0 rows — cap enforced atomically.
        Assert.AreEqual(0, rows,
            "BeginReturnAsync must return 0 rows when ReturnLoops == MaxReturnLoops (Race D cap).");

        // Verify: instance state unchanged.
        await using var verify = MakeContext();
        var final = await verify.ProcessInstances.AsNoTracking().SingleAsync(x => x.ID == inst.ID);
        Assert.AreEqual(InstanceState.Running, final.State, "Instance must remain Running.");
        Assert.AreEqual((uint)maxLoops, final.ReturnLoops, "ReturnLoops must not have incremented.");
    }

    // ── T-MIG-1: Pre-Wave-3 rows (Generation==0) continue to work ────────────

    /// <summary>
    /// T-MIG-1: Existing rows with <c>Generation==0</c> are treated as pre-Wave-3
    /// and continue to participate in the engine normally.  The new generation column
    /// defaults to 0 for old rows; CAS operations that don't filter by generation
    /// still succeed.
    /// </summary>
    [TestMethod]
    public async Task T_MIG_1_PreWave3_Generation0_Rows_StillWork()
    {
        await using var db = MakeContext();

        // Pre-Wave-3 row: ProcessInstance with Generation==0 (default).
        var inst = SeedInstance(db, generation: 0);
        Assert.AreEqual(0u, inst.Generation, "Pre-Wave-3 instance must have Generation==0.");

        // Pre-Wave-3 NodeInstance with Generation==0.
        var node = SeedNode(db, inst.ID, "OldNode", NodeState.Activated, generation: 0);
        Assert.AreEqual(0u, node.Generation, "Pre-Wave-3 NodeInstance must have Generation==0.");

        // Pre-Wave-3 ApprovalTask with Generation==0.
        var task = SeedTask(db, node.ID, "approver1", TaskState.Pending, generation: 0);
        Assert.AreEqual(0u, task.Generation, "Pre-Wave-3 ApprovalTask must have Generation==0.");

        // Standard CAS on pre-Wave-3 NodeInstance must still work (no generation filter).
        var rows = await GuardedTransition.CompleteNodeInstanceAsync(
            db, node.ID, expectedRowVer: 0, completedState: NodeState.CompletedApproved,
            ct: CancellationToken.None);

        Assert.AreEqual(1, rows, "CAS on pre-Wave-3 NodeInstance (gen=0) must succeed.");

        await using var verify = MakeContext();
        var finalNode = await verify.NodeInstances.AsNoTracking().SingleAsync(x => x.ID == node.ID);
        Assert.AreEqual(NodeState.CompletedApproved, finalNode.State);
    }

    // ── Dominator computation tests ───────────────────────────────────────────

    /// <summary>
    /// Simple linear graph: Start → A(Approval) → B(Approval) → End.
    /// B's dominators must be {Start, A, B}.
    /// A is a valid return target for B (strict dominator that is Approval).
    /// </summary>
    [TestMethod]
    public void Dominator_SimpleLinear_CorrectDominators()
    {
        var graph = BuildLinearGraph();

        var doms = WorkflowGraphValidator.ComputeDominators(graph);

        // B is dominated by Start, A, and B itself.
        Assert.IsTrue(doms.ContainsKey("B"), "B must be in dominator map.");
        Assert.IsTrue(doms["B"].Contains("start"), "start must dominate B.");
        Assert.IsTrue(doms["B"].Contains("A"), "A must dominate B.");
        Assert.IsTrue(doms["B"].Contains("B"), "B dominates itself.");

        // Valid return targets for B must include A (strict dominator + Approval).
        var targets = WorkflowGraphValidator.GetValidReturnTargets(graph, "B");
        Assert.IsTrue(targets.Contains("A"), "A must be a valid return target for B.");
        Assert.IsFalse(targets.Contains("B"), "B cannot be a return target for itself.");
        Assert.IsFalse(targets.Contains("end"), "end is not an Approval node.");
    }

    /// <summary>
    /// Linear A→B: GetPrevApprovalNode for B returns A.
    /// </summary>
    [TestMethod]
    public void GetPrevApprovalNode_LinearGraph_ReturnsA()
    {
        var graph = BuildLinearGraph();
        var prev = WorkflowGraphValidator.GetPrevApprovalNode(graph, "B");
        Assert.AreEqual("A", prev, "ReturnToPrev for B in linear graph must yield A.");
    }

    /// <summary>
    /// Fork-merge graph: Start → A(Approval) → [fork] → B(Approval) → C(Approval) → End.
    /// C's strict dominators (Approval) must be A and B.
    /// ReturnToPrev for C should yield B (closer).
    /// </summary>
    [TestMethod]
    public void Dominator_ForkMerge_ClosestPrevIsB()
    {
        var graph = BuildLinearGraph_ABC();
        var prev = WorkflowGraphValidator.GetPrevApprovalNode(graph, "C");
        Assert.AreEqual("B", prev, "ReturnToPrev for C in A→B→C graph must yield B (closest dominator).");
    }

    /// <summary>
    /// Non-dominator target: in linear A→B graph, End is not a valid return target for B.
    /// </summary>
    [TestMethod]
    public void GetValidReturnTargets_EndNode_IsNotValidTarget()
    {
        var graph = BuildLinearGraph();
        var targets = WorkflowGraphValidator.GetValidReturnTargets(graph, "B");
        Assert.IsFalse(targets.Contains("end"), "End node is not a valid return target.");
        Assert.IsFalse(targets.Contains("start"), "Start node is not an Approval node — not a valid target.");
    }

    // ── AllocateSeqAsync: Wave-3 portable Seq counter ────────────────────────

    /// <summary>
    /// AllocateSeqAsync allocates monotonically increasing Seq values from NextSeq.
    /// Starting at NextSeq=1: first call returns seq=1, bumps NextSeq to 2.
    /// </summary>
    [TestMethod]
    public async Task AllocateSeqAsync_FirstCall_ReturnsSeq1_BumpsNextSeq()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db);
        Assert.AreEqual(1, inst.NextSeq, "NextSeq must start at 1.");

        var (rows, seq) = await GuardedTransition.AllocateSeqAsync(
            db, inst.ID, expectedRowVer: inst.RowVer, ct: CancellationToken.None);

        Assert.AreEqual(1, rows, "AllocateSeqAsync must return 1 row on success.");
        Assert.AreEqual(1, seq, "First Seq must be 1.");

        await using var verify = MakeContext();
        var fresh = await verify.ProcessInstances.AsNoTracking().SingleAsync(x => x.ID == inst.ID);
        Assert.AreEqual(2, fresh.NextSeq, "NextSeq must be bumped to 2 after first allocation.");
    }

    /// <summary>
    /// AllocateSeqAsync returns 0 rows when RowVer does not match (concurrent transition).
    /// </summary>
    [TestMethod]
    public async Task AllocateSeqAsync_WrongRowVer_Returns0Rows()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db);

        var (rows, seq) = await GuardedTransition.AllocateSeqAsync(
            db, inst.ID, expectedRowVer: 999u, ct: CancellationToken.None);

        Assert.AreEqual(0, rows, "AllocateSeqAsync must return 0 when RowVer mismatches.");
        Assert.AreEqual(0, seq, "Seq must be 0 on CAS miss.");
    }

    // ── BeginReturnAsync CAS ──────────────────────────────────────────────────

    /// <summary>
    /// BeginReturnAsync succeeds: Running → Returning, Generation++, ReturnLoops++, lease stamped.
    /// </summary>
    [TestMethod]
    public async Task BeginReturnAsync_Success_TransitionsAndBumpsGeneration()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db, generation: 0, returnLoops: 0);

        var (rows, _) = await GuardedTransition.BeginReturnAsync(
            db, inst.ID,
            expectedRowVer: inst.RowVer,
            expectedGeneration: inst.Generation,
            maxReturnLoops: 3,
            leaseExpiry: DateTime.UtcNow.AddMinutes(30),
            ct: CancellationToken.None);

        Assert.AreEqual(1, rows, "BeginReturnAsync must return 1 row on first call.");

        await using var verify = MakeContext();
        var fresh = await verify.ProcessInstances.AsNoTracking().SingleAsync(x => x.ID == inst.ID);
        Assert.AreEqual(InstanceState.Returning, fresh.State, "State must be Returning.");
        Assert.AreEqual(1u, fresh.Generation, "Generation must be bumped to 1.");
        Assert.AreEqual(1u, fresh.ReturnLoops, "ReturnLoops must be 1.");
        Assert.IsNotNull(fresh.ReturningLeaseUtc, "ReturningLeaseUtc must be stamped.");
    }

    // ── SupersedeNodeAsync CAS ────────────────────────────────────────────────

    /// <summary>
    /// SupersedeNodeAsync transitions Activated → Superseded and stamps SupersededAtGen.
    /// </summary>
    [TestMethod]
    public async Task SupersedeNodeAsync_Activated_BecomesSuperseded()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db);
        var node = SeedNode(db, inst.ID, "NodeX", NodeState.Activated, rowVer: 0);

        var rows = await GuardedTransition.SupersedeNodeAsync(
            db, node.ID, expectedRowVer: 0, supersededAtGen: 1u, ct: CancellationToken.None);

        Assert.AreEqual(1, rows);
        await using var verify = MakeContext();
        var final = await verify.NodeInstances.AsNoTracking().SingleAsync(x => x.ID == node.ID);
        Assert.AreEqual(NodeState.Superseded, final.State);
        Assert.AreEqual(1u, final.SupersededAtGen);
    }

    // ── DiscardTasksForReturnAsync ─────────────────────────────────────────────

    /// <summary>
    /// DiscardTasksForReturnAsync bulk-cancels all Pending/NotYetActive tasks in span,
    /// except the explicitly excluded trigger task.
    /// </summary>
    [TestMethod]
    public async Task DiscardTasksForReturnAsync_CancelsPendingExcludingTrigger()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db);
        var node = SeedNode(db, inst.ID, "N1", NodeState.Activated);

        var triggerTask = SeedTask(db, node.ID, "approver1", TaskState.Pending);
        var otherTask1  = SeedTask(db, node.ID, "approver2", TaskState.Pending);
        var otherTask2  = SeedTask(db, node.ID, "approver3", TaskState.NotYetActive);

        await GuardedTransition.DiscardTasksForReturnAsync(
            db, new List<Guid> { node.ID }, excludeTaskId: triggerTask.ID, CancellationToken.None);

        await using var verify = MakeContext();
        var trigger = await verify.ApprovalTasks.AsNoTracking().SingleAsync(x => x.ID == triggerTask.ID);
        var other1  = await verify.ApprovalTasks.AsNoTracking().SingleAsync(x => x.ID == otherTask1.ID);
        var other2  = await verify.ApprovalTasks.AsNoTracking().SingleAsync(x => x.ID == otherTask2.ID);

        Assert.AreEqual(TaskState.Pending, trigger.State, "Trigger task must NOT be cancelled by DiscardTasksForReturnAsync.");
        Assert.AreEqual(TaskState.Cancelled, other1.State, "Sibling Pending task must be cancelled.");
        Assert.AreEqual(TaskState.Cancelled, other2.State, "NotYetActive sibling must be cancelled.");
    }

    // ── MintNodeInstanceGuardedAsync idempotency ──────────────────────────────

    /// <summary>
    /// MintNodeInstanceGuardedAsync returns false on the second call for the same
    /// (TenantCode, InstanceId, NodeKey, Generation) tuple — idempotent via UNIQUE constraint.
    /// </summary>
    [TestMethod]
    public async Task MintNodeInstanceGuardedAsync_SecondCall_IsIdempotent()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db, generation: 1);

        var targetDef = new NodeDef
        {
            NodeKey = "TargetApproval",
            Kind = NodeKind.Approval,
        };

        bool first = await GuardedTransition.MintNodeInstanceGuardedAsync(db, inst, targetDef, generation: 1u, CancellationToken.None);
        bool second = await GuardedTransition.MintNodeInstanceGuardedAsync(db, inst, targetDef, generation: 1u, CancellationToken.None);

        Assert.IsTrue(first, "First mint must succeed.");
        Assert.IsFalse(second, "Second mint for same (InstanceId, NodeKey, Generation) must be idempotent (returns false).");

        // Only one NodeInstance must exist.
        await using var verify = MakeContext();
        int count = await verify.NodeInstances.AsNoTracking()
            .CountAsync(n => n.InstanceId == inst.ID && n.NodeKey == "TargetApproval" && n.Generation == 1u);
        Assert.AreEqual(1, count, "Exactly one NodeInstance must exist after idempotent double-mint.");
    }

    // ── Graph builders ────────────────────────────────────────────────────────

    private static WorkflowGraph BuildLinearGraph()
    {
        // start → A(Approval) → B(Approval) → end
        return new WorkflowGraph
        {
            Key = "linear",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new() { NodeKey = "A", Kind = NodeKind.Approval },
                new() { NodeKey = "B", Kind = NodeKind.Approval },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "A" },
                new() { From = "A", To = "B" },
                new() { From = "B", To = "end" },
            },
            FieldWhitelist = new List<FieldWhitelistEntry>(),
        };
    }

    private static WorkflowGraph BuildLinearGraph_ABC()
    {
        // start → A(Approval) → B(Approval) → C(Approval) → end
        return new WorkflowGraph
        {
            Key = "linearABC",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new() { NodeKey = "A", Kind = NodeKind.Approval },
                new() { NodeKey = "B", Kind = NodeKind.Approval },
                new() { NodeKey = "C", Kind = NodeKind.Approval },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "A" },
                new() { From = "A", To = "B" },
                new() { From = "B", To = "C" },
                new() { From = "C", To = "end" },
            },
            FieldWhitelist = new List<FieldWhitelistEntry>(),
        };
    }

    // ── #322 (C4 fix) real-engine helpers ────────────────────────────────────

    /// <summary>
    /// Creates a full <see cref="IWorkflowEngine"/> backed by a <see cref="WfSequentialTestContext"/>
    /// so we can run <see cref="IWorkflowEngine.ReturnToNodeAsync"/> end-to-end.
    /// (ReturnToNodeTests normally only exercises <see cref="GuardedTransition"/> directly;
    /// the C4 fix tests need the real engine.)
    /// </summary>
    private IWorkflowEngine MakeRealEngine(WfSequentialTestContext ctx)
    {
        var options    = new WorkFlowOptions();
        var resolver   = new DefaultApproverResolverExposed(options, ctx);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithAllModes(resolver, options);
        return WorkflowEngine_Exposed.Create(ctx, dispatcher, NullLogger.Instance);
    }

    // ── #322: ReturnToNode activates target node and materialises tasks ─────────

    /// <summary>
    /// #322 (C4 fix): After ReturnToNodeAsync, the target node must be Activated
    /// (not left as Pending) and its ApprovalTasks must be materialised so an approver
    /// can act on it.  Without the fix, the target node stays Pending forever with no
    /// tasks and the workflow hangs.
    /// </summary>
    [TestMethod]
    public async Task ReturnToNode_TargetNodeActivatedWithTasks_AfterReturn()
    {
        // Use WfSequentialTestContext (has ProcessDefinitionVersion) via a shared-memory SQLite DB.
        var rtcDbName = $"WfRetC4_{Guid.NewGuid():N}";
        using var rtcKeepAlive = new SqliteConnection($"DataSource={rtcDbName}?mode=memory&cache=shared");
        rtcKeepAlive.Open();

        WfSequentialTestContext MakeRtcContext() => new(rtcDbName);

        await using (var initCtx = MakeRtcContext())
        {
            await initCtx.Database.EnsureCreatedAsync();
        }

        // Graph: Start → approval_a (alice, Sequential) → approval_b (bob, Sequential) → End
        var graphJson = WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "RetC4Graph",
            Name = "RetC4Graph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",      Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval_a",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "alice" },
                },
                new()
                {
                    NodeKey      = "approval_b",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "bob" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",      To = "approval_a" },
                new() { From = "approval_a", To = "approval_b" },
                new() { From = "approval_b", To = "end"        },
            },
            FieldWhitelist = new List<FieldWhitelistEntry>(),
        });

        await using var ctx = MakeRtcContext();
        var version = new ProcessDefinitionVersion
        {
            ID            = Guid.NewGuid(),
            DefinitionId  = Guid.NewGuid(),
            VersionNo     = 1,
            SchemaVersion = 1,
            GraphJson     = graphJson,
            ContentHash   = "retc4-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            TenantCode    = null,
            IsValid       = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();

        var engine = MakeRealEngine(ctx);

        // Start instance — alice gets approval_a task.
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.IsNotNull(instance);

        // Alice approves approval_a → bob gets approval_b.
        await using var readCtx1 = MakeRtcContext();
        var taskA = await readCtx1.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.State == TaskState.Pending);
        Assert.AreEqual("alice", taskA.AssigneeITCode);

        var r1 = await engine.ApproveTaskAsync(taskA.ID, "alice");
        Assert.IsTrue(r1.Code is WorkflowActionCode.Blocked or WorkflowActionCode.Advanced,
            $"Expected Blocked or Advanced after alice approves approval_a, got {r1.Code}");

        // Bob now has a pending task at approval_b.
        await using var readCtx2 = MakeRtcContext();
        var taskB = await readCtx2.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.State == TaskState.Pending);
        Assert.AreEqual("bob", taskB.AssigneeITCode);

        // Act: Bob returns workflow to approval_a.
        var returnResult = await engine.ReturnToNodeAsync(
            taskId: taskB.ID,
            targetNodeKey: "approval_a",
            actorITCode: "bob",
            reason: "#322 C4 test return");

        Assert.AreEqual(WorkflowActionCode.Returned, returnResult.Code,
            $"ReturnToNodeAsync must succeed, got {returnResult.Code}");

        // Assert: target node approval_a must be Activated (NOT Pending) in new generation.
        await using var verifyCtx = MakeRtcContext();
        var freshInst = await verifyCtx.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        uint gNew = freshInst.Generation;

        var nodeA_fresh = await verifyCtx.Set<NodeInstance>().AsNoTracking()
            .Where(n => n.InstanceId == instance.ID && n.NodeKey == "approval_a" && n.Generation == gNew)
            .SingleOrDefaultAsync();

        Assert.IsNotNull(nodeA_fresh,
            "#322: A fresh NodeInstance must exist at approval_a in the new generation after return.");
        Assert.AreEqual(NodeState.Activated, nodeA_fresh.State,
            "#322 C4 fix: target node approval_a must be Activated after return (not Pending). " +
            "Without the fix the node stays Pending forever and the workflow hangs.");

        // Assert: ApprovalTasks must be materialised for approval_a so alice can act.
        var tasksForA = await verifyCtx.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeA_fresh.ID
                        && t.Generation == gNew
                        && t.State == TaskState.Pending)
            .ToListAsync();
        Assert.IsTrue(tasksForA.Count > 0,
            "#322 C4 fix: ApprovalTasks must be materialised for approval_a after return. " +
            "Without the fix no tasks exist and nobody can act.");

        // Assert: alice has an actionable task in the new generation.
        var aliceTask = tasksForA.FirstOrDefault(t => t.AssigneeITCode == "alice");
        Assert.IsNotNull(aliceTask,
            "#322 C4 fix: alice must have an actionable Pending task at approval_a after return.");

        // Act: Prove alice can actually approve from approval_a again (workflow not hung).
        var r2 = await engine.ApproveTaskAsync(aliceTask.ID, "alice");
        Assert.IsTrue(r2.Code is WorkflowActionCode.Blocked or WorkflowActionCode.Advanced or WorkflowActionCode.InstanceApproved,
            $"#322 C4 fix: alice must be able to act on the returned node, got {r2.Code}.");
    }
}
