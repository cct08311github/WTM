#nullable enable
// Regression tests for #529 — InitiatorAutoApprove on the LEADING Sequential step
// permanently stranded the node.
//
// Bug: with approvers = [initiator, human2, human3] and InitiatorAutoApprove=true,
// SequentialApprovalHandler.OnEnterAsync minted step 0 as AutoApproved but only ever
// advanced SequencePointer past it when ALL tasks ended up AutoApproved. Because
// pointer stayed at 0 on a terminal AutoApproved task, no task was ever Pending, no
// timeout timer got armed (WorkflowEngine.cs ~line 624 hardcoded SequenceOrder==0),
// and the node blocked forever — the mid-chain #361 skip loop only runs AFTER a human
// approves the pointer task, which can never happen when the pointer itself is stuck
// on a leading AutoApproved slot.
//
// Fix: OnEnterAsync now advances SequencePointer past the leading CONTIGUOUS run of
// AutoApproved steps and promotes the first non-auto step to Pending; the engine's
// timer-arm site now re-reads the fresh SequencePointer instead of assuming 0.
//
// Tests:
//   T_529_01: pointer advances past step 0 → human2 has a Pending task, step 0 is AutoApproved.
//   T_529_02: normal approve sequence (human2 → human3) completes the node/instance.
//   T_529_03: timeout timer arms on human2's task (SequenceOrder==1), not step 0.
//   T_529_04: all-initiator chain still completes immediately (pointer reaches TotalRequired).
//
// All tests use SQLite shared-in-memory (never EF InMemory — spec §7.6 / #119 / #162).
// Reuses WfEngineTestContext / NodeKindDispatcher_Exposed / WorkflowEngine_Exposed
// (EngineTests.cs) and DefaultApproverResolverExposed (SequentialTests.cs), same
// pattern as TimerArmCancelTests.cs.

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

