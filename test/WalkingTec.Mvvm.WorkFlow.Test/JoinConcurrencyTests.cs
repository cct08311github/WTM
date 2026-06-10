#nullable enable
// WF-17 — Join CAS concurrency conformance tests.
//
// Covers:
//   T-JOIN-1: Race — two concurrent IncrementJoinArrivedAsync callers; exactly-one
//             FireJoinIfSatisfiedAsync winner (×20 rounds).
//   T-JOIN-2: DecrementJoinExpectedAsync underflow guard — cannot go below
//             JoinArrivedCount (prevents count from becoming satisfiable via underflow).
//   T-JOIN-3: Orphan fail-closed — all branches die without arriving →
//             DecrementJoinExpectedAsync drives ExpectedArrivals to 0 → fail-closed.
//   T-JOIN-4: Stale-generation guard — FireJoinIfSatisfiedAsync no-ops on
//             stale epoch (generation mismatch).
//
// All tests use SQLite shared-in-memory (NEVER EF InMemory — spec §7.6 / #119 / #162).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class JoinConcurrencyTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfJoinConcur_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        using var db = MakeContext();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    private WfJoinTestContext MakeContext() => new(_dbName);

    // ── Seed helpers ─────────────────────────────────────────────────────────────

    private ProcessInstance SeedInstance(WfJoinTestContext db, uint generation = 0)
    {
        var inst = new ProcessInstance
        {
            ID                   = Guid.NewGuid(),
            State                = InstanceState.Running,
            RowVer               = 0,
            InitiatorITCode      = "user1",
            DefinitionVersionId  = Guid.NewGuid(),
            IsValid              = true,
            TenantCode           = "T1",
            Generation           = generation,
            NextSeq              = 1,
        };
        db.ProcessInstances.Add(inst);
        db.SaveChanges();
        return inst;
    }

    /// <summary>
    /// Seeds a Join NodeInstance with caller-supplied join counters.
    /// </summary>
    private NodeInstance SeedJoinNode(
        WfJoinTestContext db,
        Guid instanceId,
        string nodeKey,
        uint generation = 0,
        uint rowVer = 0,
        int expectedArrivals = 2,
        int arrivedCount = 0,
        NodeState state = NodeState.Activated)
    {
        var node = new NodeInstance
        {
            ID                   = Guid.NewGuid(),
            InstanceId           = instanceId,
            NodeKey              = nodeKey,
            NodeKind             = NodeKind.Join,
            State                = state,
            RowVer               = rowVer,
            TenantCode           = "T1",
            Generation           = generation,
            JoinExpectedArrivals = expectedArrivals,
            JoinArrivedCount     = arrivedCount,
        };
        db.NodeInstances.Add(node);
        db.SaveChanges();
        return node;
    }

    // ── T-JOIN-1: Race — two concurrent IncrementJoinArrivedAsync + FireJoinIfSatisfiedAsync ──

    /// <summary>
    /// T-JOIN-1: Two branches complete concurrently, each calling IncrementJoinArrivedAsync
    /// then FireJoinIfSatisfiedAsync.  JoinExpectedArrivals==2, so the second incremented
    /// arrival satisfies the condition.  Exactly one FireJoinIfSatisfiedAsync call must
    /// return rows==1 (the gate-opener); the other must return rows==0 (already fired).
    ///
    /// Validates: single-statement conditional CAS prevents double-fire.
    /// </summary>
    [TestMethod]
    public async Task T_JOIN_1_Race_TwoConcurrentArrivals_ExactlyOneFireWinner()
    {
        const int Rounds = 20;

        for (int round = 0; round < Rounds; round++)
        {
            var dbName = $"WfJoin1_{round}_{Guid.NewGuid():N}";
            await using var keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
            keepAlive.Open();
            await using var seedCtx = new WfJoinTestContext(dbName);
            seedCtx.Database.EnsureCreated();

            var instanceId = Guid.NewGuid();
            var joinId     = Guid.NewGuid();

            // ProcessInstance row (needed for FK).
            seedCtx.ProcessInstances.Add(new ProcessInstance
            {
                ID                  = instanceId,
                State               = InstanceState.Running,
                RowVer              = 0,
                InitiatorITCode     = "user1",
                DefinitionVersionId = Guid.NewGuid(),
                IsValid             = true,
                TenantCode          = "T1",
                Generation          = 0,
                NextSeq             = 1,
            });
            // Join node: expects 2 arrivals, 0 have arrived yet.
            seedCtx.NodeInstances.Add(new NodeInstance
            {
                ID                   = joinId,
                InstanceId           = instanceId,
                NodeKey              = "join1",
                NodeKind             = NodeKind.Join,
                State                = NodeState.Activated,
                RowVer               = 0,
                TenantCode           = "T1",
                Generation           = 0,
                JoinExpectedArrivals = 2,
                JoinArrivedCount     = 0,
            });
            await seedCtx.SaveChangesAsync();

            // Both callers: increment arrived, then attempt to fire.
            // They race; only the caller whose IncrementJoinArrivedAsync brings the count
            // to exactly == expectedArrivals can win FireJoinIfSatisfiedAsync.
            var barrier = new SemaphoreSlim(0, 2);

            async Task<int> BranchTask()
            {
                await barrier.WaitAsync();
                await using var db = new WfJoinTestContext(dbName);

                // Re-read fresh RowVer before each CAS attempt (retry-loop up to 10 attempts).
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    var fresh = await db.NodeInstances.AsNoTracking()
                        .Where(n => n.ID == joinId)
                        .Select(n => new { n.RowVer, n.Generation })
                        .FirstAsync();

                    int incrRows = await GuardedTransition.IncrementJoinArrivedAsync(
                        db, joinId,
                        expectedRowVer: fresh.RowVer,
                        generation: fresh.Generation,
                        ct: CancellationToken.None);

                    if (incrRows == 0)
                    {
                        // Lost this CAS slot — someone else incremented ahead of us.
                        await Task.Yield();
                        continue;
                    }

                    // Won the increment.  Now try to fire.
                    var afterIncr = await db.NodeInstances.AsNoTracking()
                        .Where(n => n.ID == joinId)
                        .Select(n => new { n.RowVer, n.Generation })
                        .FirstAsync();

                    int fireRows = await GuardedTransition.FireJoinIfSatisfiedAsync(
                        db, joinId,
                        expectedRowVer: afterIncr.RowVer,
                        generation: afterIncr.Generation,
                        ct: CancellationToken.None);

                    return fireRows;
                }

                return 0; // could not increment in retry budget — treated as loser
            }

            var t1 = Task.Run(BranchTask);
            var t2 = Task.Run(BranchTask);
            barrier.Release(2);

            int[] results = await Task.WhenAll(t1, t2);
            int winners = results.Count(r => r == 1);

            Assert.IsTrue(
                winners == 1,
                $"Round {round}: T-JOIN-1 — expected exactly 1 FireJoinIfSatisfiedAsync winner, " +
                $"got [{results[0]},{results[1]}].");

            // Final state: Join must be CompletedApproved.
            await using var verify = new WfJoinTestContext(dbName);
            var finalJoin = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == joinId);
            Assert.AreEqual(NodeState.CompletedApproved, finalJoin.State,
                $"Round {round}: T-JOIN-1 — Join node must be CompletedApproved after both branches arrive.");
            Assert.AreEqual(2, finalJoin.JoinArrivedCount,
                $"Round {round}: T-JOIN-1 — JoinArrivedCount must be 2.");
        }
    }

    // ── T-JOIN-2: DecrementJoinExpectedAsync underflow guard ─────────────────────

    /// <summary>
    /// T-JOIN-2: DecrementJoinExpectedAsync must NOT decrement when
    /// JoinExpectedArrivals == JoinArrivedCount (underflow would make
    /// the count go below arrived, which is nonsensical and would corrupt the barrier).
    ///
    /// Precondition: expectedArrivals==2, arrivedCount==2 (already satisfied).
    /// The WHERE clause includes: AND JoinExpectedArrivals > JoinArrivedCount
    /// → so the CAS must return rows==0 and leave the counters unchanged.
    /// </summary>
    [TestMethod]
    public async Task T_JOIN_2_DecrementJoinExpected_UnderflowGuard_NoOp()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db);

        // Seed a Join node that is already satisfied (arrivedCount == expectedArrivals).
        var joinNode = SeedJoinNode(
            db, inst.ID, "join_underflow",
            expectedArrivals: 2, arrivedCount: 2, state: NodeState.Activated);

        // Attempt to decrement — must be blocked by the guard clause.
        int rows = await GuardedTransition.DecrementJoinExpectedAsync(
            db, joinNode.ID,
            expectedRowVer: joinNode.RowVer,
            generation: joinNode.Generation,
            ct: CancellationToken.None);

        Assert.AreEqual(0, rows,
            "DecrementJoinExpectedAsync must return 0 (no-op) when " +
            "JoinExpectedArrivals == JoinArrivedCount (underflow guard).");

        // Verify counters unchanged.
        await using var verify = MakeContext();
        var final = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == joinNode.ID);
        Assert.AreEqual(2, final.JoinExpectedArrivals,
            "JoinExpectedArrivals must remain 2 — decrement was blocked.");
        Assert.AreEqual(2, final.JoinArrivedCount,
            "JoinArrivedCount must remain 2 — no side effect.");
    }

    /// <summary>
    /// T-JOIN-2b: DecrementJoinExpectedAsync succeeds when
    /// JoinExpectedArrivals > JoinArrivedCount (normal orphan-shrink path).
    /// </summary>
    [TestMethod]
    public async Task T_JOIN_2b_DecrementJoinExpected_NormalOrphanShrink_Succeeds()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db);

        // expectedArrivals=3, arrivedCount=1 → dead non-arriving branch: 1
        var joinNode = SeedJoinNode(
            db, inst.ID, "join_shrink",
            expectedArrivals: 3, arrivedCount: 1, state: NodeState.Activated);

        int rows = await GuardedTransition.DecrementJoinExpectedAsync(
            db, joinNode.ID,
            expectedRowVer: joinNode.RowVer,
            generation: joinNode.Generation,
            ct: CancellationToken.None);

        Assert.AreEqual(1, rows,
            "DecrementJoinExpectedAsync must return 1 when JoinExpectedArrivals > JoinArrivedCount.");

        await using var verify = MakeContext();
        var final = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == joinNode.ID);
        Assert.AreEqual(2, final.JoinExpectedArrivals,
            "JoinExpectedArrivals must be decremented by 1 (3→2).");
        Assert.AreEqual(1, final.JoinArrivedCount,
            "JoinArrivedCount must be unchanged.");
    }

    // ── T-JOIN-3: Orphan fail-closed ──────────────────────────────────────────────

    /// <summary>
    /// T-JOIN-3: All feeding branches die without arriving.
    /// DecrementJoinExpectedAsync is called once per dead branch.
    /// After all decrements, JoinExpectedArrivals reaches 0.
    ///
    /// This mirrors what CheckJoinOrphanAsync does in the engine.
    /// The test verifies the CAS sequence directly (unit level, not engine level).
    ///
    /// Scenario: expectedArrivals=2, arrivedCount=0, 2 dead branches → 2 decrements.
    /// After 2 decrements, JoinExpectedArrivals==0; the engine would then
    /// CompleteNodeInstance(CompletedRejected) and log a FailClosed event.
    /// This test only validates the CAS chain; engine-level fail-closed is tested in
    /// ParallelGatewayTests.
    /// </summary>
    [TestMethod]
    public async Task T_JOIN_3_OrphanFailClosed_AllBranchesDead_ExpectedReachesZero()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db);

        var joinNode = SeedJoinNode(
            db, inst.ID, "join_orphan",
            expectedArrivals: 2, arrivedCount: 0, state: NodeState.Activated);

        // First decrement (dead branch 1).
        var fresh1 = await db.NodeInstances.AsNoTracking()
            .Where(n => n.ID == joinNode.ID)
            .Select(n => new { n.RowVer, n.Generation })
            .FirstAsync();
        int rows1 = await GuardedTransition.DecrementJoinExpectedAsync(
            db, joinNode.ID,
            expectedRowVer: fresh1.RowVer,
            generation: fresh1.Generation,
            ct: CancellationToken.None);
        Assert.AreEqual(1, rows1, "First decrement must succeed.");

        // Second decrement (dead branch 2) — re-read RowVer.
        var fresh2 = await db.NodeInstances.AsNoTracking()
            .Where(n => n.ID == joinNode.ID)
            .Select(n => new { n.RowVer, n.Generation })
            .FirstAsync();
        int rows2 = await GuardedTransition.DecrementJoinExpectedAsync(
            db, joinNode.ID,
            expectedRowVer: fresh2.RowVer,
            generation: fresh2.Generation,
            ct: CancellationToken.None);
        Assert.AreEqual(1, rows2, "Second decrement must succeed (ExpectedArrivals 2→1→0).");

        // Final state: JoinExpectedArrivals==0.
        await using var verify = MakeContext();
        var final = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == joinNode.ID);
        Assert.AreEqual(0, final.JoinExpectedArrivals,
            "After 2 decrements on a 2-arrival Join, JoinExpectedArrivals must reach 0.");
        Assert.AreEqual(0, final.JoinArrivedCount,
            "JoinArrivedCount must remain 0 (no branch arrived).");
        Assert.AreEqual(NodeState.Activated, final.State,
            "CAS-only test: state remains Activated; engine would flip to CompletedRejected.");
    }

    // ── T-JOIN-4: Stale-generation guard ─────────────────────────────────────────

    /// <summary>
    /// T-JOIN-4: FireJoinIfSatisfiedAsync must no-op when the generation epoch
    /// in the WHERE clause does not match the node's current Generation.
    ///
    /// A stale-epoch caller with generation=0 must not fire a node whose
    /// generation has advanced to 1 (e.g., after a return-path bump).
    /// </summary>
    [TestMethod]
    public async Task T_JOIN_4_StaleGeneration_FireJoin_NoOp()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db, generation: 1); // instance is at gen 1

        // Join node is gen 1, already satisfied (arrived==expected).
        var joinNode = SeedJoinNode(
            db, inst.ID, "join_stale",
            generation: 1, rowVer: 0,
            expectedArrivals: 1, arrivedCount: 1, state: NodeState.Activated);

        // Caller with stale generation 0 — must not fire.
        int rows = await GuardedTransition.FireJoinIfSatisfiedAsync(
            db, joinNode.ID,
            expectedRowVer: joinNode.RowVer,
            generation: 0u, // stale — node is gen 1
            ct: CancellationToken.None);

        Assert.AreEqual(0, rows,
            "FireJoinIfSatisfiedAsync must return 0 (no-op) when generation is stale.");

        // Node must remain Activated (not fired).
        await using var verify = MakeContext();
        var final = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == joinNode.ID);
        Assert.AreEqual(NodeState.Activated, final.State,
            "Stale-epoch fire must leave node in Activated state.");
    }

    /// <summary>
    /// T-JOIN-4b: IncrementJoinArrivedAsync must also no-op on stale generation.
    /// </summary>
    [TestMethod]
    public async Task T_JOIN_4b_StaleGeneration_IncrementJoinArrived_NoOp()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db, generation: 1);

        var joinNode = SeedJoinNode(
            db, inst.ID, "join_stale_incr",
            generation: 1, rowVer: 0,
            expectedArrivals: 2, arrivedCount: 0, state: NodeState.Activated);

        // Stale caller uses generation 0.
        int rows = await GuardedTransition.IncrementJoinArrivedAsync(
            db, joinNode.ID,
            expectedRowVer: joinNode.RowVer,
            generation: 0u,
            ct: CancellationToken.None);

        Assert.AreEqual(0, rows,
            "IncrementJoinArrivedAsync must return 0 (no-op) on stale generation.");

        await using var verify = MakeContext();
        var final = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == joinNode.ID);
        Assert.AreEqual(0, final.JoinArrivedCount,
            "JoinArrivedCount must remain 0 after stale-epoch increment.");
    }
}

