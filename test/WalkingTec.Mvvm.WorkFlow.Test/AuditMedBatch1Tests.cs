#nullable enable
// AuditMedBatch1Tests.cs — Regression tests for audit medium-severity batch 1 fixes.
//
// C16: ApprovePercent HasPrecision(5,4) — verifies EF model metadata via ApplyWorkFlowModels.
// C11: Sequential reject cancels AddedPending tasks (加签-injected rows).
// C10: Any-mode approval completion sets DecidedBy on NodeInstance.
//
// DB: SQLite shared-in-memory (WfSequentialTestContext from SequentialTests.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ── Minimal DbContext that applies the production WorkFlow EF model ───────────

/// <summary>
/// A minimal SQLite DbContext that calls <see cref="WorkFlowDbContextExtensions.ApplyWorkFlowModels"/>
/// so C16 tests can verify the EF model metadata declared by the production mapping.
/// </summary>
internal sealed class WfProductionModelContext : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder b) =>
        b.UseSqlite("DataSource=:memory:");

    protected override void OnModelCreating(ModelBuilder m) =>
        m.ApplyWorkFlowModels();
}

// ── Tests ─────────────────────────────────────────────────────────────────────

[TestClass]
public class AuditMedBatch1Tests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfAuditMedBatch1_{Guid.NewGuid():N}";
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

    // ── C16: ApprovePercent HasPrecision(5,4) ─────────────────────────────────

    /// <summary>
    /// C16 fix: <see cref="WorkFlowDbContextExtensions.ApplyWorkFlowModels"/> must declare
    /// ApprovePercent with precision (5,4) so ratio quorum thresholds like 0.6667 are not
    /// silently truncated under decimal(18,2) on SqlServer/MySQL/Oracle.
    /// Verified by inspecting the compiled EF model metadata (no DB round-trip needed).
    /// </summary>
    [TestMethod]
    public void C16_ApprovePercent_HasPrecision_5_4()
    {
        using var ctx = new WfProductionModelContext();

        var entityType = ctx.Model.FindEntityType(typeof(NodeInstance));
        Assert.IsNotNull(entityType,
            "NodeInstance must be registered in the production EF model.");

        var prop = entityType.FindProperty("ApprovePercent");
        Assert.IsNotNull(prop,
            "ApprovePercent property must be found on NodeInstance.");

        int? precision = prop.GetPrecision();
        int? scale     = prop.GetScale();

        Assert.AreEqual(5, precision,
            $"ApprovePercent precision must be 5 (C16 fix), got {precision}.");
        Assert.AreEqual(4, scale,
            $"ApprovePercent scale must be 4 (C16 fix), got {scale}.");
    }

    // ── C11: Sequential reject cancels AddedPending tasks ──────────────────────

    /// <summary>
    /// C11 fix: when a Sequential node is rejected, AddedPending tasks (加签-injected
    /// tasks not yet reached by the SequencePointer) must be Cancelled along with
    /// NotYetActive tasks.  Before the fix they were left as dangling un-actionable rows.
    /// </summary>
    [TestMethod]
    public async Task C11_Sequential_Reject_Cancels_AddedPending_Tasks()
    {
        const string A1 = "alice";
        const string A2 = "bob";
        const string A3 = "carol_added_pending";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        // Start a 2-approver Sequential workflow.
        var version  = await SeedVersionAsync(ctx, SeqTestGraphs.TwoApprovers(A1, A2));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running after StartAsync.");

        // Read the NodeInstance and the existing tasks.
        await using var readCtx = MakeContext();
        var nodeInst = await readCtx.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(NodeState.Activated, nodeInst.State);

        // The engine creates NotYetActive for A2 (second step). Verify A1 is Pending.
        var taskA1 = await readCtx.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.AssigneeITCode == A1);
        Assert.AreEqual(TaskState.Pending, taskA1.State, "A1 must be Pending (first step).");

        // Directly insert an AddedPending task for A3 (simulates 加签 injection at slot 2+).
        // Use a separate context to avoid tracking conflicts.
        await using var insertCtx = MakeContext();
        insertCtx.Set<ApprovalTask>().Add(new ApprovalTask
        {
            ID              = Guid.NewGuid(),
            NodeInstanceId  = nodeInst.ID,
            AssigneeITCode  = A3,
            SequenceOrder   = 99,    // beyond current chain
            State           = TaskState.AddedPending,
            RowVer          = 0,
            IsValid         = true,
            TenantCode      = null,
        });
        await insertCtx.SaveChangesAsync();

        // Reject A1's task → Sequential node must cancel all remaining tasks.
        var rejectResult = await engine.RejectTaskAsync(taskA1.ID, A1, "test reject");
        Assert.AreEqual(WorkflowActionCode.Rejected, rejectResult.Code,
            $"Reject must result in Rejected, got {rejectResult.Code}.");

        // Verify the AddedPending task is now Cancelled (C11 fix).
        await using var verifyCtx = MakeContext();
        var addedTask = await verifyCtx.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.AssigneeITCode == A3 && t.NodeInstanceId == nodeInst.ID);
        Assert.AreEqual(TaskState.Cancelled, addedTask.State,
            "C11 fix: AddedPending task must be Cancelled after Sequential reject.");
    }

    // ── C10: Any-mode approval sets DecidedBy on NodeInstance ─────────────────

    /// <summary>
    /// C10 fix: when an Any-mode (或签) node completes because the first approver approved,
    /// NodeInstance.DecidedBy must be set to that approver's ITCode.
    /// Before the fix, All/Any completion went through AdvanceAsync (no actor context) and
    /// DecidedBy was never stamped.
    /// </summary>
    [TestMethod]
    public async Task C10_Any_ApproveToCompletion_Sets_DecidedBy()
    {
        const string A1 = "alice";
        const string A2 = "bob";
        const string A3 = "carol";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        // Start a 3-approver Any-mode workflow.
        var version  = await SeedVersionAsync(ctx,
            AllAnyGraphs.AnyApprovers(new[] { A1, A2, A3 }));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running after StartAsync.");

        // Read the NodeInstance and find A1's task.
        await using var readCtx = MakeContext();
        var nodeInst = await readCtx.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var taskA1 = await readCtx.Set<ApprovalTask>().AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.AssigneeITCode == A1);
        Assert.AreEqual(TaskState.Pending, taskA1.State, "A1 must have a Pending task.");

        // A1 approves → Any-mode: one approval is enough → instance Approved.
        var result = await engine.ApproveTaskAsync(taskA1.ID, A1);
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, result.Code,
            $"Any-mode: first approval must reach InstanceApproved, got {result.Code}.");

        // C10 fix: verify DecidedBy is set to A1's ITCode on the approval NodeInstance.
        await using var verifyCtx = MakeContext();
        var completedNode = await verifyCtx.Set<NodeInstance>().AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);
        Assert.AreEqual(NodeState.CompletedApproved, completedNode.State,
            "NodeInstance must be CompletedApproved.");
        Assert.AreEqual(A1, completedNode.DecidedBy,
            $"C10 fix: DecidedBy must be '{A1}' (the approver who completed the Any node), got '{completedNode.DecidedBy}'.");
    }
}
