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

    // ── T_GW_4: concurrent-loser catch path ───────────────────────────────────────

    /// <summary>
    /// T_GW_4: A second engine invocation that calls ParallelGatewayHandler.OnEnterAsync
    /// for the same gateway on an already-minted fork hits the unique-constraint catch path
    /// and becomes an idempotent no-op.
    ///
    /// <para><strong>Ordering:</strong> sequential — engine1 mints+commits first, then engine2
    /// re-drives the same gateway entry via a fresh DbContext.  This deterministically exercises
    /// the catch path (DbUpdateException on unique-constraint violation) without relying on
    /// timing-sensitive true-simultaneous parallelism.  The catch path is the same code
    /// regardless of whether the loser arrives nanoseconds or milliseconds late.</para>
    ///
    /// <para><strong>Asserts:</strong>
    /// <list type="bullet">
    ///   <item>The second OnEnterAsync call does NOT throw.</item>
    ///   <item>The DB contains EXACTLY N branch NodeInstances (not 2N) after both calls.</item>
    ///   <item>The Join NodeInstance has JoinExpectedArrivals == N (the count was pinned
    ///         atomically by engine1 and was NOT clobbered by the loser's catch path).</item>
    /// </list>
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task T_GW_4_ParallelGateway_ConcurrentLoser_CatchPath_IsIdempotentNoOp()
    {
        // ── Phase 1: engine1 mints branches + join via StartAsync ────────────────
        var (engine1, ctx1) = MakeEngine();
        await using var _ctx1 = ctx1;

        var version = await SeedVersionAsync(ctx1, AndFork3BranchGraph());
        // Use a non-null tenantCode so the UNIQUE index on
        // (TenantCode, InstanceId, NodeKey, Generation) correctly fires when
        // the loser re-drives the same gateway — SQLite treats NULL!=NULL in
        // unique indexes (SQL standard), which would prevent constraint violations.
        const string tenantCode = "T_GW4";
        var instance = await engine1.StartAsync(
            version.ID,
            formDataJson: null,
            initiatorITCode: "initiator_gw4",
            tenantCode: tenantCode,
            ct: CancellationToken.None);

        // Sanity: engine1 reached Approved (all Cc branches are pass-through).
        Assert.AreEqual(InstanceState.Approved, instance.State,
            "T_GW_4 Phase 1: engine1 must drive instance to Approved before the loser test.");

        // Verify baseline DB state: 3 branch NodeInstances + 1 Join.
        await using var snapshot = MakeContext();
        int baselineBranches = await snapshot.Set<NodeInstance>()
            .CountAsync(n => n.InstanceId == instance.ID
                          && n.NodeKind   != NodeKind.Join
                          && n.NodeKey    != "start"
                          && n.NodeKey    != "fork"
                          && n.NodeKey    != "end");
        Assert.AreEqual(3, baselineBranches,
            "T_GW_4 Phase 1: expected exactly 3 branch NodeInstances after engine1.");

        var baselineJoin = await snapshot.Set<NodeInstance>()
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Join);
        Assert.IsNotNull(baselineJoin, "T_GW_4 Phase 1: Join NodeInstance must exist after engine1.");
        Assert.AreEqual(3, baselineJoin!.JoinExpectedArrivals,
            "T_GW_4 Phase 1: JoinExpectedArrivals must be 3 after engine1.");

        // ── Phase 2: engine2 re-drives ParallelGatewayHandler.OnEnterAsync ──────
        // Use a fresh DbContext (simulates a second engine instance) and directly invoke
        // the handler — this deterministically exercises the unique-constraint catch path.
        await using var db2 = MakeContext();

        // Deserialize the graph.
        var graphJson = version.GraphJson;
        var graph     = WorkflowGraphSerializer.Deserialize(graphJson);

        var forkNodeDef = graph.Nodes.First(n => n.NodeKey == "fork");

        // Re-read the fork NodeInstance (created by engine1 during StartAsync drain).
        var forkNodeInst = await db2.Set<NodeInstance>()
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.InstanceId == instance.ID && n.NodeKey == "fork");
        Assert.IsNotNull(forkNodeInst, "T_GW_4 Phase 2: fork NodeInstance must exist for re-drive.");

        // Re-read the ProcessInstance.
        var processInst = await db2.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID);

        var handler = new ParallelGatewayHandler(NullLogger<ParallelGatewayHandler>.Instance);

        var ctx2 = new NodeHandlerContext
        {
            NodeDef         = forkNodeDef,
            NodeInstance    = forkNodeInst,
            ProcessInstance = processInst,
            Graph           = graph,
            Db              = db2,
            CancellationToken = CancellationToken.None,
        };

        // Must NOT throw — the catch path is the idempotent no-op.
        await handler.OnEnterAsync(ctx2);

        // ── Phase 3: assert DB state is unchanged ─────────────────────────────────
        await using var verify = MakeContext();

        // Still exactly 3 branch NodeInstances (not 6).
        int finalBranches = await verify.Set<NodeInstance>()
            .CountAsync(n => n.InstanceId == instance.ID
                          && n.NodeKind   != NodeKind.Join
                          && n.NodeKey    != "start"
                          && n.NodeKey    != "fork"
                          && n.NodeKey    != "end");
        Assert.AreEqual(3, finalBranches,
            "T_GW_4: DB must still contain exactly 3 branch NodeInstances after the loser's no-op — not 2N=6.");

        // JoinExpectedArrivals still pinned at 3 (loser catch path must NOT clobber it).
        var finalJoin = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Join);
        Assert.IsNotNull(finalJoin, "T_GW_4: Join NodeInstance must still exist after loser no-op.");
        Assert.AreEqual(3, finalJoin!.JoinExpectedArrivals,
            "T_GW_4: JoinExpectedArrivals must remain 3 — loser catch path must not re-pin it.");
    }

    // ── T_GW_5: NULL TenantCode — pre-check provides idempotency ─────────────────

    /// <summary>
    /// T_GW_5: Branch-mint is idempotent when TenantCode is NULL on SQLite shared-memory,
    /// where the UNIQUE index on (TenantCode, InstanceId, NodeKey, Generation) does NOT fire
    /// for NULL values (SQL standard — NULL is DISTINCT from NULL in unique indexes on SQLite,
    /// PostgreSQL, MySQL, Oracle).
    ///
    /// <para>Strategy: call <see cref="ParallelGatewayHandler.OnEnterAsync"/> twice in
    /// sequence for the same instance/generation with <c>tenantCode = null</c>.
    /// A fresh <see cref="NodeHandlerContext"/> and <see cref="DbContext"/> is used for the
    /// second call so the second invocation has no EF change-tracker memory of the first.
    /// Because the unique index does NOT fire (NULL != NULL), the catch block is NEVER
    /// reached — the pre-check (reading existing NodeKeys before Add) is the ONLY guard.
    /// Asserts: exactly N branch NodeInstances (not 2N) and one Join with
    /// JoinExpectedArrivals == N, no exception.</para>
    /// </summary>
    [TestMethod]
    public async Task T_GW_5_ParallelGateway_NullTenantCode_PreCheck_ProvidesIdempotency()
    {
        // Build and seed the graph.
        var graphJson = AndFork3BranchGraph();
        var graph     = WorkflowGraphSerializer.Deserialize(graphJson);
        var forkDef   = graph.Nodes.First(n => n.NodeKey == "fork");

        // Use null TenantCode — this is the vulnerable case for the unique index.
        const string? tenantCode = null;

        // Seed a ProcessInstance with tenantCode = null.
        Guid instanceId;
        await using (var seed = MakeContext())
        {
            await SeedVersionAsync(seed, graphJson, tenantCode);

            var inst = new ProcessInstance
            {
                ID                  = Guid.NewGuid(),
                State               = InstanceState.Running,
                RowVer              = 0,
                InitiatorITCode     = "gw5_user",
                DefinitionVersionId = Guid.NewGuid(),
                IsValid             = true,
                TenantCode          = tenantCode,
                Generation          = 0,
                NextSeq             = 1,
            };
            seed.Set<ProcessInstance>().Add(inst);
            await seed.SaveChangesAsync();
            instanceId = inst.ID;
        }

        // Seed the fork NodeInstance itself (the gateway node that the handler is invoked for).
        await using (var seed2 = MakeContext())
        {
            seed2.Set<NodeInstance>().Add(new NodeInstance
            {
                ID          = Guid.NewGuid(),
                TenantCode  = tenantCode,
                InstanceId  = instanceId,
                NodeKey     = "fork",
                NodeKind    = NodeKind.ParallelGateway,
                State       = NodeState.Pending,
                Generation  = 0,
                RowVer      = 0,
            });
            await seed2.SaveChangesAsync();
        }

        var handler = new ParallelGatewayHandler(NullLogger<ParallelGatewayHandler>.Instance);

        // ── First call ───────────────────────────────────────────────────────────
        await using var db1 = MakeContext();
        var processInst1 = await db1.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instanceId);
        var forkInst1 = await db1.Set<NodeInstance>().AsNoTracking()
            .FirstAsync(n => n.InstanceId == instanceId && n.NodeKey == "fork");

        var ctx1 = new NodeHandlerContext
        {
            NodeDef           = forkDef,
            NodeInstance      = forkInst1,
            ProcessInstance   = processInst1,
            Graph             = graph,
            Db                = db1,
            CancellationToken = CancellationToken.None,
        };
        await handler.OnEnterAsync(ctx1);   // must not throw

        // ── Second call (fresh DbContext — no change-tracker memory of first call) ──
        await using var db2 = MakeContext();
        var processInst2 = await db2.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instanceId);
        var forkInst2 = await db2.Set<NodeInstance>().AsNoTracking()
            .FirstAsync(n => n.InstanceId == instanceId && n.NodeKey == "fork");

        var ctx2 = new NodeHandlerContext
        {
            NodeDef           = forkDef,
            NodeInstance      = forkInst2,
            ProcessInstance   = processInst2,
            Graph             = graph,
            Db                = db2,
            CancellationToken = CancellationToken.None,
        };
        // Must NOT throw — the pre-check (not the unique-index catch) is the guard here.
        await handler.OnEnterAsync(ctx2);

        // ── Assertions ────────────────────────────────────────────────────────────
        await using var verify = MakeContext();

        // Exactly 3 branch NodeInstances (not 6).
        int branchCount = await verify.Set<NodeInstance>()
            .CountAsync(n => n.InstanceId == instanceId
                          && n.NodeKey    != "fork"
                          && n.NodeKind   != NodeKind.Join);
        Assert.AreEqual(3, branchCount,
            "T_GW_5: must have exactly 3 branch NodeInstances (not 2N=6) — pre-check provides idempotency for NULL TenantCode.");

        // Exactly one Join NodeInstance with JoinExpectedArrivals == 3.
        var join = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.InstanceId == instanceId && n.NodeKind == NodeKind.Join);
        Assert.IsNotNull(join,
            "T_GW_5: Join NodeInstance must exist.");
        Assert.AreEqual(3, join!.JoinExpectedArrivals,
            "T_GW_5: JoinExpectedArrivals must be 3 — not doubled by the second idempotent call.");
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
