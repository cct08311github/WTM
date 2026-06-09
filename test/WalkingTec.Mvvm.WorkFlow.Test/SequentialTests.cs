#nullable enable
// WF-8: SequentialApprovalHandler + IApproverResolver integration tests.
//
// Tests:
//   1. Full 2-step Sequential approve flow → instance Approved, EventLog monotonic.
//   2. Single-step Sequential approve → instance Approved.
//   3. Reject mid-sequence → instance Rejected, remaining tasks Cancelled.
//   4. Early-act guard (wrong assignee) → TaskNotActive, no state change.
//   5. Resolver — User rule: comma-separated ITCodes minted as tasks.
//   6. Resolver — Role rule: users from FrameworkUserRole resolved.
//   7. Resolver — dedupe: duplicate ITCode in list skips second occurrence.
//   8. Resolver — empty result → NoApprover; explicit AutoApprove opt-in → instance Approved.
//  10. Default policy (FailClose) — no-approver FAILS CLOSED (red-line safety, WF-8).
//  11. EscalateToAdmin with empty AdminFallbackITCode → FailClose (not AutoApprove) (red-line safety, WF-8).
//  12. EscalateToAdmin with valid AdminFallbackITCode → Pending admin task minted (escalation works).
//   9. Concurrency: two concurrent ApproveTaskAsync on the same Pending task → one wins, one AlreadyHandled.
//
// All tests use SQLite shared-in-memory (never EF InMemory — spec §7.6 / #119 / #162).

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

// ── Extended DbContext: adds FrameworkUserRole for Role-resolution tests ────────

/// <summary>
/// Extends <see cref="WfEngineTestContext"/> with <see cref="FrameworkUserRole"/>
/// support for the Role-resolution tests in WF-8.
/// </summary>
internal sealed class WfSequentialTestContext : DbContext
{
    private readonly string _connStr;

    public WfSequentialTestContext(string connStr) { _connStr = connStr; }

    protected override void OnConfiguring(DbContextOptionsBuilder b) =>
        b.UseSqlite($"DataSource={_connStr}?mode=memory&cache=shared");

    protected override void OnModelCreating(ModelBuilder m)
    {
        // ProcessDefinitionVersion
        m.Entity<ProcessDefinitionVersion>(e =>
        {
            e.ToTable("Wf_ProcessDefinitionVersion");
            e.HasKey(x => x.ID);
            e.Property(x => x.GraphJson).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.DefinitionId);
            e.Property(x => x.VersionNo);
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.IsValid);
            e.Ignore(x => x.Definition);
        });

        // ProcessInstance
        m.Entity<ProcessInstance>(e =>
        {
            e.ToTable("Wf_ProcessInstance");
            e.HasKey(x => x.ID);
            e.Property(x => x.State);
            e.Property(x => x.RowVer);
            e.Property(x => x.InitiatorITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.DefinitionVersionId);
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.FormDataJson);
            e.Property(x => x.BusinessType).HasMaxLength(200);
            e.Property(x => x.BusinessKey).HasMaxLength(200);
            e.Property(x => x.IsValid);
            e.Ignore(x => x.DefinitionVersion);
        });

        // NodeInstance
        m.Entity<NodeInstance>(e =>
        {
            e.ToTable("Wf_NodeInstance");
            e.HasKey(x => x.ID);
            e.Property(x => x.State);
            e.Property(x => x.RowVer);
            e.Property(x => x.NodeKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.NodeKind);
            e.Property(x => x.ApproveMode);
            e.Property(x => x.InstanceId);
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.ActivatedAt);
            e.Property(x => x.DecidedBy).HasMaxLength(50);
            e.Property(x => x.ApprovedCount);
            e.Property(x => x.RejectedCount);
            e.Property(x => x.TotalRequired);
            e.Property(x => x.SequencePointer);
            e.Property(x => x.RejectGate);
            e.Property(x => x.RejectPolicy);
            e.Ignore(x => x.Instance);
        });

        // ApprovalTask
        m.Entity<ApprovalTask>(e =>
        {
            e.ToTable("Wf_ApprovalTask");
            e.HasKey(x => x.ID);
            e.Property(x => x.State);
            e.Property(x => x.RowVer);
            e.Property(x => x.AssigneeITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.NodeInstanceId);
            e.Property(x => x.SequenceOrder);
            e.Property(x => x.Comment);
            e.Property(x => x.ActedAtUtc);
            e.Property(x => x.DueUtc);
            e.Property(x => x.IsValid);
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Ignore(x => x.NodeInstance);
        });

        // WorkflowEventLog
        m.Entity<WorkflowEventLog>(e =>
        {
            e.ToTable("Wf_WorkflowEventLog");
            e.HasKey(x => x.ID);
            e.Property(x => x.InstanceId);
            e.Property(x => x.Seq);
            e.Property(x => x.Action);
            e.Property(x => x.ActorITCode).HasMaxLength(50);
            e.Property(x => x.NodeKey).HasMaxLength(100);
            e.Property(x => x.BeforeState).HasMaxLength(50);
            e.Property(x => x.AfterState).HasMaxLength(50);
            e.Property(x => x.Reason);
            e.Property(x => x.OccurredUtc);
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Ignore(x => x.Instance);
        });

        // CcRecord
        m.Entity<CcRecord>(e =>
        {
            e.ToTable("Wf_CcRecord");
            e.HasKey(x => x.ID);
            e.Property(x => x.InstanceId);
            e.Property(x => x.NodeKey).HasMaxLength(100);
            e.Property(x => x.RecipientITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.Trigger);
            e.Property(x => x.SentAtUtc);
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Ignore(x => x.Instance);
        });

        // FrameworkUserRole — for Role-resolution tests.
        m.Entity<FrameworkUserRole>(e =>
        {
            e.ToTable("Fw_UserRole");
            e.HasKey(x => x.ID);
            e.Property(x => x.UserCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.RoleCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.TenantCode).HasMaxLength(50);
        });
    }
}

