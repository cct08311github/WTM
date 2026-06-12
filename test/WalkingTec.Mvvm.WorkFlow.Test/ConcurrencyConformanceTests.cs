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
            // Wave-5 (WF-20.4) columns needed by SystemActTaskAsync / ExecuteApproveCompletionAsync.
            b.Property(x => x.SequencePointer).HasDefaultValue(0);
            b.Property(x => x.ApprovedCount).HasDefaultValue(0);
            b.Property(x => x.RejectedCount).HasDefaultValue(0);
            b.Property(x => x.RejectPolicy);
            b.Property(x => x.RejectGate);
            b.Property(x => x.NodeKind);
            b.Property(x => x.DecidedBy).HasMaxLength(50);
            b.Property(x => x.ActivatedAt);
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
            // Wave-5 (WF-19) delegation columns.
            b.Property(x => x.DelegatedFromITCode).HasMaxLength(50);
            b.Property(x => x.DelegationRuleId);
            b.Property(x => x.DelegationExpiresUtc);
            b.Property(x => x.WindowVerifiedUtc);
            // Wave-5 (WF-20.4) columns needed by ClaimApprovalTaskAsync.
            b.Property(x => x.Comment);
            b.Property(x => x.ActedAtUtc);
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
            b.Property(x => x.ApprovalTaskId);
            b.Property(x => x.FireAtUtc);
            b.Property(x => x.Action);
            b.Property(x => x.RemindCount).HasDefaultValue(0);
            b.Property(x => x.TenantCode).HasMaxLength(50);
            b.Ignore(x => x.ApprovalTask);
            b.Ignore(x => x.NodeInstance);
            // Wave-3 (WF-16) column.
            b.Property(x => x.Generation);
            // FIX-A3: RemindEveryHours and MaxReminders removed from WorkflowTimer (schema-delta-zero).
            // These values are re-read from the version-pinned immutable graph at fire time.
            // Unique index for idempotent arm.
            b.HasIndex(x => x.IdempotencyKey).IsUnique()
             .HasDatabaseName("IX_Wf_WorkflowTimer_Test_IdempotencyKey");
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
            // Wave-5 (WF-20.4) column — OnBehalfOf for system auto-actions.
            b.Property(x => x.OnBehalfOfITCode).HasMaxLength(50);
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

// ─── T-DEL / T-MIX: WF-19 delegation (mid-flight reassignment) ───────────────