// ── WfJoinTestContext — minimal SQLite context for Join CAS tests ──────────────

/// <summary>
/// SQLite shared-in-memory DbContext used by JoinConcurrencyTests.
/// Covers ProcessInstance + NodeInstance with all WF-16 + WF-17 columns.
/// </summary>
internal sealed class WfJoinTestContext : DbContext
{
    private readonly string _dbName;

    public DbSet<ProcessInstance> ProcessInstances => Set<ProcessInstance>();
    public DbSet<NodeInstance>    NodeInstances    => Set<NodeInstance>();

    public WfJoinTestContext(string dbName) { _dbName = dbName; }

    protected override void OnConfiguring(DbContextOptionsBuilder b) =>
        b.UseSqlite($"DataSource={_dbName}?mode=memory&cache=shared");

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<ProcessInstance>(b =>
        {
            b.ToTable("Wf_ProcessInstance_Join");
            b.HasKey(x => x.ID);
            b.Property(x => x.State);
            b.Property(x => x.RowVer);
            b.Property(x => x.InitiatorITCode).HasMaxLength(50).IsRequired();
            b.Property(x => x.DefinitionVersionId);
            b.Property(x => x.TenantCode).HasMaxLength(50);
            b.Property(x => x.IsValid);
            b.Ignore(x => x.DefinitionVersion);
            // WF-16 columns.
            b.Property(x => x.Generation);
            b.Property(x => x.ReturnLoops);
            b.Property(x => x.NextSeq).HasDefaultValue(1);
            b.Property(x => x.ReturningLeaseUtc);
        });

        m.Entity<NodeInstance>(b =>
        {
            b.ToTable("Wf_NodeInstance_Join");
            b.HasKey(x => x.ID);
            b.Property(x => x.State);
            b.Property(x => x.RowVer);
            b.Property(x => x.NodeKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.NodeKind);
            b.Property(x => x.InstanceId);
            b.Property(x => x.TenantCode).HasMaxLength(50);
            b.Ignore(x => x.Instance);
            // WF-16 columns.
            b.Property(x => x.Generation);
            b.Property(x => x.SupersededAtGen);
            // WF-17 columns.
            b.Property(x => x.ForkGroupId);
            b.Property(x => x.JoinNodeKey).HasMaxLength(100);
            b.Property(x => x.JoinExpectedArrivals).HasDefaultValue(0);
            b.Property(x => x.JoinArrivedCount).HasDefaultValue(0);
            b.Property(x => x.AckMode);
        });
    }
}