// ── Graph helpers for Sequential tests ────────────────────────────────────────

internal static class SeqTestGraphs
{
    /// <summary>Start → Approval (Sequential, 1 approver) → End.</summary>
    public static string SingleApprover(string approverITCode) =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "SingleApproverGraph",
            Name = "SingleApproverGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = approverITCode },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });

    /// <summary>Start → Approval (Sequential, 2 comma-separated approvers) → End.</summary>
    public static string TwoApprovers(string approver1, string approver2) =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "TwoApproverGraph",
            Name = "TwoApproverGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    // Comma-separated: DefaultApproverResolver splits by ','
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = $"{approver1},{approver2}" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });

    /// <summary>Start → Approval (Sequential, Role rule) → End.</summary>
    public static string RoleApprover(string roleName) =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "RoleApproverGraph",
            Name = "RoleApproverGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "Role", Value = roleName },
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

// ── Main test fixture ─────────────────────────────────────────────────────────

[TestClass]
public class SequentialTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfSeq_{Guid.NewGuid():N}";
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

    /// <summary>Build an engine with real <see cref="DefaultApproverResolver"/> and default options.</summary>
    private (IWorkflowEngine engine, WfSequentialTestContext ctx) MakeEngine(
        WorkFlowOptions? options = null)
    {
        var ctx = MakeContext();
        var opts = options ?? new WorkFlowOptions();
        var resolver = new DefaultApproverResolverExposed(opts, ctx);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
        var engine = WorkflowEngine_Exposed.Create(ctx, dispatcher, NullLogger.Instance);
        return (engine, ctx);
    }

    private async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfSequentialTestContext ctx,
        string graphJson,
        string? tenantCode = null)
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
            TenantCode = tenantCode,
            IsValid = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }

    // ── Test 1: Full 2-step Sequential approve → instance Approved ─────────────

    /// <summary>
    /// Start → Approval(approver1, approver2 sequential) → End:
    /// Approve step-0 → Advanced; Approve step-1 → InstanceApproved.
    /// EventLog must be monotonic.  Both tasks end as Approved.
    /// </summary>
    [TestMethod]
    public async Task Sequential_TwoStep_BothApprove_ReachesApproved()
    {
        const string A1 = "alice";
        const string A2 = "bob";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, SeqTestGraphs.TwoApprovers(A1, A2));

        var instance = await engine.StartAsync(
            version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running after StartAsync (blocked at Approval node).");

        // Find task for step 0 (alice).
        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        Assert.AreEqual(NodeState.Activated, nodeInst.State);
        Assert.AreEqual(0, nodeInst.SequencePointer);
        Assert.AreEqual(2, nodeInst.TotalRequired);

        var task0 = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);
        Assert.AreEqual(TaskState.Pending, task0.State);
        Assert.AreEqual(A1, task0.AssigneeITCode);

        var task1Upfront = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 1);
        Assert.AreEqual(TaskState.NotYetActive, task1Upfront.State,
            "Step 1 must be NotYetActive before step 0 is approved.");

        // Approve step 0 (alice).
        var r1 = await engine.ApproveTaskAsync(task0.ID, A1, "LGTM step 0");
        Assert.AreEqual(WorkflowActionCode.Advanced, r1.Code,
            $"Step 0 approve must return Advanced (more steps remain), got {r1.Code}.");

        // Verify step 1 now Pending.
        await using var read2 = MakeContext();
        var task1After = await read2.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 1);
        Assert.AreEqual(TaskState.Pending, task1After.State,
            "Step 1 must be Pending after step 0 is approved.");
        Assert.AreEqual(A2, task1After.AssigneeITCode);

        // Approve step 1 (bob) — last step.
        var r2 = await engine.ApproveTaskAsync(task1After.ID, A2, "LGTM step 1");
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, r2.Code,
            $"Step 1 approve (last step) must return InstanceApproved, got {r2.Code}.");

        // Verify instance Approved.
        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, finalInst.State,
            "Instance must be Approved after all sequential steps complete.");

        // Verify both tasks Approved.
        var allTasks = await readFinal.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .OrderBy(t => t.SequenceOrder)
            .ToListAsync();
        Assert.AreEqual(2, allTasks.Count);
        Assert.AreEqual(TaskState.Approved, allTasks[0].State, "Task 0 must be Approved.");
        Assert.AreEqual(TaskState.Approved, allTasks[1].State, "Task 1 must be Approved.");

        // Verify EventLog monotonic sequence.
        var events = await readFinal.Set<WorkflowEventLog>()
            .AsNoTracking()
            .Where(e => e.InstanceId == instance.ID)
            .OrderBy(e => e.Seq)
            .ToListAsync();
        Assert.IsTrue(events.Count >= 3,
            $"Expected ≥3 log entries (Submit + 2 Approves), got {events.Count}.");
        for (int i = 0; i < events.Count; i++)
            Assert.AreEqual(i + 1, events[i].Seq, $"EventLog Seq[{i}] must be {i + 1} (monotonic).");
    }

    // ── Test 2: Single-step Sequential approve → instance Approved ─────────────

    [TestMethod]
    public async Task Sequential_SingleStep_Approve_ReachesApproved()
    {
        const string Actor = "solo_approver";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, SeqTestGraphs.SingleApprover(Actor));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var task = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID);
        Assert.AreEqual(TaskState.Pending, task.State);

        var result = await engine.ApproveTaskAsync(task.ID, Actor);
        Assert.AreEqual(WorkflowActionCode.InstanceApproved, result.Code,
            $"Single-step approve must return InstanceApproved, got {result.Code}.");

        await using var readFinal = MakeContext();
        var final = await readFinal.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, final.State);
    }

    // ── Test 3: Reject mid-sequence → instance Rejected, others Cancelled ──────

    [TestMethod]
    public async Task Sequential_RejectAtStep0_InstanceRejected_OthersCancel()
    {
        const string A1 = "charlie";
        const string A2 = "dave";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, SeqTestGraphs.TwoApprovers(A1, A2));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var task0 = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);

        var result = await engine.RejectTaskAsync(task0.ID, A1, "Not approved.");
        Assert.AreEqual(WorkflowActionCode.Rejected, result.Code,
            $"Reject must return Rejected, got {result.Code}.");

        // Instance must be Rejected.
        await using var readFinal = MakeContext();
        var finalInst = await readFinal.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Rejected, finalInst.State,
            "Instance must be Rejected after a reject action.");

        // Step 0 must be Rejected, step 1 must be Cancelled.
        var tasks = await readFinal.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .OrderBy(t => t.SequenceOrder)
            .ToListAsync();
        Assert.AreEqual(TaskState.Rejected,   tasks[0].State, "Task 0 must be Rejected.");
        Assert.AreEqual(TaskState.Cancelled,  tasks[1].State, "Task 1 must be Cancelled (downstream).");

        // NodeInstance must be CompletedRejected.
        var finalNode = await readFinal.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);
        Assert.AreEqual(NodeState.CompletedRejected, finalNode.State,
            "NodeInstance must be CompletedRejected after reject.");
    }

    // ── Test 4: Early-act guard → TaskNotActive if wrong assignee ──────────────

    [TestMethod]
    public async Task Sequential_EarlyAct_WrongAssignee_ReturnsTaskNotActive()
    {
        const string Assignee   = "eve";
        const string WrongActor = "mallory";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, SeqTestGraphs.SingleApprover(Assignee));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var task = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID);

        // Wrong actor tries to approve.
        var result = await engine.ApproveTaskAsync(task.ID, WrongActor);
        Assert.AreEqual(WorkflowActionCode.TaskNotActive, result.Code,
            $"Wrong assignee must get TaskNotActive, got {result.Code}.");

        // Task state must remain Pending.
        await using var readAfter = MakeContext();
        var taskAfter = await readAfter.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == task.ID);
        Assert.AreEqual(TaskState.Pending, taskAfter.State,
            "Task must remain Pending after a failed early-act attempt.");

        // Instance must remain Running.
        var instAfter = await readAfter.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);
        Assert.AreEqual(InstanceState.Running, instAfter.State,
            "Instance must remain Running after failed early-act.");
    }

    // ── Test 5: Resolver — User rule, comma-separated list ────────────────────

    [TestMethod]
    public async Task Resolver_UserRule_CommaSeparated_MintsTwoTasks()
    {
        const string A1 = "frank";
        const string A2 = "grace";
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, SeqTestGraphs.TwoApprovers(A1, A2));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        Assert.AreEqual(2, nodeInst.TotalRequired,
            "TotalRequired must be 2 for a 2-approver comma-separated rule.");

        var tasks = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .OrderBy(t => t.SequenceOrder)
            .ToListAsync();

        Assert.AreEqual(2, tasks.Count, "Two tasks must be minted upfront.");
        Assert.AreEqual(A1, tasks[0].AssigneeITCode, "Step 0 must be assigned to first approver.");
        Assert.AreEqual(A2, tasks[1].AssigneeITCode, "Step 1 must be assigned to second approver.");
        Assert.AreEqual(TaskState.Pending,       tasks[0].State, "Step 0 must be Pending.");
        Assert.AreEqual(TaskState.NotYetActive,  tasks[1].State, "Step 1 must be NotYetActive.");
    }

    // ── Test 6: Resolver — Role rule resolves users from FrameworkUserRole ─────

    [TestMethod]
    public async Task Resolver_RoleRule_ResolvesUsersFromDb()
    {
        const string Role    = "Managers";
        const string Member1 = "henry";
        const string Member2 = "iris";

        // Seed FrameworkUserRole rows.
        await using var seedCtx = MakeContext();
        seedCtx.Set<FrameworkUserRole>().AddRange(
            new FrameworkUserRole { ID = Guid.NewGuid(), UserCode = Member1, RoleCode = Role },
            new FrameworkUserRole { ID = Guid.NewGuid(), UserCode = Member2, RoleCode = Role });
        await seedCtx.SaveChangesAsync();

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, SeqTestGraphs.RoleApprover(Role));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running with role-based approvers.");

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        Assert.AreEqual(2, nodeInst.TotalRequired,
            "TotalRequired must be 2 (two members in role).");

        var tasks = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .OrderBy(t => t.SequenceOrder)
            .ToListAsync();

        Assert.AreEqual(2, tasks.Count, "Two tasks must be minted for two role members.");
        // DefaultApproverResolver orders by UserCode: henry < iris
        Assert.AreEqual(Member1, tasks[0].AssigneeITCode, $"Step 0 must be '{Member1}' (alpha-first).");
        Assert.AreEqual(Member2, tasks[1].AssigneeITCode, $"Step 1 must be '{Member2}'.");
    }

    // ── Test 7: Resolver — dedupe skips second occurrence of same ITCode ───────

    [TestMethod]
    public async Task Resolver_Dedupe_DuplicateITCode_SkippedAfterFirst()
    {
        // Two identical approvers — DefaultApproverResolver must dedupe them to 1.
        const string A = "jack";
        // Construct a graph with the comma-sep trick "jack,jack"
        var graph = WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "DedupeGraph",
            Name = "DedupeGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = $"{A},{A}" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, graph);
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        // After dedupe, only 1 task should exist.
        Assert.AreEqual(1, nodeInst.TotalRequired,
            "Dedupe must reduce two identical approvers to one. TotalRequired must be 1.");

        var tasks = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();
        Assert.AreEqual(1, tasks.Count, "Only one task must be minted after dedupe.");
        Assert.AreEqual(A, tasks[0].AssigneeITCode);
    }

    // ── Test 8: Resolver — empty result → NoApprover → explicit AutoApprove (opt-in) ──

    [TestMethod]
    public async Task Resolver_EmptyResult_NoApprover_ExplicitAutoApprove_ReachesApproved()
    {
        // An unknown rule type will trigger UnsupportedRuleType → same code path as NoApprover.
        // AutoApproveOnMissingHandler must be EXPLICITLY set to AutoApprove (no longer the default).
        // The default (FailClose) is tested in Test 10.
        var graph = WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "NoApproverGraph",
            Name = "NoApproverGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "UnknownType", Value = "whatever" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });

        // Use AutoApprove policy (default).
        var opts = new WorkFlowOptions
        {
            AutoApproveOnMissingHandler = AutoApproveOnMissingHandlerPolicy.AutoApprove,
        };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, graph);
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        // AutoApprove means the node completes immediately and the instance reaches Approved.
        Assert.AreEqual(InstanceState.Approved, instance.State,
            "AutoApprove policy on NoApprover must let the instance reach Approved immediately.");

        // No tasks should be minted.
        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var taskCount = await read1.Set<ApprovalTask>()
            .CountAsync(t => t.NodeInstanceId == nodeInst.ID);
        Assert.AreEqual(0, taskCount,
            "No tasks should be minted when AutoApprove policy fires on missing handler.");
    }

    // ── Test 10: Default policy (FailClose) — no-approver FAILS CLOSED ───────────

    /// <summary>
    /// Red-line safety test: with default <see cref="WorkFlowOptions"/> (AutoApproveOnMissingHandler
    /// = FailClose), a node whose approver cannot be resolved must NOT auto-approve.
    /// The instance must remain Running and the NodeInstance TotalRequired must be int.MaxValue
    /// (the fail-closed sentinel), indicating the node is blocked for admin attention.
    /// </summary>
    [TestMethod]
    public async Task Resolver_EmptyResult_DefaultPolicy_FailCloses_NotAutoApproved()
    {
        // Use default WorkFlowOptions — AutoApproveOnMissingHandler defaults to FailClose.
        var graph = WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "NoApproverFailCloseGraph",
            Name = "NoApproverFailCloseGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    // Unknown rule type → UnsupportedRuleType → same no-approver path.
                    ApproverRule = new ApproverRuleDef { Type = "UnknownType", Value = "nobody" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });

        // Default options — AutoApproveOnMissingHandler = FailClose (the safe default).
        var opts = new WorkFlowOptions(); // do NOT set AutoApproveOnMissingHandler
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, graph);
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        // FailClose: instance must remain Running (blocked), NOT Approved.
        Assert.AreEqual(InstanceState.Running, instance.State,
            "FailClose policy: instance must remain Running when no approver can be resolved. " +
            "It must NOT silently reach Approved. (Red-line: WF-8 safety default.)");

        // The NodeInstance TotalRequired must be set to int.MaxValue (fail-closed sentinel).
        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        Assert.AreEqual(int.MaxValue, nodeInst.TotalRequired,
            "FailClose policy: TotalRequired must be int.MaxValue (fail-closed sentinel) — " +
            "the node must never complete without admin intervention.");

        // No tasks must be minted.
        var taskCount = await read1.Set<ApprovalTask>()
            .CountAsync(t => t.NodeInstanceId == nodeInst.ID);
        Assert.AreEqual(0, taskCount,
            "FailClose policy: no ApprovalTask rows should be minted when the node fails closed.");
    }

    // ── Test 11: EscalateToAdmin with empty AdminFallbackITCode → FailClose ──────

    /// <summary>
    /// Red-line safety test: when policy is EscalateToAdmin but AdminFallbackITCode is
    /// empty/unset, the engine must FAIL CLOSED — it must NOT fall through to AutoApprove.
    /// </summary>
    [TestMethod]
    public async Task EscalateToAdmin_EmptyAdminCode_FailCloses_NotAutoApproved()
    {
        var graph = WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "EscalateNoAdminGraph",
            Name = "EscalateNoAdminGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "UnknownType", Value = "nobody" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });

        // EscalateToAdmin with NO admin code configured → must fail closed.
        var opts = new WorkFlowOptions
        {
            AutoApproveOnMissingHandler = AutoApproveOnMissingHandlerPolicy.EscalateToAdmin,
            AdminFallbackITCode = null, // intentionally empty
        };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, graph);
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        // Must remain Running — NOT auto-approved.
        Assert.AreEqual(InstanceState.Running, instance.State,
            "EscalateToAdmin with empty AdminFallbackITCode must fail closed (not auto-approve). " +
            "(Red-line: WF-8 safety fix.)");

        // NodeInstance TotalRequired must be int.MaxValue (fail-closed sentinel), not 0 (auto-approve).
        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        Assert.AreEqual(int.MaxValue, nodeInst.TotalRequired,
            "EscalateToAdmin with empty admin: TotalRequired must be int.MaxValue (fail-closed sentinel). " +
            "Prior bug: this fell through to AutoApprove (TotalRequired=0).");

        // No tasks minted (no admin to assign to).
        var taskCount = await read1.Set<ApprovalTask>()
            .CountAsync(t => t.NodeInstanceId == nodeInst.ID);
        Assert.AreEqual(0, taskCount,
            "No tasks must be minted when EscalateToAdmin fails closed (no admin configured).");
    }

    // ── Test 12: EscalateToAdmin with valid AdminFallbackITCode → task minted ────

    /// <summary>
    /// Regression guard: when EscalateToAdmin is configured with a valid AdminFallbackITCode,
    /// a Pending task must be minted for the admin (the escalation path must still work).
    /// </summary>
    [TestMethod]
    public async Task EscalateToAdmin_ValidAdminCode_MintsAdminTask()
    {
        const string AdminCode = "super_admin";

        var graph = WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "EscalateWithAdminGraph",
            Name = "EscalateWithAdminGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "UnknownType", Value = "nobody" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });

        var opts = new WorkFlowOptions
        {
            AutoApproveOnMissingHandler = AutoApproveOnMissingHandlerPolicy.EscalateToAdmin,
            AdminFallbackITCode = AdminCode,
        };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, graph);
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        // Instance must be Running (blocked at admin task).
        Assert.AreEqual(InstanceState.Running, instance.State,
            "EscalateToAdmin with valid admin: instance must be Running (blocked at admin task).");

        await using var read1 = MakeContext();
        var nodeInst = await read1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        // TotalRequired must be 1 (one admin task).
        Assert.AreEqual(1, nodeInst.TotalRequired,
            "EscalateToAdmin: TotalRequired must be 1 (one admin task minted).");

        // Admin task must be Pending.
        var adminTask = await read1.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.NodeInstanceId == nodeInst.ID);
        Assert.IsNotNull(adminTask, "An admin task must be minted.");
        Assert.AreEqual(AdminCode, adminTask.AssigneeITCode,
            "Admin task must be assigned to AdminFallbackITCode.");
        Assert.AreEqual(TaskState.Pending, adminTask.State,
            "Admin task must be Pending.");
    }

    // ── Test 9: Concurrency — two concurrent ApproveTaskAsync → one wins ───────

    [TestMethod]
    public async Task Sequential_ConcurrentApprove_ExactlyOneWinner_OneAlreadyHandled()
    {
        const int Rounds = 10;

        for (int round = 0; round < Rounds; round++)
        {
            // Fresh DB per round.
            var dbName = $"WfSeqConc_{round}_{Guid.NewGuid():N}";
            await using var keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
            keepAlive.Open();

            await using var initCtx = new WfSequentialTestContext(dbName);
            initCtx.Database.EnsureCreated();

            // Seed definition.
            const string Actor = "approver_conc";
            var graphJson = SeqTestGraphs.SingleApprover(Actor);
            var versionId = Guid.NewGuid();
            initCtx.Set<ProcessDefinitionVersion>().Add(new ProcessDefinitionVersion
            {
                ID = versionId,
                DefinitionId = Guid.NewGuid(),
                VersionNo = 1,
                SchemaVersion = 1,
                GraphJson = graphJson,
                ContentHash = "conc-" + versionId,
                IsValid = true,
            });
            await initCtx.SaveChangesAsync();

            // Start via engine1.
            var opts = new WorkFlowOptions();
            await using var startCtx = new WfSequentialTestContext(dbName);
            var startResolver   = new DefaultApproverResolverExposed(opts, startCtx);
            var startDispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(startResolver, opts);
            var engine1 = WorkflowEngine_Exposed.Create(startCtx, startDispatcher, NullLogger.Instance);
            var instance = await engine1.StartAsync(versionId, null, "initiator", null);

            Assert.AreEqual(InstanceState.Running, instance.State,
                $"Round {round}: instance must be Running (blocked at approval).");

            // Find the Pending task.
            await using var readCtx = new WfSequentialTestContext(dbName);
            var nodeInst = await readCtx.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
            var task = await readCtx.Set<ApprovalTask>()
                .AsNoTracking()
                .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.State == TaskState.Pending);

            // Race two approvals.
            var barrier = new SemaphoreSlim(0, 2);
            var instanceId = instance.ID;
            var taskId = task.ID;

            Task<WorkflowActionResult> MakeApproveTask()
            {
                return Task.Run(async () =>
                {
                    await barrier.WaitAsync();
                    var raceOpts = new WorkFlowOptions();
                    await using var raceCtx = new WfSequentialTestContext(dbName);
                    var raceResolver   = new DefaultApproverResolverExposed(raceOpts, raceCtx);
                    var raceDispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(raceResolver, raceOpts);
                    var raceEngine = WorkflowEngine_Exposed.Create(raceCtx, raceDispatcher, NullLogger.Instance);
                    return await raceEngine.ApproveTaskAsync(taskId, Actor);
                });
            }

            var t1 = MakeApproveTask();
            var t2 = MakeApproveTask();
            barrier.Release(2);

            var results = await Task.WhenAll(t1, t2);

            int approved = results.Count(r => r.Code == WorkflowActionCode.InstanceApproved);
            // The losing racer may return AlreadyHandled (CAS failed before guard checks),
            // or NodeClosed (guard checks find the node already completed by the winner).
            // Both are valid "one side lost" outcomes from the CAS discipline.
            int lost = results.Count(r => r.Code == WorkflowActionCode.AlreadyHandled
                                           || r.Code == WorkflowActionCode.NodeClosed);

            Assert.AreEqual(1, approved,
                $"Round {round}: exactly 1 result must be InstanceApproved, got [{results[0].Code},{results[1].Code}].");
            Assert.AreEqual(1, lost,
                $"Round {round}: exactly 1 result must be AlreadyHandled or NodeClosed (loser), got [{results[0].Code},{results[1].Code}].");
        }
    }
}

