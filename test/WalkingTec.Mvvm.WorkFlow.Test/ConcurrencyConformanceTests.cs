#nullable enable
// WF-3 — ProviderConformance concurrency gate.
//
// Strategy: reuse the PROVEN WF-0 app-incremented uint RowVer / guarded-CAS pattern
// (from ConcurrencySpikeTests.cs in WalkingTec.Mvvm.Core.Test) but run it against
// real WorkFlow entities (ProcessInstance, NodeInstance, ApprovalTask, WorkflowTimer).
//
// SQLite shared-in-memory variant runs on every CI push.
// The six live-DB variants (SqlServer / PgSql / MySql / Oracle / DaMeng / SQLite-file)
// are tagged [TestCategory("ProviderConformance")] and require real connection-string
// env vars — they run nightly / on release, NEVER as part of a normal PR gate.
//
// The build fails if the SQLite CAS returns anything other than exactly 1 winner + N-1
// losers — this is the T-PROV-0 spec requirement (spec §10).
//
// SQLite shared in-memory setup is copied from AuditInterceptorTests.cs
// (same pattern as WF-0 / ConcurrencySpikeTests.cs).

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

// ─── Minimal DbContext wrapping real WorkFlow entities ────────────────────────

/// <summary>
/// Minimal SQLite-only DbContext that exercises the WorkFlow entity schema +
/// RowVer concurrency mapping without requiring a full consumer DataContext.
/// </summary>
internal sealed class WfTestContext : DbContext
{
    private readonly string _connectionString;

    public DbSet<ProcessInstance> ProcessInstances => Set<ProcessInstance>();
    public DbSet<NodeInstance> NodeInstances => Set<NodeInstance>();
    public DbSet<ApprovalTask> ApprovalTasks => Set<ApprovalTask>();
    public DbSet<WorkflowTimer> WorkflowTimers => Set<WorkflowTimer>();
    public DbSet<WorkflowEventLog> WorkflowEventLogs => Set<WorkflowEventLog>();

    public WfTestContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Shared-memory SQLite: all connections to the same name share one DB.
        optionsBuilder.UseSqlite($"DataSource={_connectionString}?mode=memory&cache=shared");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ProcessInstance — minimal columns for CAS test.
        modelBuilder.Entity<ProcessInstance>(b =>
        {
            b.ToTable("Wf_ProcessInstance_Test");
            b.HasKey(x => x.ID);
            b.Property(x => x.State);
            b.Property(x => x.RowVer);
            b.Property(x => x.InitiatorITCode).HasMaxLength(50).IsRequired();
            b.Property(x => x.DefinitionVersionId);
            b.Property(x => x.TenantCode).HasMaxLength(50);
            // IsValid (from PersistPoco) — required column.
            b.Property(x => x.IsValid);
            // Ignore navigations not relevant to CAS test.
            b.Ignore(x => x.DefinitionVersion);
            // Wave-3 (WF-16) columns.
            b.Property(x => x.Generation);
            b.Property(x => x.ReturnLoops);
            b.Property(x => x.NextSeq).HasDefaultValue(1);
            b.Property(x => x.ReturningLeaseUtc);
        });

        // NodeInstance — minimal columns for CAS test.
        modelBuilder.Entity<NodeInstance>(b =>
        {
            b.ToTable("Wf_NodeInstance_Test");
            b.HasKey(x => x.ID);
            b.Property(x => x.State);
            b.Property(x => x.RowVer);
            b.Property(x => x.NodeKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.InstanceId);
            b.Property(x => x.TenantCode).HasMaxLength(50);
            b.Property(x => x.TotalRequired);
            b.Property(x => x.ApprovePercent);
            b.Property(x => x.ApproveMode);
            b.Property(x => x.JoinExpectedArrivals).HasDefaultValue(0);
            b.Property(x => x.JoinArrivedCount).HasDefaultValue(0);
            b.Ignore(x => x.Instance);
            // Wave-3 (WF-16) columns.
            b.Property(x => x.Generation);
            b.Property(x => x.SupersededAtGen);
            // Non-filtered unique index for MintNodeInstanceGuardedAsync idempotency.
            b.HasIndex(x => new { x.TenantCode, x.InstanceId, x.NodeKey, x.Generation })
             .IsUnique()
             .HasDatabaseName("IX_Wf_NodeInstance_Test_TenantCode_InstanceId_NodeKey_Generation");
            // Wave-4 (WF-18) column.
            b.Property(x => x.ApproverSetEpoch).HasDefaultValue(0u);
        });

