#nullable enable
// Issue #665: strand-reaper candidate SELECT determinism + starvation-proof ordering.
//
// Tests:
//   T-WF665-01: With more healthy Sequential candidates than StrandReaperBatchSize, a
//               stranded node with the OLDEST ActivatedAt — even though it is minted (and
//               therefore physically inserted) LAST — must still land inside the first
//               reaper window and be re-driven. This proves the fix orders by staleness
//               (oldest-ActivatedAt-first), not by an arbitrary/physical row order that a
//               bare `.Take(N)` without `OrderBy` would otherwise depend on.
//
// Strategy: mirrors Wf359StrandReaperTests — WfSequentialTestContext (SQLite shared-memory),
// real engine StartAsync to mint healthy/stranded nodes, direct DB manipulation to inject the
// strand signature (task at SequencePointer terminal, pointer not advanced) and to backdate
// the stranded node's ActivatedAt, then one WorkflowTimerExecutor.RunTickAsync tick.
//
// DB: SQLite shared-in-memory (WfSequentialTestContext from SequentialTests.cs).

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class Wf665ReaperDeterminismTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"Wf665_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        using var db = MakeDb();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    // ── Helpers (mirrors Wf359StrandReaperTests) ─────────────────────────────────

    private WfSequentialTestContext MakeDb() =>
        new($"{_dbName}?mode=memory&cache=shared");

    /// <summary>Build a real engine (Sequential handler wired).</summary>
    private static IWorkflowEngine MakeEngine(WfSequentialTestContext db)
    {
        var opts = new WorkFlowOptions();
        var resolver = new DefaultApproverResolverExposed(opts, db);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
        return WorkflowEngine_Exposed.Create(db, dispatcher, NullLogger.Instance);
    }

    /// <summary>Build a WorkflowTimerExecutor with the given db and a fixed strand-reaper batch size.</summary>
    private static WorkflowTimerExecutor MakeExecutor(
        WfSequentialTestContext db,
        IWorkflowEngine? engine,
        int strandReaperBatchSize)
    {
        var opts = Options.Create(new WorkFlowOptions
        {
            StrandReaperBatchSize = strandReaperBatchSize,
        });
        return new WorkflowTimerExecutor(
            db, opts,
            NullLogger<WorkflowTimerExecutor>.Instance,
            engine);
    }

    /// <summary>
    /// Seed one healthy (non-stranded) single-approver Sequential node: after StartAsync the
    /// node is Activated, TotalRequired=1, SequencePointer=0, task at order 0 is Pending — a
    /// perfectly normal "waiting for human approval" candidate that must never be re-driven.
    /// </summary>
    private async Task SeedHealthyNodeAsync(string approverITCode)
    {
        await using var db = MakeDb();

        var version = new ProcessDefinitionVersion
        {
            ID            = Guid.NewGuid(),
            DefinitionId  = Guid.NewGuid(),
            VersionNo     = 1,
            SchemaVersion = 1,
            GraphJson     = SeqTestGraphs.SingleApprover(approverITCode),
            ContentHash   = "hash-wf665-healthy-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            IsValid       = true,
        };
        db.Set<ProcessDefinitionVersion>().Add(version);
        await db.SaveChangesAsync();

        var engine = MakeEngine(db);
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State,
            "T-WF665: healthy seed instance must be Running after start");
    }

    /// <summary>
    /// Seed a 2-approver Sequential node, inject the crash-window strand signature (task at
    /// SequencePointer==0 marked AutoApproved, pointer left at 0 — same injection Wf359 uses),
    /// then backdate <see cref="NodeInstance.ActivatedAt"/> to <paramref name="activatedAt"/>.
    ///
    /// Backdating (rather than relying on insertion order) proves the reaper's ordering keys
    /// off genuine staleness, not off the incidental physical/insertion position of the row —
    /// this node is minted AFTER all the healthy nodes in the test but must still sort FIRST.
    /// </summary>
    private async Task<Guid> SeedStrandedNodeAsync(string approver1, string approver2, DateTime activatedAt)
    {
        await using var seedDb = MakeDb();

        var version = new ProcessDefinitionVersion
        {
            ID            = Guid.NewGuid(),
            DefinitionId  = Guid.NewGuid(),
            VersionNo     = 1,
            SchemaVersion = 1,
            GraphJson     = SeqTestGraphs.TwoApprovers(approver1, approver2),
            ContentHash   = "hash-wf665-strand-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            IsValid       = true,
        };
        seedDb.Set<ProcessDefinitionVersion>().Add(version);
        await seedDb.SaveChangesAsync();

        var engine = MakeEngine(seedDb);
        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State,
            "T-WF665: stranded seed instance must be Running after start");

        var nodeInst = await seedDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        // ── Inject strand: task at pointer 0 terminal (AutoApproved), pointer stays at 0 ──
        await using var strandDb = MakeDb();
        var task0 = await strandDb.Set<ApprovalTask>()
            .Where(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0)
            .SingleAsync();
        task0.State = TaskState.AutoApproved;
        task0.ActedAtUtc = DateTime.UtcNow;
        await strandDb.SaveChangesAsync();

        // ── Backdate ActivatedAt so this (last-minted) node is the OLDEST candidate ──
        await using var backdateDb = MakeDb();
        var nodeToBackdate = await backdateDb.Set<NodeInstance>()
            .SingleAsync(n => n.ID == nodeInst.ID);
        nodeToBackdate.ActivatedAt = activatedAt;
        await backdateDb.SaveChangesAsync();

        return nodeInst.ID;
    }

    // ── Test: oldest-ActivatedAt-first keeps the strand inside a truncated window ────────────

    /// <summary>
    /// T-WF665-01: seed StrandReaperBatchSize+2 healthy Sequential nodes (recently activated),
    /// then one additional stranded node minted LAST but with a far-in-the-past ActivatedAt.
    /// Total candidates (healthy+1) exceed StrandReaperBatchSize, so a naive `.Take(N)` without
    /// deterministic ordering could easily miss the strand (especially one inserted last).
    ///
    /// After one reaper tick, the stranded node must have been re-driven — proving the
    /// oldest-ActivatedAt-first ordering keeps genuinely old/stuck nodes inside the window
    /// regardless of how many healthy candidates crowd the same predicate, and regardless of
    /// insertion order.
    /// </summary>
    [TestMethod]
    public async Task T_WF665_01_OldestActivatedFirst_StrandStaysInsideWindow_DespiteExceedingBatchSize()
    {
        const int batchSize = 3;
        const int healthyCount = batchSize + 2; // 5 healthy + 1 stranded = 6 candidates > batchSize(3)

        var now = DateTime.UtcNow;

        // Seed healthy nodes FIRST — physically inserted before the strand, so a naive
        // insertion-order Take() would favor them over a strand inserted afterward.
        for (int i = 0; i < healthyCount; i++)
        {
            await SeedHealthyNodeAsync($"wf665_healthy_{i}");
        }

        // Seed the stranded node LAST, but with ActivatedAt one full day in the past.
        var strandedNodeId = await SeedStrandedNodeAsync(
            "wf665_alice", "wf665_bob", activatedAt: now.AddDays(-1));

        // ── Run ONE reaper tick with a batch size smaller than the candidate pool ──
        await using var execDb = MakeDb();
        var execEngine = MakeEngine(execDb);
        var executor = MakeExecutor(execDb, execEngine, batchSize);

        await executor.RunTickAsync(DateTime.UtcNow, CancellationToken.None);

        // ── Assert: the stranded node WAS re-driven despite batchSize < total candidates ──
        await using var verifyDb = MakeDb();
        var afterNode = await verifyDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == strandedNodeId);

        bool advanced = afterNode.SequencePointer == 1
            || afterNode.State == NodeState.CompletedApproved
            || afterNode.State == NodeState.CompletedRejected;

        Assert.IsTrue(advanced,
            $"T-WF665-01: the stranded node (oldest ActivatedAt) must be inside the first reaper " +
            $"window and re-driven, even though {healthyCount} healthy candidates + itself = " +
            $"{healthyCount + 1} total candidates exceed StrandReaperBatchSize={batchSize}. " +
            $"SequencePointer={afterNode.SequencePointer}, State={afterNode.State}.");
    }

    /// <summary>
    /// T-WF665-02: sanity check that the candidate SELECT's ordering is stable/deterministic —
    /// running two ticks back-to-back on an untouched healthy population (no strand present)
    /// must not re-drive anything (pointer stays 0) and must not throw. Guards against a
    /// regression where the ORDER BY expression itself fails to translate on SQLite (this
    /// project's test provider) even though it targets the other 6 supported providers too.
    /// </summary>
    [TestMethod]
    public async Task T_WF665_02_OrderByTranslates_HealthyPopulation_NeverRedriven()
    {
        const int batchSize = 2;
        const int healthyCount = batchSize + 3;

        for (int i = 0; i < healthyCount; i++)
        {
            await SeedHealthyNodeAsync($"wf665_stable_{i}");
        }

        await using var execDb = MakeDb();
        var execEngine = MakeEngine(execDb);
        var executor = MakeExecutor(execDb, execEngine, batchSize);

        // Two ticks — the ORDER BY must translate cleanly on every call, and no healthy node
        // (task Pending at pointer 0) may ever be re-driven.
        await executor.RunTickAsync(DateTime.UtcNow, CancellationToken.None);
        await executor.RunTickAsync(DateTime.UtcNow, CancellationToken.None);

        await using var verifyDb = MakeDb();
        var allPointersZero = await verifyDb.Set<NodeInstance>()
            .AsNoTracking()
            .Where(n => n.NodeKind == NodeKind.Approval)
            .AllAsync(n => n.SequencePointer == 0 && n.State == NodeState.Activated);

        Assert.IsTrue(allPointersZero,
            "T-WF665-02: healthy nodes must never be re-driven by the strand reaper, and the " +
            "deterministic ORDER BY must translate without error on every tick.");
    }
}
