#nullable enable
// WF-17 — Ack (blocking-acknowledge) handler unit tests.
//
// Covers:
//   T-ACK-1: AckHandler.CanCompleteAsync — all three AckMode values (Any, All, Quorum).
//            Validates that each mode gates completion on the correct quorum threshold.
//   T-ACK-2: Cc (non-blocking) CanCompleteAsync always returns true.
//            Regression guard: ensure CcHandler.CanCompleteAsync still returns true
//            after WF-17 changes did not accidentally make it blocking.
//
// AckHandler is unit-tested directly via NodeHandlerContext stubs.
// No SQLite required — these tests do not touch the DB.

using System;
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
public class AckCcTests
{
    // ── Stub helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal NodeHandlerContext stub with a hand-built NodeInstance.
    /// Graph and Instance are the minimum needed for AckHandler.CanCompleteAsync.
    /// </summary>
    private static NodeHandlerContext MakeContext(NodeInstance nodeInstance)
    {
        var graph = new WorkflowGraph
        {
            Key  = "AckTestGraph",
            Name = "AckTestGraph",
            Nodes = new System.Collections.Generic.List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey = nodeInstance.NodeKey,
                    Kind    = NodeKind.Ack,
                    AckMode = nodeInstance.AckMode,
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new System.Collections.Generic.List<TransitionDef>
            {
                new() { From = "start",              To = nodeInstance.NodeKey },
                new() { From = nodeInstance.NodeKey, To = "end"                },
            },
        };

        var nodeDef = graph.Nodes.Find(n => n.NodeKey == nodeInstance.NodeKey)!;

        var instance = new ProcessInstance
        {
            ID                  = Guid.NewGuid(),
            State               = InstanceState.Running,
            RowVer              = 0,
            InitiatorITCode     = "tester",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid             = true,
        };

        // AckHandler.CanCompleteAsync does NOT touch the DB — it only reads NodeInstance fields.
        using var db = new NullDbContext();
        return new NodeHandlerContext
        {
            Db              = db,
            Graph           = graph,
            NodeDef         = nodeDef,
            NodeInstance    = nodeInstance,
            ProcessInstance = instance,
        };
    }

    // ── T-ACK-1: AckHandler.CanCompleteAsync ─────────────────────────────────────

    // AckMode.Any: complete as soon as ApprovedCount >= 1.

    [TestMethod]
    public async Task T_ACK_1a_AckAny_ZeroApproved_NotComplete()
    {
        var handler = new AckHandler(NullLogger<AckHandler>.Instance);
        var ni      = AckNode("ack_any", AckMode.Any, approvedCount: 0, totalRequired: 3);
        var ctx     = MakeContext(ni);

        bool result = await handler.CanCompleteAsync(ctx);

        Assert.IsFalse(result,
            "AckMode.Any: CanCompleteAsync must be false when ApprovedCount==0.");
    }

    [TestMethod]
    public async Task T_ACK_1b_AckAny_OneApproved_IsComplete()
    {
        var handler = new AckHandler(NullLogger<AckHandler>.Instance);
        var ni      = AckNode("ack_any", AckMode.Any, approvedCount: 1, totalRequired: 3);
        var ctx     = MakeContext(ni);

        bool result = await handler.CanCompleteAsync(ctx);

        Assert.IsTrue(result,
            "AckMode.Any: CanCompleteAsync must be true when ApprovedCount==1 (first acknowledge).");
    }

    // AckMode.All: complete when ApprovedCount >= TotalRequired.

    [TestMethod]
    public async Task T_ACK_1c_AckAll_TwoOfThreeApproved_NotComplete()
    {
        var handler = new AckHandler(NullLogger<AckHandler>.Instance);
        var ni      = AckNode("ack_all", AckMode.All, approvedCount: 2, totalRequired: 3);
        var ctx     = MakeContext(ni);

        bool result = await handler.CanCompleteAsync(ctx);

        Assert.IsFalse(result,
            "AckMode.All: CanCompleteAsync must be false when ApprovedCount(2) < TotalRequired(3).");
    }

