#nullable enable
// WF-19: DelegationResolvingDecorator unit + integration tests.
//
// Tests (T-DEL-* mapping from spec §6):
//   T-DEL-01: Transitive chain A→B→C: task minted for C, DelegatedFromITCode=A, TotalRequired=1.
//   T-DEL-02a: Cycle A→B→A, no AdminFallback → node BLOCKS (TotalRequired=int.MaxValue, FailClose).
//   T-DEL-02b: Cycle A→B→A, AdminFallback configured → task goes to fallback user.
//   T-DEL-03: Self-delegation A→A → cycle detected on first revisit, FailClose fallback.
//   T-DEL-04: Hop cap exceeded (4-deep chain, MaxDelegationHops=2) → stops at 3rd hop,
//             logged, no throw/loop; TotalRequired=1 (the capped delegatee).
//   T-DEL-07: Dedupe D→C where C is also a direct approver → one C slot, TotalRequired=1.
//   T-DEL-ED1: Expired rule is ignored (EndUtc in the past); base approver used directly.
//   T-DEL-ED2: Scope mismatch → rule for different DefinitionCode not applied.
//   T-DEL-ED3: Overlapping rules for same principal → deterministic pick (earliest StartUtc then lowest ID).
//
// All tests use SQLite shared-in-memory (never EF InMemory — spec §7.6).
// DelegationResolvingDecorator is internal; InternalsVisibleTo in the main project exposes it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ── DbContext for delegation tests ────────────────────────────────────────────────

/// <summary>
/// SQLite shared-in-memory DbContext that includes the DelegationRule table in addition
/// to the standard WorkFlow entities needed by the engine.
/// </summary>
internal sealed class WfDelegationTestContext : DbContext
{
    private readonly string _connStr;

    public WfDelegationTestContext(string connStr) { _connStr = connStr; }

    protected override void OnConfiguring(DbContextOptionsBuilder b) =>
        b.UseSqlite($"DataSource={_connStr}?mode=memory&cache=shared");

    protected override void OnModelCreating(ModelBuilder m)
    {
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
            e.Property(x => x.Generation);
            e.Property(x => x.ReturnLoops);
            e.Property(x => x.NextSeq).HasDefaultValue(1);
            e.Property(x => x.ReturningLeaseUtc);
        });

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
            e.Property(x => x.ApprovePercent);
            e.Property(x => x.RejectGate);
            e.Property(x => x.RejectPolicy);
            e.Ignore(x => x.Instance);
            e.Property(x => x.Generation);
            e.Property(x => x.SupersededAtGen);
            e.Property(x => x.ForkGroupId);
            e.Property(x => x.JoinNodeKey).HasMaxLength(100);
            e.Property(x => x.JoinExpectedArrivals).HasDefaultValue(0);
            e.Property(x => x.JoinArrivedCount).HasDefaultValue(0);
            e.Property(x => x.AckMode);
            e.Property(x => x.ApproverSetEpoch).HasDefaultValue(0u);
            // FIX-5: DefinitionCode must be in the schema so the engine can persist it.
            e.Property(x => x.DefinitionCode).HasMaxLength(100);
            e.HasIndex(x => new { x.TenantCode, x.InstanceId, x.NodeKey, x.Generation })
             .IsUnique()
             .HasDatabaseName("IX_Wf_NodeInstance_TenantCode_InstanceId_NodeKey_Generation");
        });

        m.Entity<ApprovalTask>(e =>
        {
            e.ToTable("Wf_ApprovalTask");
            e.HasKey(x => x.ID);
            e.Property(x => x.State);
            e.Property(x => x.RowVer);
            e.Property(x => x.AssigneeITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.NodeInstanceId);
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.SequenceOrder);
            e.Property(x => x.Comment);
            e.Property(x => x.ActedAtUtc);
            e.Property(x => x.DueUtc);
            e.Property(x => x.IsValid);
            e.Property(x => x.DelegatedFromITCode).HasMaxLength(50);
            e.Property(x => x.DelegationRuleId);
            e.Property(x => x.DelegationExpiresUtc);
            e.Property(x => x.WindowVerifiedUtc);
            e.Property(x => x.Generation);
            e.Property(x => x.AddDepth).HasDefaultValue(0);
            e.Ignore(x => x.NodeInstance);
        });

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
            e.HasIndex(x => new { x.TenantCode, x.InstanceId, x.Seq }).IsUnique();
        });

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

        // DelegationRule — WF-19 core table.
        m.Entity<DelegationRule>(e =>
        {
            e.ToTable("Wf_DelegationRule");
            e.HasKey(x => x.ID);
            e.Property(x => x.PrincipalITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.DelegateeITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.ScopeDefinitionCode).HasMaxLength(100);
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.StartUtc);
            e.Property(x => x.EndUtc);
            e.Property(x => x.IsValid);
        });

        // WorkflowTimer — WF-20 stub; needed by CancelTimersForReturnAsync during 回退.
        m.Entity<WorkflowTimer>(e =>
        {
            e.ToTable("Wf_WorkflowTimer");
            e.HasKey(x => x.ID);
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.NodeInstanceId);
            e.Property(x => x.ApprovalTaskId);
            e.Property(x => x.FireAtUtc);
            e.Property(x => x.Action);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status);
            e.Property(x => x.RemindCount);
            e.Property(x => x.RowVer);
            e.Property(x => x.Generation);
            // Ignore navigations — test context does not set up cross-entity FKs.
            e.Ignore(x => x.ApprovalTask);
            e.Ignore(x => x.NodeInstance);
        });
    }
}

// ── Graph helpers for delegation tests ───────────────────────────────────────────

internal static class DelTestGraphs
{
    /// <summary>Start → Approval (Sequential, single base approver) → End.</summary>
    public static string SingleApprover(string approverITCode, string definitionCode = "TestDef") =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = definitionCode,
            Name = definitionCode,
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
                new() { From = "start",    To = "approval1" },
                new() { From = "approval1", To = "end"      },
            },
        });

    /// <summary>Start → Approval (Sequential, comma-separated base approvers) → End.</summary>
    public static string MultiApprover(string approvers, string definitionCode = "TestDef") =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = definitionCode,
            Name = definitionCode,
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = approvers },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",    To = "approval1" },
                new() { From = "approval1", To = "end"      },
            },
        });

    /// <summary>Start → Approval (Any/或签, comma-separated base approvers) → End.</summary>
    public static string MultiApproverAny(string approvers, string definitionCode = "TestDef") =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = definitionCode,
            Name = definitionCode,
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Any,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = approvers },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",    To = "approval1" },
                new() { From = "approval1", To = "end"      },
            },
        });

    /// <summary>
    /// Two sequential Approval nodes with ReturnToNode capability.
    /// approval1 must come before approval2; approval2 can return-to-node approval1.
    /// </summary>
    public static string TwoApprovalNodesReturnEnabled(
        string approver1, string approver2, string definitionCode = "ReturnTestDef") =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = definitionCode,
            Name = definitionCode,
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",     Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = approver1 },
                },
                new()
                {
                    NodeKey      = "approval2",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = approver2 },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "approval2" },
                new() { From = "approval2", To = "end"       },
            },
        });

    /// <summary>Start → Approval (All/会签, comma-separated base approvers) → End.</summary>
    public static string MultiApproverAll(string approvers, string definitionCode = "TestDef") =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = definitionCode,
            Name = definitionCode,
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.All,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = approvers },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",    To = "approval1" },
                new() { From = "approval1", To = "end"      },
            },
        });
}