        // ApprovalTask — minimal columns for CAS test.
        modelBuilder.Entity<ApprovalTask>(b =>
        {
            b.ToTable("Wf_ApprovalTask_Test");
            b.HasKey(x => x.ID);
            b.Property(x => x.State);
            b.Property(x => x.RowVer);
            b.Property(x => x.AssigneeITCode).HasMaxLength(50).IsRequired();
            b.Property(x => x.NodeInstanceId);
            b.Property(x => x.TenantCode).HasMaxLength(50);
            b.Property(x => x.IsValid);
            b.Ignore(x => x.NodeInstance);
            // Wave-3 (WF-16) column.
            b.Property(x => x.Generation);
            // Wave-4 (WF-18) columns.
            b.Property(x => x.AddDepth).HasDefaultValue(0);
            b.Property(x => x.IsRuntimeInjected);
            b.Property(x => x.AddedByITCode).HasMaxLength(50);
            b.Property(x => x.SequenceOrder);
            // FIX-G unique index: prevents concurrent double-加签 inflating TotalRequired.
            b.HasIndex(x => new { x.NodeInstanceId, x.AssigneeITCode, x.Generation })
             .IsUnique()
             .HasDatabaseName("IX_Wf_ApprovalTask_Test_Node_Assignee_Gen");
        });

        // WorkflowTimer — minimal columns for CAS test.
        modelBuilder.Entity<WorkflowTimer>(b =>
        {
            b.ToTable("Wf_WorkflowTimer_Test");
            b.HasKey(x => x.ID);
            b.Property(x => x.Status);
            b.Property(x => x.RowVer);
            b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.NodeInstanceId);
            b.Property(x => x.TenantCode).HasMaxLength(50);
            b.Ignore(x => x.ApprovalTask);
            b.Ignore(x => x.NodeInstance);
            // Wave-3 (WF-16) column.
            b.Property(x => x.Generation);
        });

        // WorkflowEventLog — minimal columns for Seq allocation test.
        modelBuilder.Entity<WorkflowEventLog>(b =>
        {
            b.ToTable("Wf_WorkflowEventLog_Test");
            b.HasKey(x => x.ID);
            b.Property(x => x.InstanceId);
            b.Property(x => x.Seq);
            b.Property(x => x.ActorITCode).HasMaxLength(50);
            b.Property(x => x.Action);
            b.Property(x => x.NodeKey).HasMaxLength(100);
            b.Property(x => x.BeforeState).HasMaxLength(50);
            b.Property(x => x.AfterState).HasMaxLength(50);
            b.Property(x => x.Reason);
            b.Property(x => x.OccurredUtc);
            b.Property(x => x.TenantCode).HasMaxLength(50);
            b.Property(x => x.Generation);
            b.Ignore(x => x.Instance);
            // Same unique constraint as production (TenantCode, InstanceId, Seq).
            b.HasIndex(x => new { x.TenantCode, x.InstanceId, x.Seq }).IsUnique();
        });
    }
}

// ─── Guarded-CAS helpers (per entity) ────────────────────────────────────────

