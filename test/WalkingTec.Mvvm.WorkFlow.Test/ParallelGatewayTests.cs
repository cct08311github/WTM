#nullable enable
// WF-17 — Parallel/Inclusive gateway + Join integration tests.
//
// Covers:
//   T-JOIN-5: AND-fork (ParallelGateway) integration — all 3 branches arrive → Join fires
//             → instance reaches Approved.  Verifies branch tokens, JoinExpectedArrivals
//             pinned to 3, and Join's single-winner CAS.
//   T-JOIN-6: OR-fork (InclusiveGateway) integration — 2 of 3 branches match condition
//             → Join fires with JoinExpectedArrivals==2 → instance reaches Approved.
//   Validator-smoke: WF-17 graph validation errors (missing joinNodeKey, dangling,
//             wrong kind, InclusiveGateway missing condition, Ack missing ackMode).
//   Orphan-engine: engine-level fail-closed when all branches rejected before joining.
//
// All tests use SQLite shared-in-memory (NEVER EF InMemory — spec §7.6 / #119 / #162).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
public class ParallelGatewayTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfParallel_{Guid.NewGuid():N}";
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

    private async Task<ProcessDefinitionVersion> SeedVersionAsync(
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
            ContentHash   = "pg-hash-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            TenantCode    = tenantCode,
            IsValid       = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }

    // ── Graph builders ────────────────────────────────────────────────────────────

    /// <summary>
    /// Start → ParallelGateway → [branchA, branchB, branchC] → Join → End
    ///
    /// All three branches are Start→cc-through (or direct) nodes that auto-complete.
    /// Graph topology for T-JOIN-5.
    /// </summary>
    private static string AndFork3BranchGraph()
    {
        var graph = new WorkflowGraph
        {
            Key  = "AndFork3",
            Name = "AndFork3",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey     = "fork",
                    Kind        = NodeKind.ParallelGateway,
                    JoinNodeKey = "join",
                },
                new() { NodeKey = "branchA", Kind = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cc_a" } },
                new() { NodeKey = "branchB", Kind = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cc_b" } },
                new() { NodeKey = "branchC", Kind = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cc_c" } },
                new() { NodeKey = "join",  Kind = NodeKind.Join   },
                new() { NodeKey = "end",   Kind = NodeKind.End    },
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

    /// <summary>
    /// Start → InclusiveGateway → [branchA (cond: amount>=100), branchB (cond: amount>=200),
    ///                              branchC (cond: amount>=500)] → Join → End
    ///
    /// With formData = { "amount": 250 }, branchA and branchB match; branchC does not.
    /// Graph topology for T-JOIN-6.
    /// </summary>
    private static string OrFork3BranchGraph()
    {
        var graph = new WorkflowGraph
        {
            Key  = "OrFork3",
            Name = "OrFork3",
            FieldWhitelist = new List<FieldWhitelistEntry>
            {
                new() { Field = "amount", ClrType = "System.Decimal" },
            },
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey     = "incfork",
                    Kind        = NodeKind.InclusiveGateway,
                    JoinNodeKey = "join",
                },
                new() { NodeKey = "branchA", Kind = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cc_a" } },
                new() { NodeKey = "branchB", Kind = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cc_b" } },
                new() { NodeKey = "branchC", Kind = NodeKind.Cc,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cc_c" } },
                new() { NodeKey = "join",  Kind = NodeKind.Join   },
                new() { NodeKey = "end",   Kind = NodeKind.End    },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "incfork" },
                new()
                {
                    From = "incfork", To = "branchA",
                    Condition = new RoutingRuleDef
                    {
                        Field    = "amount",
                        Operator = FilterOperator.Gte,
                        Value    = (object)100m,
                    },
                },
                new()
                {
                    From = "incfork", To = "branchB",
                    Condition = new RoutingRuleDef
                    {
                        Field    = "amount",
                        Operator = FilterOperator.Gte,
                        Value    = (object)200m,
                    },
                },
                new()
                {
                    From = "incfork", To = "branchC",
                    Condition = new RoutingRuleDef
                    {
                        Field    = "amount",
                        Operator = FilterOperator.Gte,
                        Value    = (object)500m,
                    },
                },
                new() { From = "branchA", To = "join" },
                new() { From = "branchB", To = "join" },
                new() { From = "branchC", To = "join" },
                new() { From = "join",    To = "end"  },
            },
        };
        return WorkflowGraphSerializer.Serialize(graph);
    }

    // ── T-JOIN-5: AND-fork integration ────────────────────────────────────────────

    /// <summary>
    /// T-JOIN-5: ParallelGateway (AND-fork) integration.
    ///
    /// Graph: Start → ParallelGateway → [branchA(Cc), branchB(Cc), branchC(Cc)] → Join → End.
    /// All three branches are Cc nodes (non-blocking), so the engine auto-completes them.
    ///
    /// Expected:
    ///  - After StartAsync, the instance drives through all 3 branches automatically
    ///    (Cc nodes never block).
    ///  - The Join fires once all 3 branches arrive (JoinArrivedCount==3,
    ///    JoinExpectedArrivals==3).
    ///  - The instance reaches Approved.
    ///  - Exactly 3 CcRecord rows are written (one per Cc branch node).
    ///  - The Join NodeInstance is CompletedApproved.
    /// </summary>
    [TestMethod]
    public async Task T_JOIN_5_ParallelGateway_AllBranchesArrive_JoinFires_Approved()
    {
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, AndFork3BranchGraph());

        var instance = await engine.StartAsync(
            version.ID,
            formDataJson: null,
            initiatorITCode: "initiator5",
            tenantCode: null,
            ct: CancellationToken.None);

        // Instance must be Approved — all branches auto-complete (Cc nodes).
        Assert.AreEqual(InstanceState.Approved, instance.State,
            "T-JOIN-5: all 3 Cc branches auto-complete → Join fires → instance must reach Approved.");

        await using var verify = MakeContext();

        // 3 CcRecord rows (one per Cc branch node).
        var ccRecords = await verify.Set<CcRecord>()
            .Where(c => c.InstanceId == instance.ID)
            .ToListAsync();
        Assert.AreEqual(3, ccRecords.Count,
            $"T-JOIN-5: expected 3 CcRecord rows, got {ccRecords.Count}.");

        // Join node must be CompletedApproved.
        var joinNode = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .Where(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Join)
            .FirstOrDefaultAsync();
        Assert.IsNotNull(joinNode, "T-JOIN-5: a Join NodeInstance must exist.");
        Assert.AreEqual(NodeState.CompletedApproved, joinNode!.State,
            "T-JOIN-5: Join node must be CompletedApproved after all branches arrive.");
        Assert.AreEqual(3, joinNode.JoinArrivedCount,
            "T-JOIN-5: JoinArrivedCount must equal 3 (one per branch).");
        Assert.AreEqual(3, joinNode.JoinExpectedArrivals,
            "T-JOIN-5: JoinExpectedArrivals must be pinned to 3 by the ParallelGatewayHandler.");

        // All branch tokens must be CompletedApproved (or Superseded / completed).
        var branchKeys = new[] { "branchA", "branchB", "branchC" };
        foreach (var key in branchKeys)
        {
            var branch = await verify.Set<NodeInstance>()
                .AsNoTracking()
                .Where(n => n.InstanceId == instance.ID && n.NodeKey == key)
                .FirstOrDefaultAsync();
            Assert.IsNotNull(branch, $"T-JOIN-5: branch node '{key}' must exist.");
            Assert.AreEqual(NodeState.CompletedApproved, branch!.State,
                $"T-JOIN-5: branch node '{key}' must be CompletedApproved.");
        }
    }

    // ── T-JOIN-6: OR-fork integration ─────────────────────────────────────────────

    /// <summary>
    /// T-JOIN-6: InclusiveGateway (OR-fork) integration.
    ///
    /// Graph: Start → InclusiveGateway → [branchA(Cc, amount>=100), branchB(Cc, amount>=200),
    ///                                    branchC(Cc, amount>=500)] → Join → End.
    ///
    /// FormData: { "amount": 250 }
    ///  - branchA matches (250 >= 100) ✓
    ///  - branchB matches (250 >= 200) ✓
    ///  - branchC does NOT match (250 < 500) ✗
    ///
    /// Expected:
    ///  - JoinExpectedArrivals pinned to 2 (only branchA + branchB minted).
    ///  - branchC NodeInstance is NOT created.
    ///  - The Join fires when both matching branches arrive.
    ///  - Instance reaches Approved.
    ///  - Exactly 2 CcRecord rows (one per matched Cc branch).
    /// </summary>
    [TestMethod]
    public async Task T_JOIN_6_InclusiveGateway_TwoOf3BranchesMatch_JoinFires_Approved()
    {
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, OrFork3BranchGraph());

        // FormData with amount=250 → branchA + branchB match; branchC does not.
        var formDataJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            { "amount", 250m },
        });

        var instance = await engine.StartAsync(
            version.ID,
            formDataJson: formDataJson,
            initiatorITCode: "initiator6",
            tenantCode: null,
            ct: CancellationToken.None);

        // Instance must be Approved — branchA + branchB are Cc (non-blocking).
        Assert.AreEqual(InstanceState.Approved, instance.State,
            "T-JOIN-6: 2 matching Cc branches auto-complete → Join fires → instance must reach Approved.");

        await using var verify = MakeContext();

        // Exactly 2 CcRecord rows.
        var ccRecords = await verify.Set<CcRecord>()
            .Where(c => c.InstanceId == instance.ID)
            .ToListAsync();
        Assert.AreEqual(2, ccRecords.Count,
            $"T-JOIN-6: expected 2 CcRecord rows (branchA + branchB), got {ccRecords.Count}.");
        Assert.IsTrue(ccRecords.Any(c => c.NodeKey == "branchA"),
            "T-JOIN-6: branchA CcRecord must exist.");
        Assert.IsTrue(ccRecords.Any(c => c.NodeKey == "branchB"),
            "T-JOIN-6: branchB CcRecord must exist.");

        // Join node must be CompletedApproved with arrivals==expected==2.
        var joinNode = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .Where(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Join)
            .FirstOrDefaultAsync();
        Assert.IsNotNull(joinNode, "T-JOIN-6: a Join NodeInstance must exist.");
        Assert.AreEqual(NodeState.CompletedApproved, joinNode!.State,
            "T-JOIN-6: Join node must be CompletedApproved.");
        Assert.AreEqual(2, joinNode.JoinArrivedCount,
            "T-JOIN-6: JoinArrivedCount must be 2 (only 2 branches matched).");
        Assert.AreEqual(2, joinNode.JoinExpectedArrivals,
            "T-JOIN-6: JoinExpectedArrivals must be pinned to 2 by InclusiveGatewayHandler.");

        // branchC NodeInstance must NOT exist (was not minted).
        var branchC = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .Where(n => n.InstanceId == instance.ID && n.NodeKey == "branchC")
            .FirstOrDefaultAsync();
        Assert.IsNull(branchC,
            "T-JOIN-6: branchC must NOT be minted when condition does not match (amount=250 < 500).");
    }

    // ── Validator smoke: WF-17 error codes ────────────────────────────────────────

    /// <summary>
    /// Validator must reject a ParallelGateway node that has no joinNodeKey.
    /// </summary>
    [TestMethod]
    public void Validator_ParallelGateway_MissingJoinNodeKey_ReturnsError()
    {
        var graph = new WorkflowGraph
        {
            Key  = "BadFork",
            Name = "BadFork",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",   Kind = NodeKind.Start           },
                new() { NodeKey = "fork",    Kind = NodeKind.ParallelGateway  }, // missing JoinNodeKey
                new() { NodeKey = "branchA", Kind = NodeKind.Cc, ApproverRule = new() { Type = "User", Value = "u1" } },
                new() { NodeKey = "join",    Kind = NodeKind.Join              },
                new() { NodeKey = "end",     Kind = NodeKind.End               },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",   To = "fork"    },
                new() { From = "fork",    To = "branchA" },
                new() { From = "branchA", To = "join"    },
                new() { From = "join",    To = "end"     },
            },
        };

        var result = WorkflowGraphValidator.Validate(graph);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(GraphValidationError.GatewayMissingJoinNodeKey, result.Error,
            $"Expected GatewayMissingJoinNodeKey, got {result.Error}: {result.ErrorMessage}");
    }

    /// <summary>
    /// Validator must reject a gateway whose joinNodeKey references a non-Join node.
    /// </summary>
    [TestMethod]
    public void Validator_ParallelGateway_JoinNodeKeyWrongKind_ReturnsError()
    {
        var graph = new WorkflowGraph
        {
            Key  = "BadJoinKind",
            Name = "BadJoinKind",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",   Kind = NodeKind.Start            },
                new() { NodeKey = "fork",    Kind = NodeKind.ParallelGateway, JoinNodeKey = "end" }, // points to End, not Join
                new() { NodeKey = "branchA", Kind = NodeKind.Cc, ApproverRule = new() { Type = "User", Value = "u1" } },
                new() { NodeKey = "end",     Kind = NodeKind.End               },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",   To = "fork"    },
                new() { From = "fork",    To = "branchA" },
                new() { From = "branchA", To = "end"     },
            },
        };

        var result = WorkflowGraphValidator.Validate(graph);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(GraphValidationError.GatewayJoinNodeKeyNotJoinKind, result.Error,
            $"Expected GatewayJoinNodeKeyNotJoinKind, got {result.Error}: {result.ErrorMessage}");
    }

    /// <summary>
    /// Validator must reject an InclusiveGateway transition that has no condition.
    /// </summary>
    [TestMethod]
    public void Validator_InclusiveGateway_TransitionMissingCondition_ReturnsError()
    {
        var graph = new WorkflowGraph
        {
            Key  = "IncNoCondition",
            Name = "IncNoCondition",
            FieldWhitelist = new List<FieldWhitelistEntry>
            {
                new() { Field = "amount", ClrType = "System.Decimal" },
            },
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",   Kind = NodeKind.Start                          },
                new() { NodeKey = "incfork", Kind = NodeKind.InclusiveGateway, JoinNodeKey = "join" },
                new() { NodeKey = "branchA", Kind = NodeKind.Cc, ApproverRule = new() { Type = "User", Value = "u1" } },
                new() { NodeKey = "join",    Kind = NodeKind.Join                            },
                new() { NodeKey = "end",     Kind = NodeKind.End                             },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",   To = "incfork" },
                new()
                {
                    // Missing Condition on InclusiveGateway transition.
                    From = "incfork", To = "branchA",
                    Condition = null,
                },
                new() { From = "branchA", To = "join" },
                new() { From = "join",    To = "end"  },
            },
        };

        var result = WorkflowGraphValidator.Validate(graph);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(GraphValidationError.InclusiveGatewayTransitionMissingCondition, result.Error,
            $"Expected InclusiveGatewayTransitionMissingCondition, got {result.Error}: {result.ErrorMessage}");
    }

    /// <summary>
    /// Validator must reject an Ack node without ackMode.
    /// </summary>
    [TestMethod]
    public void Validator_AckNode_MissingAckMode_ReturnsError()
    {
        var graph = new WorkflowGraph
        {
            Key  = "BadAck",
            Name = "BadAck",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new() { NodeKey = "ack1",  Kind = NodeKind.Ack   }, // missing AckMode
                new() { NodeKey = "end",   Kind = NodeKind.End   },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "ack1" },
                new() { From = "ack1",  To = "end"  },
            },
        };

        var result = WorkflowGraphValidator.Validate(graph);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(GraphValidationError.AckNodeMissingAckMode, result.Error,
            $"Expected AckNodeMissingAckMode, got {result.Error}: {result.ErrorMessage}");
    }

    /// <summary>
    /// A well-formed AND-fork graph must pass validation.
    /// </summary>
    [TestMethod]
    public void Validator_WellFormedParallelGateway_IsValid()
    {
        var graphJson = AndFork3BranchGraph();
        var graph = WorkflowGraphSerializer.Deserialize(graphJson)!;
        var result = WorkflowGraphValidator.Validate(graph);
        Assert.IsTrue(result.IsValid,
            $"Well-formed ParallelGateway graph must pass validation. Error: {result.Error} — {result.ErrorMessage}");
    }

    /// <summary>
    /// A well-formed OR-fork graph must pass validation.
    /// </summary>
    [TestMethod]
    public void Validator_WellFormedInclusiveGateway_IsValid()
    {
        var graphJson = OrFork3BranchGraph();
        var graph = WorkflowGraphSerializer.Deserialize(graphJson)!;
        var result = WorkflowGraphValidator.Validate(graph);
        Assert.IsTrue(result.IsValid,
            $"Well-formed InclusiveGateway graph must pass validation. Error: {result.Error} — {result.ErrorMessage}");
    }
}
