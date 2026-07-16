#nullable enable
// WF-12 + WF-13 tests:
//   WithdrawAsync:
//     1. Initiator withdraws Running instance → State=Withdrawn, Pending tasks Cancelled, EventLog written.
//     2. Non-initiator (no admin flag) → NotInitiator.
//     3. Already-terminal instance → CannotWithdrawAlreadyFinal.
//     4. WithdrawPolicy=Disabled → NotAuthorized.
//     5. WithdrawPolicy=BeforeAnyAction after an approver has acted → CannotWithdrawAlreadyFinal.
//     6. T-CONC-2: concurrent Withdraw vs. instance already transitioned → loser gets CannotWithdrawAlreadyFinal.
//   ReturnToInitiatorAsync:
//     7. Approver returns task → instance Draft, node Returned, tasks Cancelled, log written.
//     8. Non-assignee → TaskNotActive.
//   WF-13 CC extra tests:
//     9. CC graph → CcRecord written with instance's TenantCode; no ApprovalTask created.
//    10. Cross-tenant CC check: recipient resolution succeeds but CcRecord carries instance tenant (not a separate tenant).
//        (Full cross-tenant DB isolation is WF-14 controller-layer concern — engine-level test verifies TenantCode stamp.)
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
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class WithdrawCcTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfWC_{Guid.NewGuid():N}";
        // #709: WfEngineTestContext now registers SqliteBusyTimeoutInterceptor (see its
        // definition in EngineTests.cs) — this raw keep-alive connection needs the same
        // PRAGMA applied by hand since it never goes through EF Core's interceptor pipeline.
        _keepAlive = SqliteSharedMemoryFixture.OpenKeepAliveWithBusyTimeout(_dbName);
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

    // Build engine from the test helpers in EngineTests.cs (same assembly).
    private (IWorkflowEngine engine, WfEngineTestContext ctx) MakeEngine(WorkFlowOptions? options = null)
    {
        var ctx = MakeContext();
        var resolver = new StaticApproverResolver();
        var opts = options ?? new WorkFlowOptions();
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
        var engine = WorkflowEngine_Exposed.CreateWithOptions(ctx, dispatcher, opts, NullLogger.Instance);
        return (engine, ctx);
    }

    // Seed a definition version.
    private async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfEngineTestContext ctx,
        string graphJson,
        string? tenantCode = null)
    {
        var version = new ProcessDefinitionVersion
        {
            ID              = Guid.NewGuid(),
            DefinitionId    = Guid.NewGuid(),
            VersionNo       = 1,
            SchemaVersion   = 1,
            GraphJson       = graphJson,
            ContentHash     = "wc-hash-" + Guid.NewGuid().ToString("N"),
            PublishedAt     = DateTime.UtcNow,
            PublishedBy     = "test",
            TenantCode      = tenantCode,
            IsValid         = true,
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
                new() { From = "start",    To = "approval1" },
                new() { From = "approval1", To = "end" },
            },
        });

    // Graph: Start → Cc (recipient="cc_user") → End
    private static string CcGraph(string recipient = "cc_user", string? tenantCode = null) =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "CcGraph",
            Name = "CcGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "cc1",
                    Kind         = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = recipient },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "cc1" },
                new() { From = "cc1",   To = "end" },
            },
        });

    // ── Test 1: Initiator withdraws Running instance ──────────────────────────

    [TestMethod]
    public async Task WithdrawAsync_ByInitiator_Succeeds_StateWithdrawn_TasksCancelled_LogWritten()
    {
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, ApprovalGraph("approver1"), tenantCode: null);
        var instance = await engine.StartAsync(version.ID, null, "initiator1", null);

        // Instance should be Running (blocked at Approval node).
        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running before withdraw.");

        var result = await engine.WithdrawAsync(instance.ID, "initiator1", reason: "changed mind");
        Assert.AreEqual(WorkflowActionCode.Withdrawn, result.Code,
            $"Expected Withdrawn, got {result.Code}: {result.Detail}");

        // Verify state in DB.
        await using var verify = MakeContext();

        var fresh = await verify.Set<ProcessInstance>().SingleAsync(x => x.ID == instance.ID);
        Assert.AreEqual(InstanceState.Withdrawn, fresh.State,
            "Instance state must be Withdrawn after successful withdraw.");

        // Pending tasks must be Cancelled.
        var tasks = await verify.Set<ApprovalTask>()
            .Where(t => verify.Set<NodeInstance>()
                              .Where(n => n.InstanceId == instance.ID)
                              .Select(n => n.ID)
                              .Contains(t.NodeInstanceId))
            .ToListAsync();

        foreach (var t in tasks)
        {
            Assert.IsTrue(
                t.State == TaskState.Cancelled || t.State == TaskState.Approved || t.State == TaskState.Rejected,
                $"Task {t.ID} must not be Pending after withdraw; got {t.State}.");
            // Since no one acted, they must be Cancelled.
            Assert.AreEqual(TaskState.Cancelled, t.State,
                $"Task {t.ID} must be Cancelled after withdraw.");
        }

        // EventLog must contain a Withdraw entry.
        var log = await verify.Set<WorkflowEventLog>()
            .Where(e => e.InstanceId == instance.ID && e.Action == EventAction.Withdraw)
            .FirstOrDefaultAsync();

        Assert.IsNotNull(log, "WorkflowEventLog must have a Withdraw entry.");
        Assert.AreEqual("initiator1", log.ActorITCode, "Withdraw log actor must be 'initiator1'.");
    }

    // ── Test 2: Non-initiator (no admin flag) ─────────────────────────────────

    [TestMethod]
    public async Task WithdrawAsync_ByNonInitiator_Returns_NotInitiator()
    {
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, ApprovalGraph("approver1"));
        var instance = await engine.StartAsync(version.ID, null, "initiator1", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        var result = await engine.WithdrawAsync(instance.ID, "someone_else");
        Assert.AreEqual(WorkflowActionCode.NotInitiator, result.Code,
            $"Expected NotInitiator, got {result.Code}.");
    }

    // ── Test 3: Already-terminal instance ────────────────────────────────────

    [TestMethod]
    public async Task WithdrawAsync_AlreadyApproved_Returns_CannotWithdrawAlreadyFinal()
    {
        // Use a trivial Start→End graph (auto-approves to Approved immediately).
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, EngineTestHelpers.SimpleStartEndGraph());
        var instance = await engine.StartAsync(version.ID, null, "initiator1", null);

        Assert.AreEqual(InstanceState.Approved, instance.State,
            "Trivial graph should be immediately Approved.");

        var result = await engine.WithdrawAsync(instance.ID, "initiator1");
        Assert.AreEqual(WorkflowActionCode.CannotWithdrawAlreadyFinal, result.Code,
            $"Expected CannotWithdrawAlreadyFinal for already-Approved instance, got {result.Code}.");
    }

    // ── Test 4: WithdrawPolicy=Disabled ──────────────────────────────────────

    [TestMethod]
    public async Task WithdrawAsync_PolicyDisabled_Returns_NotAuthorized()
    {
        var opts = new WorkFlowOptions { WithdrawPolicy = WithdrawPolicy.Disabled };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, ApprovalGraph("approver1"));
        var instance = await engine.StartAsync(version.ID, null, "initiator1", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        var result = await engine.WithdrawAsync(instance.ID, "initiator1");
        Assert.AreEqual(WorkflowActionCode.NotAuthorized, result.Code,
            $"Expected NotAuthorized when WithdrawPolicy=Disabled, got {result.Code}.");
    }

    // ── Test 5: WithdrawPolicy=BeforeAnyAction after an approver has acted ───

    [TestMethod]
    public async Task WithdrawAsync_BeforeAnyAction_AfterApproval_Returns_CannotWithdraw()
    {
        var opts = new WorkFlowOptions { WithdrawPolicy = WithdrawPolicy.BeforeAnyAction };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, ApprovalGraph("approver1"));
        var instance = await engine.StartAsync(version.ID, null, "initiator1", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        // Find the pending task and approve it.
        var task = await ctx.Set<ApprovalTask>()
            .AsNoTracking()
            .FirstAsync(t => t.State == TaskState.Pending
                              && ctx.Set<NodeInstance>()
                                    .Where(n => n.InstanceId == instance.ID)
                                    .Select(n => n.ID)
                                    .Contains(t.NodeInstanceId));

        var approveResult = await engine.ApproveTaskAsync(task.ID, "approver1");
        // The graph ends after approval so it reaches Approved.
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, approveResult.Code,
            $"Approve must reach InstanceApproved for this graph, got {approveResult.Code}.");

        // Now withdraw: instance is already Approved → CannotWithdrawAlreadyFinal.
        var result = await engine.WithdrawAsync(instance.ID, "initiator1");
        Assert.AreEqual(WorkflowActionCode.CannotWithdrawAlreadyFinal, result.Code,
            $"Expected CannotWithdrawAlreadyFinal after approval, got {result.Code}.");
    }

    // ── Test 6: T-CONC-2 — concurrent Withdraw vs. state already changed ─────

    [TestMethod]
    public async Task WithdrawAsync_TCONC2_ConcurrentWinner_LoserGetsCannotWithdraw()
    {
        // Simulate T-CONC-2 by manually advancing the instance state before calling Withdraw.
        // Because SQLite is single-writer, we approximate the race by directly advancing
        // the instance to Approved via GuardedTransition before calling WithdrawAsync.
        const int Rounds = 5;
        for (int round = 0; round < Rounds; round++)
        {
            // #709 round 2: named as one of the "at minimum" fixtures to fix — use file-WAL
            // instead of shared-cache in-memory. See SqliteSharedMemoryFixture's remarks.
            var dbPath = SqliteSharedMemoryFixture.NewFileDbPath($"WfTConc2_{round}");
            try
            {
                await using var seed = new WfEngineTestContext(dbPath, SqliteTestDbMode.FileWal);
                seed.Database.EnsureCreated();

                var version = new ProcessDefinitionVersion
                {
                    ID           = Guid.NewGuid(),
                    DefinitionId = Guid.NewGuid(),
                    VersionNo    = 1, SchemaVersion = 1,
                    GraphJson    = ApprovalGraph("approver1"),
                    ContentHash  = "tc2-" + Guid.NewGuid().ToString("N"),
                    PublishedAt  = DateTime.UtcNow, PublishedBy = "test",
                    TenantCode   = null, IsValid = true,
                };
                seed.Set<ProcessDefinitionVersion>().Add(version);
                await seed.SaveChangesAsync();

                var resolver   = new StaticApproverResolver();
                var opts       = new WorkFlowOptions();
                var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
                await using var startCtx = new WfEngineTestContext(dbPath, SqliteTestDbMode.FileWal);
                var startEngine = WorkflowEngine_Exposed.CreateWithOptions(startCtx, dispatcher, opts, NullLogger.Instance);
                var instance = await startEngine.StartAsync(version.ID, null, "initiator1", null);

                Assert.AreEqual(InstanceState.Running, instance.State,
                    $"Round {round}: Instance must be Running before T-CONC-2 test.");

                // Simulate "concurrent winner" by using GuardedTransition to flip Running → Approved.
                await using var winCtx = new WfEngineTestContext(dbPath, SqliteTestDbMode.FileWal);
                var freshInst = await winCtx.Set<ProcessInstance>().SingleAsync(x => x.ID == instance.ID);
                await GuardedTransition.AdvanceProcessInstanceAsync(
                    winCtx, instance.ID,
                    expectedState: InstanceState.Running,
                    expectedRowVer: freshInst.RowVer,
                    nextState: InstanceState.Approved);

                // Now call WithdrawAsync — instance.RowVer is stale (was bumped by the winner above).
                await using var withdrawCtx = new WfEngineTestContext(dbPath, SqliteTestDbMode.FileWal);
                var withdrawDispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
                var withdrawEngine = WorkflowEngine_Exposed.CreateWithOptions(withdrawCtx, withdrawDispatcher, opts, NullLogger.Instance);
                var result = await withdrawEngine.WithdrawAsync(instance.ID, "initiator1");

                Assert.AreEqual(WorkflowActionCode.CannotWithdrawAlreadyFinal, result.Code,
                    $"Round {round}: Withdraw loser must get CannotWithdrawAlreadyFinal, got {result.Code}.");
            }
            finally
            {
                SqliteSharedMemoryFixture.DeleteFileDatabase(dbPath);
            }
        }
    }

    // ── Test 7: ReturnToInitiatorAsync — success path ─────────────────────────

    [TestMethod]
    public async Task ReturnToInitiatorAsync_ByAssignee_InstanceDraft_NodeReturned_LogWritten()
    {
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, ApprovalGraph("approver1"));
        var instance = await engine.StartAsync(version.ID, null, "initiator1", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

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

        // Verify in DB.
        await using var verify = MakeContext();

        var freshInst = await verify.Set<ProcessInstance>().SingleAsync(x => x.ID == instance.ID);
        Assert.AreEqual(InstanceState.Draft, freshInst.State,
            "Instance must be Draft after ReturnToInitiator.");

        var approvalNode = await verify.Set<NodeInstance>()
            .Where(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval)
            .FirstOrDefaultAsync();

        Assert.IsNotNull(approvalNode, "Approval NodeInstance must exist.");
        Assert.AreEqual(NodeState.Returned, approvalNode.State,
            $"Approval node must be Returned, got {approvalNode.State}.");

        // Event log must have a Return entry.
        var log = await verify.Set<WorkflowEventLog>()
            .Where(e => e.InstanceId == instance.ID && e.Action == EventAction.Return)
            .FirstOrDefaultAsync();

        Assert.IsNotNull(log, "WorkflowEventLog must have a Return entry.");
        Assert.AreEqual("approver1", log.ActorITCode, "Return log actor must be 'approver1'.");
    }

    // ── Test 8: ReturnToInitiatorAsync — non-assignee rejected ───────────────

    [TestMethod]
    public async Task ReturnToInitiatorAsync_NonAssignee_Returns_TaskNotActive()
    {
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

        var result = await engine.ReturnToInitiatorAsync(task.ID, "wrong_person");
        Assert.AreEqual(WorkflowActionCode.TaskNotActive, result.Code,
            $"Expected TaskNotActive for non-assignee, got {result.Code}.");
    }

    // ── Test 9: CC graph — CcRecord tenant-tagged, no ApprovalTask ────────────

    [TestMethod]
    public async Task CcGraph_CcRecordWritten_WithInstanceTenant_NoApprovalTask()
    {
        const string TenantCode  = "TENANT_A";
        const string CcRecipient = "cc_user";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, CcGraph(CcRecipient), tenantCode: TenantCode);
        var instance = await engine.StartAsync(version.ID, null, "initiator_t", TenantCode);

        Assert.AreEqual(InstanceState.Approved, instance.State,
            "Start→Cc→End must reach Approved.");

        await using var verify = MakeContext();

        // CcRecord must exist and carry the instance's TenantCode.
        var ccRecords = await verify.Set<CcRecord>()
            .Where(c => c.InstanceId == instance.ID)
            .ToListAsync();

        Assert.AreEqual(1, ccRecords.Count, $"Expected 1 CcRecord, got {ccRecords.Count}.");
        Assert.AreEqual(CcRecipient, ccRecords[0].RecipientITCode, "RecipientITCode mismatch.");
        Assert.AreEqual(TenantCode, ccRecords[0].TenantCode,
            "CcRecord TenantCode must be the instance's TenantCode.");

        // No ApprovalTask must have been created by the CC node.
        var approvalTasks = await verify.Set<ApprovalTask>()
            .Where(t => verify.Set<NodeInstance>()
                              .Where(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Cc)
                              .Select(n => n.ID)
                              .Contains(t.NodeInstanceId))
            .CountAsync();

        Assert.AreEqual(0, approvalTasks,
            $"No ApprovalTask should be created for a Cc node, got {approvalTasks}.");
    }

    // ── Test 10: CC TenantCode stamp — record always carries instance tenant ──

    [TestMethod]
    public async Task CcGraph_CcRecordAlwaysCarriesInstanceTenantCode()
    {
        // Verifies engine-level invariant: CcRecord.TenantCode == instance.TenantCode.
        // Full cross-tenant resolver check is WF-14 (controller layer).
        const string InstanceTenant = "TENANT_B";
        const string CcRecipient    = "cc_other_tenant_user";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, CcGraph(CcRecipient), tenantCode: InstanceTenant);
        var instance = await engine.StartAsync(version.ID, null, "initiator_b", InstanceTenant);

        Assert.AreEqual(InstanceState.Approved, instance.State);

        await using var verify = MakeContext();

        var ccRecord = await verify.Set<CcRecord>()
            .Where(c => c.InstanceId == instance.ID)
            .SingleOrDefaultAsync();

        Assert.IsNotNull(ccRecord, "CcRecord must exist.");
        Assert.AreEqual(InstanceTenant, ccRecord.TenantCode,
            "CcRecord.TenantCode must always equal the instance's TenantCode — " +
            "never a cross-tenant code injected by a recipient's own tenant.");
    }
}

// ── Test-only helpers ──────────────────────────────────────────────────────────

/// <summary>
/// Approver resolver that returns the "Type=User" rule's Value as the single approver.
/// Used in WF-12 / WF-13 tests where approval nodes are resolved by ITCode.
/// </summary>
internal sealed class StaticApproverResolver : IApproverResolver
{
    public Task<ApproverResolution> ResolveAsync(
        DbContext db,
        ApproverRuleDef rule,
        NodeInstance nodeInstance,
        string initiatorITCode,
        CancellationToken ct = default)
    {
        if (rule.Type == "User" && !string.IsNullOrWhiteSpace(rule.Value))
            return Task.FromResult(ApproverResolution.Success(
                new[] { rule.Value }));

        return Task.FromResult(ApproverResolution.NoApprover(
            $"StaticApproverResolver: unsupported rule type '{rule.Type}'."));
    }
}