/// <summary>
/// Guarded-CAS helpers for WorkFlow entities.
/// Mirror the proven WF-0 pattern: WHERE clause includes expected State + RowVer;
/// SET clause updates to next state and increments RowVer atomically.
/// Winner gets rows-affected == 1; every loser gets 0.
/// </summary>
internal static class WfGuardedTransition
{
    /// <summary>ProcessInstance: Running → Approved (instance-level CAS).</summary>
    public static Task<int> ProcessInstance_RunningToApprovedAsync(
        WfTestContext db, Guid id, uint expectedRowVer, CancellationToken ct = default)
    {
        return db.ProcessInstances
            .Where(x => x.ID == id
                         && x.State == InstanceState.Running
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, InstanceState.Approved)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    /// <summary>NodeInstance: Activated → CompletedApproved (node-level CAS).</summary>
    public static Task<int> NodeInstance_ActivatedToCompletedAsync(
        WfTestContext db, Guid id, uint expectedRowVer, CancellationToken ct = default)
    {
        return db.NodeInstances
            .Where(x => x.ID == id
                         && x.State == NodeState.Activated
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, NodeState.CompletedApproved)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    /// <summary>ApprovalTask: Pending → Approved (task-level CAS).</summary>
    public static Task<int> ApprovalTask_PendingToApprovedAsync(
        WfTestContext db, Guid id, uint expectedRowVer, CancellationToken ct = default)
    {
        return db.ApprovalTasks
            .Where(x => x.ID == id
                         && x.State == TaskState.Pending
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, TaskState.Approved)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    /// <summary>WorkflowTimer: Armed → Fired (timer-level CAS, timeout wave).</summary>
    public static Task<int> WorkflowTimer_ArmedToFiredAsync(
        WfTestContext db, Guid id, uint expectedRowVer, CancellationToken ct = default)
    {
        return db.WorkflowTimers
            .Where(x => x.ID == id
                         && x.Status == TimerStatus.Armed
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, TimerStatus.Fired)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }
}

// ─── T-PROV-0 SQLite (runs on every CI push) ─────────────────────────────────

[TestClass]
public class ConcurrencyConformanceTests_SQLite : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfConformance_{Guid.NewGuid():N}";
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

    // ── ProcessInstance CAS ───────────────────────────────────────────────────

    /// <summary>
    /// T-PROV-0-SQLite: Two concurrent callers race to approve the same
    /// ProcessInstance.  Exactly one wins (rows-affected == 1); the other loses (== 0).
    /// Final state: Approved, RowVer == 1.
    /// </summary>
    [TestMethod]
    public async Task T_PROV_0_SQLite_ProcessInstance_CAS_ExactlyOneWinner()
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
                InitiatorITCode = "tester",
                DefinitionVersionId = Guid.NewGuid(),
                IsValid = true,
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
                    return await WfGuardedTransition.ProcessInstance_RunningToApprovedAsync(db, capturedId, 0);
                });
            }

            var t1 = MakeTask();
            var t2 = MakeTask();
            barrier.Release(2);

            int[] results = await Task.WhenAll(t1, t2);
            int winners = results.Count(r => r == 1);
            int losers  = results.Count(r => r == 0);

            Assert.AreEqual(1, winners,
                $"Round {round}: ProcessInstance CAS — expected 1 winner, got [{results[0]},{results[1]}]");
            Assert.AreEqual(1, losers,
                $"Round {round}: ProcessInstance CAS — expected 1 loser");

            await using var verify = MakeContext();
            var final = await verify.ProcessInstances.AsNoTracking().SingleAsync(x => x.ID == id);
            Assert.AreEqual(InstanceState.Approved, final.State,
                $"Round {round}: final State must be Approved");
            Assert.AreEqual(1u, final.RowVer,
                $"Round {round}: final RowVer must be 1");
        }
    }

    // ── NodeInstance CAS ──────────────────────────────────────────────────────

    /// <summary>
    /// T-PROV-0-SQLite: Two concurrent callers race to complete the same
    /// NodeInstance (会签 / 或签 completion).  Exactly one wins.
    /// </summary>
    [TestMethod]
    public async Task T_PROV_0_SQLite_NodeInstance_CAS_ExactlyOneWinner()
    {
        const int Rounds = 20;

        for (int round = 0; round < Rounds; round++)
        {
            var id = Guid.NewGuid();

            await using var seed = MakeContext();
            seed.NodeInstances.Add(new NodeInstance
            {
                ID = id,
                State = NodeState.Activated,
                RowVer = 0,
                NodeKey = $"node_{round}",
                InstanceId = Guid.NewGuid(),
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
                    return await WfGuardedTransition.NodeInstance_ActivatedToCompletedAsync(db, capturedId, 0);
                });
            }

            var t1 = MakeTask();
            var t2 = MakeTask();
            barrier.Release(2);

            int[] results = await Task.WhenAll(t1, t2);
            int winners = results.Count(r => r == 1);

            Assert.AreEqual(1, winners,
                $"Round {round}: NodeInstance CAS — expected 1 winner, got [{results[0]},{results[1]}]");

            await using var verify = MakeContext();
            var final = await verify.NodeInstances.AsNoTracking().SingleAsync(x => x.ID == id);
            Assert.AreEqual(NodeState.CompletedApproved, final.State,
                $"Round {round}: final State must be CompletedApproved");
            Assert.AreEqual(1u, final.RowVer,
                $"Round {round}: final RowVer must be 1");
        }
    }

    // ── ApprovalTask CAS ──────────────────────────────────────────────────────

    /// <summary>
    /// T-PROV-0-SQLite: Two concurrent callers race to claim the same
    /// ApprovalTask (or签 first-approve wins).  Exactly one wins.
    /// </summary>
    [TestMethod]
    public async Task T_PROV_0_SQLite_ApprovalTask_CAS_ExactlyOneWinner()
    {
        const int Rounds = 20;

        for (int round = 0; round < Rounds; round++)
        {
            var id = Guid.NewGuid();

            await using var seed = MakeContext();
            seed.ApprovalTasks.Add(new ApprovalTask
            {
                ID = id,
                State = TaskState.Pending,
                RowVer = 0,
                AssigneeITCode = "approver",
                NodeInstanceId = Guid.NewGuid(),
                IsValid = true,
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
                    return await WfGuardedTransition.ApprovalTask_PendingToApprovedAsync(db, capturedId, 0);
                });
            }

            var t1 = MakeTask();
            var t2 = MakeTask();
            barrier.Release(2);

            int[] results = await Task.WhenAll(t1, t2);
            int winners = results.Count(r => r == 1);

            Assert.AreEqual(1, winners,
                $"Round {round}: ApprovalTask CAS — expected 1 winner, got [{results[0]},{results[1]}]");

            await using var verify = MakeContext();
            var final = await verify.ApprovalTasks.AsNoTracking().SingleAsync(x => x.ID == id);
            Assert.AreEqual(TaskState.Approved, final.State,
                $"Round {round}: final State must be Approved");
            Assert.AreEqual(1u, final.RowVer,
                $"Round {round}: final RowVer must be 1");
        }
    }

    // ── WorkflowTimer CAS ─────────────────────────────────────────────────────

    /// <summary>
    /// T-PROV-0-SQLite: Two concurrent timer-fire attempts on the same timer.
    /// Exactly one wins (timeout-vs-human CAS foundation, T-CONC-4 pre-req).
    /// </summary>
    [TestMethod]
    public async Task T_PROV_0_SQLite_WorkflowTimer_CAS_ExactlyOneWinner()
    {
        const int Rounds = 10;

        for (int round = 0; round < Rounds; round++)
        {
            var id = Guid.NewGuid();
            var nodeInstanceId = Guid.NewGuid();

            await using var seed = MakeContext();
            seed.WorkflowTimers.Add(new WorkflowTimer
            {
                ID = id,
                Status = TimerStatus.Armed,
                RowVer = 0,
                IdempotencyKey = $"timer_{round}_{Guid.NewGuid():N}",
                NodeInstanceId = nodeInstanceId,
                FireAtUtc = DateTime.UtcNow,
                Action = TimerAction.Remind,
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
                    return await WfGuardedTransition.WorkflowTimer_ArmedToFiredAsync(db, capturedId, 0);
                });
            }

            var t1 = MakeTask();
            var t2 = MakeTask();
            barrier.Release(2);

            int[] results = await Task.WhenAll(t1, t2);
            int winners = results.Count(r => r == 1);

            Assert.AreEqual(1, winners,
                $"Round {round}: WorkflowTimer CAS — expected 1 winner, got [{results[0]},{results[1]}]");

            await using var verify = MakeContext();
            var final = await verify.WorkflowTimers.AsNoTracking().SingleAsync(x => x.ID == id);
            Assert.AreEqual(TimerStatus.Fired, final.Status,
                $"Round {round}: final Status must be Fired");
            Assert.AreEqual(1u, final.RowVer,
                $"Round {round}: final RowVer must be 1");
        }
    }

    // ── High-contention variant ───────────────────────────────────────────────

    /// <summary>
    /// High-fan-out variant: 8 concurrent callers race to complete one NodeInstance.
    /// Exactly one wins; all 7 others lose cleanly.
    /// </summary>
    [TestMethod]
    public async Task T_PROV_0_SQLite_NodeInstance_HighContention_ExactlyOneWinner()
    {
        const int Concurrency = 8;
        var id = Guid.NewGuid();

        await using var seed = MakeContext();
        seed.NodeInstances.Add(new NodeInstance
        {
            ID = id,
            State = NodeState.Activated,
            RowVer = 0,
            NodeKey = "high_contention_node",
            InstanceId = Guid.NewGuid(),
        });
        await seed.SaveChangesAsync();

        var barrier = new SemaphoreSlim(0, Concurrency);

        var tasks = Enumerable.Range(0, Concurrency)
            .Select(_ => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await WfGuardedTransition.NodeInstance_ActivatedToCompletedAsync(db, id, 0);
            }))
            .ToList();

        barrier.Release(Concurrency);

        int[] results = await Task.WhenAll(tasks);
        int winners = results.Count(r => r == 1);
        int losers  = results.Count(r => r == 0);

        Assert.AreEqual(1, winners,
            $"High-contention NodeInstance CAS: expected 1 winner, got {winners} winners / {losers} losers " +
            $"out of {Concurrency} concurrent callers");
        Assert.AreEqual(Concurrency - 1, losers);

        await using var verify = MakeContext();
        var final = await verify.NodeInstances.AsNoTracking().SingleAsync(x => x.ID == id);
        Assert.AreEqual(NodeState.CompletedApproved, final.State);
        Assert.AreEqual(1u, final.RowVer);
    }
}

