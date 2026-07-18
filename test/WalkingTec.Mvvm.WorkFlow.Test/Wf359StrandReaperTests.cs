#nullable enable
// WF-359: Standing strand-reaper (Phase-4) — tests for ReDriveStrandedSequentialNodesAsync.
//
// Tests:
//   T-WF359-01: Strand seed → single tick → pointer advances (re-drive succeeds).
//   T-WF359-02: Healthy instance (task Pending at pointer) → NOT re-driven (guard works).
//   T-WF359-03: StrandReaperBatchSize=0 → reaper gate-off → stranded node NOT re-driven.
//   T-WF359-04: No engine injected → gate-off → stranded node NOT re-driven.
//
// Strategy: use WfSequentialTestContext (SQLite shared-in-memory) to start a real two-step
// Sequential flow, then manually corrupt DB state to simulate the crash-window strand
// (terminal AutoApproved task at pointer, pointer not yet advanced), then run one timer tick
// and assert the pointer was advanced by the reaper.
//
// DB: SQLite shared-in-memory (WfSequentialTestContext from SequentialTests.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class Wf359StrandReaperTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"Wf359_{Guid.NewGuid():N}";
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private WfSequentialTestContext MakeDb() =>
        new($"{_dbName}?mode=memory&cache=shared");

    /// <summary>
    /// #709/#711 precedent (see T_ABBA_2902_CONC_01 in AbbaFixTests.cs): genuinely-racing
    /// concurrency tests must not use the class's shared-cache in-memory <see cref="MakeDb"/>
    /// — connection pooling + coarse table locking there are unsafe under real concurrent
    /// contention. Only <see cref="T_WF359_05_ConcurrentTicks_SameStrand_AdvancesExactlyOnce"/>
    /// (the sole test in this class that races two distinct executor contexts via
    /// Task.WhenAll) uses this helper; every other test stays on <see cref="MakeDb"/> since it
    /// only ever has one actor writing at a time.
    /// </summary>
    private static WfSequentialTestContext MakeFileWalDb(string dbPath) =>
        new(dbPath, SqliteTestDbMode.FileWal);

    /// <summary>Build a real engine (Sequential handler wired).</summary>
    private IWorkflowEngine MakeEngine(WfSequentialTestContext db)
    {
        var opts = new WorkFlowOptions();
        var resolver = new DefaultApproverResolverExposed(opts, db);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
        return WorkflowEngine_Exposed.Create(db, dispatcher, NullLogger.Instance);
    }

    /// <summary>Build a WorkflowTimerExecutor with the given db and optional engine.</summary>
    private static WorkflowTimerExecutor MakeExecutor(
        WfSequentialTestContext db,
        IWorkflowEngine? engine,
        int strandReaperBatchSize = 50)
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

    /// <summary>Seed a ProcessDefinitionVersion with a 2-approver Sequential graph.</summary>
    private async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfSequentialTestContext db,
        string approver1,
        string approver2)
    {
        var graphJson = WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = "Wf359Graph",
            Name = "Wf359Graph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start",    Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef
                    {
                        Type  = "User",
                        Value = $"{approver1},{approver2}",
                    },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });

        var version = new ProcessDefinitionVersion
        {
            ID              = Guid.NewGuid(),
            DefinitionId    = Guid.NewGuid(),
            VersionNo       = 1,
            SchemaVersion   = 1,
            GraphJson       = graphJson,
            ContentHash     = "hash-wf359-" + Guid.NewGuid().ToString("N"),
            PublishedAt     = DateTime.UtcNow,
            PublishedBy     = "test",
            IsValid         = true,
        };
        db.Set<ProcessDefinitionVersion>().Add(version);
        await db.SaveChangesAsync();
        return version;
    }

    /// <summary>
    /// Simulate the crash-window strand: mark task at SequencePointer==0 as AutoApproved
    /// but do NOT advance the pointer (pointer stays at 0, simulating crash after claim commit
    /// but before SystemContinueTaskAsync ran).
    /// </summary>
    private static async Task InjectStrandAsync(
        WfSequentialTestContext db,
        Guid nodeInstanceId,
        int pointerToStrand)
    {
        // Find the task at that sequence order and mark it AutoApproved (terminal).
        var task = await db.Set<ApprovalTask>()
            .Where(t => t.NodeInstanceId == nodeInstanceId
                         && t.SequenceOrder == pointerToStrand)
            .SingleAsync();

        task.State = TaskState.AutoApproved;
        task.ActedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // The NodeInstance.SequencePointer intentionally stays at `pointerToStrand` —
        // this is the strand: task is terminal but pointer was never advanced.
    }

    // ── Test 1: Strand seed → reaper tick → pointer advances ─────────────────

    /// <summary>
    /// T-WF359-01: A running Sequential instance where task at SequencePointer==0 is
    /// AutoApproved (strand signature) must be re-driven by Phase-4 in one tick.
    /// After the tick, SequencePointer must advance from 0 to 1 (or the instance must
    /// reach InstanceApproved if it was the last step).
    /// </summary>
    [TestMethod]
    public async Task T_WF359_01_StrandThenReap_PointerAdvances()
    {
        const string A1 = "reap_alice";
        const string A2 = "reap_bob";

        // ── Seed: use real engine to start a 2-step Sequential flow ──────────
        await using var seedDb = MakeDb();
        var version = await SeedVersionAsync(seedDb, A1, A2);
        var engine  = MakeEngine(seedDb);

        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State, "T-WF359-01: instance must be Running after start");

        // Read the node instance to get its ID.
        await using var readDb = MakeDb();
        var nodeInst = await readDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        Assert.AreEqual(NodeState.Activated,      nodeInst.State,           "T-WF359-01: node must be Activated");
        Assert.AreEqual(ApproveMode.Sequential,   nodeInst.ApproveMode,     "T-WF359-01: node must be Sequential");
        Assert.AreEqual(0,                        nodeInst.SequencePointer, "T-WF359-01: pointer must start at 0");
        Assert.AreEqual(2,                        nodeInst.TotalRequired,   "T-WF359-01: TotalRequired must be 2");

        // Verify task at order 0 exists and is Pending.
        var task0 = await readDb.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);
        Assert.AreEqual(TaskState.Pending, task0.State, "T-WF359-01: step-0 task must be Pending before strand injection");

        // ── Inject strand: mark task at pointer 0 as AutoApproved, pointer stays at 0 ──
        await using var strandDb = MakeDb();
        await InjectStrandAsync(strandDb, nodeInst.ID, pointerToStrand: 0);

        // Verify the strand signature is in place.
        await using var checkDb = MakeDb();
        var strandedNode = await checkDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);
        Assert.AreEqual(0, strandedNode.SequencePointer, "T-WF359-01: pointer must still be 0 after strand injection");

        var strandedTask = await checkDb.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);
        Assert.AreEqual(TaskState.AutoApproved, strandedTask.State, "T-WF359-01: task 0 must be AutoApproved (strand)");

        // ── Run reaper tick via WorkflowTimerExecutor ─────────────────────────
        await using var execDb = MakeDb();
        var execEngine   = MakeEngine(execDb);
        var executor     = MakeExecutor(execDb, execEngine);

        await executor.RunTickAsync(DateTime.UtcNow, CancellationToken.None);

        // ── Assert: pointer must have advanced ────────────────────────────────
        await using var verifyDb = MakeDb();
        var afterNode = await verifyDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);

        // The reaper must have called AdvanceAsync which activates step-1 (pointer→1)
        // OR the node completes (CompletedApproved) and instance reaches Approved.
        // With a 2-step flow and only step-0 processed, the node stays Activated at pointer=1.
        bool advanced = afterNode.SequencePointer == 1
            || afterNode.State == NodeState.CompletedApproved
            || afterNode.State == NodeState.CompletedRejected;
        Assert.IsTrue(advanced,
            $"T-WF359-01: SequencePointer must have advanced (was 0, now {afterNode.SequencePointer}) " +
            $"or node completed (state={afterNode.State}) — Phase-4 reaper must re-drive the strand.");
    }

    // ── Test 2: Healthy instance (Pending task at pointer) → not re-driven ───

    /// <summary>
    /// T-WF359-02: A healthy Sequential instance (task at SequencePointer is Pending — normal state)
    /// must NOT be re-driven by the strand reaper. The reaper's guard (hasTerminalAtPointer) must
    /// return false and skip the node.
    ///
    /// Verifies the guard prevents spurious AdvanceAsync calls on healthy nodes.
    /// </summary>
    [TestMethod]
    public async Task T_WF359_02_HealthyInstance_NotRedriven()
    {
        const string A1 = "healthy_alice";
        const string A2 = "healthy_bob";

        // ── Seed: healthy 2-step Sequential flow (task at pointer is Pending) ──
        await using var seedDb = MakeDb();
        var version  = await SeedVersionAsync(seedDb, A1, A2);
        var engine   = MakeEngine(seedDb);

        var instance = await engine.StartAsync(version.ID, null, "initiator", null);
        Assert.AreEqual(InstanceState.Running, instance.State, "T-WF359-02: instance must be Running after start");

        await using var readDb = MakeDb();
        var nodeInst = await readDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        // Confirm healthy state: pointer==0, task at 0 is Pending.
        Assert.AreEqual(0,                  nodeInst.SequencePointer, "T-WF359-02: pointer must be 0");
        var task0 = await readDb.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleAsync(t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == 0);
        Assert.AreEqual(TaskState.Pending, task0.State, "T-WF359-02: task 0 must be Pending (healthy, not a strand)");

        // ── Run reaper tick — healthy node must NOT be re-driven ──────────────
        await using var execDb    = MakeDb();
        var execEngine = MakeEngine(execDb);
        var executor   = MakeExecutor(execDb, execEngine);

        await executor.RunTickAsync(DateTime.UtcNow, CancellationToken.None);

        // ── Assert: pointer must NOT have changed (no re-drive) ───────────────
        await using var verifyDb  = MakeDb();
        var afterNode = await verifyDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);

        Assert.AreEqual(0,                  afterNode.SequencePointer, "T-WF359-02: pointer must stay at 0 — healthy node must not be re-driven");
        Assert.AreEqual(NodeState.Activated, afterNode.State,           "T-WF359-02: node must stay Activated (healthy, waiting for human approval)");
    }

    // ── Test 3: StrandReaperBatchSize=0 → gate-off → strand NOT re-driven ────

    /// <summary>
    /// T-WF359-03: When StrandReaperBatchSize == 0, the Phase-4 gate must be off and
    /// a strand must NOT be re-driven.
    ///
    /// Verifies the opt-out mechanism: setting batch size to 0 fully disables the reaper.
    /// </summary>
    [TestMethod]
    public async Task T_WF359_03_BatchSizeZero_ReaperDisabled_StrandNotRedriven()
    {
        const string A1 = "gate_alice";
        const string A2 = "gate_bob";

        // ── Seed and inject strand ────────────────────────────────────────────
        await using var seedDb = MakeDb();
        var version  = await SeedVersionAsync(seedDb, A1, A2);
        var engine   = MakeEngine(seedDb);

        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        await using var readDb = MakeDb();
        var nodeInst = await readDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        await using var strandDb = MakeDb();
        await InjectStrandAsync(strandDb, nodeInst.ID, pointerToStrand: 0);

        // ── Run reaper tick with BatchSize=0 (gate-off) ───────────────────────
        await using var execDb   = MakeDb();
        var execEngine  = MakeEngine(execDb);
        var executor    = MakeExecutor(execDb, execEngine, strandReaperBatchSize: 0);

        await executor.RunTickAsync(DateTime.UtcNow, CancellationToken.None);

        // ── Assert: strand remains (pointer still at 0) ───────────────────────
        await using var verifyDb = MakeDb();
        var afterNode = await verifyDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);

        Assert.AreEqual(0, afterNode.SequencePointer,
            "T-WF359-03: pointer must stay at 0 — reaper must be disabled when StrandReaperBatchSize==0");
        Assert.AreEqual(NodeState.Activated, afterNode.State,
            "T-WF359-03: node must stay Activated — gate-off prevents re-drive");
    }

    // ── Test 4: No engine injected → gate-off → strand NOT re-driven ─────────

    /// <summary>
    /// T-WF359-04: When no IWorkflowEngine is injected into WorkflowTimerExecutor,
    /// the Phase-4 engine-null gate must fire and the stranded node must NOT be re-driven.
    ///
    /// Verifies that the null-engine guard prevents NullReferenceException and correctly
    /// skips the reaper when no engine is available.
    /// </summary>
    [TestMethod]
    public async Task T_WF359_04_NoEngine_ReaperDisabled_StrandNotRedriven()
    {
        const string A1 = "noengine_alice";
        const string A2 = "noengine_bob";

        // ── Seed and inject strand ────────────────────────────────────────────
        await using var seedDb = MakeDb();
        var version  = await SeedVersionAsync(seedDb, A1, A2);
        var engine   = MakeEngine(seedDb);

        var instance = await engine.StartAsync(version.ID, null, "initiator", null);

        await using var readDb = MakeDb();
        var nodeInst = await readDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

        await using var strandDb = MakeDb();
        await InjectStrandAsync(strandDb, nodeInst.ID, pointerToStrand: 0);

        // ── Run reaper tick with engine=null ──────────────────────────────────
        await using var execDb   = MakeDb();
        var executor = MakeExecutor(execDb, engine: null); // no engine injected

        await executor.RunTickAsync(DateTime.UtcNow, CancellationToken.None);

        // ── Assert: strand remains (pointer still at 0) ───────────────────────
        await using var verifyDb = MakeDb();
        var afterNode = await verifyDb.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID);

        Assert.AreEqual(0, afterNode.SequencePointer,
            "T-WF359-04: pointer must stay at 0 — null engine gate must prevent re-drive");
        Assert.AreEqual(NodeState.Activated, afterNode.State,
            "T-WF359-04: node must stay Activated — no engine means no re-drive");
    }

    // ── Test 5: Concurrent ticks on same strand — pointer advances exactly once ─

    /// <summary>
    /// T_WF359_05_ConcurrentTicks_SameStrand_AdvancesExactlyOnce:
    ///
    /// Two reaper executor instances tick CONCURRENTLY on a single strand
    /// (pointer P=0, TotalRequired=3 so mid-chain advance happens, not completion).
    /// Only one host must win the RowVer CAS and advance the pointer to P+1.
    /// The other must lose the CAS and return AlreadyHandled — no double-advance.
    ///
    /// Assertions:
    ///   - SequencePointer == 1 (exactly P+1, never P+2)
    ///   - Exactly one Pending task at order 1 (no orphan / no duplicate)
    ///   - Instance is still Running (mid-chain, not prematurely Approved)
    /// </summary>
    [TestMethod]
    public async Task T_WF359_05_ConcurrentTicks_SameStrand_AdvancesExactlyOnce()
    {
        const string A1 = "race_alice";
        const string A2 = "race_bob";
        const string A3 = "race_carol";

        // #709/#711 precedent (see T_ABBA_2902_CONC_01 in AbbaFixTests.cs): this test races
        // two distinct executor contexts concurrently (Task.WhenAll below) against the same
        // logical database — the T-CONC/T-ABBA-CONC pattern that #709 round 2 moved off
        // shared-cache in-memory (this class's MakeDb(), still used by every OTHER — sequential,
        // non-racing — test here) onto a dedicated per-test file-WAL database. See
        // SqliteSharedMemoryFixture's remarks for why shared-cache in-memory's connection
        // pooling + coarse table locking are unsafe under genuine concurrent contention.
        // Seeding is inlined against the file-WAL database rather than through MakeDb().
        var dbPath = SqliteSharedMemoryFixture.NewFileDbPath("Wf359Conc");
        try
        {
            // ── Seed: 3-approver Sequential so the advance is mid-chain (pointer 0→1, total=3) ──
            await using var seedDb = MakeFileWalDb(dbPath);
            seedDb.Database.EnsureCreated();

            // Build a 3-approver version directly (SeedVersionAsync only supports 2 approvers).
            var graphJson = WorkflowGraphSerializer.Serialize(new WorkflowGraph
            {
                Key = "Wf359RaceGraph",
                Name = "Wf359RaceGraph",
                Nodes = new List<NodeDef>
                {
                    new() { NodeKey = "start",    Kind = NodeKind.Start },
                    new()
                    {
                        NodeKey      = "approval1",
                        Kind         = NodeKind.Approval,
                        ApproveMode  = ApproveMode.Sequential,
                        ApproverRule = new ApproverRuleDef
                        {
                            Type  = "User",
                            Value = $"{A1},{A2},{A3}",
                        },
                    },
                    new() { NodeKey = "end", Kind = NodeKind.End },
                },
                Transitions = new List<TransitionDef>
                {
                    new() { From = "start",     To = "approval1" },
                    new() { From = "approval1", To = "end"       },
                },
            });

            var version = new ProcessDefinitionVersion
            {
                ID              = Guid.NewGuid(),
                DefinitionId    = Guid.NewGuid(),
                VersionNo       = 1,
                SchemaVersion   = 1,
                GraphJson       = graphJson,
                ContentHash     = "hash-wf359-race-" + Guid.NewGuid().ToString("N"),
                PublishedAt     = DateTime.UtcNow,
                PublishedBy     = "test",
                IsValid         = true,
            };
            seedDb.Set<ProcessDefinitionVersion>().Add(version);
            await seedDb.SaveChangesAsync();

            var engine  = MakeEngine(seedDb);
            var instance = await engine.StartAsync(version.ID, null, "initiator", null);
            Assert.AreEqual(InstanceState.Running, instance.State,
                "T_WF359_05: instance must be Running after start");

            // ── Read node (TotalRequired should be 3, pointer at 0) ─────────────────
            await using var readDb = MakeFileWalDb(dbPath);
            var nodeInst = await readDb.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.InstanceId == instance.ID && n.NodeKind == NodeKind.Approval);

            Assert.AreEqual(3,                      nodeInst.TotalRequired,   "T_WF359_05: TotalRequired must be 3");
            Assert.AreEqual(0,                      nodeInst.SequencePointer, "T_WF359_05: pointer must start at 0");
            Assert.AreEqual(NodeState.Activated,    nodeInst.State,           "T_WF359_05: node must be Activated");

            // ── Inject strand: task at P=0 marked AutoApproved, pointer stays at 0 ──
            await using var strandDb = MakeFileWalDb(dbPath);
            await InjectStrandAsync(strandDb, nodeInst.ID, pointerToStrand: 0);

            // ── Build TWO executors on the same file-WAL DB ───────────────────────────
            // Each actor opens its own distinct physical connection to the same file (see
            // BuildFileWalConnectionString's Pooling=False remarks); WAL gives SQLite's own
            // lock manager real headroom to serialize the two writers instead of racing a
            // coarse shared-cache table lock (Task.WhenAll simulates two concurrent hosts
            // ticking at the same wall-clock time).
            var execDb1 = MakeFileWalDb(dbPath);
            var execDb2 = MakeFileWalDb(dbPath);
            try
            {
                var execEngine1 = MakeEngine(execDb1);
                var executor1   = MakeExecutor(execDb1, execEngine1);

                var execEngine2 = MakeEngine(execDb2);
                var executor2   = MakeExecutor(execDb2, execEngine2);

                var now = DateTime.UtcNow;

                // ── Fire both ticks concurrently ─────────────────────────────────────
                await Task.WhenAll(
                    executor1.RunTickAsync(now, CancellationToken.None),
                    executor2.RunTickAsync(now, CancellationToken.None));

                // ── Assert: pointer advanced exactly once ────────────────────────────
                await using var verifyDb = MakeFileWalDb(dbPath);

                var afterNode = await verifyDb.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleAsync(n => n.ID == nodeInst.ID);

                Assert.AreEqual(1, afterNode.SequencePointer,
                    $"T_WF359_05: SequencePointer must be exactly 1 (P+1), not {afterNode.SequencePointer} — concurrent CAS must allow exactly one winner");

                // Exactly one Pending task at order 1 (no orphan / no duplicate).
                var pendingAtOne = await verifyDb.Set<ApprovalTask>()
                    .AsNoTracking()
                    .CountAsync(t => t.NodeInstanceId == nodeInst.ID
                                      && t.SequenceOrder == 1
                                      && t.State == TaskState.Pending);

                Assert.AreEqual(1, pendingAtOne,
                    $"T_WF359_05: exactly one Pending task must exist at order 1 (got {pendingAtOne})");

                // Instance must still be Running (mid-chain — not prematurely Approved).
                var afterInstance = await verifyDb.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleAsync(i => i.ID == instance.ID);

                Assert.AreEqual(InstanceState.Running, afterInstance.State,
                    $"T_WF359_05: instance must still be Running (mid-chain advance); got {afterInstance.State}");
            }
            finally
            {
                await execDb1.DisposeAsync();
                await execDb2.DisposeAsync();
            }
        }
        finally
        {
            SqliteSharedMemoryFixture.DeleteFileDatabase(dbPath);
        }
    }
}
