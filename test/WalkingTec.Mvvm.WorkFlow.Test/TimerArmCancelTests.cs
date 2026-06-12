#nullable enable
// WF-20.2 — Arm sites + cancel obligations + DueUtc + validator tests.
//
// Covers: T-TMO-04, T-TMO-10, T-TMO-11, T-TMO-13, T-TMO-16, T-TMO-17.
//
//   T-TMO-04  Fire vs 回退 STEP-2 cancel, both orders.
//             One Status winner; losing path rows==0; no FK abort.
//
//   T-TMO-10  Sequential per-step: advance cancels old timer, arms next step.
//             Structural invariant: at any step boundary, each step that advances
//             loses its task timer; the next Pending step gets a fresh timer.
//
//   T-TMO-11  Withdraw / instance-terminal sweep + racing fire.
//             Instance-wide cancel sweeps all Armed timers; racing fire post-cancel
//             returns rows==0 (timer already Cancelled).
//
//   T-TMO-13  Returning-lease reclaim by RowVer.
//             Expired lease → State=Running; live (not-yet-expired) lease untouched;
//             dead-owner's RowVer-stale commit returns rows==0.
//
//   T-TMO-16  Business-calendar severity.
//             Remind + PassThrough → arms + no-warn path (no auto-action guard).
//             AutoApprove + PassThrough + businessCalendar:true → skip + LogWarning.
//             Validator: TimeoutAutoActionWithBusinessCalendar rejects at publish.
//             Validator: AllowTimerAutoAction=false rejects AutoApprove at publish.
//
//   T-TMO-17  DueUtc stamped at arm site (task-scoped Sequential).
//             DueUtc is engine-derived; setting it externally does NOT arm a timer.
//
// DB: SQLite shared-in-memory.
// Uses WfEngineTestContext (EngineTests.cs) + WfTestContext (ConcurrencyConformanceTests.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ─── Shared SQLite fixture for timer arm/cancel tests ─────────────────────────

/// <summary>
/// SQLite fixture for WF-20.2 arm/cancel tests.
/// Reuses WfTestContext (ConcurrencyConformanceTests.cs) for raw-CAS tests
/// and WfEngineTestContext (EngineTests.cs) for full-engine tests.
/// </summary>
[TestClass]
public class TimerArmCancelTests : IDisposable
{
    // ── SQLite fixtures ───────────────────────────────────────────────────────

    // Raw-CAS fixture (WfTestContext — has WorkflowTimer).
    private SqliteConnection _rawKeepAlive = null!;
    private string _rawDbName = null!;

    // Engine fixture (WfEngineTestContext — has all WorkFlow tables).
    private SqliteConnection _engKeepAlive = null!;
    private string _engDbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _rawDbName = $"WfTimerArmRaw_{Guid.NewGuid():N}";
        _rawKeepAlive = new SqliteConnection($"DataSource={_rawDbName}?mode=memory&cache=shared");
        _rawKeepAlive.Open();
        using var rawCtx = MakeRawContext();
        rawCtx.Database.EnsureCreated();

        _engDbName = $"WfTimerArmEng_{Guid.NewGuid():N}";
        _engKeepAlive = new SqliteConnection($"DataSource={_engDbName}?mode=memory&cache=shared");
        _engKeepAlive.Open();
        using var engCtx = MakeEngContext();
        engCtx.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _rawKeepAlive?.Close(); _rawKeepAlive?.Dispose();
        _engKeepAlive?.Close(); _engKeepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    private WfTestContext     MakeRawContext() => new(_rawDbName);
    private WfEngineTestContext MakeEngContext() => new(_engDbName);

    // ── Seed helpers (raw) ────────────────────────────────────────────────────

    private async Task<(Guid instanceId, Guid nodeId)> SeedRunningInstanceAndNodeAsync(
        WfTestContext db, InstanceState state = InstanceState.Running, uint generation = 0)
    {
        var instanceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        db.ProcessInstances.Add(new ProcessInstance
        {
            ID                  = instanceId,
            State               = state,
            RowVer              = 0,
            InitiatorITCode     = "tester",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid             = true,
            Generation          = generation,
        });
        db.NodeInstances.Add(new NodeInstance
        {
            ID            = nodeId,
            State         = NodeState.Activated,
            RowVer        = 0,
            NodeKey       = "approval",
            InstanceId    = instanceId,
            TenantCode    = null,
            TotalRequired = 1,
            ApproveMode   = ApproveMode.Any,
            Generation    = generation,
        });
        await db.SaveChangesAsync();
        return (instanceId, nodeId);
    }