// ─── T-ADD: WF-18 加签 (add-approver) CAS conformance tests ─────────────────────

/// <summary>
/// WF-18 加签 concurrency conformance suite.
///
/// Tests T-ADD-01 through T-ADD-10 and T-MIX-01 verify the R1 keystone
/// (<c>ApproverSetEpoch</c> folded into the completion CAS predicate) and the
/// FIX-G backstop (UNIQUE index on NodeInstanceId+AssigneeITCode+Generation).
///
/// All tests run against SQLite shared-in-memory — same infrastructure as T-PROV-0.
/// </summary>
[TestClass]
public class ConcurrencyConformanceTests_AddApprover : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfAddApprover_{Guid.NewGuid():N}";
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

    // ── Helper: seed a node + its tasks ──────────────────────────────────────

    private async Task<(Guid nodeId, Guid instanceId)> SeedActivatedNodeAsync(
        int totalRequired = 2, uint approverSetEpoch = 0)
    {
        var instanceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        await using var db = MakeContext();
        db.ProcessInstances.Add(new ProcessInstance
        {
            ID = instanceId,
            State = InstanceState.Running,
            RowVer = 0,
            InitiatorITCode = "initiator",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid = true,
            Generation = 0,
        });
        db.NodeInstances.Add(new NodeInstance
        {
            ID = nodeId,
            State = NodeState.Activated,
            RowVer = 0,
            NodeKey = "approval_node",
            InstanceId = instanceId,
            TotalRequired = totalRequired,
            ApproveMode = ApproveMode.All,
            ApproverSetEpoch = approverSetEpoch,
            Generation = 0,
        });
        await db.SaveChangesAsync();
        return (nodeId, instanceId);
    }

    // ── T-ADD-01: AddApproversToNodeAsync — single winner ────────────────────

    /// <summary>
    /// T-ADD-01: Two concurrent callers race to call <c>AddApproversToNodeAsync</c>
    /// (both with the same expectedApproverSetEpoch=0 and expectedRowVer=0).
    /// Exactly one wins (rows==1); the other loses (rows==0).
    /// After the winner: TotalRequired += delta, ApproverSetEpoch == 1, RowVer == 1.
    /// </summary>
    [TestMethod]
    public async Task T_ADD_01_AddApproversToNodeAsync_ExactlyOneWinner()
    {
        const int Rounds = 20;

        for (int round = 0; round < Rounds; round++)
        {
            var (nodeId, _) = await SeedActivatedNodeAsync(totalRequired: 2, approverSetEpoch: 0);

            var barrier = new SemaphoreSlim(0, 2);

            Task<int> MakeTask() => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.AddApproversToNodeAsync(
                    db, nodeId,
                    expectedRowVer: 0,
                    generation: 0,
                    expectedApproverSetEpoch: 0,
                    delta: 1);
            });

            var t1 = MakeTask();
            var t2 = MakeTask();
            barrier.Release(2);
            int[] results = await Task.WhenAll(t1, t2);
            int winners = results.Count(r => r == 1);

            Assert.AreEqual(1, winners,
                $"Round {round}: T-ADD-01 — expected 1 winner, got [{results[0]},{results[1]}]");

            await using var verify = MakeContext();
            var node = await verify.NodeInstances.AsNoTracking()
                .SingleAsync(n => n.ID == nodeId);

            Assert.AreEqual(3, node.TotalRequired, $"Round {round}: TotalRequired must be 3 (2+1)");
            Assert.AreEqual(1u, node.ApproverSetEpoch, $"Round {round}: ApproverSetEpoch must be 1");
            Assert.AreEqual(1u, node.RowVer, $"Round {round}: RowVer must be 1");
        }
    }

    // ── T-ADD-02: stale epoch → CAS fails (rows==0) ────────────────────────────

    /// <summary>
    /// T-ADD-02: A caller with a stale <c>expectedApproverSetEpoch</c> must always
    /// get rows==0 even though the RowVer and State are correct.
    /// </summary>
    [TestMethod]
    public async Task T_ADD_02_StaleEpoch_CASFails()
    {
        var (nodeId, _) = await SeedActivatedNodeAsync(totalRequired: 2, approverSetEpoch: 5);

        await using var db = MakeContext();
        int rows = await GuardedTransition.AddApproversToNodeAsync(
            db, nodeId,
            expectedRowVer: 0,
            generation: 0,
            expectedApproverSetEpoch: 4,  // stale — actual is 5
            delta: 1);

        Assert.AreEqual(0, rows, "T-ADD-02: stale epoch must return rows==0");

        await using var verify = MakeContext();
        var node = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == nodeId);
        Assert.AreEqual(2, node.TotalRequired, "T-ADD-02: TotalRequired must be unchanged");
        Assert.AreEqual(5u, node.ApproverSetEpoch, "T-ADD-02: ApproverSetEpoch must be unchanged");
    }

    // ── T-ADD-03: stale RowVer → CAS fails ────────────────────────────────────

    /// <summary>
    /// T-ADD-03: A caller with a stale <c>expectedRowVer</c> must always get rows==0
    /// even though the epoch and State are correct.
    /// </summary>
    [TestMethod]
    public async Task T_ADD_03_StaleRowVer_CASFails()
    {
        var (nodeId, _) = await SeedActivatedNodeAsync(totalRequired: 2, approverSetEpoch: 0);

        await using var db = MakeContext();
        int rows = await GuardedTransition.AddApproversToNodeAsync(
            db, nodeId,
            expectedRowVer: 99,   // stale — actual is 0
            generation: 0,
            expectedApproverSetEpoch: 0,
            delta: 1);

        Assert.AreEqual(0, rows, "T-ADD-03: stale RowVer must return rows==0");
    }

    // ── T-ADD-04: stale generation → CAS fails ────────────────────────────────

    /// <summary>
    /// T-ADD-04: A caller with a stale <c>generation</c> (e.g. old epoch after a 回退
    /// bump) must get rows==0 — ensures 加签 cannot be applied to a superseded span.
    /// </summary>
    [TestMethod]
    public async Task T_ADD_04_StaleGeneration_CASFails()
    {
        var (nodeId, _) = await SeedActivatedNodeAsync(totalRequired: 2, approverSetEpoch: 0);

        await using var db = MakeContext();
        int rows = await GuardedTransition.AddApproversToNodeAsync(
            db, nodeId,
            expectedRowVer: 0,
            generation: 1,  // stale — actual is 0
            expectedApproverSetEpoch: 0,
            delta: 1);

        Assert.AreEqual(0, rows, "T-ADD-04: stale generation must return rows==0");
    }

    // ── T-ADD-05: node not Activated → CAS fails ──────────────────────────────

    /// <summary>
    /// T-ADD-05: When the node is already CompletedApproved (not Activated),
    /// <c>AddApproversToNodeAsync</c> must return rows==0 (NodeAlreadyDecided guard).
    /// </summary>
    [TestMethod]
    public async Task T_ADD_05_NodeNotActivated_CASFails()
    {
        var (nodeId, _) = await SeedActivatedNodeAsync();

        // Complete the node first.
        await using var setup = MakeContext();
        await setup.NodeInstances
            .Where(n => n.ID == nodeId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.State, NodeState.CompletedApproved)
                                       .SetProperty(n => n.RowVer, n => n.RowVer + 1));

        await using var db = MakeContext();
        int rows = await GuardedTransition.AddApproversToNodeAsync(
            db, nodeId,
            expectedRowVer: 1,   // current after SetProperty above
            generation: 0,
            expectedApproverSetEpoch: 0,
            delta: 1);

        Assert.AreEqual(0, rows, "T-ADD-05: non-Activated node must return rows==0");
    }

    // ── T-ADD-06: AdvanceNodeApproverSetEpochAsync — bump without count change ──

    /// <summary>
    /// T-ADD-06: <c>AdvanceNodeApproverSetEpochAsync</c> bumps ApproverSetEpoch and
    /// RowVer atomically without changing TotalRequired.
    /// </summary>
    [TestMethod]
    public async Task T_ADD_06_AdvanceEpoch_BumpsEpochAndRowVer()
    {
        var (nodeId, _) = await SeedActivatedNodeAsync(totalRequired: 3, approverSetEpoch: 2);

        await using var db = MakeContext();
        int rows = await GuardedTransition.AdvanceNodeApproverSetEpochAsync(
            db, nodeId, expectedRowVer: 0);

        Assert.AreEqual(1, rows, "T-ADD-06: AdvanceNodeApproverSetEpochAsync must return 1");

        await using var verify = MakeContext();
        var node = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == nodeId);
        Assert.AreEqual(3, node.TotalRequired, "T-ADD-06: TotalRequired must be unchanged");
        Assert.AreEqual(3u, node.ApproverSetEpoch, "T-ADD-06: ApproverSetEpoch must be 3 (2+1)");
        Assert.AreEqual(1u, node.RowVer, "T-ADD-06: RowVer must be 1");
    }

    // ── T-ADD-07: AdvanceEpoch on closed node → rows==0 ──────────────────────

    /// <summary>
    /// T-ADD-07: <c>AdvanceNodeApproverSetEpochAsync</c> on a CompletedApproved node
    /// returns rows==0 (no double-bump on closed node).
    /// </summary>
    [TestMethod]
    public async Task T_ADD_07_AdvanceEpoch_ClosedNode_ReturnsZero()
    {
        var (nodeId, _) = await SeedActivatedNodeAsync();

        await using var setup = MakeContext();
        await setup.NodeInstances
            .Where(n => n.ID == nodeId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.State, NodeState.CompletedApproved)
                                       .SetProperty(n => n.RowVer, n => n.RowVer + 1));

        await using var db = MakeContext();
        int rows = await GuardedTransition.AdvanceNodeApproverSetEpochAsync(
            db, nodeId, expectedRowVer: 1);

        Assert.AreEqual(0, rows, "T-ADD-07: AdvanceEpoch on closed node must return 0");
    }

    // ── T-ADD-08: CompleteNodeInstanceAsync with epoch — wrong epoch fails ─────

    /// <summary>
    /// T-ADD-08: When <c>expectedApproverSetEpoch</c> is supplied to
    /// <c>CompleteNodeInstanceAsync</c> and the node's epoch has advanced (concurrent
    /// 加签), the CAS must return rows==0 (FIX-A/B — stale-threshold completion
    /// detection).
    /// </summary>
    [TestMethod]
    public async Task T_ADD_08_CompleteWithEpoch_StaleEpochFails()
    {
        var (nodeId, _) = await SeedActivatedNodeAsync(totalRequired: 2, approverSetEpoch: 3);

        await using var db = MakeContext();
        // Attempt completion asserting epoch==2 but actual==3 — must fail.
        int rows = await GuardedTransition.CompleteNodeInstanceAsync(
            db, nodeId,
            expectedRowVer: 0,
            completedState: NodeState.CompletedApproved,
            generation: 0,
            expectedApproverSetEpoch: 2);  // stale

        Assert.AreEqual(0, rows, "T-ADD-08: stale ApproverSetEpoch in CompleteNodeInstanceAsync must return 0");
    }

    // ── T-ADD-09: CompleteNodeInstanceAsync with correct epoch succeeds ─────────

    /// <summary>
    /// T-ADD-09: When <c>expectedApproverSetEpoch</c> matches the current node epoch,
    /// <c>CompleteNodeInstanceAsync</c> wins (rows==1) and RowVer advances.
    /// </summary>
    [TestMethod]
    public async Task T_ADD_09_CompleteWithEpoch_CorrectEpochSucceeds()
    {
        var (nodeId, _) = await SeedActivatedNodeAsync(totalRequired: 2, approverSetEpoch: 3);

        await using var db = MakeContext();
        int rows = await GuardedTransition.CompleteNodeInstanceAsync(
            db, nodeId,
            expectedRowVer: 0,
            completedState: NodeState.CompletedApproved,
            generation: 0,
            expectedApproverSetEpoch: 3);  // matches

        Assert.AreEqual(1, rows, "T-ADD-09: correct epoch must allow completion (rows==1)");

        await using var verify = MakeContext();
        var node = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == nodeId);
        Assert.AreEqual(NodeState.CompletedApproved, node.State, "T-ADD-09: node must be CompletedApproved");
        Assert.AreEqual(1u, node.RowVer, "T-ADD-09: RowVer must be 1");
    }

    // ── T-ADD-10: high-contention — 8 concurrent 加签 → exactly one winner ─────

    /// <summary>
    /// T-ADD-10: 8 concurrent callers race on the same AddApproversToNodeAsync CAS.
    /// Exactly one wins; the other 7 lose cleanly (rows==0).
    /// TotalRequired is incremented by exactly delta (not 8×delta).
    /// </summary>
    [TestMethod]
    public async Task T_ADD_10_HighContention_AddApprovers_ExactlyOneWinner()
    {
        const int Concurrency = 8;
        const int Delta = 1;
        var (nodeId, _) = await SeedActivatedNodeAsync(totalRequired: 2, approverSetEpoch: 0);

        var barrier = new SemaphoreSlim(0, Concurrency);
        var tasks = Enumerable.Range(0, Concurrency)
            .Select(_ => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.AddApproversToNodeAsync(
                    db, nodeId,
                    expectedRowVer: 0,
                    generation: 0,
                    expectedApproverSetEpoch: 0,
                    delta: Delta);
            }))
            .ToList();

        barrier.Release(Concurrency);
        int[] results = await Task.WhenAll(tasks);
        int winners = results.Count(r => r == 1);
        int losers  = results.Count(r => r == 0);

        Assert.AreEqual(1, winners,
            $"T-ADD-10: expected 1 winner, got {winners} winners / {losers} losers");
        Assert.AreEqual(Concurrency - 1, losers);

        await using var verify = MakeContext();
        var node = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == nodeId);
        Assert.AreEqual(2 + Delta, node.TotalRequired,
            $"T-ADD-10: TotalRequired must be exactly 2+{Delta}, not double-incremented");
        Assert.AreEqual(1u, node.ApproverSetEpoch, "T-ADD-10: ApproverSetEpoch must be 1");
    }

    // ── T-MIX-01: 加签 racing with completion — threshold still met ──────────

    /// <summary>
    /// T-MIX-01: Interleaved 加签 and completion race.
    ///
    /// Setup: node has TotalRequired=2, ApproverSetEpoch=0, ApprovedCount=2.
    ///
    /// Racer A: reads epoch=0, attempts CompleteNodeInstanceAsync with expectedEpoch=0.
    /// Racer B: reads epoch=0, attempts AddApproversToNodeAsync (epoch→1, Total→3).
    ///
    /// Exactly one wins; the other gets rows==0.
    /// If A wins first → node completes with original threshold.
    /// If B wins first → A's completion CAS fails; A must re-read and re-evaluate.
    ///
    /// This test verifies the ordering is safe (no double-fire, no lost increment).
    /// </summary>
    [TestMethod]
    public async Task T_MIX_01_AddVsComplete_ExactlyOneWinner()
    {
        const int Rounds = 30;

        for (int round = 0; round < Rounds; round++)
        {
            var (nodeId, _) = await SeedActivatedNodeAsync(totalRequired: 2, approverSetEpoch: 0);

            var barrier = new SemaphoreSlim(0, 2);

            // Racer A: complete with epoch=0
            Task<int> RacerA() => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.CompleteNodeInstanceAsync(
                    db, nodeId,
                    expectedRowVer: 0,
                    completedState: NodeState.CompletedApproved,
                    generation: 0,
                    expectedApproverSetEpoch: 0);
            });

            // Racer B: add approver with epoch=0
            Task<int> RacerB() => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.AddApproversToNodeAsync(
                    db, nodeId,
                    expectedRowVer: 0,
                    generation: 0,
                    expectedApproverSetEpoch: 0,
                    delta: 1);
            });

            var tA = RacerA();
            var tB = RacerB();
            barrier.Release(2);

            int rowsA = await tA;
            int rowsB = await tB;

            // Exactly one wins; both cannot win simultaneously on the same RowVer=0.
            int totalWinners = (rowsA == 1 ? 1 : 0) + (rowsB == 1 ? 1 : 0);
            Assert.AreEqual(1, totalWinners,
                $"Round {round}: T-MIX-01 — expected exactly 1 winner, got A={rowsA} B={rowsB}");

            await using var verify = MakeContext();
            var node = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == nodeId);

            if (rowsA == 1)
            {
                // A won completion: node is CompletedApproved; B's add-approver lost.
                Assert.AreEqual(NodeState.CompletedApproved, node.State,
                    $"Round {round}: A won completion — node must be CompletedApproved");
                Assert.AreEqual(2, node.TotalRequired, // unchanged (B lost)
                    $"Round {round}: A won — TotalRequired must be unchanged (2)");
            }
            else
            {
                // B won add-approver: epoch bumped; A's completion CAS failed.
                Assert.AreEqual(NodeState.Activated, node.State,
                    $"Round {round}: B won 加签 — node must still be Activated (A's completion CAS failed)");
                Assert.AreEqual(3, node.TotalRequired,
                    $"Round {round}: B won — TotalRequired must be 3 (2+1)");
                Assert.AreEqual(1u, node.ApproverSetEpoch,
                    $"Round {round}: B won — ApproverSetEpoch must be 1");
            }
        }
    }
}

