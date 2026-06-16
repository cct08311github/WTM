#nullable enable
// WF-320 PR D: ReturnToInitiatorAsync atomicity tests.
//   T7-1: Happy path — node Returned + instance Draft + event logged + siblings Cancelled.
//   T7-2: Concurrent calls — no partial state (node Returned without instance still Running).
//
// All tests use SQLite shared-in-memory (NEVER EF InMemory — spec §7.6).

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
public class Wf320ReturnToInitiatorTxTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"Wf320Tx_{Guid.NewGuid():N}";
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

    private (IWorkflowEngine engine, WfEngineTestContext ctx) MakeEngine(WorkFlowOptions? options = null)
    {
        var ctx = MakeContext();
        var resolver = new StaticApproverResolver();
        var opts = options ?? new WorkFlowOptions();
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
        var engine = WorkflowEngine_Exposed.CreateWithOptions(ctx, dispatcher, opts, NullLogger.Instance);
        return (engine, ctx);
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
            ContentHash   = "wf320-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            TenantCode    = null,
            IsValid       = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }

    // Graph: Start → Approval (approver="approver1") → End
    private static string ApprovalGraph(string approver = "approver1") =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "ApprovalGraph",
            Name = "ApprovalGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = approver },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end" },
            },
        });

    // ── T7-1: Happy path ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ReturnToInitiatorAsync_HappyPath_NodeReturned_InstanceDraft_EventLogged()
    {
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, ApprovalGraph("approver1"));
        var instance = await engine.StartAsync(version.ID, null, "initiator1", null);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running before ReturnToInitiator.");

        // Find the pending task.
        var task = await ctx.Set<ApprovalTask>()
            .AsNoTracking()
            .FirstAsync(t => t.State == TaskState.Pending
                              && ctx.Set<NodeInstance>()
                                    .Where(n => n.InstanceId == instance.ID)
                                    .Select(n => n.ID)
                                    .Contains(t.NodeInstanceId));

        var result = await engine.ReturnToInitiatorAsync(task.ID, "approver1", reason: "needs revision");
        Assert.AreEqual(WorkflowActionCode.ReturnedToInitiator, result.Code,
            $"Expected ReturnedToInitiator, got {result.Code}: {result.Detail}");

        // Verify state in DB using a fresh context.
        await using var verify = MakeContext();

        // Instance must be Draft.
        var freshInst = await verify.Set<ProcessInstance>().SingleAsync(x => x.ID == instance.ID);
        Assert.AreEqual(InstanceState.Draft, freshInst.State,
            "Instance must be Draft after ReturnToInitiator.");

        // Approval node must be Returned.
        var approvalNode = await verify.Set<NodeInstance>()
            .Where(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval)
            .FirstOrDefaultAsync();
        Assert.IsNotNull(approvalNode, "Approval NodeInstance must exist.");
        Assert.AreEqual(NodeState.Returned, approvalNode.State,
            $"Approval node must be Returned, got {approvalNode.State}.");

        // All sibling tasks must be Cancelled (the trigger task was set to Rejected during claim).
        var allTasks = await verify.Set<ApprovalTask>()
            .Where(t => t.NodeInstanceId == approvalNode.ID && t.ID != task.ID)
            .ToListAsync();
        foreach (var sibling in allTasks)
        {
            Assert.AreEqual(TaskState.Cancelled, sibling.State,
                $"Sibling task {sibling.ID} must be Cancelled after ReturnToInitiator.");
        }

        // WorkflowEventLog must have exactly one Return event.
        var logs = await verify.Set<WorkflowEventLog>()
            .Where(e => e.InstanceId == instance.ID && e.Action == EventAction.Return)
            .ToListAsync();
        Assert.AreEqual(1, logs.Count,
            $"Expected exactly 1 Return event log, got {logs.Count}.");

        var returnLog = logs[0];
        Assert.AreEqual("approver1", returnLog.ActorITCode,
            "Return event ActorITCode must be 'approver1'.");
        Assert.AreEqual(InstanceState.Running.ToString(), returnLog.BeforeState,
            "Return event BeforeState must be 'Running'.");
        Assert.AreEqual(InstanceState.Draft.ToString(), returnLog.AfterState,
            "Return event AfterState must be 'Draft'.");

        // Seq must be monotonic (positive value).
        Assert.IsTrue(returnLog.Seq >= 1, $"Return event Seq must be ≥1, got {returnLog.Seq}.");
    }

    // ── T7-2: Concurrent calls — no partial state ─────────────────────────────

    [TestMethod]
    public async Task ReturnToInitiatorAsync_ConcurrentCalls_NoPartialState()
    {
        // SQLite is single-writer so true concurrency is serialized.
        // We verify that after two concurrent calls, no node-Returned + instance-Running split exists.
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, ApprovalGraph("approver1"));
        var instance = await engine.StartAsync(version.ID, null, "initiator1", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        var task = await ctx.Set<ApprovalTask>()
            .AsNoTracking()
            .FirstAsync(t => t.State == TaskState.Pending
                              && ctx.Set<NodeInstance>()
                                    .Where(n => n.InstanceId == instance.ID)
                                    .Select(n => n.ID)
                                    .Contains(t.NodeInstanceId));

        // Fire two concurrent ReturnToInitiatorAsync on the SAME task.
        // One must succeed (ReturnedToInitiator), the other must get AlreadyHandled.
        var t1 = engine.ReturnToInitiatorAsync(task.ID, "approver1", reason: "r1");
        var t2 = engine.ReturnToInitiatorAsync(task.ID, "approver1", reason: "r2");
        var results = await Task.WhenAll(t1, t2);

        var codes = results.Select(r => r.Code).ToList();

        // At least one must have succeeded.
        Assert.IsTrue(codes.Contains(WorkflowActionCode.ReturnedToInitiator),
            $"At least one concurrent call must succeed with ReturnedToInitiator. Got: {string.Join(", ", codes)}");

        // Re-read DB — verify no partial state.
        await using var verify = MakeContext();

        var freshInst = await verify.Set<ProcessInstance>().SingleAsync(x => x.ID == instance.ID);
        var approvalNode = await verify.Set<NodeInstance>()
            .Where(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval)
            .FirstAsync();

        // ATOMICITY INVARIANT: if node is Returned, instance must NOT be Running.
        if (approvalNode.State == NodeState.Returned)
        {
            Assert.AreNotEqual(InstanceState.Running, freshInst.State,
                "A Returned node must never coexist with a Running instance (atomicity violation).");
            Assert.AreEqual(InstanceState.Draft, freshInst.State,
                "When node is Returned, instance must be Draft.");
        }

        // Additional: if instance is Draft, node must be Returned.
        if (freshInst.State == InstanceState.Draft)
        {
            Assert.AreEqual(NodeState.Returned, approvalNode.State,
                "When instance is Draft (from ReturnToInitiator), approval node must be Returned.");
        }
    }
}