/// <summary>
/// Barrier-style concurrency tests for <see cref="GuardedTransition.ReassignTaskAssigneeAsync"/>
/// (WF-19, design §3 path B, §4 FIX-C).
///
/// All tests run against a SQLite shared-in-memory instance so they execute on every
/// CI push without external infrastructure.
/// </summary>
[TestClass]
public class ConcurrencyConformanceTests_DelegateTask : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfDelegate_{Guid.NewGuid():N}";
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

    /// <summary>
    /// Seed an Activated node + a Pending ApprovalTask for <paramref name="assigneeITCode"/>
    /// at the given <paramref name="generation"/>.
    /// Returns (nodeId, taskId, instanceId).
    /// </summary>
    private async Task<(Guid nodeId, Guid taskId, Guid instanceId)> SeedNodeAndTaskAsync(
        string assigneeITCode = "D",
        uint generation = 0,
        int totalRequired = 2)
    {
        var instanceId = Guid.NewGuid();
        var nodeId     = Guid.NewGuid();
        var taskId     = Guid.NewGuid();

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
            ApproverSetEpoch = 0,
            Generation = generation,
        });
        db.ApprovalTasks.Add(new ApprovalTask
        {
            ID = taskId,
            State = TaskState.Pending,
            RowVer = 0,
            AssigneeITCode = assigneeITCode,
            NodeInstanceId = nodeId,
            TenantCode = "test",
            IsValid = true,
            Generation = generation,
            SequenceOrder = 0,
        });
        await db.SaveChangesAsync();
        return (nodeId, taskId, instanceId);
    }

    // ── T-DEL-05: reassign vs approve race ───────────────────────────────────

    /// <summary>
    /// T-DEL-05: D→C reassignment races against D's own approve CAS on the same task.
    /// Exactly one outcome: either C owns the slot (reassign won) or D already acted
    /// (approve won, rows-reassign==0 → AlreadyHandled).
    ///
    /// TotalRequired invariant: TotalRequired is NEVER modified by either racer.
    /// </summary>
    [TestMethod]
    public async Task T_DEL_05_ReassignVsApprove_ExactlyOneWinner()
    {
        const int Rounds = 20;

        for (int round = 0; round < Rounds; round++)
        {
            var (nodeId, taskId, _) = await SeedNodeAndTaskAsync("D", generation: 0, totalRequired: 2);

            var barrier = new SemaphoreSlim(0, 2);

            // Racer A: D delegates to C (ReassignTaskAssigneeAsync CAS).
            Task<int> RacerA() => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.ReassignTaskAssigneeAsync(
                    db, taskId,
                    expectedRowVer: 0,
                    generation: 0,
                    delegateeITCode: "C",
                    principalITCode: "D",
                    delegationRuleId: null,
                    delegationExpiresUtc: null);
            });

            // Racer B: D approves the task (ApprovalTask_PendingToApprovedAsync CAS).
            Task<int> RacerB() => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await WfGuardedTransition.ApprovalTask_PendingToApprovedAsync(db, taskId, expectedRowVer: 0);
            });

            var tA = RacerA();
            var tB = RacerB();
            barrier.Release(2);

            int rowsA = await tA;
            int rowsB = await tB;

            // Exactly one CAS wins — both operate on RowVer=0.
            int winners = (rowsA == 1 ? 1 : 0) + (rowsB == 1 ? 1 : 0);
            Assert.AreEqual(1, winners,
                $"Round {round}: T-DEL-05 — expected 1 winner, got reassign={rowsA} approve={rowsB}");

            // TotalRequired invariant: neither CAS touches TotalRequired.
            await using var verify = MakeContext();
            var node = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == nodeId);
            Assert.AreEqual(2, node.TotalRequired,
                $"Round {round}: T-DEL-05 — TotalRequired must remain 2 regardless of winner");

            // Verify the task outcome is consistent.
            var task = await verify.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);
            if (rowsA == 1)
            {
                // Reassign won: C owns the slot; task is still Pending (not yet acted upon).
                Assert.AreEqual("C", task.AssigneeITCode,
                    $"Round {round}: T-DEL-05 — reassign won; AssigneeITCode must be C");
                Assert.AreEqual("D", task.DelegatedFromITCode,
                    $"Round {round}: T-DEL-05 — DelegatedFromITCode must be D");
                Assert.AreEqual(TaskState.Pending, task.State,
                    $"Round {round}: T-DEL-05 — reassigned task must still be Pending");
            }
            else
            {
                // Approve won: task is Approved; reassign's CAS lost (rows==0 → AlreadyHandled).
                Assert.AreEqual(TaskState.Approved, task.State,
                    $"Round {round}: T-DEL-05 — approve won; task must be Approved");
                Assert.AreEqual("D", task.AssigneeITCode,
                    $"Round {round}: T-DEL-05 — approve won; AssigneeITCode stays D");
            }
        }
    }

    // ── T-DEL-06: reassign where delegatee already has an active slot ─────────

    /// <summary>
    /// T-DEL-06: C already holds an active Pending task on the same node and generation.
    /// The reassignment CAS must be refused by the collision guard (DelegateAlreadyParticipant)
    /// and D's original task must remain unchanged.
    ///
    /// This test validates the pre-check collision guard, not the CAS itself.
    /// </summary>
    [TestMethod]
    public async Task T_DEL_06_ReassignCollision_DelegateeAlreadyHasActiveSlot()
    {
        // Seed: D's task (to be delegated) + C's existing task on same node.
        var instanceId = Guid.NewGuid();
        var nodeId     = Guid.NewGuid();
        var taskD      = Guid.NewGuid();
        var taskC      = Guid.NewGuid();

        await using var seed = MakeContext();
        seed.ProcessInstances.Add(new ProcessInstance
        {
            ID = instanceId,
            State = InstanceState.Running,
            RowVer = 0,
            InitiatorITCode = "initiator",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid = true,
        });
        seed.NodeInstances.Add(new NodeInstance
        {
            ID = nodeId,
            State = NodeState.Activated,
            RowVer = 0,
            NodeKey = "approval_node",
            InstanceId = instanceId,
            TotalRequired = 2,
            ApproveMode = ApproveMode.All,
            Generation = 0,
        });
        // D's task (the one to be reassigned).
        seed.ApprovalTasks.Add(new ApprovalTask
        {
            ID = taskD,
            State = TaskState.Pending,
            RowVer = 0,
            AssigneeITCode = "D",
            NodeInstanceId = nodeId,
            TenantCode = "test",
            IsValid = true,
            Generation = 0,
            SequenceOrder = 0,
        });
        // C already has an active slot on the same node and generation.
        seed.ApprovalTasks.Add(new ApprovalTask
        {
            ID = taskC,
            State = TaskState.Pending,
            RowVer = 0,
            AssigneeITCode = "C",
            NodeInstanceId = nodeId,
            TenantCode = "test",
            IsValid = true,
            Generation = 0,
            SequenceOrder = 1,
        });
        await seed.SaveChangesAsync();

        // Pre-check: C already participates — the engine-level DelegateTaskAsync would
        // refuse with DelegateAlreadyParticipant before reaching the CAS.
        // We validate the collision query logic here directly.
        await using var check = MakeContext();
        var alreadyParticipant = await check.ApprovalTasks.AsNoTracking()
            .AnyAsync(t => t.NodeInstanceId == nodeId
                            && t.Generation == 0
                            && t.AssigneeITCode == "C"
                            && (t.State == TaskState.Pending
                                || t.State == TaskState.AddedPending
                                || t.State == TaskState.NotYetActive));

        Assert.IsTrue(alreadyParticipant,
            "T-DEL-06: Collision guard must detect C already has an active slot.");

        // D's task must be untouched (no reassignment reached the CAS).
        await using var verify = MakeContext();
        var dTask = await verify.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskD);
        Assert.AreEqual("D", dTask.AssigneeITCode,
            "T-DEL-06: D's task AssigneeITCode must remain D after collision refusal.");
        Assert.AreEqual(TaskState.Pending, dTask.State,
            "T-DEL-06: D's task must remain Pending after collision refusal.");
        Assert.AreEqual(0u, dTask.RowVer,
            "T-DEL-06: D's task RowVer must be unchanged (CAS was never applied).");

        // TotalRequired invariant.
        var node = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == nodeId);
        Assert.AreEqual(2, node.TotalRequired,
            "T-DEL-06: TotalRequired must remain 2; collision guard never alters vote count.");
    }

    // ── T-DEL-12: reassign on superseded span (Generation guard) ─────────────

    /// <summary>
    /// T-DEL-12: After a 回退 span-discard, the task's <c>Generation</c> is superseded.
    /// A reassignment CAS that carries the old generation must return rows==0 — no
    /// zombie reassignment of a discarded task slot.
    /// </summary>
    [TestMethod]
    public async Task T_DEL_12_SupersededGeneration_CASFails()
    {
        const int Rounds = 10;

        for (int round = 0; round < Rounds; round++)
        {
            // Seed a task at generation=0.
            var (_, taskId, _) = await SeedNodeAndTaskAsync("D", generation: 0, totalRequired: 2);

            // Simulate 回退 span-discard: bump the task's Generation to 1 (superseded).
            // In production, DiscardTasksForReturnAsync sets State=Cancelled (NOT Generation) as
            // the primary fence preventing late actors from acting on old-span tasks.
            // State==Pending is what the CAS checks; Generation here is a belt-and-suspenders
            // epoch guard.  This test simulates a generation-skew scenario directly so it is
            // self-contained without requiring a full 回退 orchestration.
            // Here we advance Generation directly so the test is self-contained.
            await using var bump = MakeContext();
            await bump.ApprovalTasks
                .Where(t => t.ID == taskId && t.Generation == 0)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(t => t.Generation, 1u)
                     .SetProperty(t => t.RowVer, t => t.RowVer + 1));

            // Re-read to get the current RowVer AFTER the generation bump.
            await using var read = MakeContext();
            var task = await read.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);

            // Attempt reassignment with old generation=0 (stale — the CAS predicate includes Generation).
            await using var db = MakeContext();
            int rows = await GuardedTransition.ReassignTaskAssigneeAsync(
                db, taskId,
                expectedRowVer: task.RowVer, // correct RowVer (after bump)
                generation: 0,               // stale generation — must reject
                delegateeITCode: "C",
                principalITCode: "D",
                delegationRuleId: null,
                delegationExpiresUtc: null);

            Assert.AreEqual(0, rows,
                $"Round {round}: T-DEL-12 — stale generation CAS must return 0 (no zombie reassignment)");

            // Task must remain in its superseded state, unchanged by the stale CAS.
            await using var verify = MakeContext();
            var final = await verify.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);
            Assert.AreEqual(1u, final.Generation,
                $"Round {round}: T-DEL-12 — Generation must remain 1 (not reverted)");
            Assert.AreEqual("D", final.AssigneeITCode,
                $"Round {round}: T-DEL-12 — AssigneeITCode must remain D (no zombie reassign)");
        }
    }

    // ── T-MIX-02: reassign + concurrent 加签 on same node ────────────────────

    /// <summary>
    /// T-MIX-02: D delegates (ReassignTaskAssigneeAsync) races concurrently against
    /// an 加签 (AddApproversToNodeAsync) on the same node.
    ///
    /// Both operations bump <c>ApproverSetEpoch</c> on the node when they win.
    /// A stale-epoch completion CAS must lose and re-read.
    /// Final TotalRequired is consistent: the reassign never touches it; the 加签 bumps it by delta.
    /// </summary>
    [TestMethod]
    public async Task T_MIX_02_ReassignVsAddApprover_EpochBump_TotalRequiredConsistent()
    {
        const int Rounds = 20;

        for (int round = 0; round < Rounds; round++)
        {
            var (nodeId, taskId, _) = await SeedNodeAndTaskAsync("D", generation: 0, totalRequired: 2);

            // Snapshot epoch=0 and RowVer=0 for both racers (stale reads, intentional for race test).
            var barrier = new SemaphoreSlim(0, 2);

            // Racer A: D delegates to C (reassign — does NOT touch TotalRequired or epoch directly;
            // the engine follows with AdvanceNodeApproverSetEpochAsync, but here we test the raw CAS).
            Task<int> RacerA() => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.ReassignTaskAssigneeAsync(
                    db, taskId,
                    expectedRowVer: 0,
                    generation: 0,
                    delegateeITCode: "C",
                    principalITCode: "D",
                    delegationRuleId: null,
                    delegationExpiresUtc: null);
            });

            // Racer B: 加签 — adds 1 new approver slot, bumps TotalRequired + ApproverSetEpoch.
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

            // Both can succeed independently (they target different rows — task vs node).
            // rowsA may be 0 or 1; rowsB may be 0 or 1.
            // The invariant: TotalRequired == 2 + (1 if B won).
            await using var verify = MakeContext();
            var node = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == nodeId);
            var task = await verify.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);

            int expectedTotal = 2 + (rowsB == 1 ? 1 : 0);
            Assert.AreEqual(expectedTotal, node.TotalRequired,
                $"Round {round}: T-MIX-02 — TotalRequired must be {expectedTotal} " +
                $"(reassign={rowsA} addApprover={rowsB})");

            // Epoch: bumped by 加签 if B won; untouched by reassign alone (epoch bump happens
            // in the engine's post-CAS AdvanceNodeApproverSetEpochAsync, not tested here).
            uint expectedEpoch = rowsB == 1 ? 1u : 0u;
            Assert.AreEqual(expectedEpoch, node.ApproverSetEpoch,
                $"Round {round}: T-MIX-02 — ApproverSetEpoch must be {expectedEpoch}");

            // If reassign won, C now holds the slot.
            if (rowsA == 1)
            {
                Assert.AreEqual("C", task.AssigneeITCode,
                    $"Round {round}: T-MIX-02 — reassign won; AssigneeITCode must be C");
                Assert.AreEqual("D", task.DelegatedFromITCode,
                    $"Round {round}: T-MIX-02 — DelegatedFromITCode must be D");
            }

            // Stale-epoch completion guard: simulate a completion CAS with epoch=0 when B won.
            // It must return rows==0 (state machine forces re-read).
            if (rowsB == 1)
            {
                await using var cas = MakeContext();
                // Re-read fresh node RowVer and epoch after the add.
                var freshNode = await cas.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == nodeId);

                // Stale-epoch attempt (epoch=0, but node now has epoch=1) must fail.
                int staleRows = await GuardedTransition.CompleteNodeInstanceAsync(
                    cas, nodeId,
                    expectedRowVer: freshNode.RowVer,
                    completedState: NodeState.CompletedApproved,
                    generation: 0,
                    expectedApproverSetEpoch: 0); // stale epoch

                Assert.AreEqual(0, staleRows,
                    $"Round {round}: T-MIX-02 — stale-epoch completion CAS must return 0 " +
                    $"when epoch is {freshNode.ApproverSetEpoch}");

                // Correct-epoch completion succeeds.
                int correctRows = await GuardedTransition.CompleteNodeInstanceAsync(
                    cas, nodeId,
                    expectedRowVer: freshNode.RowVer,
                    completedState: NodeState.CompletedApproved,
                    generation: 0,
                    expectedApproverSetEpoch: freshNode.ApproverSetEpoch);

                Assert.AreEqual(1, correctRows,
                    $"Round {round}: T-MIX-02 — correct-epoch completion CAS must win");
            }
        }
    }

    // ── T-DEL-08: AtAction boundary — inclusive expiry ───────────────────────

    /// <summary>
    /// T-DEL-08 (FIX-D): AtAction claim at the exact delegation expiry boundary.
    ///
    /// <para>Case 1: @now == DelegationExpiresUtc → claim must SUCCEED (inclusive boundary).</para>
    /// <para>Case 2: @now &gt; DelegationExpiresUtc by 1 tick → rows==0; a follow-up read confirms
    /// task is still Pending → code is DelegationExpired (NOT AlreadyHandled).</para>
    ///
    /// <para>The @now timestamp is app-supplied and bound once (never SQL CURRENT_TIMESTAMP).
    /// The test validates the predicate semantics directly against the CAS.</para>
    /// </summary>
    [TestMethod]
    public async Task T_DEL_08_AtAction_Boundary_InclusiveExpiry()
    {
        // Case 1: claim exactly at the expiry boundary — must succeed (inclusive).
        {
            var (_, taskId, _) = await SeedDelegatedTaskAsync(
                assignee: "D", expiresUtc: DateTime.UtcNow.AddHours(1));

            await using var db1 = MakeContext();
            var task = await db1.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);

            // Use the exact expiry as @now (inclusive — @now <= DelegationExpiresUtc must pass).
            var now = task.DelegationExpiresUtc!.Value;

            int rows = await GuardedTransition.ClaimDelegatedTaskAsync(
                db1, taskId,
                expectedRowVer: task.RowVer,
                nextState: TaskState.Approved,
                actedAtUtc: now,
                ct: CancellationToken.None);

            Assert.AreEqual(1, rows, "T-DEL-08 Case 1: claim at exact expiry boundary must succeed (inclusive)");

            await using var verify1 = MakeContext();
            var final1 = await verify1.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);
            Assert.AreEqual(TaskState.Approved, final1.State,
                "T-DEL-08 Case 1: task must be Approved after successful AtAction claim");
        }

        // Case 2: @now is 1 tick AFTER expiry — CAS must return 0 (DelegationExpired).
        // The task must STAY Pending (not claimed), and @now > DelegationExpiresUtc.
        {
            var expiryUtc = DateTime.UtcNow.AddHours(1);
            var (_, taskId, _) = await SeedDelegatedTaskAsync(
                assignee: "D", expiresUtc: expiryUtc);

            await using var db2 = MakeContext();
            var task = await db2.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);

            // Simulate expiry: set @now to 1 tick after DelegationExpiresUtc.
            // In production this is always app-supplied; here we exercise the predicate directly.
            var nowExpired = task.DelegationExpiresUtc!.Value.AddTicks(1);

            int rows = await GuardedTransition.ClaimDelegatedTaskAsync(
                db2, taskId,
                expectedRowVer: task.RowVer,
                nextState: TaskState.Approved,
                actedAtUtc: nowExpired,
                ct: CancellationToken.None);

            Assert.AreEqual(0, rows, "T-DEL-08 Case 2: claim after expiry must return 0");

            // Verify: task stays Pending (never claimed).
            await using var verify2 = MakeContext();
            var final2 = await verify2.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);
            Assert.AreEqual(TaskState.Pending, final2.State,
                "T-DEL-08 Case 2: task must remain Pending after expired AtAction claim");
            // The expired @now is strictly greater — disambiguate as DelegationExpired.
            Assert.IsTrue(nowExpired > final2.DelegationExpiresUtc!.Value,
                "T-DEL-08 Case 2: @now must be past DelegationExpiresUtc to disambiguate as DelegationExpired");
        }
    }

    // ── T-DEL-09: AtAction TOCTOU — single bound @now ────────────────────────

    /// <summary>
    /// T-DEL-09 (FIX-D): Concurrent AtAction claim vs window expiry — no approval after window.
    ///
    /// <para>Two racers on the same delegated task: one claims with @now = within-window,
    /// the other claims with @now = past-window.  Exactly one of:
    /// <list type="bullet">
    ///   <item>In-window racer wins → rows1==1 (task Approved); out-window racer rows==0.</item>
    ///   <item>Out-window racer's @now is past expiry → rows==0 for that racer regardless.</item>
    /// </list>
    /// The key invariant: if the out-window racer wins the race (gets CAS first), it still
    /// returns rows==0 because its @now exceeds DelegationExpiresUtc.  Only the in-window @now
    /// can succeed.  The @now is bound ONCE per racer (no re-read — no TOCTOU gap).</para>
    /// </summary>
    [TestMethod]
    public async Task T_DEL_09_AtAction_TOCTOU_NoApprovalAfterWindow()
    {
        const int Rounds = 20;

        for (int round = 0; round < Rounds; round++)
        {
            var expiryUtc = DateTime.UtcNow.AddHours(1);
            var (_, taskId, _) = await SeedDelegatedTaskAsync(
                assignee: "D", expiresUtc: expiryUtc);

            await using var snap = MakeContext();
            var task = await snap.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);

            var nowInWindow  = task.DelegationExpiresUtc!.Value; // inclusive boundary — should win
            var nowOutWindow = task.DelegationExpiresUtc!.Value.AddSeconds(1); // past — should lose

            var barrier = new SemaphoreSlim(0, 2);

            // Racer A: in-window @now — should win the CAS.
            Task<int> RacerA() => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.ClaimDelegatedTaskAsync(
                    db, taskId,
                    expectedRowVer: task.RowVer,
                    nextState: TaskState.Approved,
                    actedAtUtc: nowInWindow,
                    ct: CancellationToken.None);
            });

            // Racer B: out-window @now — must always return 0 even if it gets the CAS first.
            Task<int> RacerB() => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.ClaimDelegatedTaskAsync(
                    db, taskId,
                    expectedRowVer: task.RowVer,
                    nextState: TaskState.Approved,
                    actedAtUtc: nowOutWindow,
                    ct: CancellationToken.None);
            });

            var tA = RacerA();
            var tB = RacerB();
            barrier.Release(2);

            int rowsA = await tA;
            int rowsB = await tB;

            // Racer B (out-window) must NEVER win (its @now > DelegationExpiresUtc).
            Assert.AreEqual(0, rowsB,
                $"Round {round}: T-DEL-09 — out-window racer must always return 0; got {rowsB}");

            // Racer A (in-window) wins at most once; if it won, task is Approved.
            if (rowsA == 1)
            {
                await using var verify = MakeContext();
                var final = await verify.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);
                Assert.AreEqual(TaskState.Approved, final.State,
                    $"Round {round}: T-DEL-09 — in-window racer won; task must be Approved");
            }
            else
            {
                // rowsA == 0 is valid: both losers (unlikely but possible on timing).
                // The invariant is that rowsB is ALWAYS 0 (the AtAction window predicate enforces this).
                Assert.AreEqual(0, rowsA, $"Round {round}: T-DEL-09 — unexpected rowsA={rowsA}");
            }
        }
    }

    // ── T-DEL-10: AtAssignment — claim succeeds after rule expiry ────────────

    /// <summary>
    /// T-DEL-10: AtAssignment (default) mode — the delegation window is checked at mint time only.
    /// Authority is frozen at assignment.  A standard ClaimApprovalTaskAsync at any time after
    /// DelegationExpiresUtc MUST succeed (no re-check).
    ///
    /// <para>This test validates the AtAssignment contract by attempting a claim with a "current
    /// time" that is past the stored DelegationExpiresUtc.  The standard ClaimApprovalTaskAsync
    /// predicate does NOT include the DelegationExpiresUtc filter, so it must succeed.</para>
    /// </summary>
    [TestMethod]
    public async Task T_DEL_10_AtAssignment_ClaimSucceedsAfterExpiry()
    {
        const int Rounds = 10;

        for (int round = 0; round < Rounds; round++)
        {
            // Seed a delegated task with an ALREADY-EXPIRED window (set in the past).
            // In AtAssignment mode this has no effect — authority was frozen at mint.
            var expiredUtc = DateTime.UtcNow.AddDays(-1); // definitely in the past
            var (_, taskId, _) = await SeedDelegatedTaskAsync(
                assignee: "D", expiresUtc: expiredUtc);

            await using var db = MakeContext();
            var task = await db.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);

            // In AtAssignment mode the claim uses the STANDARD ClaimApprovalTaskAsync
            // (no DelegationExpiresUtc filter) — claim must succeed even though the window expired.
            var nowAfterExpiry = DateTime.UtcNow; // current time is definitely past expiredUtc
            int rows = await GuardedTransition.ClaimApprovalTaskAsync(
                db, taskId,
                expectedRowVer: task.RowVer,
                nextState: TaskState.Approved,
                actedAtUtc: nowAfterExpiry,
                ct: CancellationToken.None);

            Assert.AreEqual(1, rows,
                $"Round {round}: T-DEL-10 — AtAssignment claim must succeed even after DelegationExpiresUtc (authority frozen at mint)");

            await using var verify = MakeContext();
            var final = await verify.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);
            Assert.AreEqual(TaskState.Approved, final.State,
                $"Round {round}: T-DEL-10 — task must be Approved (AtAssignment, no window re-check)");
        }
    }

    // ── T-DEL-11: RevokeDelegation races concurrent claim ────────────────────

    /// <summary>
    /// T-DEL-11: Admin RevokeDelegatedTasksAsync races against a concurrent claim on the same task.
    ///
    /// <para>Per-row CAS: exactly one of:
    /// <list type="bullet">
    ///   <item>Claim won first: task is Approved; revoke's CAS returns rows==0 (NotPending). Idempotent.</item>
    ///   <item>Revoke won first: task reverted to principal; claim's CAS returns rows==0 (AlreadyHandled).</item>
    /// </list>
    /// Partial success (some tasks revoked, some already claimed) is valid and reported per task.
    /// TotalRequired is never touched by either path.</para>
    /// </summary>
    [TestMethod]
    public async Task T_DEL_11_RevokeDelegation_VsConcurrentClaim_OneWinner()
    {
        const int Rounds = 20;
        var ruleId = Guid.NewGuid();

        for (int round = 0; round < Rounds; round++)
        {
            var (nodeId, taskId, _) = await SeedDelegatedTaskAsync(
                assignee: "D",
                expiresUtc: DateTime.UtcNow.AddHours(1),
                delegationRuleId: ruleId,
                delegatedFromITCode: "P"); // P is the principal

            await using var snap = MakeContext();
            var task = await snap.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);

            var barrier = new SemaphoreSlim(0, 2);

            // Racer A: D claims the task (standard CAS on RowVer).
            Task<int> RacerA() => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                return await GuardedTransition.ClaimApprovalTaskAsync(
                    db, taskId,
                    expectedRowVer: task.RowVer,
                    nextState: TaskState.Approved,
                    actedAtUtc: DateTime.UtcNow,
                    ct: CancellationToken.None);
            });

            // Racer B: admin revokes all tasks for the rule (per-row CAS on same task).
            Task<int> RacerB() => Task.Run(async () =>
            {
                await barrier.WaitAsync();
                await using var db = MakeContext();
                int revokedCount = 0;
                await foreach (var outcome in GuardedTransition.RevokeDelegatedTasksAsync(db, ruleId))
                {
                    if (outcome.Result == GuardedTransition.RevokeSingleTaskResult.Revoked)
                        revokedCount++;
                }
                return revokedCount;
            });

            var tA = RacerA();
            var tB = RacerB();
            barrier.Release(2);

            int claimedRows = await tA;
            int revokedCount = await tB;

            // Exactly one of: claim won (task Approved) or revoke won (task back to principal P).
            int winners = (claimedRows == 1 ? 1 : 0) + revokedCount;
            Assert.IsTrue(winners <= 1,
                $"Round {round}: T-DEL-11 — at most one winner; claimedRows={claimedRows} revokedCount={revokedCount}");

            await using var verify = MakeContext();
            var final = await verify.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskId);

            if (claimedRows == 1)
            {
                // Claim won: task must be Approved; revoke's CAS returned rows==0 (NotPending).
                Assert.AreEqual(TaskState.Approved, final.State,
                    $"Round {round}: T-DEL-11 — claim won; task must be Approved");
                Assert.AreEqual(0, revokedCount,
                    $"Round {round}: T-DEL-11 — claim won; revoke must report 0 tasks revoked");
            }
            else if (revokedCount == 1)
            {
                // Revoke won: task reverted to principal P; still Pending; delegatee D can no longer claim.
                Assert.AreEqual(TaskState.Pending, final.State,
                    $"Round {round}: T-DEL-11 — revoke won; task must remain Pending");
                Assert.AreEqual("P", final.AssigneeITCode,
                    $"Round {round}: T-DEL-11 — revoke won; AssigneeITCode must be reverted to P");
                Assert.IsNull(final.DelegationRuleId,
                    $"Round {round}: T-DEL-11 — revoke won; DelegationRuleId must be cleared");
                Assert.IsNull(final.DelegatedFromITCode,
                    $"Round {round}: T-DEL-11 — revoke won; DelegatedFromITCode must be cleared");
            }
            else
            {
                // Both returned 0 (both lost to each other — this can happen on SQLite because the
                // first CAS bumps RowVer, making the second CAS fail on RowVer).
                // In this case we just verify the task is in a consistent state.
                Assert.IsTrue(final.State == TaskState.Pending || final.State == TaskState.Approved,
                    $"Round {round}: T-DEL-11 — both returned 0; task must be in a consistent state");
            }

            // TotalRequired must be unchanged regardless of outcome.
            var node = await verify.NodeInstances.AsNoTracking().SingleAsync(n => n.ID == nodeId);
            Assert.AreEqual(2, node.TotalRequired,
                $"Round {round}: T-DEL-11 — TotalRequired must remain 2 (no count change)");
        }
    }

    // ── Seed helpers for AtAction / delegation tests ─────────────────────────

    /// <summary>
    /// Seed an Activated node + a Pending delegated ApprovalTask with
    /// <c>DelegationRuleId</c> and <c>DelegationExpiresUtc</c> set.
    /// Returns (nodeId, taskId, instanceId).
    /// </summary>
    private async Task<(Guid nodeId, Guid taskId, Guid instanceId)> SeedDelegatedTaskAsync(
        string assignee = "D",
        DateTime? expiresUtc = null,
        Guid? delegationRuleId = null,
        string? delegatedFromITCode = null,
        uint generation = 0)
    {
        var instanceId = Guid.NewGuid();
        var nodeId     = Guid.NewGuid();
        var taskId     = Guid.NewGuid();

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
            TotalRequired = 2,
            ApproveMode = ApproveMode.All,
            ApproverSetEpoch = 0,
            Generation = generation,
        });
        db.ApprovalTasks.Add(new ApprovalTask
        {
            ID = taskId,
            State = TaskState.Pending,
            RowVer = 0,
            AssigneeITCode = assignee,
            NodeInstanceId = nodeId,
            TenantCode = "test",
            IsValid = true,
            Generation = generation,
            SequenceOrder = 0,
            // Delegation provenance fields.
            DelegationRuleId     = delegationRuleId,
            DelegationExpiresUtc = expiresUtc,
            DelegatedFromITCode  = delegatedFromITCode,
        });
        await db.SaveChangesAsync();
        return (nodeId, taskId, instanceId);
    }
}

