#nullable enable
// #483 — gateway hardening tests.
//
// T_GW_1: MintNodeInstanceGuardedAsync is idempotent — second call for same (InstanceId, NodeKey, Generation) returns false.
// T_GW_2: ParallelGateway mints Join with JoinExpectedArrivals set atomically (non-zero after engine StartAsync).
// T_GW_3: Validator rejects ParallelGateway/InclusiveGateway with zero outgoing transitions.

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
public class Wf483GatewayHardeningTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"Wf483_{Guid.NewGuid():N}";
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

    private (IWorkflowEngine engine, WfEngineTestContext ctx) MakeEngine()
    {
        var ctx = MakeContext();
        var dispatcher = NodeKindDispatcher_Exposed.Create();
        var engine = WorkflowEngine_Exposed.Create(ctx, dispatcher, NullLogger.Instance);
        return (engine, ctx);
    }

    // ── T_GW_1: MintNodeInstanceGuardedAsync idempotency ──────────────────────────

    /// <summary>
    /// T_GW_1: GuardedTransition.MintNodeInstanceGuardedAsync returns false (no-op)
    /// when called a second time with the same (InstanceId, NodeKey, Generation).
    /// This validates the DB-layer idempotency contract that the #483 branch-mint
    /// catch path relies on.
    /// </summary>
    [TestMethod]
    public async Task T_GW_1_MintNodeInstance_Idempotent_SecondCallReturnsFalse()
    {
        await using var db = MakeContext();

        // Seed a ProcessInstance (FK requirement).
        var inst = new ProcessInstance
        {
            ID = Guid.NewGuid(),
            State = InstanceState.Running,
            RowVer = 0,
            InitiatorITCode = "u1",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid = true,
            TenantCode = "T1",
            Generation = 0,
            NextSeq = 1,
        };
        db.Set<ProcessInstance>().Add(inst);
        await db.SaveChangesAsync();

        var nodeDef = new NodeDef { NodeKey = "branchA", Kind = NodeKind.Approval };

        // First mint — must succeed (returns true).
        bool first = await GuardedTransition.MintNodeInstanceGuardedAsync(
            db, inst, nodeDef, generation: 0, ct: CancellationToken.None);
        Assert.IsTrue(first, "T_GW_1: first mint must succeed (returns true).");

        // Second mint — same (InstanceId, NodeKey, Generation) — must be idempotent (returns false).
        await using var db2 = MakeContext();
        bool second = await GuardedTransition.MintNodeInstanceGuardedAsync(
            db2, inst, nodeDef, generation: 0, ct: CancellationToken.None);
        Assert.IsFalse(second, "T_GW_1: second mint for same (InstanceId, NodeKey, Generation) must return false (idempotent no-op).");

        // Exactly ONE NodeInstance must exist in the DB.
        await using var verify = MakeContext();
        int count = await verify.Set<NodeInstance>()
            .CountAsync(n => n.InstanceId == inst.ID && n.NodeKey == "branchA");
        Assert.AreEqual(1, count, "T_GW_1: exactly one NodeInstance must exist after idempotent double-mint.");
    }

    // ── T_GW_2: JoinExpectedArrivals set atomically ───────────────────────────────

    /// <summary>
    /// T_GW_2: After ParallelGateway StartAsync, the Join NodeInstance must have
    /// JoinExpectedArrivals > 0 set in the same commit as the mint (#483 Bug #8).
    ///
    /// Uses the same AndFork3BranchGraph() as T-JOIN-5 but focuses on the atomicity invariant.
    /// </summary>
    [TestMethod]
    public async Task T_GW_2_ParallelGateway_JoinExpectedArrivals_SetAtomicallyWithMint()
    {
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, AndFork3BranchGraph());

        var instance = await engine.StartAsync(
            version.ID,
            formDataJson: null,
            initiatorITCode: "initiator_gw2",
            tenantCode: null,
            ct: CancellationToken.None);

        // Instance must be Approved (all Cc branches auto-complete).
        Assert.AreEqual(InstanceState.Approved, instance.State,
            "T_GW_2: instance must reach Approved (all 3 Cc branches complete).");

        await using var verify = MakeContext();

        // Join must have JoinExpectedArrivals == 3 (set atomically with mint, #483 Bug #8).
        var joinNode = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .Where(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Join)
            .FirstOrDefaultAsync();

        Assert.IsNotNull(joinNode, "T_GW_2: Join NodeInstance must exist.");
        Assert.AreEqual(3, joinNode!.JoinExpectedArrivals,
            "T_GW_2: JoinExpectedArrivals must be 3 — set atomically with the Join mint (#483 Bug #8).");
        Assert.AreEqual(3, joinNode.JoinArrivedCount,
            "T_GW_2: JoinArrivedCount must be 3 (all branches arrived).");
        Assert.AreEqual(NodeState.CompletedApproved, joinNode.State,
            "T_GW_2: Join must be CompletedApproved.");
    }

    // ── T_GW_3: Validator — gateway with zero outgoing transitions ────────────────

    /// <summary>
    /// T_GW_3: WorkflowGraphValidator must reject a ParallelGateway node that has
    /// zero outgoing transitions (#483 L4 — zero-branch stranding prevention).
    /// </summary>
    [TestMethod]
    public void T_GW_3_Validator_ParallelGateway_ZeroOutgoingTransitions_ReturnsError()
    {
        var graph = new WorkflowGraph
        {
            Key  = "GwNoOutgoing",
            Name = "GwNoOutgoing",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",   Kind = NodeKind.Start            },
                new() { NodeKey = "fork",    Kind = NodeKind.ParallelGateway, JoinNodeKey = "join" },
                // No branch nodes needed — we just need zero outgoing transitions from fork.
                new() { NodeKey = "join",    Kind = NodeKind.Join             },
                new() { NodeKey = "end",     Kind = NodeKind.End              },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "fork" },
                // Note: no transitions FROM "fork" — zero outgoing.
                new() { From = "join",  To = "end"  },
            },
        };

        var result = WorkflowGraphValidator.Validate(graph);
        Assert.IsFalse(result.IsValid,
            "T_GW_3: a ParallelGateway with zero outgoing transitions must fail validation.");
        Assert.AreEqual(GraphValidationError.GatewayNoOutgoingTransitions, result.Error,
            $"T_GW_3: expected GatewayNoOutgoingTransitions, got {result.Error}: {result.ErrorMessage}");
    }

    /// <summary>
    /// T_GW_3b: Same check for InclusiveGateway.
    /// </summary>
    [TestMethod]
    public void T_GW_3b_Validator_InclusiveGateway_ZeroOutgoingTransitions_ReturnsError()
    {
        var graph = new WorkflowGraph
        {
            Key  = "IncGwNoOutgoing",
            Name = "IncGwNoOutgoing",
            FieldWhitelist = new List<FieldWhitelistEntry>
            {
                new() { Field = "amount", ClrType = "System.Decimal" },
            },
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",   Kind = NodeKind.Start              },
                new() { NodeKey = "incfork", Kind = NodeKind.InclusiveGateway, JoinNodeKey = "join" },
                new() { NodeKey = "join",    Kind = NodeKind.Join               },
                new() { NodeKey = "end",     Kind = NodeKind.End                },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",   To = "incfork" },
                // No transitions FROM incfork.
                new() { From = "join",    To = "end"     },
            },
        };

        var result = WorkflowGraphValidator.Validate(graph);
        Assert.IsFalse(result.IsValid,
            "T_GW_3b: an InclusiveGateway with zero outgoing transitions must fail validation.");
        Assert.AreEqual(GraphValidationError.GatewayNoOutgoingTransitions, result.Error,
            $"T_GW_3b: expected GatewayNoOutgoingTransitions, got {result.Error}: {result.ErrorMessage}");
    }

    // ── Graph builder helpers ─────────────────────────────────────────────────────

    private static string AndFork3BranchGraph()
    {
        var graph = new WorkflowGraph
        {
            Key  = "AndFork3_483",
            Name = "AndFork3_483",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",   Kind = NodeKind.Start },
                new() { NodeKey = "fork",    Kind = NodeKind.ParallelGateway, JoinNodeKey = "join" },
                new() { NodeKey = "branchA", Kind = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cc_a" } },
                new() { NodeKey = "branchB", Kind = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cc_b" } },
                new() { NodeKey = "branchC", Kind = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cc_c" } },
                new() { NodeKey = "join",    Kind = NodeKind.Join },
                new() { NodeKey = "end",     Kind = NodeKind.End  },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",   To = "fork"    },
                new() { From = "fork",    To = "branchA" },
                new() { From = "fork",    To = "branchB" },
                new() { From = "fork",    To = "branchC" },
                new() { From = "branchA", To = "join"    },
                new() { From = "branchB", To = "join"    },
                new() { From = "branchC", To = "join"    },
                new() { From = "join",    To = "end"     },
            },
        };
        return WorkflowGraphSerializer.Serialize(graph);
    }

    private static async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfEngineTestContext ctx,
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
            ContentHash   = "gw483-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            TenantCode    = tenantCode,
            IsValid       = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }
}