// ─── Live-DB ProviderConformance stubs (nightly / release only) ──────────────

/// <summary>
/// Live-database provider conformance suite (T-PROV, spec §10).
///
/// These tests are tagged <c>[TestCategory("ProviderConformance")]</c> and are
/// SKIPPED when the required connection-string environment variable is not set.
/// They run nightly / on release against real instances of each provider.
///
/// Required env vars (one per provider):
///   WTM_TEST_SQLSERVER_CS  — SQL Server / Azure SQL
///   WTM_TEST_PGSQL_CS      — PostgreSQL
///   WTM_TEST_MYSQL_CS      — MySQL / MariaDB (Pomelo)
///   WTM_TEST_ORACLE_CS     — Oracle
///   WTM_TEST_DAMENG_CS     — DaMeng 达梦
///
/// Per-provider EF provider packages are referenced in the test csproj
/// behind the ProviderConformance category so they do not inflate normal CI.
/// </summary>
/// <remarks>
/// NOTE: The live-DB tests are SCAFFOLDED here — bodies are filled in WF-3 once
/// each provider package is confirmed available in the build environment.
/// The scaffolding proves the test category is wired up and the skip logic works.
/// </remarks>
[TestClass]
[TestCategory("ProviderConformance")]
public class ConcurrencyConformanceTests_LiveDb
{
    // Helper: gate the stub — if env var IS set, fail loudly instead of Inconclusive so
    // a future runner can't get a false-green from an unimplemented body (#240 / WF-3).
    // If env var is absent, Inconclusive (skipped).
    private static string RequireConnectionString(string envVar)
    {
        var cs = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(cs))
        {
            Assert.Inconclusive(
                $"Skipped: environment variable '{envVar}' is not set. " +
                "Set it to a real connection string to run this ProviderConformance test.");
        }
        // Env var IS set — live-provider conformance body not yet implemented.
        // Fail explicitly so the CI run can't get a false-green when a provider is available.
        Assert.Fail(
            $"ProviderConformance body not implemented for env var '{envVar}' — tracked in #270 (WF-3). " +
            "When WF-3 is implemented, replace this Assert.Fail with the real CAS assertion.");
        return cs!; // unreachable; satisfies return type
    }

    [TestMethod]
    [TestCategory("ProviderConformance")]
    public void T_PROV_SqlServer_NodeInstance_CAS_ExactlyOneWinner()
    {
        var cs = RequireConnectionString("WTM_TEST_SQLSERVER_CS");
        // WF-3: implement using Microsoft.EntityFrameworkCore.SqlServer provider.
        // Map RowVer with IsRowVersion() (native rowversion column) per spec §7.2.
        // Assert same CAS winner=1 / loser=0 contract as SQLite tests above.
        _ = cs; // suppress unused-variable warning; body filled in WF-3
    }

    [TestMethod]
    [TestCategory("ProviderConformance")]
    public void T_PROV_PgSql_NodeInstance_CAS_ExactlyOneWinner()
    {
        var cs = RequireConnectionString("WTM_TEST_PGSQL_CS");
        // WF-3: implement using Npgsql.EntityFrameworkCore.PostgreSQL.
        // Map RowVer via UseXminAsConcurrencyToken() (shadow xmin, no extra column).
        _ = cs;
    }

    [TestMethod]
    [TestCategory("ProviderConformance")]
    public void T_PROV_MySql_NodeInstance_CAS_ExactlyOneWinner()
    {
        var cs = RequireConnectionString("WTM_TEST_MYSQL_CS");
        // WF-3: implement using Pomelo.EntityFrameworkCore.MySql.
        // Map RowVer as plain uint, app-incremented in WHERE+SET per spec §7.2.
        _ = cs;
    }

    [TestMethod]
    [TestCategory("ProviderConformance")]
    public void T_PROV_Oracle_NodeInstance_CAS_ExactlyOneWinner()
    {
        var cs = RequireConnectionString("WTM_TEST_ORACLE_CS");
        // WF-3: implement using Oracle.EntityFrameworkCore.
        // Map RowVer as plain uint, app-incremented.  Oracle fallback (SELECT...FOR UPDATE)
        // used when ExecuteUpdateAsync predicate fails to translate (spec §7.5).
        _ = cs;
    }

    [TestMethod]
    [TestCategory("ProviderConformance")]
    public void T_PROV_DaMeng_NodeInstance_CAS_ExactlyOneWinner()
    {
        var cs = RequireConnectionString("WTM_TEST_DAMENG_CS");
        // WF-3: implement using EntityFrameworkCore.Dm (达梦).
        // Map RowVer as plain uint, app-incremented.
        // DaMeng is a primary target-market DB; conformance failure here is a ship-blocker.
        _ = cs;
    }
}