// ── Test fixture ──────────────────────────────────────────────────────────────────

[TestClass]
public class DelegationTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfDelegation_{Guid.NewGuid():N}";
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

    private WfDelegationTestContext MakeContext() => new(_dbName);

    /// <summary>
    /// Creates an engine backed by a DelegationResolvingDecorator wrapping the DefaultApproverResolver.
    /// When <paramref name="options"/> is non-null, the engine is also constructed with the same
    /// options (e.g. <see cref="DelegationWindowMode"/>) so that engine-level option checks are active.
    /// Dispatcher is wired for Sequential only; use <see cref="MakeEngineAllModes"/> for Any/All nodes.
    /// </summary>
    private (IWorkflowEngine engine, WfDelegationTestContext ctx) MakeEngine(
        WorkFlowOptions? options = null)
    {
        var ctx  = MakeContext();
        var opts = options ?? new WorkFlowOptions();
        var inner    = new DefaultApproverResolverExposed(opts, ctx);
        var optWrap  = Options.Create(opts);
        var decLogger = NullLogger<DelegationResolvingDecorator>.Instance;
        var decorator = new DelegationResolvingDecorator(inner, optWrap, decLogger);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(decorator, opts);
        // Use CreateWithOptions so the engine picks up DelegationWindowMode and other opts.
        var engine     = WorkflowEngine_Exposed.CreateWithOptions(ctx, dispatcher, opts, NullLogger.Instance);
        return (engine, ctx);
    }

    /// <summary>
    /// Creates an engine wired for All/Any/Sequential nodes (CreateWithAllModes dispatcher).
    /// Used for tests targeting <see cref="AnyApprovalHandler"/> or <see cref="AllApprovalHandler"/>.
    /// </summary>
    private (IWorkflowEngine engine, WfDelegationTestContext ctx) MakeEngineAllModes(
        WorkFlowOptions? options = null)
    {
        var ctx  = MakeContext();
        var opts = options ?? new WorkFlowOptions();
        var inner    = new DefaultApproverResolverExposed(opts, ctx);
        var optWrap  = Options.Create(opts);
        var decLogger = NullLogger<DelegationResolvingDecorator>.Instance;
        var decorator = new DelegationResolvingDecorator(inner, optWrap, decLogger);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithAllModes(decorator, opts);
        var engine     = WorkflowEngine_Exposed.CreateWithOptions(ctx, dispatcher, opts, NullLogger.Instance);
        return (engine, ctx);
    }

    private DelegationRule MakeRule(
        string principal,
        string delegatee,
        string? scope = null,
        string? tenant = null,
        DateTime? start = null,
        DateTime? end = null)
    {
        var now = DateTime.UtcNow;
        return new DelegationRule
        {
            ID                 = Guid.NewGuid(),
            PrincipalITCode    = principal,
            DelegateeITCode    = delegatee,
            ScopeDefinitionCode = scope,
            TenantCode         = tenant,
            StartUtc           = start ?? now.AddHours(-1),
            EndUtc             = end   ?? now.AddHours(+8),
            IsValid            = true,
        };
    }

    private async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfDelegationTestContext ctx,
        string graphJson,
        string? tenantCode = null)
    {
        var version = new ProcessDefinitionVersion
        {
            ID            = Guid.NewGuid(),
            DefinitionId  = Guid.NewGuid(),
            VersionNo     = 1,
            SchemaVersion = 1,
            GraphJson     = graphJson,
            ContentHash   = "del-test-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            TenantCode    = tenantCode,
            IsValid       = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }

    // ── T-DEL-01: Transitive chain A→B→C ─────────────────────────────────────────

    /// <summary>
    /// T-DEL-01: Chain A→B→C at node entry.
    /// The base approver is A; B is A's delegatee; C is B's delegatee.
    /// One Pending task must be minted for C (not A or B).
    /// DelegatedFromITCode must equal A (original principal).
    /// DelegationRuleId must be non-null (the B→C rule id).
    /// TotalRequired == 1.
    /// </summary>
    [TestMethod]
    public async Task Del01_TransitiveChain_ABC_TaskMintedForC()
    {
        const string A = "alice"; const string B = "bob"; const string C = "carol";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        // Seed delegation rules: A→B and B→C (no scope restriction).
        var ruleAB = MakeRule(A, B);
        var ruleBC = MakeRule(B, C);
        ctx.Set<DelegationRule>().AddRange(ruleAB, ruleBC);
        await ctx.SaveChangesAsync();

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running (blocked at Approval node).");

        await using var verify = MakeContext();
        var tasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId != Guid.Empty)
            .ToListAsync();

        // Find the approval node to scope the query.
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        tasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        Assert.AreEqual(1, tasks.Count,
            $"Exactly one task must be minted (chain A→B→C deduces C). Got {tasks.Count}.");

        var task = tasks.Single();
        Assert.AreEqual(C, task.AssigneeITCode,
            $"Terminal delegatee must be C ('{C}'), got '{task.AssigneeITCode}'.");
        Assert.AreEqual(A, task.DelegatedFromITCode,
            $"DelegatedFromITCode must be original principal A ('{A}'), got '{task.DelegatedFromITCode}'.");
        Assert.IsNotNull(task.DelegationRuleId,
            "DelegationRuleId must be non-null for a delegated task.");
        Assert.AreEqual(ruleBC.ID, task.DelegationRuleId,
            "DelegationRuleId must be the terminal rule ID (B→C).");
        Assert.IsNotNull(task.DelegationExpiresUtc,
            "DelegationExpiresUtc must be stamped from the terminal rule's EndUtc.");

        Assert.AreEqual(1, nodeInst.TotalRequired,
            $"TotalRequired must be 1 (one deduped terminal slot). Got {nodeInst.TotalRequired}.");
    }

    // ── T-DEL-02a: Cycle A→B→A, no AdminFallback → FailClose ─────────────────────

    /// <summary>
    /// T-DEL-02a: Cycle A→B→A with empty AdminFallbackITCode.
    /// The decorator detects the cycle; no fallback is configured.
    /// The node must BLOCK via FailClose (TotalRequired == int.MaxValue).
    /// </summary>
    [TestMethod]
    public async Task Del02a_Cycle_NoAdminFallback_NodeBlocks()
    {
        const string A = "alice"; const string B = "bob";

        var opts = new WorkFlowOptions { AdminFallbackITCode = null };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        // Create cycle A→B→A.
        ctx.Set<DelegationRule>().AddRange(
            MakeRule(A, B),
            MakeRule(B, A));
        await ctx.SaveChangesAsync();

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must remain Running (blocked fail-closed).");

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        Assert.AreEqual(int.MaxValue, nodeInst.TotalRequired,
            $"FailClose must set TotalRequired=int.MaxValue. Got {nodeInst.TotalRequired}.");

        // No tasks minted (no valid approver resolved).
        var taskCount = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .CountAsync(t => t.NodeInstanceId == nodeInst.ID);

        Assert.AreEqual(0, taskCount,
            $"Zero tasks must be minted when cycle has no fallback. Got {taskCount}.");
    }

    // ── T-DEL-02b: Cycle A→B→A with AdminFallback → task goes to fallback ─────────

    /// <summary>
    /// T-DEL-02b: Cycle A→B→A with AdminFallbackITCode='admin'.
    /// The decorator routes the cycle to the admin fallback.
    /// One Pending task must be minted for 'admin'.
    /// </summary>
    [TestMethod]
    public async Task Del02b_Cycle_WithAdminFallback_TaskGoesToFallback()
    {
        const string A = "alice"; const string B = "bob"; const string Admin = "admin";

        var opts = new WorkFlowOptions { AdminFallbackITCode = Admin };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        ctx.Set<DelegationRule>().AddRange(
            MakeRule(A, B),
            MakeRule(B, A));
        await ctx.SaveChangesAsync();

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var tasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        Assert.AreEqual(1, tasks.Count,
            $"Exactly one task must be minted (fallback). Got {tasks.Count}.");
        Assert.AreEqual(Admin, tasks[0].AssigneeITCode,
            $"Task must be assigned to AdminFallback ('{Admin}'). Got '{tasks[0].AssigneeITCode}'.");
    }

    // ── T-DEL-02c: Cycle on ONE approver in a 会签 node → whole node blocks ───────

    /// <summary>
    /// T-DEL-02c: Multi-approver (All / 会签) node {A, B}; A's chain cycles (A→C→A);
    /// no AdminFallbackITCode configured.
    ///
    /// FIX-H regression guard: the old implementation would silently drop A's slot and
    /// return [B], letting the node complete with B's approval alone — A's authority
    /// would vanish undetected (fail-open defect).
    ///
    /// Correct behaviour: the ENTIRE node must BLOCK via FailClose (TotalRequired == int.MaxValue,
    /// zero tasks minted, node NOT completable) — mirroring Del02a exactly.
    /// </summary>
    [TestMethod]
    public async Task Del02c_Cycle_NoFallback_MultiApprover_NodeBlocks()
    {
        const string A = "alice"; const string B = "bob"; const string C = "carol";

        var opts = new WorkFlowOptions { AdminFallbackITCode = null };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        // A's chain cycles: A→C and C→A (B has no delegation rule — direct approver).
        ctx.Set<DelegationRule>().AddRange(
            MakeRule(A, C),
            MakeRule(C, A));
        await ctx.SaveChangesAsync();

        // Two base approvers: A (cycled) and B (direct, no delegation rule).
        var version  = await SeedVersionAsync(ctx, DelTestGraphs.MultiApproverAll($"{A},{B}"));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        // 1. Instance must remain Running (blocked fail-closed) — same as Del02a.
        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must remain Running (blocked fail-closed — whole node FailClose).");

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        // 2. TotalRequired must be int.MaxValue (impossible threshold) — same as Del02a.
        Assert.AreEqual(int.MaxValue, nodeInst.TotalRequired,
            $"FailClose must set TotalRequired=int.MaxValue (whole-node block). " +
            $"Got {nodeInst.TotalRequired}. " +
            "If this is 1 the slot-drop defect (FIX-H) regressed: node completed with B alone.");

        // 3. Zero tasks minted — the node must NOT have provided a path for B to approve alone.
        var taskCount = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .CountAsync(t => t.NodeInstanceId == nodeInst.ID);

        Assert.AreEqual(0, taskCount,
            $"Zero tasks must be minted when the whole node fails closed. Got {taskCount}. " +
            "If taskCount==1 the slot-drop defect (FIX-H) regressed: B's task was minted " +
            "even though A's chain cycled, making the node completable with B alone.");
    }

    // ── T-DEL-03: Self-delegation A→A → FailClose ────────────────────────────────

    /// <summary>
    /// T-DEL-03: Self-delegation A→A.
    /// The visited set catches A immediately on the first revisit.
    /// With no AdminFallback → FailClose (TotalRequired = int.MaxValue).
    /// </summary>
    [TestMethod]
    public async Task Del03_SelfDelegation_NoFallback_NodeBlocks()
    {
        const string A = "alice";

        var opts = new WorkFlowOptions { AdminFallbackITCode = null };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        ctx.Set<DelegationRule>().Add(MakeRule(A, A)); // self-loop
        await ctx.SaveChangesAsync();

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        Assert.AreEqual(int.MaxValue, nodeInst.TotalRequired,
            $"Self-delegation with no fallback must FailClose. Got TotalRequired={nodeInst.TotalRequired}.");

        var taskCount = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .CountAsync(t => t.NodeInstanceId == nodeInst.ID);
        Assert.AreEqual(0, taskCount, "No tasks minted for self-delegation FailClose.");
    }

    // ── T-DEL-04: Hop cap exceeded ────────────────────────────────────────────────

    /// <summary>
    /// T-DEL-04: Hop cap exceeded.
    /// Chain A→B→C→D→E (4 hops), MaxDelegationHops = 2.
    /// Resolution stops at C (after 2 hops from A: A→B is hop 1, B→C is hop 2, C→D would be hop 3 > cap).
    /// One task is minted for C; no throw, no infinite loop.
    /// </summary>
    [TestMethod]
    public async Task Del04_HopCapExceeded_StopsAtCapped_NoThrow()
    {
        const string A = "alice"; const string B = "bob";
        const string C = "carol"; const string D = "dave"; const string E = "eve";

        var opts = new WorkFlowOptions { MaxDelegationHops = 2 };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        ctx.Set<DelegationRule>().AddRange(
            MakeRule(A, B),
            MakeRule(B, C),
            MakeRule(C, D),
            MakeRule(D, E));
        await ctx.SaveChangesAsync();

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running (blocked at Approval).");

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var tasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        Assert.AreEqual(1, tasks.Count,
            $"Exactly one task must be minted (capped at C). Got {tasks.Count}.");

        // MaxDelegationHops=2: hops counted AFTER leaving the base; ++hops triggers on the NEXT rule.
        // Walk: A (base) → find A→B rule, visited.Add(B), ++hops(=1) ≤ 2 → cur=B
        //       B → find B→C rule, visited.Add(C), ++hops(=2) ≤ 2 → cur=C
        //       C → find C→D rule, visited.Add(D), ++hops(=3) > 2 → HopsCapped → return C
        // So capped terminal = C.
        Assert.AreEqual(C, tasks[0].AssigneeITCode,
            $"Capped terminal must be C. Got '{tasks[0].AssigneeITCode}'.");
    }

    // ── T-DEL-07: Dedupe — direct approver == delegatee of another ─────────────────

    /// <summary>
    /// T-DEL-07: Dedupe scenario.
    /// Base approvers: D (with rule D→C) and C (direct, no rule).
    /// The chain for D resolves to C.
    /// C is already a direct approver.
    /// Final set must contain C exactly once; TotalRequired = 1.
    /// </summary>
    [TestMethod]
    public async Task Del07_Dedupe_DelegateEqualsDirectApprover_OneSlot()
    {
        const string C = "carol"; const string D = "dave";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        // D delegates to C; C has no outgoing rule (is direct).
        ctx.Set<DelegationRule>().Add(MakeRule(D, C));
        await ctx.SaveChangesAsync();

        // Two base approvers: D (which chains to C) and C (direct).
        var version  = await SeedVersionAsync(ctx, DelTestGraphs.MultiApprover($"{D},{C}"));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var tasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        Assert.AreEqual(1, tasks.Count,
            $"Dedupe must result in exactly one slot (C). Got {tasks.Count}.");
        Assert.AreEqual(C, tasks[0].AssigneeITCode,
            $"Deduped slot must be C. Got '{tasks[0].AssigneeITCode}'.");

        // TotalRequired must also be 1 — the deduped count.
        // Re-read from DB (stored by engine after SaveChangesAsync on the update).
        var freshNode = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);
        Assert.AreEqual(1, freshNode.TotalRequired,
            $"TotalRequired must be 1 after dedup. Got {freshNode.TotalRequired}.");
    }

    // ── T-DEL-ED1: Expired rule is ignored ───────────────────────────────────────

    /// <summary>
    /// T-DEL-ED1: An expired rule (EndUtc in the past) must not apply.
    /// The base approver A must receive the task directly (no delegation).
    /// </summary>
    [TestMethod]
    public async Task DelEd1_ExpiredRule_IsIgnored_BaseApproverGetsTask()
    {
        const string A = "alice"; const string B = "bob";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        // Expired rule: EndUtc in the past.
        var expiredRule = MakeRule(A, B,
            start: DateTime.UtcNow.AddDays(-10),
            end:   DateTime.UtcNow.AddDays(-1));
        ctx.Set<DelegationRule>().Add(expiredRule);
        await ctx.SaveChangesAsync();

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var tasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        Assert.AreEqual(1, tasks.Count,
            $"Exactly one task expected (A, no delegation). Got {tasks.Count}.");
        Assert.AreEqual(A, tasks[0].AssigneeITCode,
            $"Task must go to A (expired rule must be ignored). Got '{tasks[0].AssigneeITCode}'.");
        Assert.IsNull(tasks[0].DelegationRuleId,
            "DelegationRuleId must be null for a non-delegated task.");
        Assert.IsNull(tasks[0].DelegatedFromITCode,
            "DelegatedFromITCode must be null for a non-delegated task.");
    }

    // ── T-DEL-ED2: Scope mismatch → rule not applied ─────────────────────────────

    /// <summary>
    /// T-DEL-ED2: A rule scoped to a different DefinitionCode must not apply.
    /// The base approver A must receive the task directly.
    /// </summary>
    [TestMethod]
    public async Task DelEd2_ScopeMismatch_RuleNotApplied()
    {
        const string A = "alice"; const string B = "bob";
        const string WorkflowCode = "PurchaseOrder";
        const string OtherCode    = "LeaveRequest";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        // Rule scoped to OtherCode — not applicable when the graph's key is WorkflowCode.
        ctx.Set<DelegationRule>().Add(MakeRule(A, B, scope: OtherCode));
        await ctx.SaveChangesAsync();

        var version  = await SeedVersionAsync(ctx,
            DelTestGraphs.SingleApprover(A, definitionCode: WorkflowCode));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var tasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        Assert.AreEqual(1, tasks.Count,
            $"One task expected (A, scope-mismatched rule not applied). Got {tasks.Count}.");
        Assert.AreEqual(A, tasks[0].AssigneeITCode,
            $"Task must go to A (scope-mismatched rule must be ignored). Got '{tasks[0].AssigneeITCode}'.");
        Assert.IsNull(tasks[0].DelegationRuleId,
            "DelegationRuleId must be null for a non-delegated task.");
    }

    // ── T-DEL-ED3: Deterministic pick on overlapping rules ───────────────────────

    /// <summary>
    /// T-DEL-ED3: Two overlapping active rules for the same (Principal, no scope).
    /// The decorator must deterministically pick the earliest StartUtc then lowest ID.
    /// A warning is logged (but we verify behavior, not log output).
    /// Only one task must be minted (for the delegatee of the winning rule).
    /// </summary>
    [TestMethod]
    public async Task DelEd3_OverlappingRules_DeterministicPick()
    {
        const string A  = "alice";
        const string B1 = "bob-early";   // from earlier-StartUtc rule (should win)
        const string B2 = "bob-later";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var earlierStart = DateTime.UtcNow.AddHours(-5);
        var laterStart   = DateTime.UtcNow.AddHours(-1);

        // Two rules for A, both active, different delegatees.
        // Earlier StartUtc → B1 must win.
        var ruleEarly = MakeRule(A, B1, start: earlierStart);
        var ruleLater = MakeRule(A, B2, start: laterStart);

        // Insert in reverse order to prove ordering is by StartUtc, not insert order.
        ctx.Set<DelegationRule>().AddRange(ruleLater, ruleEarly);
        await ctx.SaveChangesAsync();

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var tasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        Assert.AreEqual(1, tasks.Count,
            $"Exactly one task expected (deterministic pick). Got {tasks.Count}.");
        Assert.AreEqual(B1, tasks[0].AssigneeITCode,
            $"Task must go to B1 (earliest StartUtc wins). Got '{tasks[0].AssigneeITCode}'.");
    }

    // ── T-DEL-NOOP: No rules → base approver unchanged ───────────────────────────

    /// <summary>
    /// Backward-compatibility: when no delegation rules exist at all, the decorator
    /// must pass through the inner resolver's result unchanged.
    /// TotalRequired == 1, task assigned to the original base approver.
    /// </summary>
    [TestMethod]
    public async Task DelNoop_NoDelegationRules_BaseApproverUnchanged()
    {
        const string A = "alice";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        // No rules inserted.

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var tasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        Assert.AreEqual(1, tasks.Count,
            "No-op: exactly one task expected (no delegation). Got " + tasks.Count + ".");
        Assert.AreEqual(A, tasks[0].AssigneeITCode,
            $"Task must go to A (no delegation rules). Got '{tasks[0].AssigneeITCode}'.");
        Assert.IsNull(tasks[0].DelegationRuleId,
            "DelegationRuleId must be null (no delegation).");
    }

    // ── T-DEL-F3a: AtAction expired window → Reject blocked ──────────────────────

    /// <summary>
    /// FIX-3: In AtAction mode, a delegatee whose delegation window has expired must not
    /// be able to Reject the task.  The engine must return DelegationExpired and leave
    /// the task in Pending so a revoke sweep or manual reassignment can handle it.
    ///
    /// Setup: start a workflow with delegation A→B (rule window initially valid so the
    /// task is minted for B).  After minting, retroactively expire the task's
    /// DelegationExpiresUtc in the DB to simulate time passing beyond the window.
    /// Calling RejectTaskAsync(AtAction mode) must return DelegationExpired; task stays Pending.
    /// </summary>
    [TestMethod]
    public async Task DelF3a_AtAction_ExpiredWindow_Reject_ReturnsDelegationExpired_TaskStaysPending()
    {
        const string A = "alice"; // base approver (principal)
        const string B = "bob";   // delegatee

        // AtAction mode: window is re-checked at claim time.
        var opts = new WorkFlowOptions { DelegationWindowMode = DelegationWindowMode.AtAction };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        // Seed a rule that is currently valid (so StartAsync mints the task for B).
        var rule = MakeRule(A, B, end: DateTime.UtcNow.AddHours(1));
        ctx.Set<DelegationRule>().Add(rule);
        await ctx.SaveChangesAsync();

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State, "Instance must be Running.");

        // Find the task minted for the delegatee B.
        await using var readCtx = MakeContext();
        var nodeInst = await readCtx.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var task = await readCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID);

        Assert.AreEqual(B, task.AssigneeITCode,
            $"Task must be for delegatee B. Got '{task.AssigneeITCode}'.");
        Assert.IsNotNull(task.DelegationExpiresUtc,
            "DelegationExpiresUtc must be set (AtAction requires it to be stamped).");

        // Retroactively expire the delegation window in the DB to simulate time passing.
        await readCtx.Set<ApprovalTask>()
            .Where(t => t.ID == task.ID)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DelegationExpiresUtc,
                DateTime.UtcNow.AddHours(-1)));

        // Act: delegatee B tries to Reject with AtAction mode active.
        var result = await engine.RejectTaskAsync(task.ID, B, reason: "FIX-3 expired test");

        // Assert: expired window → DelegationExpired; task stays Pending.
        Assert.AreEqual(WorkflowActionCode.DelegationExpired, result.Code,
            $"FIX-3: AtAction expired delegatee Reject must return DelegationExpired. Got {result.Code}.");

        await using var assertCtx = MakeContext();
        var refreshed = await assertCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == task.ID);

        Assert.AreEqual(TaskState.Pending, refreshed.State,
            "FIX-3: Task must remain Pending after DelegationExpired — no state mutation.");
    }

    // ── T-DEL-F3b: AtAction expired window → ReturnToInitiator blocked ───────────

    /// <summary>
    /// FIX-3: In AtAction mode, a delegatee whose delegation window has expired must not
    /// be able to trigger ReturnToInitiator.  The engine must return DelegationExpired
    /// and leave the task in Pending.
    ///
    /// Same setup as T-DEL-F3a but calls ReturnToInitiatorAsync instead.
    /// </summary>
    [TestMethod]
    public async Task DelF3b_AtAction_ExpiredWindow_ReturnToInitiator_ReturnsDelegationExpired_TaskStaysPending()
    {
        const string A = "alice"; // base approver (principal)
        const string B = "bob";   // delegatee

        // AtAction mode: window is re-checked at claim time.
        var opts = new WorkFlowOptions { DelegationWindowMode = DelegationWindowMode.AtAction };
        var (engine, ctx) = MakeEngine(opts);
        await using var _ = ctx;

        // Seed a rule that is currently valid (so StartAsync mints the task for B).
        var rule = MakeRule(A, B, end: DateTime.UtcNow.AddHours(1));
        ctx.Set<DelegationRule>().Add(rule);
        await ctx.SaveChangesAsync();

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State, "Instance must be Running.");

        // Find the task minted for the delegatee B.
        await using var readCtx = MakeContext();
        var nodeInst = await readCtx.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var task = await readCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID);

        Assert.AreEqual(B, task.AssigneeITCode,
            $"Task must be for delegatee B. Got '{task.AssigneeITCode}'.");
        Assert.IsNotNull(task.DelegationExpiresUtc,
            "DelegationExpiresUtc must be set (AtAction requires it to be stamped).");

        // Retroactively expire the delegation window in the DB to simulate time passing.
        await readCtx.Set<ApprovalTask>()
            .Where(t => t.ID == task.ID)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DelegationExpiresUtc,
                DateTime.UtcNow.AddHours(-1)));

        // Act: delegatee B tries to return-to-initiator with AtAction mode active.
        var result = await engine.ReturnToInitiatorAsync(task.ID, B, reason: "FIX-3 expired test");

        // Assert: expired window → DelegationExpired; task stays Pending.
        Assert.AreEqual(WorkflowActionCode.DelegationExpired, result.Code,
            $"FIX-3: AtAction expired delegatee ReturnToInitiator must return DelegationExpired. Got {result.Code}.");

        await using var assertCtx = MakeContext();
        var refreshed = await assertCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == task.ID);

        Assert.AreEqual(TaskState.Pending, refreshed.State,
            "FIX-3: Task must remain Pending after DelegationExpired — no state mutation.");
    }

    // ── T-DEL-F4: Any-mode tasks carry correct Generation + delegation provenance ─

    /// <summary>
    /// FIX-4: AnyApprovalHandler must stamp Generation = nodeInst.Generation and
    /// delegation provenance on minted tasks, mirroring AllApprovalHandler.
    ///
    /// Setup: single base approver A delegated to B via a valid rule.
    /// Start a workflow with ApproveMode.Any.  Verify the minted task:
    ///   • AssigneeITCode == B (delegatee)
    ///   • Generation == nodeInst.Generation (not 0 if default)
    ///   • DelegatedFromITCode == A (original principal)
    ///   • DelegationRuleId != null
    ///   • DelegationExpiresUtc != null
    ///
    /// Generation walk finding: before FIX-4, Any-mode minted tasks had Generation=0.
    /// After a 回退 (old tasks explicitly Cancelled by DiscardTasksForReturnAsync), the
    /// Cancelled state prevented stale CAS claims — the Cancelled fence was present.
    /// However, Generation=0 stamping was still load-bearing for the unique index
    /// IX_Wf_ApprovalTask_Node_Assignee_Gen: inserting a second task for the same
    /// approver on the same node post-回退 would violate the index (both Generation=0).
    /// With FIX-4, tasks carry Generation=gNew (gOld+1 after 回退), so the index allows
    /// both old (Cancelled, gen=0) and new (Pending, gen=1) rows to coexist.
    /// </summary>
    [TestMethod]
    public async Task DelF4_AnyMode_MintedTasks_CarryGeneration_And_DelegationProvenance()
    {
        const string A = "alice"; // base approver (principal)
        const string B = "bob";   // delegatee

        var (engine, ctx) = MakeEngineAllModes();
        await using var _ = ctx;

        // Seed delegation rule A→B.
        var rule = MakeRule(A, B);
        ctx.Set<DelegationRule>().Add(rule);
        await ctx.SaveChangesAsync();

        // Use MultiApproverAny so AnyApprovalHandler is used.
        var version  = await SeedVersionAsync(ctx, DelTestGraphs.MultiApproverAny(A));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        Assert.AreEqual(InstanceState.Running, instance.State, "Instance must be Running.");

        await using var verify = MakeContext();
        var nodeInst = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var tasks = await verify.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        Assert.AreEqual(1, tasks.Count,
            $"One task expected (A delegated to B, single slot). Got {tasks.Count}.");

        var task = tasks.Single();

        // FIX-4 assertions: Generation stamping.
        Assert.AreEqual(nodeInst.Generation, task.Generation,
            $"FIX-4: Task Generation must equal nodeInst.Generation ({nodeInst.Generation}). Got {task.Generation}.");

        // FIX-4 assertions: delegation provenance.
        Assert.AreEqual(B, task.AssigneeITCode,
            $"Task must be assigned to delegatee B. Got '{task.AssigneeITCode}'.");
        Assert.AreEqual(A, task.DelegatedFromITCode,
            $"FIX-4: DelegatedFromITCode must be original principal A. Got '{task.DelegatedFromITCode}'.");
        Assert.AreEqual(rule.ID, task.DelegationRuleId,
            $"FIX-4: DelegationRuleId must equal the rule ID. Got '{task.DelegationRuleId}'.");
        Assert.IsNotNull(task.DelegationExpiresUtc,
            "FIX-4: DelegationExpiresUtc must be stamped from the rule's EndUtc.");
    }

    // ── T-DEL-F5: post-回退 DefinitionCode preserved on fresh NodeInstance ─────────

    /// <summary>
    /// FIX-5: ExecuteReturnToNodeAsync STEP-5 must stamp DefinitionCode = graph.Key on the
    /// fresh NodeInstance minted at the return target.  Without it, the DelegationResolvingDecorator
    /// would see DefinitionCode=null on the re-entered node and silently treat all scoped rules
    /// as "global-only" — scope-restricted delegation rules for that node would be ignored.
    ///
    /// Setup:
    ///   - Two-node workflow (approval1 → approval2); initiator starts.
    ///   - approver1 approves approval1 → approval2 node activated with task for approver2.
    ///   - approver2 calls ReturnToNodeAsync(taskId, "approval1") → engine executes 回退.
    ///   - After 回退, a fresh NodeInstance is minted at approval1 with Generation=1.
    ///   - Verify the fresh NodeInstance at approval1 carries DefinitionCode == graph.Key.
    /// </summary>
    [TestMethod]
    public async Task DelF5_ReturnToNode_FreshNodeInstance_HasDefinitionCode()
    {
        const string Approver1 = "alice";
        const string Approver2 = "bob";
        const string DefinitionCode = "ReturnTestDef";

        var (engine, ctx) = MakeEngineAllModes();
        await using var _ = ctx;

        // Use a two-node graph: approval1 → approval2, both Sequential.
        var version = await SeedVersionAsync(
            ctx, DelTestGraphs.TwoApprovalNodesReturnEnabled(Approver1, Approver2, DefinitionCode));

        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State, "Instance must be Running at approval1.");

        // Find and approve the approval1 task to advance to approval2.
        await using var readCtx1 = MakeContext();
        var node1 = await readCtx1.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID
                               && n.NodeKind == NodeKind.Approval
                               && n.State == NodeState.Activated);
        Assert.AreEqual("approval1", node1.NodeKey, "First activated node must be approval1.");

        var task1 = await readCtx1.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == node1.ID && t.State == TaskState.Pending);

        var approveResult = await engine.ApproveTaskAsync(task1.ID, Approver1, "FIX-5 approve step1");
        // When approval1 (single-slot Sequential) completes and approval2 is minted and waiting
        // for a human approver, the engine returns Blocked (approval2 is awaiting human action).
        // This is correct: NodeCompleted on approval1 → Blocked at approval2.
        Assert.AreEqual(WorkflowActionCode.Blocked, approveResult.Code,
            $"Approve approval1 (single sequential) should return Blocked (approval2 awaits human). Got {approveResult.Code}.");

        // Now find the approval2 task.
        await using var readCtx2 = MakeContext();
        var node2 = await readCtx2.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID
                               && n.NodeKind == NodeKind.Approval
                               && n.State == NodeState.Activated);
        Assert.AreEqual("approval2", node2.NodeKey, "Second activated node must be approval2.");

        var task2 = await readCtx2.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == node2.ID && t.State == TaskState.Pending);

        // Approver2 triggers ReturnToNode(approval1) to force a 回退.
        var returnResult = await engine.ReturnToNodeAsync(task2.ID, "approval1", Approver2, "FIX-5 return test");
        Assert.AreEqual(WorkflowActionCode.Returned, returnResult.Code,
            $"ReturnToNode must return Returned. Got {returnResult.Code}.");

        // After 回退, instance should be Running with a fresh approval1 NodeInstance (Generation=1).
        await using var assertCtx = MakeContext();
        var freshNode = await assertCtx.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID
                               && n.NodeKey == "approval1"
                               && n.Generation == 1);

        // FIX-5: DefinitionCode must be preserved on the fresh NodeInstance.
        Assert.AreEqual(DefinitionCode, freshNode.DefinitionCode,
            $"FIX-5: Fresh NodeInstance at approval1 after 回退 must have DefinitionCode='{DefinitionCode}'. " +
            $"Got '{freshNode.DefinitionCode}'.");
    }

    // ── T-DEL-F7: RevokeDelegatedTasksAsync also revokes NotYetActive/AddedPending ─

    /// <summary>
    /// FIX-7: RevokeDelegatedTasksAsync must include NotYetActive tasks (Sequential
    /// future-step slots) in the revocation sweep, not just Pending tasks.
    ///
    /// Setup:
    ///   - Multi-approver Sequential workflow: base approvers = [A, B].  A is delegated to X.
    ///   - Start workflow → Sequential handler mints slot0 (Pending) for X (delegated from A)
    ///     and slot1 (NotYetActive) for B.  But because A is delegated to X via rule R,
    ///     slot0 gets AssigneeITCode=X and DelegationRuleId=R.ID.
    ///   - If we also seed a delegation for B→X in rule R (so slot1 also gets DelegationRuleId=R),
    ///     then RevokeDelegatedTasksAsync(R.ID) must cancel BOTH slot0 (Pending) and slot1 (NotYetActive).
    ///
    /// Test: verify that after revoke, both tasks are reverted to their original principals
    ///       (AssigneeITCode reverted; DelegationRuleId cleared).
    /// </summary>
    [TestMethod]
    public async Task DelF7_RevokeDelegatedTasksAsync_IncludesNotYetActiveTasks()
    {
        // Scenario:
        //   - Two-slot Sequential workflow: base approvers A, B.
        //   - Only B is delegated to X via ruleForB (A has no delegation rule → slot0 stays as A).
        //   - Workflow starts → slot0 (A, Pending) + slot1 (X delegated from B, NotYetActive).
        //   - RevokeDelegationAsync(ruleForB) must revert slot1 from X back to B.
        //   - FIX-7: without the fix, slot1 (NotYetActive) would be skipped; with FIX-7 it is reverted.
        const string A = "alice";   // base approver slot0 — NO delegation rule (stays as A)
        const string B = "bob";     // base approver slot1 — delegated to X
        const string X = "xavier";  // delegatee for B only

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        // Only seed B→X; A has no rule and stays as A (slot0 Pending).
        var ruleForB = MakeRule(B, X, scope: null);
        ctx.Set<DelegationRule>().Add(ruleForB);
        await ctx.SaveChangesAsync();

        // Multi-approver Sequential: A (direct, slot0), B→X delegated (slot1 NotYetActive).
        var version  = await SeedVersionAsync(ctx, DelTestGraphs.MultiApprover($"{A},{B}"));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State);

        // Verify the slots.
        await using var verifyCtx = MakeContext();
        var nodeInst = await verifyCtx.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        var tasks = await verifyCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .OrderBy(t => t.SequenceOrder)
            .Where(t => t.NodeInstanceId == nodeInst.ID)
            .ToListAsync();

        Assert.AreEqual(2, tasks.Count, $"Two tasks expected (A + B→X). Got {tasks.Count}.");

        // Slot0: A, Pending (no delegation).
        var slot0 = tasks.First(t => t.SequenceOrder == 0);
        Assert.AreEqual(TaskState.Pending, slot0.State,      "Slot0 must be Pending.");
        Assert.AreEqual(A, slot0.AssigneeITCode,             "Slot0 must be A (no delegation).");
        Assert.IsNull(slot0.DelegationRuleId,                "Slot0 must not have a DelegationRuleId.");

        // Slot1: X (delegated from B), NotYetActive.
        var slot1 = tasks.First(t => t.SequenceOrder == 1);
        Assert.AreEqual(TaskState.NotYetActive, slot1.State, "Slot1 must be NotYetActive.");
        Assert.AreEqual(X, slot1.AssigneeITCode,             "Slot1 must be assigned to X (delegated from B).");
        Assert.AreEqual(ruleForB.ID, slot1.DelegationRuleId, "Slot1 DelegationRuleId must be ruleForB.");

        // Revoke ruleForB — FIX-7: this must reach the NotYetActive slot.
        await engine.RevokeDelegationAsync(ruleForB.ID, "admin");

        // After revoke: slot1 must be reverted to B.
        await using var assertCtx = MakeContext();
        var slot1After = await assertCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == slot1.ID);

        Assert.AreEqual(TaskState.NotYetActive, slot1After.State,
            "FIX-7: NotYetActive slot must remain NotYetActive after revoke (state preserved; assignee reverted).");
        Assert.AreEqual(B, slot1After.AssigneeITCode,
            $"FIX-7: Slot1 AssigneeITCode must be reverted to principal B. Got '{slot1After.AssigneeITCode}'.");
        Assert.IsNull(slot1After.DelegationRuleId,
            "FIX-7: DelegationRuleId must be cleared after revoke.");
        Assert.IsNull(slot1After.DelegatedFromITCode,
            "FIX-7: DelegatedFromITCode must be cleared after revoke.");

        // Slot0 (A) must be untouched.
        var slot0After = await assertCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == slot0.ID);
        Assert.AreEqual(TaskState.Pending, slot0After.State, "Slot0 must remain Pending.");
        Assert.AreEqual(A, slot0After.AssigneeITCode,        "Slot0 assignee must remain A.");
    }

    // ── T-DEL-F8: AtAction + Oracle/DaMeng → InvalidOperationException at startup ─

    /// <summary>
    /// FIX-8: WorkflowEngine production constructor must throw InvalidOperationException
    /// when DelegationWindowMode == AtAction AND DBTypeEnum is Oracle or DaMeng.
    ///
    /// Rationale: ClaimDelegatedTaskAsync folds a nullable-DateTime comparison into
    /// ExecuteUpdateAsync's WHERE clause.  Oracle and DaMeng EF Core providers do not
    /// reliably translate this predicate (RETURNING-clause semantics differ; DaMeng
    /// provider has incomplete nullable DateTime support).
    ///
    /// The guard mirrors the existing DBTypeEnum.Memory guard (ValidateDbType) but fires
    /// eagerly in the constructor for the AtAction+Oracle/DaMeng combo.
    ///
    /// Uses <see cref="MinimalIDataContextDbContext"/> (defined in MemoryGuardTests.cs) because
    /// the production WorkflowEngine constructor casts IDataContext → DbContext; a plain Moq mock
    /// cannot be cast to DbContext and would throw InvalidCastException before the guard fires.
    /// </summary>
    [TestMethod]
    public void DelF8_AtAction_Oracle_ThrowsAtConstruction()
    {
        foreach (var badProvider in new[] { DBTypeEnum.Oracle, DBTypeEnum.DaMeng })
        {
            // MinimalIDataContextDbContext (defined in MemoryGuardTests.cs) is a DbContext
            // subclass that also implements IDataContext.  It satisfies the production constructor
            // cast `_db = (DbContext)dc` while also providing DBType.
            // The SQLite backing store is irrelevant — the guard throws before any DB access.
            var dbName = $"WfF8_{badProvider}_{Guid.NewGuid():N}";
            using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(
                $"DataSource={dbName}?mode=memory&cache=shared");
            keepAlive.Open();

            var dc      = new MinimalIDataContextDbContext(dbName, badProvider);
            var opts    = new WorkFlowOptions { DelegationWindowMode = DelegationWindowMode.AtAction };
            var optWrap = Options.Create(opts);
            var dispatcher     = NodeKindDispatcher_Exposed.CreateWithSequential(
                new DefaultApproverResolverExposed(opts, dc), opts);
            var routingEval    = new WalkingTec.Mvvm.WorkFlow.Engine.Routing.WhitelistRoutingEvaluator(
                NullLogger<WalkingTec.Mvvm.WorkFlow.Engine.Routing.WhitelistRoutingEvaluator>.Instance);
            var engineLogger   = NullLogger<WorkflowEngine>.Instance;

            // Call the PRODUCTION constructor (IDataContext path) directly.
            // InternalsVisibleTo allows this from the test project.
            var ex = Assert.ThrowsException<InvalidOperationException>(
                () => new WorkflowEngine(dc, dispatcher, routingEval, optWrap, engineLogger),
                $"FIX-8: WorkflowEngine production constructor with AtAction + {badProvider} must throw.");

            StringAssert.Contains(ex.Message, badProvider.ToString(),
                $"FIX-8: Exception message must mention '{badProvider}'. Got: {ex.Message}");
            StringAssert.Contains(ex.Message, "AtAction",
                $"FIX-8: Exception message must mention 'AtAction'. Got: {ex.Message}");
        }
    }

    [TestMethod]
    public void DelF8_AtAction_SQLite_DoesNotThrow()
    {
        // AtAction with SQLite should construct cleanly (no guard).
        var dbName = $"WfF8SQLite_{Guid.NewGuid():N}";
        using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(
            $"DataSource={dbName}?mode=memory&cache=shared");
        keepAlive.Open();

        var dc         = new MinimalIDataContextDbContext(dbName, DBTypeEnum.SQLite);
        var opts       = new WorkFlowOptions { DelegationWindowMode = DelegationWindowMode.AtAction };
        var optWrap    = Options.Create(opts);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(
            new DefaultApproverResolverExposed(opts, dc), opts);
        var routingEval  = new WalkingTec.Mvvm.WorkFlow.Engine.Routing.WhitelistRoutingEvaluator(
            NullLogger<WalkingTec.Mvvm.WorkFlow.Engine.Routing.WhitelistRoutingEvaluator>.Instance);
        var engineLogger = NullLogger<WorkflowEngine>.Instance;

        // Should not throw — SQLite is supported; guard must NOT fire for SQLite.
        new WorkflowEngine(dc, dispatcher, routingEval, optWrap, engineLogger);
    }

    // ── T-DEL-F9: DelegateTaskAsync engine-level tests ────────────────────────────

    /// <summary>
    /// FIX-9a: DelegateTaskAsync returns NotAuthorized when actor is not the task assignee.
    /// </summary>
    [TestMethod]
    public async Task DelF9a_DelegateTaskAsync_NonAssignee_ReturnsNotAuthorized()
    {
        const string Assignee    = "alice";
        const string NonAssignee = "charlie";
        const string Delegatee   = "dave";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(Assignee));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        await using var readCtx = MakeContext();
        var task = await readCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.State == TaskState.Pending);

        // charlie tries to delegate alice's task.
        var result = await engine.DelegateTaskAsync(task.ID, NonAssignee, Delegatee);
        Assert.AreEqual(WorkflowActionCode.NotAuthorized, result.Code,
            $"FIX-9a: Non-assignee delegation must return NotAuthorized. Got {result.Code}.");
    }

    /// <summary>
    /// FIX-9b: DelegateTaskAsync returns TaskNotActive when task is not in Pending state.
    /// </summary>
    [TestMethod]
    public async Task DelF9b_DelegateTaskAsync_TaskNotPending_ReturnsTaskNotActive()
    {
        const string Assignee  = "alice";
        const string Delegatee = "bob";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(Assignee));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        await using var readCtx = MakeContext();
        var task = await readCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.State == TaskState.Pending);

        // First approve the task so it moves to Approved state.
        var approveResult = await engine.ApproveTaskAsync(task.ID, Assignee, "done");
        Assert.IsTrue(approveResult.Code == WorkflowActionCode.Advanced
                      || approveResult.Code == WorkflowActionCode.InstanceApproved,
            $"Approve should advance or complete. Got {approveResult.Code}.");

        // Now try to delegate the already-approved task.
        var result = await engine.DelegateTaskAsync(task.ID, Assignee, Delegatee);
        Assert.AreEqual(WorkflowActionCode.TaskNotActive, result.Code,
            $"FIX-9b: Delegating an already-approved task must return TaskNotActive. Got {result.Code}.");
    }

    /// <summary>
    /// FIX-9c: DelegateTaskAsync happy path — epoch bumped, event log row written, TotalRequired unchanged.
    /// </summary>
    [TestMethod]
    public async Task DelF9c_DelegateTaskAsync_HappyPath_EpochBumped_EventLogWritten_TotalRequiredUnchanged()
    {
        const string Assignee  = "alice";
        const string Delegatee = "bob";

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version  = await SeedVersionAsync(ctx, DelTestGraphs.SingleApprover(Assignee));
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        await using var readCtx = MakeContext();
        var nodeInstBefore = await readCtx.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);
        var taskBefore = await readCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInstBefore.ID && t.State == TaskState.Pending);

        var epochBefore        = nodeInstBefore.ApproverSetEpoch;
        var totalRequiredBefore = nodeInstBefore.TotalRequired;

        // Delegate Alice's task to Bob.
        var result = await engine.DelegateTaskAsync(taskBefore.ID, Assignee, Delegatee);
        Assert.AreEqual(WorkflowActionCode.Advanced, result.Code,
            $"FIX-9c: DelegateTaskAsync happy path must return Advanced. Got {result.Code}.");

        await using var assertCtx = MakeContext();

        // Task must now be assigned to Bob.
        var taskAfter = await assertCtx.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == taskBefore.ID);
        Assert.AreEqual(Delegatee, taskAfter.AssigneeITCode,
            $"FIX-9c: Task must be reassigned to '{Delegatee}'. Got '{taskAfter.AssigneeITCode}'.");
        Assert.AreEqual(Assignee, taskAfter.DelegatedFromITCode,
            $"FIX-9c: DelegatedFromITCode must be original principal '{Assignee}'. Got '{taskAfter.DelegatedFromITCode}'.");

        // ApproverSetEpoch must be bumped.
        var nodeInstAfter = await assertCtx.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInstBefore.ID);
        Assert.IsTrue(nodeInstAfter.ApproverSetEpoch > epochBefore,
            $"FIX-9c: ApproverSetEpoch must be bumped. Before={epochBefore}, After={nodeInstAfter.ApproverSetEpoch}.");

        // TotalRequired must be unchanged (FIX-C: delegate is a slot transfer, not a new slot).
        Assert.AreEqual(totalRequiredBefore, nodeInstAfter.TotalRequired,
            $"FIX-9c: TotalRequired must be unchanged. Before={totalRequiredBefore}, After={nodeInstAfter.TotalRequired}.");

        // Event log must have a Delegate entry.
        var eventLog = await assertCtx.Set<WorkflowEventLog>()
            .AsNoTracking()
            .Where(e => e.InstanceId == instance.ID)
            .OrderByDescending(e => e.Seq)
            .FirstOrDefaultAsync();
        Assert.IsNotNull(eventLog, "FIX-9c: Event log must have at least one row.");
        Assert.AreEqual(EventAction.Delegate, eventLog!.Action,
            $"FIX-9c: Last event log row must have Action=Delegate. Got {eventLog.Action}.");
    }
}
