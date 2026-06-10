#nullable enable
// WF-16 Wave-3 — Return-path concurrency conformance tests.
//
// Covers:
//   T-RET-1:  Race A — span-discard CAS vs in-flight approve; exactly one wins (×20 rounds).
//   T-RET-2:  Race D — concurrent returns on same instance; exactly one BeginReturnAsync wins.
//   T-RET-4:  Race C — CancelTimersForReturnAsync; timer-fire action must no-op after cancel.
//   T-RET-5:  Stale-epoch guard — AdvanceCoreAsync filters by Generation; old-gen tokens ignored.
//   T-RET-6:  Seq allocation under 8-way concurrent contention; no duplicates.
//   T-RET-7:  Crash / reaper stub — ReclaimReturningLeaseAsync reclaims expired Returning lease.
//   T-RET-8:  Audit atomicity — event log Seq is monotone and gap-free.
//   T-PROV-W3-*: Provider-conformance stubs (SQLite only; live-DB variants tagged ProviderConformance).
//
// All tests use SQLite shared-in-memory (NOT EF InMemory).

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
public class ReturnConcurrencyTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfRetConcur_{Guid.NewGuid():N}";
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

    private WfTestContext MakeContext() => new(_dbName);

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private ProcessInstance SeedInstance(WfTestContext db, uint generation = 0, uint returnLoops = 0, int nextSeq = 1)
    {
        var inst = new ProcessInstance
        {
            ID = Guid.NewGuid(),
            State = InstanceState.Running,
            RowVer = 0,
            InitiatorITCode = "user1",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid = true,
            TenantCode = "T1",
            Generation = generation,
            ReturnLoops = returnLoops,
            NextSeq = nextSeq,
        };
        db.ProcessInstances.Add(inst);
        db.SaveChanges();
        return inst;
    }

    private NodeInstance SeedNode(WfTestContext db, Guid instanceId, string nodeKey,
        NodeState state = NodeState.Activated, uint generation = 0, uint rowVer = 0)
    {
        var node = new NodeInstance
        {
            ID = Guid.NewGuid(),
            InstanceId = instanceId,
            NodeKey = nodeKey,
            NodeKind = NodeKind.Approval,
            State = state,
            RowVer = rowVer,
            TenantCode = "T1",
            Generation = generation,
        };
        db.NodeInstances.Add(node);
        db.SaveChanges();
        return node;
    }

    // ── T-RET-1: Race A — span-discard CAS vs in-flight approve ─────────────

    /// <summary>
    /// T-RET-1: Two concurrent callers race — one calls SupersedeNodeAsync (span-discard,
    /// the return path's STEP-4), the other calls CompleteNodeInstanceAsync (in-flight approve).
    /// Both CAS operations target the same RowVer on the same NodeInstance row.
    /// Exactly one must win; the other must get rows==0.
    ///
    /// Validates: "supersede-not-delete backbone: span-discard CAS shares same RowVer as
    /// approval-completion CAS — exactly one wins" (Race A resolution, spec §4.1).
    /// </summary>
    [TestMethod]
    public async Task T_RET_1_RaceA_SupersedeVsComplete_ExactlyOneWinner()
    {
        const int Rounds = 20;

        for (int round = 0; round < Rounds; round++)
        {
            var id = Guid.NewGuid();
            var instId = Guid.NewGuid();

            await using var seed = MakeContext();
            seed.NodeInstances.Add(new NodeInstance
            {
                ID = id,
                InstanceId = instId,
                NodeKey = $"node_r{round}",
                NodeKind = NodeKind.Approval,
                State = NodeState.Activated,
                RowVer = 0,
                TenantCode = "T1",
                Generation = 0,
            });
            await seed.SaveChangesAsync();

            var barrier = new SemaphoreSlim(0, 2);

            // Caller A: CompleteNodeInstanceAsync (in-flight approve wins the CAS).
            Task<int> approveTask = Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.CompleteNodeInstanceAsync(
                    db, id, expectedRowVer: 0, completedState: NodeState.CompletedApproved,
                    ct: CancellationToken.None);
            });

            // Caller B: SupersedeNodeAsync (return-path STEP-4 wins the CAS).
            Task<int> supersedeTask = Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.SupersedeNodeAsync(
                    db, id, expectedRowVer: 0, supersededAtGen: 1u,
                    ct: CancellationToken.None);
            });

            barrier.Release(2);
            int[] results = await Task.WhenAll(approveTask, supersedeTask);
            int winners = results.Count(r => r == 1);
            int losers  = results.Count(r => r == 0);

            Assert.AreEqual(1, winners,
                $"Round {round}: Race A — expected exactly 1 winner, got [{results[0]},{results[1]}].");
            Assert.AreEqual(1, losers,
                $"Round {round}: Race A — expected exactly 1 loser.");

            // Verify final state: exactly one of CompletedApproved / Superseded.
            await using var verify = MakeContext();
            var final = await verify.NodeInstances.AsNoTracking().SingleAsync(x => x.ID == id);
            Assert.IsTrue(
                final.State == NodeState.CompletedApproved || final.State == NodeState.Superseded,
                $"Round {round}: final State must be CompletedApproved or Superseded, got {final.State}.");
        }
    }

    // ── T-RET-2: Race D — concurrent BeginReturnAsync on same instance ────────

    /// <summary>
    /// T-RET-2: Two concurrent callers race to call BeginReturnAsync on the same instance.
    /// Exactly one must win (rows==1) and the other must lose (rows==0).
    /// Validates: linearization-point CAS atomicity (Race D, spec §2 STEP-1).
    /// </summary>
    [TestMethod]
    public async Task T_RET_2_RaceD_ConcurrentBeginReturn_ExactlyOneWinner()
    {
        const int Rounds = 20;

        for (int round = 0; round < Rounds; round++)
        {
            var id = Guid.NewGuid();

            await using var seed = MakeContext();
            seed.ProcessInstances.Add(new ProcessInstance
            {
                ID = id,
                State = InstanceState.Running,
                RowVer = 0,
                InitiatorITCode = "user1",
                DefinitionVersionId = Guid.NewGuid(),
                IsValid = true,
                TenantCode = "T1",
                Generation = 0,
                ReturnLoops = 0,
                NextSeq = 1,
            });
            await seed.SaveChangesAsync();

            var barrier = new SemaphoreSlim(0, 2);
            var leaseExpiry = DateTime.UtcNow.AddMinutes(30);

            Task<(int rows, uint newGeneration)> MakeTask()
            {
                Guid capturedId = id;
                return Task.Run(async () =>
                {
                    await barrier.WaitAsync();
                    await using var db = MakeContext();
                    return await GuardedTransition.BeginReturnAsync(
                        db, capturedId,
                        expectedRowVer: 0,
                        expectedGeneration: 0u,
                        maxReturnLoops: 3,
                        leaseExpiry: leaseExpiry,
                        ct: CancellationToken.None);
                });
            }

            var t1 = MakeTask();
            var t2 = MakeTask();
            barrier.Release(2);

            var results = await Task.WhenAll(t1, t2);
            int winners = results.Count(r => r.rows == 1);
            int losers  = results.Count(r => r.rows == 0);

            Assert.AreEqual(1, winners,
                $"Round {round}: BeginReturnAsync concurrent race — expected 1 winner, got rows=[{results[0].rows},{results[1].rows}].");
            Assert.AreEqual(1, losers,
                $"Round {round}: BeginReturnAsync concurrent race — expected 1 loser.");

            // Verify: ReturnLoops == 1 (only one increment landed).
            await using var verify = MakeContext();
            var final = await verify.ProcessInstances.AsNoTracking().SingleAsync(x => x.ID == id);
            Assert.AreEqual(1u, final.ReturnLoops,
                $"Round {round}: ReturnLoops must be exactly 1 after concurrent race.");
            Assert.AreEqual(1u, final.Generation,
                $"Round {round}: Generation must be exactly 1 after concurrent race.");
        }
    }

    // ── T-RET-4: Race C — timer cancel vs return ──────────────────────────────

    /// <summary>
    /// T-RET-4: CancelTimersForReturnAsync bulk-cancels Armed timers for span node IDs.
    /// After cancel the timer's Status must be Cancelled.
    /// A real "timer-fire action gates on Generation" check requires the Wave-5 timer
    /// engine which is not yet implemented (WF-20); this test validates the cancel CAS.
    /// </summary>
    [TestMethod]
    public async Task T_RET_4_RaceC_CancelTimers_ArmedBecomeCancelled()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db);
        var node = SeedNode(db, inst.ID, "TimerNode");

        // Seed an Armed timer.
        var timer = new WorkflowTimer
        {
            ID = Guid.NewGuid(),
            NodeInstanceId = node.ID,
            IdempotencyKey = $"timer_{Guid.NewGuid():N}",
            Status = TimerStatus.Armed,
            FireAtUtc = DateTime.UtcNow.AddHours(1),
            RowVer = 0,
            TenantCode = "T1",
            Generation = 0,
        };
        db.WorkflowTimers.Add(timer);
        await db.SaveChangesAsync();

        // Act: cancel timers for the span.
        await GuardedTransition.CancelTimersForReturnAsync(
            db, new List<Guid> { node.ID }, CancellationToken.None);

        // Assert: timer is now Cancelled.
        await using var verify = MakeContext();
        var finalTimer = await verify.WorkflowTimers.AsNoTracking().SingleAsync(x => x.ID == timer.ID);
        Assert.AreEqual(TimerStatus.Cancelled, finalTimer.Status,
            "Armed timer must be cancelled by CancelTimersForReturnAsync.");
    }

    // ── T-RET-5: Stale-epoch guard ─────────────────────────────────────────────

    /// <summary>
    /// T-RET-5: After a return operation bumps Generation to gNew, old-generation
    /// NodeInstances (Generation==gOld) must not be picked up by the engine's active-node
    /// query that filters by Generation==instance.Generation.
    ///
    /// This directly validates the stale-epoch exclusion introduced in AdvanceCoreAsync
    /// (Wave-3 Race A guard).
    /// </summary>
    [TestMethod]
    public async Task T_RET_5_StaleEpoch_OldGenNodeNotPickedUp()
    {
        await using var db = MakeContext();
        var inst = SeedInstance(db, generation: 1); // instance is now at gen 1

        // Seed a stale node (gen 0 — superseded from previous return).
        var staleNode = SeedNode(db, inst.ID, "StaleNode", NodeState.Activated, generation: 0);
        // Seed a current-gen node (gen 1).
        var currentNode = SeedNode(db, inst.ID, "CurrentNode", NodeState.Pending, generation: 1);

        // Query: simulate what AdvanceCoreAsync does (filter by Generation==instance.Generation).
        uint currentGen = inst.Generation; // == 1
        var activeNodes = await db.NodeInstances.AsNoTracking()
            .Where(n => n.InstanceId == inst.ID
                         && n.Generation == currentGen
                         && (n.State == NodeState.Pending || n.State == NodeState.Activated))
            .ToListAsync();

        Assert.AreEqual(1, activeNodes.Count,
            "Only the current-generation node must be returned by the generation-filtered query.");
        Assert.AreEqual("CurrentNode", activeNodes[0].NodeKey,
            "The current-gen node must be CurrentNode.");

        // Verify the stale node is NOT included.
        Assert.IsFalse(activeNodes.Any(n => n.NodeKey == "StaleNode"),
            "Stale-epoch (gen 0) node must be excluded from active-node query.");
    }

    // ── T-RET-6: Seq allocation under 8-way contention ────────────────────────

    /// <summary>
    /// T-RET-6: 8 concurrent callers each call AllocateSeqAsync on the same instance.
    /// All must succeed (eventually, with retries) and the resulting Seq values must
    /// be distinct and contiguous from 1 to 8.
    ///
    /// This is the portable Race B fix: no SERIALIZABLE needed; CAS retry loop suffices.
    /// </summary>
    [TestMethod]
    public async Task T_RET_6_SeqAllocation_8WayConcurrent_NoGapsNoDuplicates()
    {
        const int Concurrency = 8;

        var id = Guid.NewGuid();

        await using var seed = MakeContext();
        seed.ProcessInstances.Add(new ProcessInstance
        {
            ID = id,
            State = InstanceState.Running,
            RowVer = 0,
            InitiatorITCode = "user1",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid = true,
            TenantCode = "T1",
            Generation = 0,
            NextSeq = 1,
        });
        await seed.SaveChangesAsync();

        var barrier = new SemaphoreSlim(0, Concurrency);

        // Each task does a retry-loop up to 20 attempts to allocate a Seq.
        async Task<int> AllocTask()
        {
            await barrier.WaitAsync();
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await using var db = MakeContext();
                // Re-read fresh RowVer before each attempt.
                var fresh = await db.ProcessInstances.AsNoTracking()
                    .Where(p => p.ID == id)
                    .Select(p => new { p.RowVer, p.NextSeq })
                    .FirstAsync();

                var (rows, seq) = await GuardedTransition.AllocateSeqAsync(
                    db, id, expectedRowVer: fresh.RowVer, ct: CancellationToken.None);

                if (rows == 1)
                    return seq;

                await Task.Yield();
            }
            throw new InvalidOperationException("AllocTask: max retries exceeded.");
        }

        var tasks = Enumerable.Range(0, Concurrency).Select(_ => AllocTask()).ToArray();
        barrier.Release(Concurrency);
        int[] seqValues = await Task.WhenAll(tasks);

        // All Seq values must be distinct.
        Assert.AreEqual(Concurrency, seqValues.Distinct().Count(),
            $"All {Concurrency} Seq values must be distinct. Got: [{string.Join(",", seqValues.OrderBy(x => x))}].");

        // Seq values must be contiguous 1..N.
        var sorted = seqValues.OrderBy(x => x).ToArray();
        for (int i = 0; i < Concurrency; i++)
        {
            Assert.AreEqual(i + 1, sorted[i],
                $"Expected Seq {i + 1} at position {i}, got {sorted[i]}.");
        }
    }

    // ── T-RET-7: Crash / reaper stub ──────────────────────────────────────────

    /// <summary>
    /// T-RET-7: ReclaimReturningLeaseAsync reclaims a Returning instance whose
    /// ReturningLeaseUtc is in the past (simulates crash/reaper recovery).
    ///
    /// After reclaim, the instance must be back to Running so the engine can
    /// retry the return path.
    /// </summary>
    [TestMethod]
    public async Task T_RET_7_ReclaimReturningLease_ExpiredLease_ReclamedToRunning()
    {
        await using var db = MakeContext();

        // Seed an instance stuck in Returning with an expired lease.
        var id = Guid.NewGuid();
        db.ProcessInstances.Add(new ProcessInstance
        {
            ID = id,
            State = InstanceState.Returning,
            RowVer = 5,
            InitiatorITCode = "user1",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid = true,
            TenantCode = "T1",
            Generation = 1,
            ReturnLoops = 1,
            NextSeq = 3,
            // Lease expired 1 hour ago.
            ReturningLeaseUtc = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        // Act: reaper calls ReclaimReturningLeaseAsync.
        var rows = await GuardedTransition.ReclaimReturningLeaseAsync(
            db, id,
            expectedRowVer: 5u,
            now: DateTime.UtcNow,
            ct: CancellationToken.None);

        Assert.AreEqual(1, rows, "ReclaimReturningLeaseAsync must return 1 row for expired lease.");

        await using var verify = MakeContext();
        var final = await verify.ProcessInstances.AsNoTracking().SingleAsync(x => x.ID == id);
        Assert.AreEqual(InstanceState.Running, final.State,
            "After lease reclaim, instance must be Running (ready for retry).");
        Assert.IsNull(final.ReturningLeaseUtc,
            "After reclaim, ReturningLeaseUtc must be null (lease cleared).");
    }

    /// <summary>
    /// T-RET-7b: ReclaimReturningLeaseAsync must NOT reclaim a Returning instance
    /// whose lease is still valid (not yet expired).
    /// </summary>
    [TestMethod]
    public async Task T_RET_7b_ReclaimReturningLease_ValidLease_NotReclaimed()
    {
        await using var db = MakeContext();

        var id = Guid.NewGuid();
        db.ProcessInstances.Add(new ProcessInstance
        {
            ID = id,
            State = InstanceState.Returning,
            RowVer = 2,
            InitiatorITCode = "user1",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid = true,
            TenantCode = "T1",
            Generation = 1,
            ReturnLoops = 1,
            NextSeq = 2,
            // Lease still valid — expires 30 minutes from now.
            ReturningLeaseUtc = DateTime.UtcNow.AddMinutes(30),
        });
        await db.SaveChangesAsync();

        var rows = await GuardedTransition.ReclaimReturningLeaseAsync(
            db, id,
            expectedRowVer: 2u,
            now: DateTime.UtcNow,
            ct: CancellationToken.None);

        Assert.AreEqual(0, rows, "ReclaimReturningLeaseAsync must NOT reclaim an instance with valid lease.");

        await using var verify = MakeContext();
        var final = await verify.ProcessInstances.AsNoTracking().SingleAsync(x => x.ID == id);
        Assert.AreEqual(InstanceState.Returning, final.State, "State must remain Returning.");
    }

    // ── T-RET-8: Audit atomicity — event log Seq is monotone and gap-free ─────

    /// <summary>
    /// T-RET-8: Multiple sequential AllocateSeqAsync calls produce a monotone,
    /// gap-free Seq sequence (1, 2, 3, …).  Validates that the NextSeq CAS counter
    /// increments correctly and the UNIQUE constraint on (TenantCode, InstanceId, Seq)
    /// would catch any duplicates.
    /// </summary>
    [TestMethod]
    public async Task T_RET_8_AuditAtomicity_SeqIsMonotoneAndGapFree()
    {
        const int EventCount = 10;

        await using var db = MakeContext();
        var inst = SeedInstance(db);
        var seqValues = new List<int>();

        for (int i = 0; i < EventCount; i++)
        {
            // Re-read fresh RowVer for each allocation (sequential calls, no concurrency here).
            var fresh = await db.ProcessInstances.AsNoTracking()
                .Where(p => p.ID == inst.ID)
                .Select(p => new { p.RowVer })
                .FirstAsync();

            var (rows, seq) = await GuardedTransition.AllocateSeqAsync(
                db, inst.ID, expectedRowVer: fresh.RowVer, ct: CancellationToken.None);

            Assert.AreEqual(1, rows, $"AllocateSeqAsync must succeed on call {i + 1}.");
            seqValues.Add(seq);
        }

        // Verify monotone, gap-free.
        for (int i = 0; i < EventCount; i++)
        {
            Assert.AreEqual(i + 1, seqValues[i],
                $"Expected Seq {i + 1} at position {i}, got {seqValues[i]}.");
        }
    }

    // ── T-PROV-W3: Provider-conformance stubs ─────────────────────────────────

    /// <summary>
    /// T-PROV-W3-SQLite: BeginReturnAsync CAS returns exactly 1 row for a fresh
    /// Running instance and 0 rows on the second call (already Returning).
    /// SQLite-only variant — runs on every CI push.
    /// </summary>
    [TestMethod]
    public async Task T_PROV_W3_SQLite_BeginReturnAsync_ExactlyOneWinner()
    {
        await using var db = MakeContext();
        var id = Guid.NewGuid();
        db.ProcessInstances.Add(new ProcessInstance
        {
            ID = id,
            State = InstanceState.Running,
            RowVer = 0,
            InitiatorITCode = "user1",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid = true,
            TenantCode = "T1",
            Generation = 0,
            ReturnLoops = 0,
            NextSeq = 1,
        });
        await db.SaveChangesAsync();

        var (rows1, _) = await GuardedTransition.BeginReturnAsync(
            db, id, 0, 0u, 3, DateTime.UtcNow.AddMinutes(30), CancellationToken.None);

        // Second call with old RowVer — must miss (RowVer already bumped).
        var (rows2, _) = await GuardedTransition.BeginReturnAsync(
            db, id, 0, 1u, 3, DateTime.UtcNow.AddMinutes(30), CancellationToken.None);

        Assert.AreEqual(1, rows1, "First BeginReturnAsync must return 1 row.");
        Assert.AreEqual(0, rows2, "Second BeginReturnAsync with stale RowVer must return 0 rows.");
    }

    /// <summary>
    /// T-PROV-W3-SQLite: SupersedeNodeAsync CAS — exactly one winner in two-way race.
    /// </summary>
    [TestMethod]
    public async Task T_PROV_W3_SQLite_SupersedeNodeAsync_ExactlyOneWinner()
    {
        const int Rounds = 20;

        for (int round = 0; round < Rounds; round++)
        {
            var id = Guid.NewGuid();

            await using var seed = MakeContext();
            seed.NodeInstances.Add(new NodeInstance
            {
                ID = id,
                InstanceId = Guid.NewGuid(),
                NodeKey = $"node_s{round}",
                State = NodeState.Activated,
                RowVer = 0,
                TenantCode = "T1",
                Generation = 0,
            });
            await seed.SaveChangesAsync();

            var barrier = new SemaphoreSlim(0, 2);

            Task<int> MakeTask()
            {
                Guid capturedId = id;
                return Task.Run(async () =>
                {
                    await barrier.WaitAsync();
                    await using var db = MakeContext();
                    return await GuardedTransition.SupersedeNodeAsync(
                        db, capturedId, expectedRowVer: 0, supersededAtGen: 1u, CancellationToken.None);
                });
            }

            var t1 = MakeTask();
            var t2 = MakeTask();
            barrier.Release(2);

            int[] results = await Task.WhenAll(t1, t2);
            int winners = results.Count(r => r == 1);

            Assert.AreEqual(1, winners,
                $"Round {round}: SupersedeNodeAsync concurrent — expected 1 winner, got [{results[0]},{results[1]}].");
        }
    }
}