// ── Thin wrapper to make DefaultApproverResolver accessible from tests ─────────

/// <summary>
/// Test helper: wraps <see cref="DefaultApproverResolver"/> making it constructable
/// directly in tests without going through DI.  The <c>db</c> argument on
/// <see cref="ResolveAsync"/> is forwarded unchanged for Role resolution queries.
/// </summary>
internal sealed class DefaultApproverResolverExposed : IApproverResolver
{
    private readonly DefaultApproverResolver _inner;

    public DefaultApproverResolverExposed(WorkFlowOptions opts, DbContext db)
    {
        // DefaultManagerChainProvider is the no-op default.
        var options  = Microsoft.Extensions.Options.Options.Create(opts);
        var mcLogger = NullLogger<DefaultManagerChainProvider>.Instance;
        var mc       = new DefaultManagerChainProvider(mcLogger);
        var rLogger  = NullLogger<DefaultApproverResolver>.Instance;
        _inner = new DefaultApproverResolver(options, mc, rLogger);
    }

    public Task<ApproverResolution> ResolveAsync(
        DbContext db,
        ApproverRuleDef rule,
        NodeInstance nodeInstance,
        string initiatorITCode,
        CancellationToken ct = default)
        => _inner.ResolveAsync(db, rule, nodeInstance, initiatorITCode, ct);
}