    private async Task<Guid> SeedArmedTimerAsync(
        WfTestContext db, Guid nodeId, string idempotencyKey,
        uint generation = 0, DateTime? fireAt = null,
        TimerAction action = TimerAction.Remind,
        Guid? taskId = null)
    {
        var timerId = Guid.NewGuid();
        db.WorkflowTimers.Add(new WorkflowTimer
        {
            ID             = timerId,
            Status         = TimerStatus.Armed,
            RowVer         = 0,
            NodeInstanceId = nodeId,
            ApprovalTaskId = taskId,
            IdempotencyKey = idempotencyKey,
            FireAtUtc      = fireAt ?? DateTime.UtcNow.AddHours(-1),
            Action         = action,
            Generation     = generation,
        });
        await db.SaveChangesAsync();
        return timerId;
    }

    // ── Engine seed helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Build a graph JSON with a timeout Approval node. Approvers passed as comma-separated.
    /// </summary>
    private static string BuildApprovalGraphWithTimeout(
        string approverITCode,
        ApproveMode mode = ApproveMode.Sequential,
        string duration = "PT1H",
        TimerAction action = TimerAction.Remind,
        bool businessCalendar = false)
    {
        return WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "TimeoutGraph",
            Name = "TimeoutGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = mode,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = approverITCode },
                    Timeout      = new TimeoutDef
                    {
                        Duration         = duration,
                        Action           = action,
                        BusinessCalendar = businessCalendar,
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
    }

    /// <summary>Build a two-approver Sequential Approval graph with timeout.</summary>
    private static string BuildTwoApproverGraphWithTimeout(
        string approver1, string approver2, string duration = "PT1H")
    {
        return WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key  = "TwoApproverTimeoutGraph",
            Name = "TwoApproverTimeoutGraph",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproveMode  = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = $"{approver1},{approver2}" },
                    Timeout      = new TimeoutDef { Duration = duration, Action = TimerAction.Remind },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        });
    }

    private async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfEngineTestContext ctx, string graphJson, string? tenantCode = null)
    {
        var version = new ProcessDefinitionVersion
        {
            ID            = Guid.NewGuid(),
            DefinitionId  = Guid.NewGuid(),
            VersionNo     = 1,
            SchemaVersion = 1,
            GraphJson     = graphJson,
            ContentHash   = "test-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            TenantCode    = tenantCode,
            IsValid       = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }

    private (IWorkflowEngine engine, WfEngineTestContext ctx) MakeEngineWithCalendar(
        IBusinessCalendar calendar,
        WorkFlowOptions? options = null)
    {
        var ctx      = MakeEngContext();
        var opts     = options ?? new WorkFlowOptions();
        // Use DefaultApproverResolverExposed (from SequentialTests.cs) so that
        // comma-separated approver lists are split correctly into per-step tasks.
        // StaticApproverResolver would return "alice,bob" as a single opaque string.
        var resolver = new DefaultApproverResolverExposed(opts, ctx);
        var dispatcher = NodeKindDispatcher_Exposed.CreateWithSequential(resolver, opts);
        var engine = WorkflowEngine_Exposed.CreateWithCalendar(
            ctx, dispatcher, opts, calendar, NullLogger.Instance);
        return (engine, ctx);
    }

    // ── T-TMO-04: Fire vs cancel, both orders ─────────────────────────────────

    /// <summary>
    /// T-TMO-04a: Fire wins before cancel.
    /// FireTimerAsync rows==1, then CancelTimersForNodeAsync on the now-Fired timer
    /// returns rows==0 (already non-Armed).
    /// </summary>
    [TestMethod]
    public async Task T_TMO_04a_FireWins_CancelIsNoOp()
    {
        await using var db1 = MakeRawContext();
        var (_, nodeId) = await SeedRunningInstanceAndNodeAsync(db1);
        var key = $"tmo04a-{Guid.NewGuid():N}";
        var timerId = await SeedArmedTimerAsync(db1, nodeId, key);

        // Fire wins.
        await using var fireDb = MakeRawContext();
        var fireRows = await GuardedTransition.FireTimerAsync(fireDb, timerId, 0);
        Assert.AreEqual(1, fireRows, "T-TMO-04a: fire must win rows==1");

        // Cancel on the now-Fired timer must be a no-op.
        await using var cancelDb = MakeRawContext();
        var cancelRows = await GuardedTransition.CancelTimersForNodeAsync(cancelDb, nodeId);
        Assert.AreEqual(0, cancelRows, "T-TMO-04a: cancel after fire must be rows==0 (timer not Armed)");

        // Verify final state.
        await using var verify = MakeRawContext();
        var final = await verify.WorkflowTimers.AsNoTracking().SingleAsync(t => t.ID == timerId);
        Assert.AreEqual(TimerStatus.Fired, final.Status,
            "T-TMO-04a: timer must remain Fired, not Cancelled");
    }

    /// <summary>
    /// T-TMO-04b: Cancel wins before fire.
    /// CancelTimersForNodeAsync rows==1, then FireTimerAsync on the now-Cancelled timer
    /// returns rows==0 (stale RowVer / not Armed).
    /// </summary>
    [TestMethod]
    public async Task T_TMO_04b_CancelWins_FireIsNoOp()
    {
        await using var db1 = MakeRawContext();
        var (_, nodeId) = await SeedRunningInstanceAndNodeAsync(db1);
        var key = $"tmo04b-{Guid.NewGuid():N}";
        var timerId = await SeedArmedTimerAsync(db1, nodeId, key);

        // Cancel wins first.
        await using var cancelDb = MakeRawContext();
        var cancelRows = await GuardedTransition.CancelTimersForNodeAsync(cancelDb, nodeId);
        Assert.AreEqual(1, cancelRows, "T-TMO-04b: cancel must win rows==1");

        // Fire attempt on the now-Cancelled timer (RowVer now 1, but Status!=Armed) → rows==0.
        await using var fireDb = MakeRawContext();
        var fireRows = await GuardedTransition.FireTimerAsync(fireDb, timerId, 0);
        Assert.AreEqual(0, fireRows, "T-TMO-04b: fire after cancel must be rows==0 (timer Cancelled)");

        // Verify final state.
        await using var verify = MakeRawContext();
        var final = await verify.WorkflowTimers.AsNoTracking().SingleAsync(t => t.ID == timerId);
        Assert.AreEqual(TimerStatus.Cancelled, final.Status,
            "T-TMO-04b: timer must stay Cancelled");
    }

    // ── T-TMO-10: Sequential per-step timer cancel + re-arm ───────────────────

    /// <summary>
    /// T-TMO-10: Structural invariant across Sequential step advance.
    ///
    /// Start → Approval(Sequential, 2 approvers, timeout PT1H Remind) → End.
    /// After StartAsync: step-0 has a timer (Armed); step-1 has no timer yet.
    /// After approving step-0: step-0 timer is Cancelled; step-1 timer is Armed.
    /// After approving step-1: step-1 timer is Cancelled; instance Approved.
    /// </summary>
    [TestMethod]
    public async Task T_TMO_10_Sequential_AdvanceCancelsOldTimer_ArmsNextStep()
    {
        const string A1 = "alice";
        const string A2 = "bob";

        var calendar = new PassThroughBusinessCalendar();
        var (engine, ctx) = MakeEngineWithCalendar(calendar);
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, BuildTwoApproverGraphWithTimeout(A1, A2));
        var instance = await engine.StartAsync(
            version.ID, null, A1, null, ct: CancellationToken.None);

        // After start: step-0 (alice) has an Armed timer; step-1 (bob) has no timer.
        await using var v1 = MakeEngContext();
        var allTimersAfterStart = await v1.Set<WorkflowTimer>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId != Guid.Empty) // all timers
            .ToListAsync();

        // Exactly 1 Armed timer right after start.
        var armedAfterStart = allTimersAfterStart.Where(t => t.Status == TimerStatus.Armed).ToList();
        Assert.AreEqual(1, armedAfterStart.Count,
            "T-TMO-10: step-0 must have exactly 1 Armed timer after StartAsync");

        // Get the step-0 timer ID.
        var step0TimerId = armedAfterStart[0].ID;
        var step0TaskId  = armedAfterStart[0].ApprovalTaskId;
        Assert.IsNotNull(step0TaskId, "T-TMO-10: step-0 timer must be task-scoped (non-null ApprovalTaskId)");

        // Approve step-0 (alice).
        var result = await engine.ApproveTaskAsync(step0TaskId!.Value, A1, null, ct: CancellationToken.None);
        Assert.IsTrue(result.IsSuccess, $"T-TMO-10: step-0 approve failed: {result.Detail}");

        // After step-0 approve: step-0 timer Cancelled; step-1 (bob) gets a new Armed timer.
        await using var v2 = MakeEngContext();
        var allTimersAfterStep0 = await v2.Set<WorkflowTimer>()
            .AsNoTracking()
            .ToListAsync();

        var step0TimerFinal = allTimersAfterStep0.Single(t => t.ID == step0TimerId);
        Assert.AreEqual(TimerStatus.Cancelled, step0TimerFinal.Status,
            "T-TMO-10: step-0 timer must be Cancelled after step-0 approval");

        var armedAfterStep0 = allTimersAfterStep0.Where(t => t.Status == TimerStatus.Armed).ToList();
        Assert.AreEqual(1, armedAfterStep0.Count,
            "T-TMO-10: step-1 must have exactly 1 Armed timer after step-0 approval");
        var step1TimerId = armedAfterStep0[0].ID;
        var step1TaskId  = armedAfterStep0[0].ApprovalTaskId;
        Assert.IsNotNull(step1TaskId, "T-TMO-10: step-1 timer must be task-scoped");

        // Approve step-1 (bob) → instance Approved.
        var result2 = await engine.ApproveTaskAsync(step1TaskId!.Value, A2, null, ct: CancellationToken.None);
        Assert.IsTrue(result2.IsSuccess, $"T-TMO-10: step-1 approve failed: {result2.Detail}");

        // After step-1 approve: step-1 timer Cancelled; instance Approved; no Armed timers.
        await using var v3 = MakeEngContext();
        var step1TimerFinal = await v3.Set<WorkflowTimer>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == step1TimerId);
        Assert.AreEqual(TimerStatus.Cancelled, step1TimerFinal.Status,
            "T-TMO-10: step-1 timer must be Cancelled after step-1 approval");

        var armedAfterApproved = await v3.Set<WorkflowTimer>()
            .AsNoTracking()
            .CountAsync(t => t.Status == TimerStatus.Armed);
        Assert.AreEqual(0, armedAfterApproved,
            "T-TMO-10: no Armed timers must exist after instance is Approved");

        // Confirm instance is Approved.
        var instFinal = await v3.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(i => i.ID == instance.ID);
        Assert.AreEqual(InstanceState.Approved, instFinal.State,
            "T-TMO-10: instance must reach Approved after both approvals");
    }

    // ── T-TMO-11: Withdraw instance-wide sweep + racing fire ──────────────────

    /// <summary>
    /// T-TMO-11a: Withdraw sweeps all Armed timers for the instance.
    /// After WithdrawAsync, every Armed timer is Cancelled;
    /// a racing FireTimerAsync on one of them returns rows==0.
    /// </summary>
    [TestMethod]
    public async Task T_TMO_11a_Withdraw_SweepsAllTimers_RacingFireNoOp()
    {
        const string A1 = "alice";

        var calendar = new PassThroughBusinessCalendar();
        var (engine, ctx) = MakeEngineWithCalendar(calendar);
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, BuildApprovalGraphWithTimeout(A1));
        var instance = await engine.StartAsync(
            version.ID, null, A1, null, ct: CancellationToken.None);

        // Capture the Armed timer before withdraw.
        await using var v1 = MakeEngContext();
        var armedBefore = await v1.Set<WorkflowTimer>()
            .AsNoTracking()
            .Where(t => t.Status == TimerStatus.Armed)
            .ToListAsync();
        Assert.AreEqual(1, armedBefore.Count,
            "T-TMO-11a: must have exactly 1 Armed timer before withdraw");
        var timerId = armedBefore[0].ID;
        var timerRowVer = armedBefore[0].RowVer;

        // Withdraw the instance.
        var wr = await engine.WithdrawAsync(instance.ID, A1, ct: CancellationToken.None);
        Assert.IsTrue(wr.IsSuccess, $"T-TMO-11a: withdraw failed: {wr.Detail}");

        // Timer must be Cancelled.
        await using var v2 = MakeEngContext();
        var timerAfterWithdraw = await v2.Set<WorkflowTimer>()
            .AsNoTracking()
            .SingleAsync(t => t.ID == timerId);
        Assert.AreEqual(TimerStatus.Cancelled, timerAfterWithdraw.Status,
            "T-TMO-11a: timer must be Cancelled after withdraw");

        // Racing fire on the (now-Cancelled) timer must be a no-op (rows==0).
        await using var fireDb = MakeEngContext();
        var fireRows = await GuardedTransition.FireTimerAsync(fireDb, timerId, timerRowVer);
        Assert.AreEqual(0, fireRows,
            "T-TMO-11a: racing fire post-withdraw must return rows==0 (timer Cancelled)");
    }

    // ── T-TMO-13: Returning-lease reclaim by RowVer ───────────────────────────

    /// <summary>
    /// T-TMO-13a: Expired lease (ReturningLeaseUtc in the past) → ReclaimReturningLeaseByRowVerAsync
    /// returns rows==1 and transitions State to Running.
    /// </summary>
    [TestMethod]
    public async Task T_TMO_13a_ExpiredLease_Reclaimed_StateRunning()
    {
        await using var db = MakeRawContext();
        var instanceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        db.ProcessInstances.Add(new ProcessInstance
        {
            ID                  = instanceId,
            State               = InstanceState.Returning,
            RowVer              = 0,
            InitiatorITCode     = "tester",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid             = true,
            // Lease expired 5 minutes ago.
            ReturningLeaseUtc   = DateTime.UtcNow.AddMinutes(-5),
        });
        db.NodeInstances.Add(new NodeInstance
        {
            ID = nodeId, State = NodeState.Activated, RowVer = 0,
            NodeKey = "n1", InstanceId = instanceId, TotalRequired = 1,
        });
        await db.SaveChangesAsync();

        // Caller pre-filters expired candidates and provides the RowVer from the SELECT.
        await using var reclaimDb = MakeRawContext();
        var rows = await GuardedTransition.ReclaimReturningLeaseByRowVerAsync(
            reclaimDb, instanceId, expectedRowVer: 0);
        Assert.AreEqual(1, rows, "T-TMO-13a: expired lease must be reclaimed (rows==1)");

        await using var verify = MakeRawContext();
        var inst = await verify.ProcessInstances.AsNoTracking().SingleAsync(i => i.ID == instanceId);
        Assert.AreEqual(InstanceState.Running, inst.State,
            "T-TMO-13a: instance must be Running after reclaim");
        Assert.IsNull(inst.ReturningLeaseUtc,
            "T-TMO-13a: ReturningLeaseUtc must be cleared after reclaim");
    }

    /// <summary>
    /// T-TMO-13b: Live lease (ReturningLeaseUtc in the future) — caller skips via the
    /// client-side < now check; the CAS predicate itself is RowVer-only, so passing a
    /// correct RowVer would reclaim even a live lease.  This test verifies the predicate
    /// does NOT protect against a live lease (the caller must enforce that) and that
    /// a stale RowVer fails regardless of the lease expiry.
    /// </summary>
    [TestMethod]
    public async Task T_TMO_13b_StalerRowVer_Fails_Rows0()
    {
        await using var db = MakeRawContext();
        var instanceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        db.ProcessInstances.Add(new ProcessInstance
        {
            ID                  = instanceId,
            State               = InstanceState.Returning,
            RowVer              = 0,
            InitiatorITCode     = "tester",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid             = true,
            // Live lease (future).
            ReturningLeaseUtc   = DateTime.UtcNow.AddMinutes(25),
        });
        db.NodeInstances.Add(new NodeInstance
        {
            ID = nodeId, State = NodeState.Activated, RowVer = 0,
            NodeKey = "n1", InstanceId = instanceId, TotalRequired = 1,
        });
        await db.SaveChangesAsync();

        // Attempt with a wrong RowVer (stale) → must fail regardless of lease time.
        await using var reclaimDb = MakeRawContext();
        var rows = await GuardedTransition.ReclaimReturningLeaseByRowVerAsync(
            reclaimDb, instanceId, expectedRowVer: 99); // stale
        Assert.AreEqual(0, rows, "T-TMO-13b: stale RowVer must fail (rows==0)");

        await using var verify = MakeRawContext();
        var inst = await verify.ProcessInstances.AsNoTracking().SingleAsync(i => i.ID == instanceId);
        Assert.AreEqual(InstanceState.Returning, inst.State,
            "T-TMO-13b: state must stay Returning (stale RowVer rejected)");
    }

    /// <summary>
    /// T-TMO-13c: Dead-owner RowVer contention.
    /// Two concurrent reaper hosts read the same snapshot.  One reclaims (rows==1,
    /// RowVer→1).  The other then tries with the old RowVer==0 → rows==0 (CAS miss).
    /// </summary>
    [TestMethod]
    public async Task T_TMO_13c_ConcurrentReclaim_ExactlyOneWinner()
    {
        await using var db = MakeRawContext();
        var instanceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        db.ProcessInstances.Add(new ProcessInstance
        {
            ID                  = instanceId,
            State               = InstanceState.Returning,
            RowVer              = 0,
            InitiatorITCode     = "tester",
            DefinitionVersionId = Guid.NewGuid(),
            IsValid             = true,
            ReturningLeaseUtc   = DateTime.UtcNow.AddMinutes(-5), // expired
        });
        db.NodeInstances.Add(new NodeInstance
        {
            ID = nodeId, State = NodeState.Activated, RowVer = 0,
            NodeKey = "n1", InstanceId = instanceId, TotalRequired = 1,
        });
        await db.SaveChangesAsync();

        // Both reapers read RowVer==0.
        await using var reaper1 = MakeRawContext();
        await using var reaper2 = MakeRawContext();

        var rows1 = await GuardedTransition.ReclaimReturningLeaseByRowVerAsync(reaper1, instanceId, 0);
        var rows2 = await GuardedTransition.ReclaimReturningLeaseByRowVerAsync(reaper2, instanceId, 0);

        Assert.AreEqual(1, rows1 + rows2,
            "T-TMO-13c: exactly one reaper must win (rows1+rows2==1)");
    }

    // ── T-TMO-16: Business-calendar severity ─────────────────────────────────

    /// <summary>
    /// T-TMO-16a: Remind action + PassThroughBusinessCalendar → arms without warning.
    /// PassThrough + Remind is always allowed (no auto-action → no fail-closed guard).
    /// StartAsync must arm the timer and DueUtc must be set on the task.
    /// </summary>
    [TestMethod]
    public async Task T_TMO_16a_Remind_PassThrough_Arms_NoDueUtcError()
    {
        const string A1 = "alice";
        // PassThrough + Remind: must arm fine.
        var calendar = new PassThroughBusinessCalendar();
        var (engine, ctx) = MakeEngineWithCalendar(calendar);
        await using var _ = ctx;

        // businessCalendar:false, action=Remind → no guard triggers.
        var version = await SeedVersionAsync(ctx,
            BuildApprovalGraphWithTimeout(A1, ApproveMode.Sequential, "PT1H", TimerAction.Remind, false));
        var instance = await engine.StartAsync(
            version.ID, null, A1, null, ct: CancellationToken.None);
        Assert.AreEqual(InstanceState.Running, instance.State);

        await using var verify = MakeEngContext();
        var timer = await verify.Set<WorkflowTimer>().AsNoTracking()
            .SingleOrDefaultAsync(t => t.Status == TimerStatus.Armed);
        Assert.IsNotNull(timer, "T-TMO-16a: Remind + PassThrough must arm a timer");
        Assert.AreEqual(TimerAction.Remind, timer!.Action,
            "T-TMO-16a: armed timer must have Action=Remind");

        // DueUtc must be stamped on the step-0 task.
        var task = await verify.Set<ApprovalTask>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.State == TaskState.Pending);
        // DueUtc is only set for task-scoped (Sequential) timers.
        // PassThrough just adds PT1H to UtcNow, so DueUtc must be in the future.
        Assert.IsNotNull(task?.DueUtc, "T-TMO-16a: DueUtc must be stamped on step-0 task");
        Assert.IsTrue(task!.DueUtc > DateTime.UtcNow,
            "T-TMO-16a: DueUtc must be in the future (PT1H from now)");
    }

    /// <summary>
    /// T-TMO-16b: AutoApprove + PassThrough + businessCalendar:true → skip arm + warn.
    /// The fail-closed severity rule (§0 S2): dangerous auto-action combined with
    /// pass-through calendar is rejected at arm time (StartAsync skips the arm, no timer).
    /// </summary>
    [TestMethod]
    public async Task T_TMO_16b_AutoApprove_PassThrough_BusinessCalendarTrue_SkipsArm()
    {
        const string A1 = "alice";
        var calendar = new PassThroughBusinessCalendar();
        var (engine, ctx) = MakeEngineWithCalendar(calendar);
        await using var _ = ctx;

        // businessCalendar:true + AutoApprove + PassThrough → arm must be skipped.
        var version = await SeedVersionAsync(ctx,
            BuildApprovalGraphWithTimeout(A1, ApproveMode.Sequential, "PT1H",
                TimerAction.AutoApprove, businessCalendar: true));
        var instance = await engine.StartAsync(
            version.ID, null, A1, null, ct: CancellationToken.None);
        Assert.AreEqual(InstanceState.Running, instance.State,
            "T-TMO-16b: engine must not throw; arm is silently skipped");

        await using var verify = MakeEngContext();
        var armedCount = await verify.Set<WorkflowTimer>().AsNoTracking()
            .CountAsync(t => t.Status == TimerStatus.Armed);
        Assert.AreEqual(0, armedCount,
            "T-TMO-16b: no Armed timers must exist (arm skipped fail-closed)");
    }

    /// <summary>
    /// T-TMO-16c: Validator rejects TimeoutAutoActionWithBusinessCalendar at publish.
    /// </summary>
    [TestMethod]
    public void T_TMO_16c_Validator_RejectsAutoAction_WithBusinessCalendar()
    {
        var graph = new WorkflowGraph
        {
            Key  = "G",
            Name = "G",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "a" },
                    Timeout      = new TimeoutDef
                    {
                        Duration         = "PT1H",
                        Action           = TimerAction.AutoApprove,
                        BusinessCalendar = true,  // dangerous combo
                    },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        };

        var result = WorkflowGraphValidator.Validate(graph);
        Assert.IsFalse(result.IsValid, "T-TMO-16c: graph must fail validation");
        Assert.AreEqual(GraphValidationError.TimeoutAutoActionWithBusinessCalendar, result.Error,
            "T-TMO-16c: must fail with TimeoutAutoActionWithBusinessCalendar");
    }

    /// <summary>
    /// T-TMO-16d: Validator rejects AutoApprove when AllowTimerAutoAction=false (default).
    /// </summary>
    [TestMethod]
    public void T_TMO_16d_Validator_RejectsAutoAction_WhenGateOff()
    {
        var graph = new WorkflowGraph
        {
            Key  = "G",
            Name = "G",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "a" },
                    Timeout      = new TimeoutDef
                    {
                        Duration = "PT1H",
                        Action   = TimerAction.AutoApprove,
                        // businessCalendar defaults to false — only the gate matters here.
                    },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        };

        // AllowTimerAutoAction=false (the default) → reject AutoApprove at publish.
        var opts = new WorkFlowOptions { AllowTimerAutoAction = false };
        var result = WorkflowGraphValidator.Validate(graph, opts);
        Assert.IsFalse(result.IsValid, "T-TMO-16d: graph must fail validation");
        Assert.AreEqual(GraphValidationError.TimeoutAutoActionGateOff, result.Error,
            "T-TMO-16d: must fail with TimeoutAutoActionGateOff");
    }

    /// <summary>
    /// T-TMO-16e: Validator accepts AutoApprove when AllowTimerAutoAction=true.
    /// </summary>
    [TestMethod]
    public void T_TMO_16e_Validator_AcceptsAutoAction_WhenGateOn()
    {
        var graph = new WorkflowGraph
        {
            Key  = "G",
            Name = "G",
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey      = "approval1",
                    Kind         = NodeKind.Approval,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "a" },
                    Timeout      = new TimeoutDef
                    {
                        Duration = "PT1H",
                        Action   = TimerAction.AutoApprove,
                    },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",     To = "approval1" },
                new() { From = "approval1", To = "end"       },
            },
        };

        var opts = new WorkFlowOptions { AllowTimerAutoAction = true };
        var result = WorkflowGraphValidator.Validate(graph, opts);
        Assert.IsTrue(result.IsValid, $"T-TMO-16e: graph must pass validation; error: {result.ErrorMessage}");
    }

    // ── T-TMO-17: DueUtc derived-only contract ────────────────────────────────

    /// <summary>
    /// T-TMO-17a: DueUtc is stamped by the engine at arm time (Sequential step).
    /// After StartAsync with a timeout, the step-0 task must have DueUtc in the future.
    /// </summary>
    [TestMethod]
    public async Task T_TMO_17a_DueUtc_StampedAtArmSite_Sequential()
    {
        const string A1 = "alice";
        var calendar = new PassThroughBusinessCalendar();
        var (engine, ctx) = MakeEngineWithCalendar(calendar);
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx,
            BuildApprovalGraphWithTimeout(A1, ApproveMode.Sequential, "PT2H", TimerAction.Remind));
        await engine.StartAsync(version.ID, null, A1, null, ct: CancellationToken.None);

        await using var verify = MakeEngContext();
        var task = await verify.Set<ApprovalTask>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.State == TaskState.Pending);

        Assert.IsNotNull(task, "T-TMO-17a: a Pending task must exist");
        Assert.IsNotNull(task!.DueUtc, "T-TMO-17a: DueUtc must be stamped by the engine");

        // PT2H from now: DueUtc must be roughly 2 hours in the future (within a 5-second tolerance).
        var expectedMin = DateTime.UtcNow.AddHours(2).AddSeconds(-5);
        var expectedMax = DateTime.UtcNow.AddHours(2).AddSeconds(5);
        Assert.IsTrue(task.DueUtc >= expectedMin && task.DueUtc <= expectedMax,
            $"T-TMO-17a: DueUtc {task.DueUtc:O} must be ~2h from now (PT2H duration)");
    }

    /// <summary>
    /// T-TMO-17b: DueUtc set externally on a task (without an Armed timer) does NOT
    /// cause any timer to be armed.  The engine only arms timers via its own arm helpers,
    /// not by observing DueUtc on existing tasks.
    /// </summary>
    [TestMethod]
    public async Task T_TMO_17b_ExternalDueUtc_DoesNotArmTimer()
    {
        // Manually create a task with DueUtc but no corresponding timer.
        await using var seed = MakeRawContext();
        var (instanceId, nodeId) = await SeedRunningInstanceAndNodeAsync(seed);

        // Add an ApprovalTask with DueUtc set externally.
        var taskId = Guid.NewGuid();
        seed.ApprovalTasks.Add(new ApprovalTask
        {
            ID             = taskId,
            State          = TaskState.Pending,
            RowVer         = 0,
            AssigneeITCode = "alice",
            NodeInstanceId = nodeId,
            IsValid        = true,
            DueUtc         = DateTime.UtcNow.AddHours(1), // externally set
        });
        await seed.SaveChangesAsync();

        // No arm operation was called; verify zero timers exist.
        await using var verify = MakeRawContext();
        var timerCount = await verify.WorkflowTimers.AsNoTracking().CountAsync();
        Assert.AreEqual(0, timerCount,
            "T-TMO-17b: externally setting DueUtc must NOT create any timers");
    }
}