[TestClass]
public class Wf529LeadingAutoApproveTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"Wf529_{Guid.NewGuid():N}";
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

    private WfEngineTestContext MakeContext() => new(_dbName);

    private static WorkflowEngine MakeEngine(
        WfEngineTestContext ctx, WorkFlowOptions options, IBusinessCalendar? calendar = null)
    {
        var resolver = new DefaultApproverResolverExposed(options, ctx);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, options);
        return calendar is null
            ? WorkflowEngine_Exposed.CreateWithOptions(ctx, dispatcher, options, NullLogger.Instance)
            : WorkflowEngine_Exposed.CreateWithCalendar(ctx, dispatcher, options, calendar, NullLogger.Instance);
    }

    private async Task<ProcessDefinitionVersion> SeedVersionAsync(WfEngineTestContext ctx, string graphJson)
    {
        var version = new ProcessDefinitionVersion
        {
            ID            = Guid.NewGuid(),
            DefinitionId  = Guid.NewGuid(),
            VersionNo     = 1,
            SchemaVersion = 1,
            GraphJson     = graphJson,
            ContentHash   = "test-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            IsValid       = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }

    /// <summary>
    /// 3-approver Sequential graph where the INITIATOR is the LEADING (step 0) approver:
    /// initiator (auto) → human2 (real) → human3 (real). Optionally carries a Timeout.
    /// </summary>
    private static string LeadingAutoGraph(bool withTimeout = false) =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "Wf529LeadingAutoGraph",
            Name = "Wf529LeadingAutoGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "nodeA",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    // initiator (step 0, auto-approved) → human2 (step 1) → human3 (step 2)
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "initiator,human2,human3" },
                    Timeout      = withTimeout
                        ? new TimeoutDef { Duration = "PT1H", Action = TimerAction.Remind }
                        : null,
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "nodeA" },
                new() { From = "nodeA", To = "end"   },
            },
        });

    /// <summary>2-approver Sequential graph where BOTH approvers are the initiator (all-auto).</summary>
    private static string AllInitiatorGraph() =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "Wf529AllInitiatorGraph",
            Name = "Wf529AllInitiatorGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "nodeA",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
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
    /// T_529_01: leading InitiatorAutoApprove must not strand the node.
    ///
    /// approvers = [initiator, human2, human3], InitiatorAutoApprove=true.
    /// After StartAsync: step 0 must be AutoApproved, the SequencePointer must have
    /// advanced to 1 (NOT stuck at 0), and human2 must have a Pending task at step 1.
    /// Pre-fix: pointer stayed at 0, no task was ever Pending — the node was stranded.
    /// </summary>
    [TestMethod]
    public async Task T_529_01_LeadingAutoApprove_PointerAdvances_Human2Pending()
    {
        await using var ctx = MakeContext();
        var version = await SeedVersionAsync(ctx, LeadingAutoGraph());
        var opts = new WorkFlowOptions { InitiatorAutoApprove = true };
        var engine = MakeEngine(ctx, opts);

        var instance = await engine.StartAsync(version.ID, null, "initiator", null, ct: CancellationToken.None);
        Assert.AreEqual(InstanceState.Running, instance.State,
            "T_529_01: StartAsync must produce a Running instance (not stranded).");

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        // CORE ASSERTION: pointer must have advanced past the leading AutoApproved step 0.
        Assert.AreEqual(1, nodeInst.SequencePointer,
            "T_529_01: SequencePointer must advance to 1, past the leading AutoApproved step 0.");

        var step0Task = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);
        Assert.AreEqual(TaskState.AutoApproved, step0Task.State,
            "T_529_01: step 0 (initiator) must be AutoApproved.");

        // CORE ASSERTION: human2 must have a Pending task — the node is NOT stranded.
        var human2Task = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 1);
        Assert.AreEqual(TaskState.Pending, human2Task.State,
            "T_529_01: human2 (step 1) must be Pending after the leading auto-approved run. " +
            "A non-Pending step 1 here is exactly the stranded state #529 must prevent.");
        Assert.AreEqual("human2", human2Task.AssigneeITCode);

        var human3Task = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 2);
        Assert.AreEqual(TaskState.NotYetActive, human3Task.State,
            "T_529_01: human3 (step 2) must still be NotYetActive.");
    }

    /// <summary>
    /// T_529_02: normal approve sequence completes the node after a leading auto-approve.
    ///
    /// human2 approves step 1 → human3 must become Pending. human3 approves step 2 →
    /// instance must reach Approved. Proves the node recovers fully once #529 is fixed,
    /// not just that a Pending task exists.
    /// </summary>
    [TestMethod]
    public async Task T_529_02_LeadingAutoApprove_NormalApproveSequence_CompletesNode()
    {
        await using var ctx = MakeContext();
        var version = await SeedVersionAsync(ctx, LeadingAutoGraph());
        var opts = new WorkFlowOptions { InitiatorAutoApprove = true };
        var engine = MakeEngine(ctx, opts);

        var instance = await engine.StartAsync(version.ID, null, "initiator", null, ct: CancellationToken.None);

        await using var readCtx = MakeContext();
        var human2Task = await readCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.State == TaskState.Pending && t.AssigneeITCode == "human2");

        // human2 approves — human3 must now be Pending.
        var result1 = await engine.ApproveTaskAsync(human2Task.ID, "human2", null, ct: CancellationToken.None);
        Assert.IsTrue(
            result1.Code is WorkflowActionCode.Blocked
                or WorkflowActionCode.Advanced
                or WorkflowActionCode.InstanceApproved
                or WorkflowActionCode.AlreadyHandled,
            $"T_529_02: human2's approve must not return an error code. Got {result1.Code}.");

        await using var midCtx = MakeContext();
        var human3Task = await midCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.State == TaskState.Pending && t.AssigneeITCode == "human3");
        Assert.IsNotNull(human3Task,
            "T_529_02: human3 must have a Pending task after human2 approves.");

        var midInst = await midCtx.Set<ProcessInstance>().AsNoTracking().SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Running, midInst.State,
            "T_529_02: instance must still be Running — human3 hasn't approved yet.");

        // human3 approves — instance must reach Approved.
        var result2 = await engine.ApproveTaskAsync(human3Task!.ID, "human3", null, ct: CancellationToken.None);
        Assert.IsTrue(result2.IsSuccess || result2.Code == WorkflowActionCode.InstanceApproved,
            $"T_529_02: human3's approve must succeed. Got {result2.Code}.");

        await using var finalCtx = MakeContext();
        var finalInst = await finalCtx.Set<ProcessInstance>().AsNoTracking().SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State,
            "T_529_02: instance must reach Approved after human3's approval.");
    }

    /// <summary>
    /// T_529_03: the timeout timer must arm on human2's task (the first Pending step),
    /// not on step 0 (which is AutoApproved and never Pending).
    ///
    /// Pre-fix: WorkflowEngine's arm site hardcoded SequenceOrder==0, so even if the
    /// pointer had been advanced, no timer would ever be armed for a node whose first
    /// Pending step lives at SequenceOrder &gt; 0.
    /// </summary>
    [TestMethod]
    public async Task T_529_03_LeadingAutoApprove_TimeoutTimer_ArmsOnHuman2Task()
    {
        var calendar = new PassThroughBusinessCalendar();
        await using var ctx = MakeContext();
        var version = await SeedVersionAsync(ctx, LeadingAutoGraph(withTimeout: true));
        var opts = new WorkFlowOptions { InitiatorAutoApprove = true };
        var engine = MakeEngine(ctx, opts, calendar);

        var instance = await engine.StartAsync(version.ID, null, "initiator", null, ct: CancellationToken.None);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var verify = MakeContext();
        var armedTimers = await verify.Set<WorkflowTimer>()
            .AsNoTracking()
            .Where(t => t.Status == TimerStatus.Armed)
            .ToListAsync();

        Assert.AreEqual(1, armedTimers.Count,
            "T_529_03: exactly one Armed timer must exist after StartAsync.");

        var human2Task = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.AssigneeITCode == "human2");
        Assert.AreEqual(TaskState.Pending, human2Task.State);

        Assert.AreEqual(human2Task.ID, armedTimers[0].ApprovalTaskId,
            "T_529_03: the Armed timer must be scoped to human2's task (the first Pending step), " +
            "not step 0 (AutoApproved, never Pending).");
    }

    /// <summary>
    /// T_529_04: all-initiator chain (every step auto-approved) must still complete
    /// immediately — the leading-run pointer advance must not regress the pre-existing
    /// all-auto completion path (mirrors T_361_02 in Wf357358361TxTests.cs).
    /// </summary>
    [TestMethod]
    public async Task T_529_04_AllInitiatorChain_StillCompletesImmediately()
    {
        await using var ctx = MakeContext();
        var version = await SeedVersionAsync(ctx, AllInitiatorGraph());
        var opts = new WorkFlowOptions { InitiatorAutoApprove = true };
        var engine = MakeEngine(ctx, opts);

        var instance = await engine.StartAsync(version.ID, null, "initiator", null, ct: CancellationToken.None);

        await using var verify = MakeContext();
        var pendingCount = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .CountAsync(t => t.State == TaskState.Pending);
        Assert.AreEqual(0, pendingCount,
            "T_529_04: an all-auto chain must leave no human Pending task.");

        var finalInst = await verify.Set<ProcessInstance>().AsNoTracking().SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State,
            "T_529_04: an all-auto chain must reach Approved immediately after StartAsync.");
    }
}