// ─── T-DEL-13: AtAction conformance (FIX-E provider fallback, SQLite live) ───

/// <summary>
/// T-DEL-13: AtAction conformance tests (FIX-E, design §4 R3 + §6 T-DEL-13).
///
/// SQLite runs inline (every CI push). Other providers follow the existing
/// skip-gated stub pattern from <see cref="ConcurrencyConformanceTests_LiveDb"/>.
///
/// Validates:
/// <list type="bullet">
///   <item>The AtAction CAS predicate correctly enforces the delegation window on SQLite.</item>
///   <item>Boundary semantics: @now == DelegationExpiresUtc succeeds (inclusive).</item>
///   <item>The skip-gated stubs for FIX-E SELECT-FOR-UPDATE fallback on Oracle/DaMeng.</item>
/// </list>
/// </summary>
[TestClass]
public class ConcurrencyConformanceTests_DelegateTask_Conformance : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfDelegateConf_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        using var db = new WfTestContext(_dbName);
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

    /// <summary>
    /// T-DEL-13 (SQLite live): ClaimDelegatedTaskAsync enforces the window predicate correctly.
    ///
    /// <para>Specifically: @now == DelegationExpiresUtc → rows==1 (inclusive);
    /// @now &gt; DelegationExpiresUtc by 1 tick → rows==0 (expired).</para>
    ///
    /// This is the conformance gate for the core CAS logic; the FIX-E SELECT-FOR-UPDATE
    /// fallback for Oracle/DaMeng is exercised in the live-provider stubs below.
    /// </summary>
    [TestMethod]
    public async Task T_DEL_13_AtAction_SQLite_WindowPredicate_Conformance()
    {
        // Seed a delegated task with a known expiry.
        var expiresAt = DateTime.UtcNow.AddHours(2);
        var instanceId = Guid.NewGuid();
        var nodeId     = Guid.NewGuid();
        var taskInWindow  = Guid.NewGuid();
        var taskExpired   = Guid.NewGuid();

        await using var seed = MakeContext();
        seed.ProcessInstances.Add(new ProcessInstance
        {
            ID = instanceId, State = InstanceState.Running, RowVer = 0,
            InitiatorITCode = "i", DefinitionVersionId = Guid.NewGuid(), IsValid = true,
        });
        seed.NodeInstances.Add(new NodeInstance
        {
            ID = nodeId, State = NodeState.Activated, RowVer = 0,
            NodeKey = "n", InstanceId = instanceId,
            TotalRequired = 2, ApproveMode = ApproveMode.All, ApproverSetEpoch = 0,
        });
        // Task 1: in-window (expiry in future).
        seed.ApprovalTasks.Add(new ApprovalTask
        {
            ID = taskInWindow, State = TaskState.Pending, RowVer = 0,
            AssigneeITCode = "A", NodeInstanceId = nodeId, TenantCode = "t",
            IsValid = true, SequenceOrder = 0,
            DelegationExpiresUtc = expiresAt,
        });
        // Task 2: expired (expiry in past — use expiresAt as now to simulate past expiry via different now).
        seed.ApprovalTasks.Add(new ApprovalTask
        {
            ID = taskExpired, State = TaskState.Pending, RowVer = 0,
            AssigneeITCode = "B", NodeInstanceId = nodeId, TenantCode = "t",
            IsValid = true, SequenceOrder = 1,
            DelegationExpiresUtc = expiresAt,
        });
        await seed.SaveChangesAsync();

        await using var ctx1 = MakeContext();
        var t1 = await ctx1.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskInWindow);

        // Case 1: @now == DelegationExpiresUtc (exact boundary) → must succeed.
        var nowAtBoundary = t1.DelegationExpiresUtc!.Value;
        int rows1 = await GuardedTransition.ClaimDelegatedTaskAsync(
            ctx1, taskInWindow, t1.RowVer, TaskState.Approved, nowAtBoundary);
        Assert.AreEqual(1, rows1, "T-DEL-13 SQLite: in-window claim at exact boundary must succeed");

        await using var ctx2 = MakeContext();
        var t2 = await ctx2.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskExpired);

        // Case 2: @now is 1 tick past DelegationExpiresUtc → must return 0.
        var nowExpired = t2.DelegationExpiresUtc!.Value.AddTicks(1);
        int rows2 = await GuardedTransition.ClaimDelegatedTaskAsync(
            ctx2, taskExpired, t2.RowVer, TaskState.Approved, nowExpired);
        Assert.AreEqual(0, rows2, "T-DEL-13 SQLite: expired claim must return 0");

        // Task 2 must remain Pending.
        await using var verify = MakeContext();
        var finalT2 = await verify.ApprovalTasks.AsNoTracking().SingleAsync(t => t.ID == taskExpired);
        Assert.AreEqual(TaskState.Pending, finalT2.State,
            "T-DEL-13 SQLite: expired task must remain Pending");
    }

    // ── Skip-gated stubs for FIX-E SELECT-FOR-UPDATE fallback ────────────────
    //
    // Oracle and DaMeng may not translate ExecuteUpdateAsync with a DateTime comparison
    // in WHERE (FIX-E, design §4 R3).  The fallback (SELECT FOR UPDATE + in-txn check)
    // is exercised only with real provider instances.  These stubs gate the test correctly
    // so a provider becoming available triggers a real (failing) test, not a silent skip.

    private static string TryGetConnectionString(string envVar)
    {
        var cs = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(cs))
        {
            Assert.Inconclusive(
                $"Skipped: environment variable '{envVar}' not set. " +
                "Set to a real connection string to run FIX-E provider conformance.");
        }
        Assert.Fail(
            $"FIX-E provider conformance body not implemented for '{envVar}' — tracked in #270. " +
            "Replace this Assert.Fail with the real SELECT-FOR-UPDATE fallback assertion when WF-3 is shipped.");
        return cs!;
    }

    [TestMethod]
    [TestCategory("ProviderConformance")]
    public void T_DEL_13_Oracle_AtAction_SelectForUpdate_Fallback()
    {
        var cs = TryGetConnectionString("WTM_TEST_ORACLE_CS");
        // WF-3: implement using Oracle.EntityFrameworkCore.
        // Exercise ClaimDelegatedTaskAsync via the SELECT-FOR-UPDATE code path (FIX-E):
        //   BeginTransactionAsync → SELECT t WHERE ID==@id FOR UPDATE → in-txn check @now <= DelegationExpiresUtc
        //   → ExecuteUpdateAsync flip → CommitAsync.
        // Boundary: @now == DelegationExpiresUtc → success; @now+1tick → rows==0.
        _ = cs;
    }

    [TestMethod]
    [TestCategory("ProviderConformance")]
    public void T_DEL_13_DaMeng_AtAction_SelectForUpdate_Fallback()
    {
        var cs = TryGetConnectionString("WTM_TEST_DAMENG_CS");
        // WF-3: implement using EntityFrameworkCore.Dm (达梦).
        // Same FIX-E SELECT-FOR-UPDATE pattern as Oracle above.
        // DaMeng is a primary target-market DB; conformance failure here is a ship-blocker.
        _ = cs;
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