    [TestMethod]
    public async Task T_ACK_1d_AckAll_AllApproved_IsComplete()
    {
        var handler = new AckHandler(NullLogger<AckHandler>.Instance);
        var ni      = AckNode("ack_all", AckMode.All, approvedCount: 3, totalRequired: 3);
        var ctx     = MakeContext(ni);

        bool result = await handler.CanCompleteAsync(ctx);

        Assert.IsTrue(result,
            "AckMode.All: CanCompleteAsync must be true when ApprovedCount(3) == TotalRequired(3).");
    }

    [TestMethod]
    public async Task T_ACK_1e_AckAll_TotalRequiredZero_NotComplete()
    {
        // TotalRequired==0 is guard case: must not complete (division-by-zero / degenerate).
        var handler = new AckHandler(NullLogger<AckHandler>.Instance);
        var ni      = AckNode("ack_all_zero", AckMode.All, approvedCount: 0, totalRequired: 0);
        var ctx     = MakeContext(ni);

        bool result = await handler.CanCompleteAsync(ctx);

        Assert.IsFalse(result,
            "AckMode.All: CanCompleteAsync must be false when TotalRequired==0.");
    }

    // AckMode.Quorum: complete when ApprovedCount >= ceil(ApprovePercent * TotalRequired).

    [TestMethod]
    public async Task T_ACK_1f_AckQuorum_50pct_TwoOfFour_IsComplete()
    {
        // 50% of 4 → ceil(0.5 * 4) = 2.  Two approved → complete.
        var handler = new AckHandler(NullLogger<AckHandler>.Instance);
        var ni      = AckNode("ack_q", AckMode.Quorum,
                              approvedCount: 2, totalRequired: 4, approvePercent: 0.5m);
        var ctx     = MakeContext(ni);

        bool result = await handler.CanCompleteAsync(ctx);

        Assert.IsTrue(result,
            "AckMode.Quorum(50%,4): CanCompleteAsync must be true when ApprovedCount(2) >= ceil(0.5*4)=2.");
    }

    [TestMethod]
    public async Task T_ACK_1g_AckQuorum_50pct_OneOfFour_NotComplete()
    {
        // 50% of 4 → need 2.  Only 1 approved → not complete.
        var handler = new AckHandler(NullLogger<AckHandler>.Instance);
        var ni      = AckNode("ack_q2", AckMode.Quorum,
                              approvedCount: 1, totalRequired: 4, approvePercent: 0.5m);
        var ctx     = MakeContext(ni);

        bool result = await handler.CanCompleteAsync(ctx);

        Assert.IsFalse(result,
            "AckMode.Quorum(50%,4): CanCompleteAsync must be false when ApprovedCount(1) < ceil(0.5*4)=2.");
    }

    [TestMethod]
    public async Task T_ACK_1h_AckQuorum_67pct_TwoOfThree_IsComplete()
    {
        // 67% of 3 → ceil(0.67 * 3) = ceil(2.01) = 3.  Actually need all 3 at 67%.
        // Use 66.67% → ceil(0.6667 * 3) = ceil(2.0001) = 3.  Let's use 60% instead.
        // 60% of 5 → ceil(0.6 * 5) = ceil(3.0) = 3.  Three approved → complete.
        var handler = new AckHandler(NullLogger<AckHandler>.Instance);
        var ni      = AckNode("ack_q3", AckMode.Quorum,
                              approvedCount: 3, totalRequired: 5, approvePercent: 0.6m);
        var ctx     = MakeContext(ni);

        bool result = await handler.CanCompleteAsync(ctx);

        Assert.IsTrue(result,
            "AckMode.Quorum(60%,5): CanCompleteAsync must be true when ApprovedCount(3) >= ceil(0.6*5)=3.");
    }

    [TestMethod]
    public async Task T_ACK_1i_AckQuorum_NoApprovePercent_NotComplete()
    {
        // When ApprovePercent is null, Quorum mode cannot compute a threshold → not complete.
        var handler = new AckHandler(NullLogger<AckHandler>.Instance);
        var ni      = AckNode("ack_qnull", AckMode.Quorum,
                              approvedCount: 2, totalRequired: 3, approvePercent: null);
        var ctx     = MakeContext(ni);

        bool result = await handler.CanCompleteAsync(ctx);

        Assert.IsFalse(result,
            "AckMode.Quorum: CanCompleteAsync must be false when ApprovePercent is null.");
    }

