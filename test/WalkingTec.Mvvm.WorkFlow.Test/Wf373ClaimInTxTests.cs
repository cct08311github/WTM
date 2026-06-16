#nullable enable
// WF-373 regression tests — Sequential Claim-in-Transaction atomicity.
//
// Three tests:
//   1. Sequential_Approve_MidChain_Atomic   — approving step 0 advances pointer+task atomically
//   2. Sequential_Approve_LastStep_CallsAdvance — approving all steps reaches InstanceApproved
//   3. Sequential_Reject_Atomic             — reject step 0 cancels remaining tasks atomically
//
// All tests use SQLite shared-in-memory (never EF InMemory — spec §7.6 / #119 / #162).
// This file reuses WfSequentialTestContext and helpers defined in SequentialTests.cs
// (same namespace, same assembly).

using System;
using System.Collections.Generic;
using System.Linq;
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
public class Wf373ClaimInTxTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfClaimInTx373_{Guid.NewGuid():N}";
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
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
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

    // ── Test 1: Approving step 0 atomically advances pointer AND activates step-1 task ──

    /// <summary>
    /// WF-373 claim-in-tx atomic guard: approving step 0 of a 3-step Sequential node MUST
    /// atomically (a) advance SequencePointer from 0 to 1, AND (b) activate the step-1 task
    /// to Pending in the same transaction. Result MUST be WorkflowActionCode.Advanced.
    /// </summary>
    [TestMethod]
    public async Task Sequential_Approve_MidChain_Atomic()
    {
        const string A1 = "alice_wf373";
        const string A2 = "bob_wf373";
        const string A3 = "carol_wf373";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, ThreeApproversGraph(A1, A2, A3));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        // Read initial state.
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

        // Approve step 0 — must return Advanced (steps remain).
        var r0 = await engine.ApproveTaskAsync(task0.ID, A1, "step 0 ok");
        Assert.AreEqual(WorkflowActionCode.Advanced, r0.Code,
            $"Step 0 approve must return Advanced (steps remain), got {r0.Code}.");

        // ATOMIC ASSERTION: both pointer and step-1 task must have advanced in the same tx.
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
    }

    // ── Test 2: Approving all 3 steps reaches InstanceApproved ──────────────────────────

    /// <summary>
    /// Full happy-path: approve all 3 steps in sequence (A1 → A2 → A3).
    /// Final result MUST be WorkflowActionCode.InstanceApproved and
    /// ProcessInstance.State MUST be InstanceState.Approved.
    /// </summary>
    [TestMethod]
    public async Task Sequential_Approve_LastStep_CallsAdvance()
    {
        const string A1 = "alice_wf373";
        const string A2 = "bob_wf373";
        const string A3 = "carol_wf373";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, ThreeApproversGraph(A1, A2, A3));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        // Load step 0.
        await using var read0 = MakeContext();
        var nodeInst = await read0.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var task0 = await read0.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);

        // Step 0 → Advanced.
        var r0 = await engine.ApproveTaskAsync(task0.ID, A1, "step 0 ok");
        Assert.AreEqual(WorkflowActionCode.Advanced, r0.Code,
            $"Step 0 must return Advanced, got {r0.Code}.");

        // Load step 1.
        await using var read1 = MakeContext();
        var task1 = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 1);
        Assert.AreEqual(TaskState.Pending, task1.State);

        // Step 1 → Advanced.
        var r1 = await engine.ApproveTaskAsync(task1.ID, A2, "step 1 ok");
        Assert.AreEqual(WorkflowActionCode.Advanced, r1.Code,
            $"Step 1 must return Advanced, got {r1.Code}.");

        // Load step 2.
        await using var read2 = MakeContext();
        var task2 = await read2.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 2);
        Assert.AreEqual(TaskState.Pending, task2.State);

        // Step 2 (last) → InstanceApproved.
        var r2 = await engine.ApproveTaskAsync(task2.ID, A3, "step 2 ok");
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, r2.Code,
            $"Last step must return InstanceApproved, got {r2.Code}.");

        // ProcessInstance state must be Approved.
        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State,
            "ProcessInstance.State must be Approved after all steps approved.");
    }

    // ── Test 3: Reject step 0 atomically cancels remaining tasks ─────────────────────────

    /// <summary>
    /// WF-373 reject atomicity: rejecting step 0 MUST atomically:
    /// (a) mark step-0 task as Rejected,
    /// (b) cancel all remaining tasks (order 1 and 2),
    /// (c) mark the NodeInstance as CompletedRejected,
    /// (d) mark the ProcessInstance as Rejected.
    /// Result MUST be WorkflowActionCode.Rejected.
    /// </summary>
    [TestMethod]
    public async Task Sequential_Reject_Atomic()
    {
        const string A1 = "alice_rej373";
        const string A2 = "bob_rej373";
        const string A3 = "carol_rej373";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, ThreeApproversGraph(A1, A2, A3));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        // Load step 0.
        await using var read0 = MakeContext();
        var nodeInst = await read0.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var task0 = await read0.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);
        Assert.AreEqual(TaskState.Pending, task0.State);

        // Reject step 0.
        var result = await engine.RejectTaskAsync(task0.ID, A1, "rejected by test");
        Assert.AreEqual(WorkflowActionCode.Rejected, result.Code,
            $"Reject must return WorkflowActionCode.Rejected, got {result.Code}.");

        // ATOMIC ASSERTIONS: all state changes must have committed together.
        await using var readFinal = MakeContext();

        // Step-0 task must be Rejected.
        var task0After = await readFinal.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == task0.ID);
        Assert.AreEqual(TaskState.Rejected, task0After.State,
            "Step-0 task must be Rejected.");
        Assert.IsNotNull(task0After.ActedAtUtc,
            "Step-0 task ActedAtUtc must be set after rejection.");
        Assert.AreEqual("rejected by test", task0After.Comment,
            "Step-0 task Comment must match the rejection reason.");

        // Remaining tasks (order 1 and 2) must be Cancelled.
        var remainingTasks = await readFinal.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder > 0)
            .OrderBy(t => t.SequenceOrder)
            .ToListAsync();
        Assert.AreEqual(2, remainingTasks.Count,
            "There must be exactly 2 remaining tasks (order 1 and 2).");
        foreach (var t in remainingTasks)
        {
            Assert.AreEqual(TaskState.Cancelled, t.State,
                $"Task at SequenceOrder={t.SequenceOrder} must be Cancelled after rejection.");
        }

        // NodeInstance state must be CompletedRejected.
        var nodeInstAfter = await readFinal.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);
        Assert.AreEqual(NodeState.CompletedRejected, nodeInstAfter.State,
            "NodeInstance.State must be CompletedRejected after rejection.");

        // ProcessInstance state must be Rejected.
        var finalInst = await readFinal.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Rejected, finalInst.State,
            "ProcessInstance.State must be Rejected after step-0 rejection.");
    }

    // ── Static engine factory for WfAbbaTestContext (fault-injection tests) ──────

    private static WorkflowEngine MakeEngineForAbba(WfAbbaTestContext ctx, WorkFlowOptions? opts = null)
    {
        var options    = opts ?? new WorkFlowOptions();
        var resolver   = new DefaultApproverResolverExposed(options, ctx);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, options);
        return WorkflowEngine_Exposed.CreateWithOptions(ctx, dispatcher, options, NullLogger.Instance);
    }

    // ── Seed helper: 2-step Sequential instance via WfAbbaTestContext ────────────

    private static async Task<(ProcessInstance inst, ApprovalTask task0, ApprovalTask task1, NodeInstance nodeInst)>
        SeedTwoApproverAbbaAsync(string dbName)
    {
        var graphJson = WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "TwoApproverSeqGraph",
            Name = "TwoApproverSeqGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",     Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "alice,bob" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
            FieldWhitelist = new List<FieldWhitelistEntry>(),
        });

        await using var ctx = new WfAbbaTestContext(dbName);
        var ver = new ProcessDefinitionVersion
        {
            ID          = Guid.NewGuid(),
            GraphJson   = graphJson,
            ContentHash = $"crash373-{Guid.NewGuid():N}",
            VersionNo   = 1,
            TenantCode  = "T1",
            IsValid     = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(ver);
        await ctx.SaveChangesAsync();

        var engine = MakeEngineForAbba(ctx);
        var inst   = await engine.StartAsync(ver.ID, null, "initiator", "T1");

        await using var readCtx = new WfAbbaTestContext(dbName);
        var nodeInst = await readCtx.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == inst.ID && n.NodeKind == NodeKind.Approval);
        var task0 = await readCtx.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);
        var task1 = await readCtx.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 1);

        return (inst, task0, task1, nodeInst);
    }

    // ── Test 1: Approve mid-chain — claim crash → full rollback ──────────────────

    /// <summary>
    /// WF-373 fault injection: mid-chain approve crash (step 0 of 2).
    /// The interceptor throws on the 1st UPDATE to Wf_ApprovalTask (= the claim CAS).
    /// Asserts: alice's task stays Pending (rollback), pointer unchanged.
    /// Re-drive with clean engine must succeed (Advanced, pointer=1).
    /// </summary>
    [TestMethod]
    public async Task T_WF373_01_Approve_MidChain_ClaimCrash_FullRollback()
    {
        var dbName = $"WfCrash373_01_{Guid.NewGuid():N}";
        using var kl = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        kl.Open();
        using (var schemaCtx = new WfAbbaTestContext(dbName))
            schemaCtx.Database.EnsureCreated();

        var (inst, task0, task1, nodeInst) = await SeedTwoApproverAbbaAsync(dbName);

        // Arm the interceptor: throw on the 1st UPDATE to Wf_ApprovalTask (claim CAS).
        var interceptor = new MidTxFaultInterceptor();
        interceptor.Arm("Wf_ApprovalTask", 1);

        await using var faultCtx = new WfAbbaTestContext(dbName, interceptor);
        var faultEngine = MakeEngineForAbba(faultCtx);

        // The engine must throw because the interceptor fires mid-transaction.
        bool threw = false;
        try { await faultEngine.ApproveTaskAsync(task0.ID, "alice"); }
        catch (Exception ex) when (ex is not AssertFailedException) { threw = true; }

        Assert.IsTrue(threw, "T_WF373_01: ApproveTaskAsync must throw when fault interceptor fires.");
        Assert.IsTrue(interceptor.FaultFired, "T_WF373_01: FaultFired must be true after injected throw.");

        // Verify rollback: alice's task must still be Pending.
        await using var verify1 = new WfAbbaTestContext(dbName);
        var aliceTask = await verify1.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.ID == task0.ID);
        Assert.AreEqual(TaskState.Pending, aliceTask.State,
            "T_WF373_01: alice's task must remain Pending after rollback.");

        // Verify pointer unchanged.
        var nodeAfter = await verify1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);
        Assert.AreEqual(0, nodeAfter.SequencePointer,
            "T_WF373_01: SequencePointer must remain 0 after rollback.");

        // bob's task must still be NotYetActive.
        var bobTask = await verify1.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.ID == task1.ID);
        Assert.AreEqual(TaskState.NotYetActive, bobTask.State,
            "T_WF373_01: bob's task must still be NotYetActive after rollback.");

        // Re-drive with a clean engine — must succeed.
        await using var cleanCtx = new WfAbbaTestContext(dbName);
        var cleanEngine = MakeEngineForAbba(cleanCtx);
        var result = await cleanEngine.ApproveTaskAsync(task0.ID, "alice");
        Assert.IsTrue(
            result.Code is WorkflowActionCode.Advanced
                       or WorkflowActionCode.InstanceApproved
                       or WorkflowActionCode.NodeCompleted
                       or WorkflowActionCode.Blocked,
            $"T_WF373_01: Re-drive must succeed (Advanced/InstanceApproved/NodeCompleted/Blocked). Got {result.Code}.");

        // alice's task must now be Approved.
        await using var verify2 = new WfAbbaTestContext(dbName);
        var aliceTaskFinal = await verify2.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.ID == task0.ID);
        Assert.AreEqual(TaskState.Approved, aliceTaskFinal.State,
            "T_WF373_01: alice's task must be Approved after successful re-drive.");

        // Pointer must have advanced to 1.
        var nodeFinal = await verify2.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);
        Assert.AreEqual(1, nodeFinal.SequencePointer,
            "T_WF373_01: SequencePointer must be 1 after successful re-drive.");
    }

    // ── Test 2: Approve last step — claim crash → rollback ───────────────────────

    /// <summary>
    /// WF-373 fault injection: last-step approve crash (step 1 of 2).
    /// First approve alice cleanly (step 0 → Advanced). Then arm fault for bob's
    /// approval (step 1). After crash, bob's task stays Pending. Re-drive succeeds
    /// with InstanceApproved.
    /// </summary>
    [TestMethod]
    public async Task T_WF373_02_Approve_LastStep_ClaimCrash_Rollback()
    {
        var dbName = $"WfCrash373_02_{Guid.NewGuid():N}";
        using var kl = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        kl.Open();
        using (var schemaCtx = new WfAbbaTestContext(dbName))
            schemaCtx.Database.EnsureCreated();

        var (inst, task0, task1, nodeInst) = await SeedTwoApproverAbbaAsync(dbName);

        // Step 1: approve alice cleanly (step 0 → Advanced).
        await using var cleanCtx1 = new WfAbbaTestContext(dbName);
        var cleanEngine1 = MakeEngineForAbba(cleanCtx1);
        var r0 = await cleanEngine1.ApproveTaskAsync(task0.ID, "alice");
        Assert.IsTrue(
            r0.Code is WorkflowActionCode.Advanced
                    or WorkflowActionCode.Blocked
                    or WorkflowActionCode.NodeCompleted,
            $"T_WF373_02 setup: alice's step 0 approve must return Advanced/Blocked/NodeCompleted. Got {r0.Code}.");

        // Re-read bob's task (it should now be Pending after alice's approval advanced the pointer).
        await using var read1 = new WfAbbaTestContext(dbName);
        var bobTask = await read1.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.ID == task1.ID);
        Assert.AreEqual(TaskState.Pending, bobTask.State,
            "T_WF373_02 setup: bob's task must be Pending after alice approved step 0.");

        // Arm fault: throw on the 1st UPDATE to Wf_ApprovalTask (bob's claim CAS).
        var interceptor = new MidTxFaultInterceptor();
        interceptor.Arm("Wf_ApprovalTask", 1);

        await using var faultCtx = new WfAbbaTestContext(dbName, interceptor);
        var faultEngine = MakeEngineForAbba(faultCtx);

        bool threw = false;
        try { await faultEngine.ApproveTaskAsync(bobTask.ID, "bob"); }
        catch (Exception ex) when (ex is not AssertFailedException) { threw = true; }

        Assert.IsTrue(threw, "T_WF373_02: ApproveTaskAsync must throw when fault fires.");
        Assert.IsTrue(interceptor.FaultFired, "T_WF373_02: FaultFired must be true.");

        // Bob's task must still be Pending (rollback).
        await using var verify1 = new WfAbbaTestContext(dbName);
        var bobAfterFault = await verify1.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.ID == bobTask.ID);
        Assert.AreEqual(TaskState.Pending, bobAfterFault.State,
            "T_WF373_02: bob's task must remain Pending after fault rollback.");

        // Re-drive with clean engine — bob approves last step → InstanceApproved.
        await using var cleanCtx2 = new WfAbbaTestContext(dbName);
        var cleanEngine2 = MakeEngineForAbba(cleanCtx2);
        var r1 = await cleanEngine2.ApproveTaskAsync(bobTask.ID, "bob");
        Assert.IsTrue(
            r1.Code is WorkflowActionCode.InstanceApproved
                    or WorkflowActionCode.Advanced
                    or WorkflowActionCode.NodeCompleted,
            $"T_WF373_02: Re-drive must return InstanceApproved/Advanced/NodeCompleted. Got {r1.Code}.");

        // Instance must be Approved.
        await using var verify2 = new WfAbbaTestContext(dbName);
        var finalInst = await verify2.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == inst.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State,
            "T_WF373_02: instance must be Approved after successful re-drive.");
    }

    // ── Test 3: Reject — claim crash → full rollback ──────────────────────────────

    /// <summary>
    /// WF-373 fault injection: reject crash (step 0 of 2).
    /// Interceptor throws on the 1st UPDATE to Wf_ApprovalTask.
    /// Asserts full rollback: alice's task Pending, nodeInst Activated, bob's task
    /// NotYetActive, instance Running. Re-drive with clean engine must return Rejected.
    /// </summary>
    [TestMethod]
    public async Task T_WF373_03_Reject_ClaimCrash_FullRollback()
    {
        var dbName = $"WfCrash373_03_{Guid.NewGuid():N}";
        using var kl = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        kl.Open();
        using (var schemaCtx = new WfAbbaTestContext(dbName))
            schemaCtx.Database.EnsureCreated();

        var (inst, task0, task1, nodeInst) = await SeedTwoApproverAbbaAsync(dbName);

        // Arm fault: throw on the 1st UPDATE to Wf_ApprovalTask.
        var interceptor = new MidTxFaultInterceptor();
        interceptor.Arm("Wf_ApprovalTask", 1);

        await using var faultCtx = new WfAbbaTestContext(dbName, interceptor);
        var faultEngine = MakeEngineForAbba(faultCtx);

        bool threw = false;
        try { await faultEngine.RejectTaskAsync(task0.ID, "alice", "fault test"); }
        catch (Exception ex) when (ex is not AssertFailedException) { threw = true; }

        Assert.IsTrue(threw, "T_WF373_03: RejectTaskAsync must throw when fault fires.");
        Assert.IsTrue(interceptor.FaultFired, "T_WF373_03: FaultFired must be true.");

        // Verify full rollback.
        await using var verify1 = new WfAbbaTestContext(dbName);

        var aliceTask = await verify1.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.ID == task0.ID);
        Assert.AreEqual(TaskState.Pending, aliceTask.State,
            "T_WF373_03: alice's task must remain Pending after rollback.");

        var nodeAfterFault = await verify1.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);
        Assert.AreEqual(NodeState.Activated, nodeAfterFault.State,
            "T_WF373_03: nodeInst must remain Activated after rollback.");

        var bobTask = await verify1.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.ID == task1.ID);
        Assert.AreEqual(TaskState.NotYetActive, bobTask.State,
            "T_WF373_03: bob's task must remain NotYetActive (not Cancelled) after rollback.");

        var instAfterFault = await verify1.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == inst.ID);
        Assert.AreEqual(InstanceState.Running, instAfterFault.State,
            "T_WF373_03: instance must remain Running after rollback.");

        // Re-drive: alice rejects cleanly.
        await using var cleanCtx = new WfAbbaTestContext(dbName);
        var cleanEngine = MakeEngineForAbba(cleanCtx);
        var result = await cleanEngine.RejectTaskAsync(task0.ID, "alice", "actual reject");
        Assert.AreEqual(WorkflowActionCode.Rejected, result.Code,
            $"T_WF373_03: Re-drive reject must return Rejected. Got {result.Code}.");

        await using var verify2 = new WfAbbaTestContext(dbName);
        var finalInst = await verify2.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == inst.ID);
        Assert.AreEqual(InstanceState.Rejected, finalInst.State,
            "T_WF373_03: instance must be Rejected after successful re-drive.");
    }

    // ── Test 4: Lock order — ApprovalTask write before NodeInstance write ─────────

    /// <summary>
    /// WF-373 lock-order structural assertion: during mid-chain Sequential approve,
    /// the first write to Wf_ApprovalTask must precede any write to Wf_NodeInstance.
    /// Uses TableOrderInterceptor (already defined in AbbaFixTests.cs).
    /// </summary>
    [TestMethod]
    public async Task T_WF373_04_LockOrder_TaskBeforeNode()
    {
        var dbName = $"WfCrash373_04_{Guid.NewGuid():N}";
        using var kl = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        kl.Open();
        using (var schemaCtx = new WfAbbaTestContext(dbName))
            schemaCtx.Database.EnsureCreated();

        var (inst, task0, task1, nodeInst) = await SeedTwoApproverAbbaAsync(dbName);

        // Use TableOrderInterceptor to record write order.
        var interceptor = new TableOrderInterceptor();
        interceptor.Clear();

        await using var orderedCtx = new WfAbbaTestContext(dbName, interceptor);
        var engine = MakeEngineForAbba(orderedCtx);

        var result = await engine.ApproveTaskAsync(task0.ID, "alice");
        Assert.IsTrue(
            result.Code is WorkflowActionCode.Advanced
                       or WorkflowActionCode.InstanceApproved
                       or WorkflowActionCode.NodeCompleted
                       or WorkflowActionCode.Blocked,
            $"T_WF373_04: ApproveTaskAsync must succeed. Got {result.Code}.");

        var writes = interceptor.TableWrites;

        int firstTaskWriteIdx = writes
            .Select((t, i) => (table: t, idx: i))
            .Where(x => x.table.IndexOf("ApprovalTask", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(x => (int?)x.idx)
            .FirstOrDefault() ?? -1;

        Assert.IsTrue(firstTaskWriteIdx >= 0,
            $"T_WF373_04: at least one ApprovalTask write must be recorded. Writes: [{string.Join(", ", writes)}]");

        int? firstNodeWriteIdx = writes
            .Select((t, i) => (table: t, idx: i))
            .Where(x => x.table.IndexOf("NodeInstance", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(x => (int?)x.idx)
            .FirstOrDefault();

        if (firstNodeWriteIdx.HasValue)
        {
            Assert.IsTrue(firstTaskWriteIdx < firstNodeWriteIdx.Value,
                $"T_WF373_04: First ApprovalTask write (index {firstTaskWriteIdx}) must precede " +
                $"first NodeInstance write (index {firstNodeWriteIdx.Value}). " +
                $"Write order: [{string.Join(", ", writes)}]");
        }
        // If there are no NodeInstance writes (e.g. the engine inlined the advance without
        // a separate NodeInstance UPDATE), the constraint is trivially satisfied.
    }

    // ── Test 5: CAS loser returns AlreadyHandled ──────────────────────────────────

    /// <summary>
    /// WF-373 CAS-loser: pre-mutate alice's task to Approved in DB before calling
    /// ApproveTaskAsync. The engine's re-read Pending check (CAS guard) must detect
    /// the task is already handled and return AlreadyHandled — no partial state.
    /// </summary>
    [TestMethod]
    public async Task T_WF373_05_CasLoser_ReturnsAlreadyHandled()
    {
        var dbName = $"WfCrash373_05_{Guid.NewGuid():N}";
        using var kl = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        kl.Open();
        using (var schemaCtx = new WfAbbaTestContext(dbName))
            schemaCtx.Database.EnsureCreated();

        var (inst, task0, task1, nodeInst) = await SeedTwoApproverAbbaAsync(dbName);

        // Pre-mutate alice's task to Approved, simulating a concurrent actor that already claimed.
        await using var mutCtx = new WfAbbaTestContext(dbName);
        await mutCtx.Set<ApprovalTask>()
            .Where(t => t.ID == task0.ID)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, TaskState.Approved)
                .SetProperty(x => x.ActedAtUtc, DateTime.UtcNow));

        // Call ApproveTaskAsync with a fresh engine — must detect already-handled and return AlreadyHandled.
        await using var cleanCtx = new WfAbbaTestContext(dbName);
        var engine = MakeEngineForAbba(cleanCtx);
        var result = await engine.ApproveTaskAsync(task0.ID, "alice");

        Assert.AreEqual(WorkflowActionCode.AlreadyHandled, result.Code,
            $"T_WF373_05: ApproveTaskAsync on an already-Approved task must return AlreadyHandled. Got {result.Code}.");
    }
}

/// <summary>
/// Intercepts non-query DB commands and throws <see cref="InvalidOperationException"/>
/// after the Nth UPDATE touching the target table.
/// Used by WF-373 fault-injection tests to simulate mid-transaction crashes.
/// </summary>
internal sealed class MidTxFaultInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    private string? _targetTable;
    private int _throwAfterNth;
    // _hitCount is accessed via Interlocked.Increment which provides the full memory fence;
    // volatile is not required for correctness but is omitted here intentionally for symmetry
    // clarity — Interlocked operations are sufficient on all .NET memory models.
    private int _hitCount;
    private volatile bool _faultFired;

    public bool FaultFired => _faultFired;

    /// <summary>Arm the interceptor before the operation under test.</summary>
    public void Arm(string targetTable, int throwAfterNthUpdate)
    {
        _targetTable    = targetTable;
        _throwAfterNth  = throwAfterNthUpdate;
        _hitCount       = 0;
        _faultFired     = false;
    }

    public override System.Threading.Tasks.ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>>
        NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            System.Threading.CancellationToken cancellationToken = default)
    {
        MaybeThrow(command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> NonQueryExecuting(
        System.Data.Common.DbCommand command,
        Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
    {
        MaybeThrow(command.CommandText);
        return base.NonQueryExecuting(command, eventData, result);
    }

    private void MaybeThrow(string sql)
    {
        if (_targetTable is null || _faultFired) return;

        bool isUpdate = sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase);
        bool touchesTarget = sql.IndexOf(_targetTable, StringComparison.OrdinalIgnoreCase) >= 0;

        if (!isUpdate || !touchesTarget) return;

        var n = System.Threading.Interlocked.Increment(ref _hitCount);
        if (n == _throwAfterNth)
        {
            _faultFired = true;
            throw new InvalidOperationException(
                $"WF-373 injected fault: throw after {_throwAfterNth}th UPDATE on {_targetTable}.");
        }
    }
}