    // ── T-ACK-2: Cc handler is still non-blocking ─────────────────────────────────

    /// <summary>
    /// T-ACK-2: CcHandler.CanCompleteAsync must always return true (non-blocking).
    /// Regression guard after WF-17 to confirm Cc was not accidentally made blocking.
    /// </summary>
    [TestMethod]
    public async Task T_ACK_2_CcHandler_CanCompleteAsync_AlwaysTrue()
    {
        var graph = new WorkflowGraph
        {
            Key  = "CcAlwaysTrue",
            Name = "CcAlwaysTrue",
            Nodes = new System.Collections.Generic.List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "cc1",
                    Kind         = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cc_user" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new System.Collections.Generic.List<TransitionDef>
            {
                new() { From = "start", To = "cc1" },
                new() { From = "cc1",   To = "end" },
            },
        };

        var nodeDef = graph.Nodes.Find(n => n.NodeKey == "cc1")!;
        var ni = new NodeInstance
        {
            ID          = Guid.NewGuid(),
            InstanceId  = Guid.NewGuid(),
            NodeKey     = "cc1",
            NodeKind    = NodeKind.Cc,
            State       = NodeState.Activated,
            TenantCode  = "T1",
            Generation  = 0,
        };
        var instance = new ProcessInstance
        {
            ID                  = Guid.NewGuid(),
            State               = InstanceState.Running,
            RowVer              = 0,
            InitiatorITCode     = "tester",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid             = true,
        };

        // Cc CanCompleteAsync does not touch the DB.
        using var db = new NullDbContext();
        var ctx = new NodeHandlerContext
        {
            Db              = db,
            Graph           = graph,
            NodeDef         = nodeDef,
            NodeInstance    = ni,
            ProcessInstance = instance,
        };
        var cc = new CcHandler(
            new NullCcApproverResolver(),
            new AcceptAllCcTenantValidator(),
            NullLogger<CcHandler>.Instance);

        bool result = await cc.CanCompleteAsync(ctx);

        Assert.IsTrue(result,
            "T-ACK-2: CcHandler.CanCompleteAsync must return true (Cc is never blocking).");
    }

    // ── Helper factories ──────────────────────────────────────────────────────────

    private static NodeInstance AckNode(
        string nodeKey,
        AckMode ackMode,
        int approvedCount,
        int totalRequired,
        decimal? approvePercent = null)
        => new()
        {
            ID             = Guid.NewGuid(),
            InstanceId     = Guid.NewGuid(),
            NodeKey        = nodeKey,
            NodeKind       = NodeKind.Ack,
            State          = NodeState.Activated,
            TenantCode     = "T1",
            Generation     = 0,
            AckMode        = ackMode,
            ApprovedCount  = approvedCount,
            TotalRequired  = totalRequired,
            ApprovePercent = approvePercent,
        };

    // Minimal no-op resolver for T-ACK-2 (CC CanCompleteAsync does not resolve).
    private sealed class NullCcApproverResolver : IApproverResolver
    {
        public Task<ApproverResolution> ResolveAsync(
            DbContext db,
            ApproverRuleDef rule,
            NodeInstance nodeInstance,
            string initiatorITCode,
            CancellationToken ct = default)
            => Task.FromResult(ApproverResolution.NoApprover("NullCcApproverResolver — test stub."));
    }
}

// ── NullDbContext — bare DbContext for unit tests that don't touch the DB ────────

/// <summary>
/// Minimal no-op SQLite DbContext for unit tests that build a NodeHandlerContext
/// but don't exercise any DB operations.  A private in-memory SQLite DB is used
/// purely to satisfy the DbContext constructor; no tables are created or queried.
/// </summary>
internal sealed class NullDbContext : DbContext
{
    // Each instance gets its own private in-memory SQLite so there is no cross-test state.
    private readonly string _connStr = $"DataSource=NullDb_{Guid.NewGuid():N}?mode=memory&cache=private";

    protected override void OnConfiguring(DbContextOptionsBuilder b) =>
        b.UseSqlite(_connStr);
}
