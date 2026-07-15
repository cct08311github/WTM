#nullable enable
// WF-20.1: WorkflowTimerExecutor skeleton — GATE-0 orphan retirement + fire txn + Remind action.
// WF-20.3: Remind chain next-link INSERT (cap composition) + IWorkflowNotifier DIM wiring.
// WF-20.4: AutoApprove / AutoReject — double-gate + in-txn bounded DRAIN + NotifyTimeoutAutoActionedAsync.
// WF-20.5: Escalate + AtAction expired-delegation sweep (phase-3 reaper).
//
// Sub-scope WF-20.1 delivers:
//   • Candidate SELECT: TimerBatchSize, (Status,FireAtUtc) index shape, IgnoreQueryFilters + justification.
//   • GATE-0: orphan retirement for non-Running/generation-mismatch/non-Activated timers.
//   • Per-timer txn: FireTimerAsync + Remind action (next-link INSERT + TimeoutRemind event).
//
// Sub-scope WF-20.3 delivers:
//   • Remind chain next-link INSERT with min(MaxReminders ?? MaxRemindersDefault, MaxRemindersHardCap) cap.
//   • RemindEveryHours==null → one-shot (no next-link).
//   • Chain links inherit Generation.
//   • Post-commit: NotifyTimeoutRemindAsync called with re-read current Pending assignees,
//     re-gated on node Activated + generation match; null-check + try/catch + LogError.
//   • NotifyTimeoutEscalatedAsync / NotifyTimeoutAutoActionedAsync call-site markers exist
//     in the executor dispatch (WF-20.4/20.5 stub comments).
//
// Sub-scope WF-20.4 delivers:
//   • Double-gate (AllowTimerAutoAction + concrete WorkflowEngine type-test).
//   • In-txn bounded DRAIN (pendingTaskCount+8) per design §5.
//   • Post-commit: NotifyTimeoutAutoActionedAsync for each successfully acted task.
//   • AutoReject routes through handlers — Any-unanimity and All-RejectGate respected.
//
// Escalate is gated to WF-20.5.
// Arm sites (§2) are gated to WF-20.2.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Scoped service — one per poller tick scope.  Drives the per-timer fire pipeline:
/// candidate SELECT → GATE-0 orphan check → per-timer transactional fire + action.
///
/// <para>Only <see cref="TimerAction.Remind"/> is fully implemented here (WF-20.1/20.3).
/// Other actions are stubbed with <see cref="TimerFireOutcome.FailClosed"/> and a
/// clearly-marked seam comment — they are filled in WF-20.4 (auto-actions) and
/// WF-20.5 (escalation).</para>
///
/// <para><see cref="IWorkflowNotifier"/> DIMs are wired in WF-20.3.  The notifier is
/// optional — null or a non-registered notifier is a silent no-op.  All notification
/// calls are post-commit and surrounded by null-check + try/catch + LogError.</para>
/// </summary>
internal sealed class WorkflowTimerExecutor
{
    private readonly IDataContext? _dc;
    // Test-path: direct DbContext (WfTestContext is DbContext but not IDataContext).
    private readonly DbContext? _dbDirect;
    private readonly WorkFlowOptions _options;
    private readonly ILogger<WorkflowTimerExecutor> _logger;
    private readonly IWorkflowNotifier? _notifier;
    // WF-20.4: typed to IWorkflowEngine for type-test; system auto-actions require the concrete
    // WorkflowEngine.SystemClaimTaskAsync / SystemContinueTaskAsync path.  Custom IWorkflowEngine registrations → downgrade.
    private readonly IWorkflowEngine? _engine;
    // #666: deserialized-graph cache. Defaults to a private (non-shared) instance when not
    // supplied — production DI always supplies the process-wide singleton (see
    // ServiceCollectionExtensions.AddWtmWorkFlow).
    private readonly IWorkflowGraphProvider _graphProvider;

    public WorkflowTimerExecutor(
        IDataContext dc,
        IOptions<WorkFlowOptions> options,
        ILogger<WorkflowTimerExecutor> logger,
        IWorkflowEngine? engine = null,
        IWorkflowNotifier? notifier = null,
        IWorkflowGraphProvider? graphProvider = null)
    {
        _dc = dc;
        _dbDirect = null;
        _options = options.Value;
        _logger = logger;
        _engine = engine;
        _notifier = notifier;
        _graphProvider = graphProvider ?? new WorkflowGraphProvider();
    }

    /// <summary>Test / direct-DbContext constructor (mirrors WorkflowEngine's test path).
    /// <para><c>_dc</c> is null in this path — <c>DBType</c> guards are skipped; tests always
    /// supply a real SQLite DbContext, never EF InMemory.</para></summary>
    internal WorkflowTimerExecutor(
        DbContext db,
        IOptions<WorkFlowOptions> options,
        ILogger<WorkflowTimerExecutor> logger,
        IWorkflowEngine? engine = null,
        IWorkflowNotifier? notifier = null,
        IWorkflowGraphProvider? graphProvider = null)
    {
        _dc = null;
        _dbDirect = db ?? throw new ArgumentNullException(nameof(db));
        _options = options.Value;
        _logger = logger;
        _engine = engine;
        _notifier = notifier;
        _graphProvider = graphProvider ?? new WorkflowGraphProvider();
    }

    // Returns the underlying DbContext from either the prod or test constructor path.
    private DbContext GetDb() =>
        _dbDirect
        ?? (_dc as DbContext)
        ?? throw new InvalidOperationException(
            "WorkflowTimerExecutor requires IDataContext to be a DbContext subclass.");

    /// <summary>
    /// Run one complete tick: fetch due timers, gate-check, fire each in its own transaction.
    /// </summary>
    /// <param name="now">UTC instant bound ONCE per tick by the hosted service (never SQL CURRENT_TIMESTAMP).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunTickAsync(DateTime now, CancellationToken ct)
    {
        // Phase-1: fire due timers.
        await FireDueTimersAsync(now, ct);

        // Phase-2: Returning-lease reclaim (crash recovery for 回退 operations).
        await ReclaimExpiredLeasesAsync(now, ct);

        // Phase-3: AtAction expired-delegation sweep (WF-20.5).
        // Gated: DelegationWindowMode==AtAction AND DelegationExpiredSweep==RevertToPrincipal.
        await SweepExpiredAtActionDelegationsAsync(now, ct);

        // Phase-4: Strand-reaper — re-drive Sequential nodes where the SequencePointer advance was
        // lost (crash between system auto-approve claim commit and the post-commit continuation).
        // Re-drive entry: WorkflowEngine.SystemContinueTaskAsync (RowVer CAS-guarded, idempotent).
        // Default-ON (batch size 50); disabled by setting WorkFlowOptions.StrandReaperBatchSize = 0.
        await ReDriveStrandedSequentialNodesAsync(now, ct);
    }

    // ── Phase-1: fire due timers ───────────────────────────────────────────────

    private async Task FireDueTimersAsync(DateTime now, CancellationToken ct)
    {
        var db = GetDb();

        // Candidate SELECT: armed timers due by @now, ordered by fire time (FIFO fairness).
        // IgnoreQueryFilters: cross-tenant system sweep — every downstream write is a
        // PK + RowVer (+State/Generation) single-row CAS, so no cross-tenant write is possible.
        // TenantCode is copied from the source instance row on every event log append.
        // FIX-A3: RemindEveryHours and MaxReminders are NOT on WorkflowTimer (schema-delta-zero).
        // They are re-read from the immutable version-pinned graph at fire time.
        var candidates = await db.Set<WorkflowTimer>()
            .IgnoreQueryFilters() // justified: cross-tenant system reaper sweep; all writes are PK-CAS
            .AsNoTracking()
            .Where(t => t.Status == TimerStatus.Armed && t.FireAtUtc <= now)
            .OrderBy(t => t.FireAtUtc)
            .Take(_options.TimerBatchSize)
            .Select(t => new
            {
                t.ID,
                t.RowVer,
                t.Action,
                t.NodeInstanceId,
                t.ApprovalTaskId,
                t.Generation,
                t.RemindCount,
                t.IdempotencyKey,
                t.TenantCode,
            })
            .ToListAsync(ct);

        foreach (var timer in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // FIX-A1: Set the scoped IDataContext's TenantCode to the current timer's TenantCode
            // before processing so that EF's HasQueryFilter (TenantCode == this.TenantCode) on
            // every WorkFlow ITenant entity resolves correctly for all downstream writes.
            // The candidate SELECT uses IgnoreQueryFilters() (cross-tenant sweep) so the timers
            // are discovered correctly regardless of tenant; but ExecuteUpdateAsync goes through
            // the same HasQueryFilter and therefore needs the tenant set on the context instance.
            // All downstream writes are PK+RowVer CAS so no cross-tenant write is ever possible.
            // The engine resolved from the same DI scope shares this DbContext instance — setting
            // TenantCode here scopes all engine writes within the same timer's processing.
            //
            // Test path: _dc is null (WfTestContext has no ITenant filters) — no action needed.
            string? previousTenantCode = null;
            if (_dc is not null)
            {
                previousTenantCode = _dc.TenantCode;
                _dc.SetTenantCode(timer.TenantCode);
            }

            try
            {
                await ProcessTimerAsync(db, timer.ID, timer.RowVer, timer.Action,
                    timer.NodeInstanceId, timer.ApprovalTaskId, timer.Generation,
                    timer.RemindCount, timer.IdempotencyKey, timer.TenantCode,
                    now, ct);
            }
            catch (Exception ex)
            {
                // Per-timer try/catch: a poisoned timer never blocks the rest of the batch.
                // The timer stays Armed and will be retried on the next tick.
                _logger.LogError(ex,
                    "Timer {TimerId} failed during fire pipeline — timer stays Armed for retry",
                    timer.ID);
            }
            finally
            {
                // Restore the context TenantCode so the next timer (or any subsequent scope use)
                // does not inherit this timer's tenant. This is a belt-and-suspenders reset;
                // the real isolation is the per-timer ProcessTimerAsync call path.
                if (_dc is not null)
                    _dc.SetTenantCode(previousTenantCode);
            }
        }
    }

    private async Task ProcessTimerAsync(
        DbContext db,
        Guid timerId,
        uint timerRowVer,
        TimerAction action,
        Guid nodeInstanceId,
        Guid? approvalTaskId,
        uint timerGeneration,
        int remindCount,
        string idempotencyKey,
        string? tenantCode,
        DateTime now,
        CancellationToken ct)
    {
        // ── GATE-0: pre-checks without a transaction ───────────────────────────
        // If any of these fail, retire the orphan inside a minimal fire txn (timer→Fired,
        // zero action side-effects, no event spam per Race C design).

        // Read instance + node state for orphan detection.
        // NodeKey is also projected for FIX-A3: graph re-read at fire time.
        var nodeSnap = await db.Set<NodeInstance>()
            .IgnoreQueryFilters() // cross-tenant system sweep (same justification as candidate SELECT)
            .AsNoTracking()
            .Where(n => n.ID == nodeInstanceId)
            .Select(n => new { n.InstanceId, n.State, n.Generation, n.RowVer, n.NodeKey })
            .FirstOrDefaultAsync(ct);

        if (nodeSnap == null)
        {
            // Orphan: node no longer exists (shouldn't happen due to Restrict FK, but be safe).
            _logger.LogWarning("Timer {TimerId} orphan: NodeInstance {NodeId} not found — retiring",
                timerId, nodeInstanceId);
            await RetireOrphanAsync(db, timerId, timerRowVer, ct);
            return;
        }

        var instanceSnap = await db.Set<ProcessInstance>()
            .IgnoreQueryFilters() // cross-tenant system sweep
            .AsNoTracking()
            .Where(i => i.ID == nodeSnap.InstanceId)
            // DefinitionVersionId projected for FIX-A3: graph re-read at fire time.
            .Select(i => new { i.ID, i.State, i.Generation, i.RowVer, i.TenantCode, i.NextSeq, i.DefinitionVersionId })
            .FirstOrDefaultAsync(ct);

        if (instanceSnap == null)
        {
            _logger.LogWarning("Timer {TimerId} orphan: ProcessInstance for node {NodeId} not found — retiring",
                timerId, nodeInstanceId);
            await RetireOrphanAsync(db, timerId, timerRowVer, ct);
            return;
        }

        // FIX-A2 GATE-0: if the instance is in the Returning sub-state (a 回退-to-node operation is
        // in progress), SKIP this timer for this tick — do NOT retire it.  The 回退 operation will
        // either complete (advancing Generation → the timer becomes a generation-mismatch orphan on
        // the next tick and is then retired cleanly) or its lease expires and is reclaimed by Phase-2.
        // Skipping shrinks the fire-vs-return contention window and preserves SLA for any nodes that
        // survive the return operation.
        if (instanceSnap.State == InstanceState.Returning)
        {
            _logger.LogDebug(
                "Timer {TimerId} deferred: ProcessInstance {InstanceId} is in Returning sub-state " +
                "(回退 in progress) — timer stays Armed until return completes or lease expires",
                timerId, instanceSnap.ID);
            return;
        }

        // GATE-0 orphan conditions: not Running, generation mismatch, or node not Activated.
        bool isOrphan =
            instanceSnap.State != InstanceState.Running
            || timerGeneration != instanceSnap.Generation
            || nodeSnap.State != NodeState.Activated;

        if (isOrphan)
        {
            // Retire orphan: flip timer to Fired, zero downstream side-effects (Race C).
            _logger.LogDebug(
                "Timer {TimerId} orphan (InstanceState={InstanceState}, " +
                "TimerGen={TimerGen}, InstanceGen={InstanceGen}, NodeState={NodeState}) — retiring",
                timerId, instanceSnap.State, timerGeneration, instanceSnap.Generation, nodeSnap.State);
            await RetireOrphanAsync(db, timerId, timerRowVer, ct);
            return;
        }

        // ── FIX-A3: Load TimeoutDef from the version-pinned immutable graph ──────
        // RemindEveryHours, MaxReminders, and EscalateTo are NOT stored on WorkflowTimer.
        // They are re-read here from ProcessDefinitionVersion.GraphJson (immutable once published)
        // via the same LoadNodeDefAsync pattern WorkflowEngine uses (spec §7 ZERO schema/migration delta).
        // On any load failure (version deleted, node renamed) the action degrades gracefully:
        // • Remind → one-shot (no next-link, no chain)  [fail-closed: better to send one remind]
        // • Escalate → AdminFallbackITCode only          [fail-closed: existing FailClosed path]
        // This is acceptable because the graph is immutable and deletion is extremely rare.
        TimeoutDef? timeoutDef = null;
        if (action == TimerAction.Remind || action == TimerAction.Escalate)
        {
            timeoutDef = await LoadTimerNodeDefAsync(db, instanceSnap.DefinitionVersionId, nodeSnap.NodeKey, ct);
            // Null is acceptable — handlers degrade gracefully (one-shot / AdminFallbackITCode).
        }

        // ── Per-timer transaction (lock order: WorkflowTimer first) ──────────
        //
        // FIX-A2 deadlock-victim semantics:
        // Any remaining cross-txn cycle (e.g. vs the 回退 txn's instance-first lock order)
        // is resolved by the DB choosing this txn as the deadlock victim → classified retry
        // (bounded, in-tick) → if exhausted, timer stays Armed → next-tick retry (self-healing,
        // no data loss).  DO NOT add lock hints or serializable isolation to "fix" this — the
        // retry is correct.
        //
        // FIX-C: capture the concrete engine once outside the txn for post-commit continuation.
        // HandleAutoActionAsync uses SystemClaimTaskAsync (IN-TXN) inside the fire transaction;
        // after txn.CommitAsync the executor drives SystemContinueTaskAsync (POST-COMMIT) here.
        var concreteEngineForContinuation = _engine as WorkflowEngine;

        // #667 fix: this used to be a BARE, unretried BeginTransactionAsync — this file had ZERO
        // CreateExecutionStrategy call sites before #667 completion. Under a host-configured
        // retrying execution strategy (EnableRetryOnFailure), EVERY timer fire threw
        // InvalidOperationException("...does not support user-initiated transactions..."),
        // silently breaking Remind/Escalate/AutoApprove/AutoReject SLA processing for any host
        // that had opted into that commonly-recommended cloud SQL Server/Postgres setting.
        // Routed through the shared WorkflowTransactionExecutor helper (the same helper
        // WorkflowEngine.ExecuteInTransactionAsync forwards to — no duplicated retry/legality
        // logic). retryOnDeadlock: true is appropriate: each timer fire is CAS-guarded (Step-1
        // Fire CAS) and idempotent under ChangeTracker.Clear()-then-replay, and — unlike the
        // Return-path txA/txB — there is no per-timer nesting: FireDueTimersAsync's foreach loop
        // never holds an ambient transaction across iterations, so this call is always the
        // outermost (owning) transaction boundary; no ambient-tx nesting risk.
        var (txnResult, bodyExtra) = await WorkflowTransactionExecutor.ExecuteInTransactionAsync(
            db, _options, _logger,
            async innerCt =>
        {
            await using var txn = await db.Database.BeginTransactionAsync(innerCt);
            try
            {
                // Step-1: Fire CAS — the multi-host mutex (lock-order first: WorkflowTimer).
                var fireRows = await GuardedTransition.FireTimerAsync(db, timerId, timerRowVer, innerCt);
                if (fireRows == 0)
                {
                    // Another host won the fire CAS — rollback, no side-effects.
                    await txn.RollbackAsync(innerCt);
                    _logger.LogDebug("Timer {TimerId} LostRace — another host won the fire CAS", timerId);
                    return (WorkflowActionResult.AlreadyHandled,
                        new TimerFireBodyExtra(TimerFireOutcome.LostRace, null, null, null));
                }

                // Step-2: Action-decision branch.
                // AutoApprove/AutoReject use HandleAutoActionAsync returning (outcome, actedTasks, continuationContexts) tuple;
                // Escalate uses HandleEscalateAsync returning (outcome, escalateInfo) tuple;
                // other actions use simple TimerFireOutcome returns.
                List<(Guid taskId, string assigneeITCode)>? autoActedTasks = null;
                // FIX-C: collect per-task continuation contexts to run POST-COMMIT (split claim from continuation).
                List<WorkflowEngine.SystemClaimContext>? autoContinuationContexts = null;
                EscalateInfo? escalateInfo = null;
                TimerFireOutcome outcome;

                if (action == TimerAction.AutoApprove || action == TimerAction.AutoReject)
                {
                    // WF-20.4: double-gate + in-txn bounded DRAIN.
                    // FIX-B3: pass approvalTaskId so task-scoped timers drain only their own task.
                    // FIX-C: HandleAutoActionAsync now only runs the IN-TXN claim CAS + event rows.
                    //        Continuation (AdvanceAsync, timer re-arm, WF-15 notifier) is post-commit.
                    var (actionOutcome, actedTasks, continuationContexts) = await HandleAutoActionAsync(
                        db, action, nodeInstanceId, approvalTaskId, timerGeneration,
                        instanceSnap.ID, instanceSnap.RowVer, instanceSnap.TenantCode,
                        now, innerCt);
                    outcome = actionOutcome;
                    autoActedTasks = actedTasks;
                    autoContinuationContexts = continuationContexts;
                }
                else if (action == TimerAction.Escalate)
                {
                    // WF-20.5: Escalate — task-scoped vs node-scoped dispatch.
                    // FIX-A3: timeoutDef re-read from graph above; passed in lieu of removed entity columns.
                    var (escalateOutcome, info) = await HandleEscalateAsync(
                        db, timerId, nodeInstanceId, approvalTaskId, timerGeneration,
                        remindCount, idempotencyKey, tenantCode,
                        timeoutDef,
                        instanceSnap.ID, instanceSnap.RowVer, instanceSnap.TenantCode,
                        now, innerCt);
                    outcome = escalateOutcome;
                    escalateInfo = info;
                }
                else
                {
                    outcome = action switch
                    {
                        // FIX-A3: timeoutDef re-read from graph above; passed in lieu of removed entity columns.
                        TimerAction.Remind => await HandleRemindAsync(
                            db, timerId, nodeInstanceId, approvalTaskId, timerGeneration,
                            remindCount, idempotencyKey, tenantCode,
                            timeoutDef,
                            instanceSnap.ID, instanceSnap.RowVer, instanceSnap.TenantCode,
                            now, innerCt),

                        _ => HandleUnknownAction(timerId, action),
                    };
                }

                await txn.CommitAsync(innerCt);

                return (WorkflowActionResult.Advanced,
                    new TimerFireBodyExtra(outcome, autoActedTasks, autoContinuationContexts, escalateInfo));
            }
            catch
            {
                await txn.RollbackAsync(CancellationToken.None);
                throw; // classified by ExecuteInTransactionAsync; a non-deadlock exception
                       // re-throws out to the per-timer catch in FireDueTimersAsync, exactly as
                       // before #667.
            }
        },
            defaultExtra: new TimerFireBodyExtra(TimerFireOutcome.LostRace, null, null, null),
            ct,
            retryOnDeadlock: true);

        if (txnResult.Code == WorkflowActionCode.DeadlockRetryExhausted)
        {
            // Deadlock-classified failure on every retry attempt — same ultimate outcome as the
            // pre-#667 raw-throw path (timer stays Armed for next-tick retry), but now closed
            // instead of propagating a raw provider exception up through FireDueTimersAsync.
            _logger.LogError(
                "Timer {TimerId} fire transaction exhausted deadlock retries — timer stays Armed for next-tick retry",
                timerId);
            return;
        }

        if (bodyExtra.Outcome == TimerFireOutcome.LostRace)
        {
            // Another host won the fire CAS — no side-effects, nothing further to do this tick.
            return;
        }

        var outcome2 = bodyExtra.Outcome;
        var autoActedTasks2 = bodyExtra.AutoActedTasks;
        var autoContinuationContexts2 = bodyExtra.AutoContinuationContexts;
        var escalateInfo2 = bodyExtra.EscalateInfo;

        // ── Post-commit: FIX-C — run auto-action continuations BEFORE notifications ──
        // The fire txn committed the task-claim CASes + TimeoutFire event rows.
        // Now drive the node-completion continuation (increment → TryComplete → AdvanceAsync
        // recursion + timer re-arm + WF-15 in-engine notifier calls) outside any transaction
        // (moved fully outside the strategy-wrapped delegate by #667 completion — was previously
        // inside the try, after CommitAsync, which risked the outer catch calling RollbackAsync
        // on an already-committed transaction if a post-commit call ever threw uncaught).
        //
        // Per-task try/catch + LogError: a continuation failure MUST NOT undo the committed
        // claims (tasks are already AutoApproved/AutoRejected — the SLA action is recorded).
        // Phase-4 (ReDriveStrandedSequentialNodesAsync) is the recovery backstop for this window.
        // It detects: Running instance + Activated Sequential node + terminal task at SequencePointer
        // + SequencePointer not yet advanced → re-drives via AdvanceAsync.
        // This is the documented crash-profile limitation (design §6 R3 Known limitation).
        if (autoContinuationContexts2 is { Count: > 0 } && concreteEngineForContinuation is not null)
        {
            foreach (var ctx in autoContinuationContexts2)
            {
                try
                {
                    await concreteEngineForContinuation.SystemContinueTaskAsync(ctx, ct);
                }
                catch (Exception contEx)
                {
                    // Continuation failure: task is already committed (AutoApproved/AutoRejected).
                    // Log and continue — node may stay at Activated with zero Pending tasks;
                    // Phase-4 (ReDriveStrandedSequentialNodesAsync) re-drives it next tick
                    // (documented crash-profile limitation §6 R3).
                    _logger.LogError(contEx,
                        "FIX-C: SystemContinueTaskAsync failed for task {TaskId} (node {NodeId}) — " +
                        "claim is committed; continuation failure logged and suppressed. " +
                        "Node may require manual re-drive if left with zero Pending tasks.",
                        ctx.TaskId, nodeInstanceId);
                }
            }
        }

        // ── Post-commit: notify (re-gated on fresh read) ──────────────────
        // WF-20.3: call NotifyTimeoutRemindAsync for Remind outcome.
        if (outcome2 == TimerFireOutcome.Fired && action == TimerAction.Remind && _notifier is not null)
        {
            await NotifyRemindAsync(db, _notifier, nodeInstanceId, timerGeneration,
                instanceSnap.ID, remindCount, ct);
        }

        // WF-20.4: call NotifyTimeoutAutoActionedAsync for each successfully acted task.
        if (outcome2 == TimerFireOutcome.Fired
            && (action == TimerAction.AutoApprove || action == TimerAction.AutoReject)
            && autoActedTasks2 is { Count: > 0 }
            && _notifier is not null)
        {
            await NotifyAutoActionedAsync(db, _notifier, nodeInstanceId, timerGeneration,
                instanceSnap.ID, action, autoActedTasks2, ct);
        }

        // WF-20.5: call NotifyTimeoutEscalatedAsync post-commit for Escalate action.
        if (action == TimerAction.Escalate && _notifier is not null && escalateInfo2 is not null)
        {
            await NotifyEscalateAsync(db, _notifier, nodeInstanceId, timerGeneration,
                instanceSnap.ID, escalateInfo2, ct);
        }

        // FIX-B4: DowngradedToRemind paths (gate-off auto-action, custom-engine, escalate-no-target,
        // escalate-collision) commit a FailClosed event but historically sent NO notification and armed
        // NO follow-up chain link.  Execute real Remind semantics post-commit:
        //   • Notify current Pending assignees (NotifyTimeoutRemindAsync re-gated on fresh read).
        //   • Arm the next Remind chain link when the graph def has RemindEveryHours.
        // This ensures the human assignee is alerted even when the auto/escalation gate is off.
        if (outcome2 == TimerFireOutcome.DowngradedToRemind && _notifier is not null)
        {
            await NotifyRemindAsync(db, _notifier, nodeInstanceId, timerGeneration,
                instanceSnap.ID, remindCount, ct);
        }
        // EscalateCollisionNotifyOnly is handled by NotifyEscalateAsync above (uses Remind fallback).
        // DowngradedToRemind from escalate (no-target / gate-off) may also need chain arm.
        // The chain-link insert is inside the transaction (HandleRemindAsync / HandleEscalateAsync).
        // For DowngradedToRemind paths the chain-link was NOT inserted in-txn because the gate
        // aborted early.  Insert it post-commit here using the same BuildNextLinkKey logic.
        if (outcome2 == TimerFireOutcome.DowngradedToRemind && timeoutDef?.RemindEveryHours.HasValue == true)
        {
            await TryArmDowngradedRemindChainLinkAsync(
                db, nodeInstanceId, approvalTaskId, timerGeneration,
                remindCount, idempotencyKey, tenantCode, timeoutDef, now, ct);
        }

        _logger.LogDebug(
            "Timer {TimerId} outcome={Outcome} (action={Action})",
            timerId, outcome2, action);
    }

    /// <summary>
    /// #667 completion: carries the per-timer-fire body's outcome + auto-action/escalate
    /// context out of the strategy-wrapped <see cref="WorkflowTransactionExecutor.ExecuteInTransactionAsync"/>
    /// delegate to the post-commit continuation/notification code (which must run OUTSIDE the
    /// transactional unit). See <see cref="ProcessTimerAsync"/>.
    /// </summary>
    private sealed record TimerFireBodyExtra(
        TimerFireOutcome Outcome,
        List<(Guid taskId, string assigneeITCode)>? AutoActedTasks,
        List<WorkflowEngine.SystemClaimContext>? AutoContinuationContexts,
        EscalateInfo? EscalateInfo);

    // ── Remind action (WF-20.1 fully implemented) ────────────────────────────

    private async Task<TimerFireOutcome> HandleRemindAsync(
        DbContext db,
        Guid timerId,
        Guid nodeInstanceId,
        Guid? approvalTaskId,
        uint timerGeneration,
        int remindCount,
        string idempotencyKey,
        string? timerTenantCode,
        TimeoutDef? timeoutDef,
        Guid instanceId,
        uint instanceRowVer,
        string? instanceTenantCode,
        DateTime now,
        CancellationToken ct)
    {
        // ── Next-link chain arm (WF-20.3) ───────────────────────────────────────
        // FIX-A3: RemindEveryHours and MaxReminders are re-read from the version-pinned graph
        // (timeoutDef) instead of being carried on the timer row.  This preserves the
        // design §7 ZERO schema/migration delta promise.
        // Cap: min(MaxReminders ?? MaxRemindersDefault, MaxRemindersHardCap)
        // RemindEveryHours == null (or timeoutDef null) → one-shot; no next-link inserted.
        // Chain links inherit Generation (Race-C gate on every link).
        int? remindEveryHours = timeoutDef?.RemindEveryHours;
        int? maxReminders     = timeoutDef?.MaxReminders;

        if (remindEveryHours.HasValue)
        {
            int effectiveCap = Math.Min(
                maxReminders ?? _options.MaxRemindersDefault,
                _options.MaxRemindersHardCap);

            if (remindCount + 1 < effectiveCap)
            {
                // Derive next-link idempotency key by incrementing the counter suffix.
                // Convention: keys end with ":{RemindCount}" — replace it with ":{RemindCount+1}".
                // If the key doesn't follow this convention, append a suffix.
                string nextKey = BuildNextLinkKey(idempotencyKey, remindCount);

                // FIX-A3: next-link row does NOT carry RemindEveryHours/MaxReminders.
                // Those are re-read from the immutable graph on every fire (schema-delta-zero).
                db.Set<WorkflowTimer>().Add(new WorkflowTimer
                {
                    ID              = Guid.NewGuid(),
                    TenantCode      = timerTenantCode,
                    NodeInstanceId  = nodeInstanceId,
                    ApprovalTaskId  = approvalTaskId,
                    Status          = TimerStatus.Armed,
                    RowVer          = 0,
                    IdempotencyKey  = nextKey,
                    FireAtUtc       = now.AddHours(remindEveryHours.Value),
                    Action          = TimerAction.Remind,
                    Generation      = timerGeneration, // chain inherits Generation (Race C)
                    RemindCount     = remindCount + 1,
                });
                // SaveChanges is called below with the event log row in the same SaveChangesAsync.
            }
        }
        // RemindEveryHours == null (or timeoutDef null) → one-shot, no next-link inserted.

        // ── Append TimeoutRemind event via AllocateSeqAsync ──────────────────────
        var seqResult = await AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
        if (seqResult.seq > 0)
        {
            db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
            {
                ID            = Guid.NewGuid(),
                TenantCode    = instanceTenantCode,
                InstanceId    = instanceId,
                Seq           = seqResult.seq,
                Action        = EventAction.TimeoutRemind,
                NodeKey       = null, // filled in WF-20.2 when we have NodeDef context
                ActorITCode   = null, // system action
                Generation    = (int?)timerGeneration,
                OccurredUtc   = now,
            });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            // Seq allocation failed — save any next-link row independently.
            // The missed event is an audit gap, not a correctness issue.
            _logger.LogWarning(
                "Timer {TimerId} Remind: Seq allocation failed after retries — event not appended",
                timerId);
            // Still save the next-link timer if it was added above.
            await db.SaveChangesAsync(ct);
        }

        return TimerFireOutcome.Fired;
    }

    /// <summary>
    /// Build the idempotency key for the next chain link.
    /// Replaces the trailing ":{n}" counter suffix with ":{n+1}".
    /// Falls back to appending ":{n+1}" if the convention is not recognized.
    /// </summary>
    private static string BuildNextLinkKey(string currentKey, int currentRemindCount)
    {
        string suffix = $":{currentRemindCount}";
        string nextSuffix = $":{currentRemindCount + 1}";

        if (currentKey.EndsWith(suffix, StringComparison.Ordinal))
            return string.Concat(currentKey.AsSpan(0, currentKey.Length - suffix.Length), nextSuffix);

        // Fallback: append next suffix.
        return currentKey + nextSuffix;
    }

    // ── AutoApprove / AutoReject action (WF-20.4) ────────────────────────────

    /// <summary>
    /// Implements the in-txn bounded DRAIN for AutoApprove/AutoReject per design §5.
    /// FIX-C: only the IN-TXN part runs here (inside the fire transaction).
    /// The post-commit continuation is driven by the caller after txn.CommitAsync.
    /// <list type="number">
    ///   <item>Double-gate: (a) <see cref="WorkFlowOptions.AllowTimerAutoAction"/> must be true;
    ///         (b) <see cref="_engine"/> must be the concrete <see cref="WorkflowEngine"/>
    ///         (type-tested).  Either gate off → LOUD Remind downgrade + FailClosed event.</item>
    ///   <item>COUNT Pending tasks in scope; bound DRAIN at pendingTaskCount+8 to prevent infinite loops.</item>
    ///   <item>Per Pending task: call <see cref="WorkflowEngine.SystemClaimTaskAsync"/>
    ///         (IN-TXN: CAS AutoApproved/AutoRejected + TimeoutFire event row).
    ///         null return → human won / superseded → skip silently.
    ///         On success: collect the <see cref="WorkflowEngine.SystemClaimContext"/> for
    ///         the caller to drive <see cref="WorkflowEngine.SystemContinueTaskAsync"/> post-commit.</item>
    ///   <item>Return acted (taskId, assigneeITCode) pairs for post-commit notification,
    ///         plus continuation contexts for post-commit node completion.</item>
    /// </list>
    /// <para>Lock-order (§0 invariant): WorkflowTimer (fire CAS already held) → ApprovalTask
    /// (SystemClaimTaskAsync CAS) → ProcessInstance(Seq counter).  NodeInstance writes
    /// (completion/advance) are POST-COMMIT via SystemContinueTaskAsync.</para>
    /// </summary>
    private async Task<(TimerFireOutcome outcome, List<(Guid taskId, string assigneeITCode)>? actedTasks, List<WorkflowEngine.SystemClaimContext>? continuationContexts)> HandleAutoActionAsync(
        DbContext db,
        TimerAction action,
        Guid nodeInstanceId,
        Guid? approvalTaskId,
        uint timerGeneration,
        Guid instanceId,
        uint instanceRowVer,
        string? instanceTenantCode,
        DateTime now,
        CancellationToken ct)
    {
        // ── Gate (a): AllowTimerAutoAction must be true ───────────────────────
        if (!_options.AllowTimerAutoAction)
        {
            _logger.LogWarning(
                "AutoAction: action={Action} for node {NodeId} — AllowTimerAutoAction=false. " +
                "Downgrading to Remind (task stays Pending). Set AllowTimerAutoAction=true to enable.",
                action, nodeInstanceId);

            // Write a FailClosed event to record the downgrade.
            var seqFc = await AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
            if (seqFc.rows == 1)
            {
                db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
                {
                    ID           = Guid.NewGuid(),
                    TenantCode   = instanceTenantCode,
                    InstanceId   = instanceId,
                    Seq          = seqFc.seq,
                    Action       = EventAction.FailClosed,
                    NodeKey      = null,
                    ActorITCode  = null,
                    Generation   = (int?)timerGeneration,
                    Reason       = $"AllowTimerAutoAction=false — {action} downgraded to Remind",
                    OccurredUtc  = now,
                });
                await db.SaveChangesAsync(ct);
            }
            return (TimerFireOutcome.DowngradedToRemind, null, null);
        }

        // ── Gate (b): concrete WorkflowEngine type-test ───────────────────────
        if (_engine is not WorkflowEngine concreteEngine)
        {
            _logger.LogWarning(
                "AutoAction: action={Action} for node {NodeId} — custom IWorkflowEngine registered " +
                "(type={EngineType}). SystemClaimTaskAsync is not available on custom engines. " +
                "Downgrading to Remind (task stays Pending).",
                action, nodeInstanceId, _engine?.GetType().Name ?? "null");

            var seqFc2 = await AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
            if (seqFc2.rows == 1)
            {
                db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
                {
                    ID           = Guid.NewGuid(),
                    TenantCode   = instanceTenantCode,
                    InstanceId   = instanceId,
                    Seq          = seqFc2.seq,
                    Action       = EventAction.FailClosed,
                    NodeKey      = null,
                    ActorITCode  = null,
                    Generation   = (int?)timerGeneration,
                    Reason       = $"Custom IWorkflowEngine — {action} downgraded to Remind",
                    OccurredUtc  = now,
                });
                await db.SaveChangesAsync(ct);
            }
            return (TimerFireOutcome.DowngradedToRemind, null, null);
        }

        // ── Load node + instance snapshots for SystemClaimTaskAsync ─────────────
        var nodeSnap = await db.Set<NodeInstance>()
            .IgnoreQueryFilters() // cross-tenant system sweep (same justification as candidate SELECT)
            .AsNoTracking()
            .Where(n => n.ID == nodeInstanceId)
            .Select(n => new
            {
                n.ID, n.State, n.Generation, n.RowVer, n.InstanceId,
                n.ApproveMode, n.NodeKey, n.TenantCode, n.SequencePointer,
                n.TotalRequired, n.ApprovePercent, n.RejectPolicy, n.ApproverSetEpoch,
            })
            .FirstOrDefaultAsync(ct);

        if (nodeSnap == null || nodeSnap.State != NodeState.Activated || nodeSnap.Generation != timerGeneration)
        {
            // Node disappeared or generation changed between GATE-0 and now — orphan, no-op.
            _logger.LogDebug(
                "AutoAction: node {NodeId} no longer Activated/gen-matched after GATE-0 — skipping drain",
                nodeInstanceId);
            return (TimerFireOutcome.SupersededNoOp, null, null);
        }

        var instanceSnap = await db.Set<ProcessInstance>()
            .IgnoreQueryFilters() // cross-tenant system sweep
            .AsNoTracking()
            .Where(i => i.ID == instanceId)
            .FirstOrDefaultAsync(ct);

        if (instanceSnap == null)
        {
            _logger.LogDebug(
                "AutoAction: instance {InstanceId} not found after GATE-0 — skipping drain", instanceId);
            return (TimerFireOutcome.SupersededNoOp, null, null);
        }

        // Build NodeInstance + ProcessInstance objects for SystemClaimTaskAsync.
        // We only need the fields the engine's completion logic accesses.
        var nodeInstForEngine = new NodeInstance
        {
            ID              = nodeSnap.ID,
            State           = nodeSnap.State,
            Generation      = nodeSnap.Generation,
            RowVer          = nodeSnap.RowVer,
            InstanceId      = nodeSnap.InstanceId,
            ApproveMode     = nodeSnap.ApproveMode,
            NodeKey         = nodeSnap.NodeKey,
            TenantCode      = nodeSnap.TenantCode,
            SequencePointer = nodeSnap.SequencePointer,
            TotalRequired   = nodeSnap.TotalRequired,
            ApprovePercent  = nodeSnap.ApprovePercent,
            RejectPolicy    = nodeSnap.RejectPolicy,
            ApproverSetEpoch = nodeSnap.ApproverSetEpoch,
        };

        // ── Count pending tasks to bound the DRAIN ────────────────────────────
        // FIX-B3: when approvalTaskId is non-null (task-scoped timer), drain claims ONLY that
        // specific task.  A stale Sequential step-k timer must never auto-approve step k+1.
        // Node-scoped timers (approvalTaskId==null) keep the original node-wide drain.
        var pendingTasks = await db.Set<ApprovalTask>()
            .IgnoreQueryFilters() // cross-tenant system sweep; writes are PK+RowVer CAS
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInstanceId
                         && t.State == TaskState.Pending
                         && t.Generation == timerGeneration
                         && (approvalTaskId == null || t.ID == approvalTaskId.Value))
            .Select(t => new { t.ID, t.RowVer, t.AssigneeITCode })
            .ToListAsync(ct);

        // Drain bound: pendingTaskCount+8 (design §5 guard against phantom inserts / gaps).
        int drainBound = pendingTasks.Count + 8;
        int drainIterations = 0;

        var nextState = action == TimerAction.AutoApprove
            ? TaskState.AutoApproved
            : TaskState.AutoRejected;

        var acted = new List<(Guid taskId, string assigneeITCode)>();
        // FIX-C: collect SystemClaimContext objects for post-commit continuation.
        var continuationContexts = new List<WorkflowEngine.SystemClaimContext>();

        foreach (var pending in pendingTasks)
        {
            if (drainIterations >= drainBound)
            {
                _logger.LogWarning(
                    "AutoAction: drain bound {Bound} reached for node {NodeId} — stopping early (safety guard)",
                    drainBound, nodeInstanceId);
                break;
            }

            ct.ThrowIfCancellationRequested();
            drainIterations++;

            // Re-read instanceSnap RowVer to keep Seq allocation fresh across drain iterations.
            // FIX-C: only ProcessInstance.Seq (not NodeInstance.State) is read here because
            // NodeInstance writes (completion/advance) are post-commit — the node stays Activated
            // during the entire in-txn drain; its SequencePointer and ApprovedCount are unchanged.
            var freshInstSnap = await db.Set<ProcessInstance>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(i => i.ID == instanceId)
                .Select(i => new
                {
                    i.ID, i.State, i.Generation, i.RowVer,
                    i.TenantCode, i.NextSeq, i.DefinitionVersionId,
                    i.InitiatorITCode, i.IsValid,
                })
                .FirstOrDefaultAsync(ct);

            if (freshInstSnap == null || freshInstSnap.State != InstanceState.Running)
            {
                // Instance reached terminal state (a prior post-commit continuation completed the workflow
                // between ticks, or the instance was cancelled).  No further claims needed.
                _logger.LogDebug(
                    "AutoAction: instance {InstanceId} is no longer Running during drain — stopping",
                    instanceId);
                break;
            }

            // Build ProcessInstance for the engine call.
            var processInstForEngine = new ProcessInstance
            {
                ID                  = freshInstSnap.ID,
                State               = freshInstSnap.State,
                Generation          = freshInstSnap.Generation,
                RowVer              = freshInstSnap.RowVer,
                TenantCode          = freshInstSnap.TenantCode,
                NextSeq             = freshInstSnap.NextSeq,
                DefinitionVersionId = freshInstSnap.DefinitionVersionId,
                InitiatorITCode     = freshInstSnap.InitiatorITCode,
                IsValid             = freshInstSnap.IsValid,
            };

            // FIX-C: call SystemClaimTaskAsync (IN-TXN part only).
            // Lock-order §0: WorkflowTimer (fire CAS, already held) → ApprovalTask (this CAS)
            // → ProcessInstance (Seq counter).  NodeInstance writes are post-commit.
            var claimCtx = await concreteEngine.SystemClaimTaskAsync(
                taskId:           pending.ID,
                taskRowVer:       pending.RowVer,
                nextState:        nextState,
                timerGeneration:  timerGeneration,
                assigneeITCode:   pending.AssigneeITCode ?? string.Empty,
                now:              now,
                nodeInst:         nodeInstForEngine,
                instance:         processInstForEngine,
                ct:               ct);

            if (claimCtx is not null)
            {
                // CAS won — task claimed by the system auto-action.
                // Collect context for post-commit continuation.
                acted.Add((pending.ID, pending.AssigneeITCode ?? string.Empty));
                continuationContexts.Add(claimCtx);
            }
            // claimCtx==null: human or concurrent auto-action won — skip silently (rows==0).
            // FIX-C: no node refresh between drain iterations — NodeInstance state is only
            // mutated post-commit.  The in-txn drain claims task rows only; SequencePointer
            // and ApprovedCount reflect the pre-drain state throughout.  This is correct because
            // the continuation (post-commit) will re-read all necessary state from the DB.
        }

        return (TimerFireOutcome.Fired, acted, continuationContexts);
    }

    private TimerFireOutcome HandleUnknownAction(Guid timerId, TimerAction action)
    {
        _logger.LogError(
            "Timer {TimerId} has unknown TimerAction={Action} — treating as FailClosed",
            timerId, action);
        return TimerFireOutcome.FailClosed;
    }

    // ── Post-commit Remind notification (WF-20.3) ────────────────────────────

    /// <summary>
    /// Post-commit notify for Remind: re-read node + instance + current Pending assignees
    /// re-gated on node Activated + generation match (≤1 bounded-staleness reminder accepted
    /// per design §4).  Null-check + try/catch + LogError; never propagates to caller.
    /// </summary>
    private async Task NotifyRemindAsync(
        DbContext db,
        IWorkflowNotifier notifier,
        Guid nodeInstanceId,
        uint timerGeneration,
        Guid instanceId,
        int remindCount,
        CancellationToken ct)
    {
        try
        {
            // Re-gate: fresh read to confirm the node is still Activated with same generation.
            var freshNode = await db.Set<NodeInstance>()
                .IgnoreQueryFilters() // cross-tenant system sweep; same justification as candidate SELECT
                .AsNoTracking()
                .Where(n => n.ID == nodeInstanceId
                            && n.State == NodeState.Activated
                            && n.Generation == timerGeneration)
                .Select(n => new { n.ID, n.State, n.Generation, n.NodeKey, n.ApproveMode, n.InstanceId })
                .FirstOrDefaultAsync(ct);

            if (freshNode is null)
            {
                // Node no longer Activated or generation changed — skip notification (≤1 bounded staleness).
                _logger.LogDebug(
                    "NotifyRemindAsync: node {NodeId} no longer Activated/gen-matched — skipping notification",
                    nodeInstanceId);
                return;
            }

            var freshInstance = await db.Set<ProcessInstance>()
                .IgnoreQueryFilters() // cross-tenant system sweep
                .AsNoTracking()
                .Where(i => i.ID == instanceId)
                .FirstOrDefaultAsync(ct);

            if (freshInstance is null)
            {
                _logger.LogDebug(
                    "NotifyRemindAsync: instance {InstanceId} not found — skipping notification",
                    instanceId);
                return;
            }

            // Re-read current Pending assignees (honors delegation/escalation reassignments).
            // We do a minimal projection — only ITCode — so the recipients are current at fire time.
            var pendingAssignees = await db.Set<ApprovalTask>()
                .IgnoreQueryFilters() // cross-tenant system sweep
                .AsNoTracking()
                .Where(t => t.NodeInstanceId == nodeInstanceId
                             && t.State == TaskState.Pending
                             && t.Generation == timerGeneration)
                .Select(t => t.AssigneeITCode)
                .ToListAsync(ct);

            // Build minimal NodeInstance and ProcessInstance objects for the notifier card.
            // We only populate the fields the notifier contract accesses (identifiers, no PII).
            var nodeForNotifier = new NodeInstance
            {
                ID          = freshNode.ID,
                NodeKey     = freshNode.NodeKey,
                State       = freshNode.State,
                ApproveMode = freshNode.ApproveMode,
                InstanceId  = freshNode.InstanceId,
                Generation  = freshNode.Generation,
            };

            await notifier.NotifyTimeoutRemindAsync(freshInstance, nodeForNotifier, remindCount, ct)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "NotifyRemindAsync: sent TimeoutRemind for node {NodeId}, remindCount={RemindCount}, " +
                "pending assignees: {Count}",
                nodeInstanceId, remindCount, pendingAssignees.Count);
        }
        catch (Exception ex)
        {
            // Notification failure never rolls back the engine transaction.
            _logger.LogError(ex,
                "NotifyRemindAsync: failed to deliver timeout remind notification for node {NodeId} " +
                "— notification failure ignored (engine state committed)",
                nodeInstanceId);
        }
    }

    // ── Post-commit AutoAction notification (WF-20.4) ────────────────────────

    /// <summary>
    /// Post-commit notify for AutoApprove/AutoReject: re-read node + instance for each acted task,
    /// re-gated on node Activated + generation match (≤1 bounded-staleness accepted per design §5).
    /// Null-check + try/catch + LogError; never propagates to caller.
    /// </summary>
    private async Task NotifyAutoActionedAsync(
        DbContext db,
        IWorkflowNotifier notifier,
        Guid nodeInstanceId,
        uint timerGeneration,
        Guid instanceId,
        TimerAction action,
        List<(Guid taskId, string assigneeITCode)> actedTasks,
        CancellationToken ct)
    {
        try
        {
            // Re-gate: fresh read to confirm the node is still in the expected generation.
            // The node may have advanced to a terminal state after all tasks were drained —
            // we still deliver notifications for the acted tasks (bounded-staleness ≤1 tick).
            var freshNode = await db.Set<NodeInstance>()
                .IgnoreQueryFilters() // cross-tenant system sweep; same justification as candidate SELECT
                .AsNoTracking()
                .Where(n => n.ID == nodeInstanceId && n.Generation == timerGeneration)
                .Select(n => new { n.ID, n.State, n.Generation, n.NodeKey, n.ApproveMode, n.InstanceId })
                .FirstOrDefaultAsync(ct);

            if (freshNode is null)
            {
                // Generation changed — skip (race condition; a new wave superseded this one).
                _logger.LogDebug(
                    "NotifyAutoActionedAsync: node {NodeId} gen-mismatch — skipping notification",
                    nodeInstanceId);
                return;
            }

            var freshInstance = await db.Set<ProcessInstance>()
                .IgnoreQueryFilters() // cross-tenant system sweep
                .AsNoTracking()
                .Where(i => i.ID == instanceId)
                .FirstOrDefaultAsync(ct);

            if (freshInstance is null)
            {
                _logger.LogDebug(
                    "NotifyAutoActionedAsync: instance {InstanceId} not found — skipping notification",
                    instanceId);
                return;
            }

            var nodeForNotifier = new NodeInstance
            {
                ID          = freshNode.ID,
                NodeKey     = freshNode.NodeKey,
                State       = freshNode.State,
                ApproveMode = freshNode.ApproveMode,
                InstanceId  = freshNode.InstanceId,
                Generation  = freshNode.Generation,
            };

            string outcome = action == TimerAction.AutoApprove ? "AutoApproved" : "AutoRejected";

            foreach (var (taskId, assigneeITCode) in actedTasks)
            {
                ct.ThrowIfCancellationRequested();

                // Minimal task shell for the notifier card — identifiers only, no form data / PII.
                var taskForNotifier = new ApprovalTask
                {
                    ID             = taskId,
                    AssigneeITCode = assigneeITCode,
                    NodeInstanceId = nodeInstanceId,
                    State          = action == TimerAction.AutoApprove
                                     ? TaskState.AutoApproved
                                     : TaskState.AutoRejected,
                };

                await notifier.NotifyTimeoutAutoActionedAsync(
                    freshInstance, nodeForNotifier, taskForNotifier, outcome, ct)
                    .ConfigureAwait(false);
            }

            _logger.LogDebug(
                "NotifyAutoActionedAsync: sent {Outcome} notifications for node {NodeId}, " +
                "acted task count={Count}",
                outcome, nodeInstanceId, actedTasks.Count);
        }
        catch (Exception ex)
        {
            // Notification failure never rolls back the engine transaction.
            _logger.LogError(ex,
                "NotifyAutoActionedAsync: failed to deliver timeout auto-action notification for node {NodeId} " +
                "— notification failure ignored (engine state committed)",
                nodeInstanceId);
        }
    }

    // ── Orphan retirement ─────────────────────────────────────────────────────

    private async Task RetireOrphanAsync(DbContext db, Guid timerId, uint timerRowVer, CancellationToken ct)
    {
        // Minimal fire CAS — flips timer to Fired, zero downstream side-effects.
        // rows==0 is fine: another host already retired it.
        await GuardedTransition.FireTimerAsync(db, timerId, timerRowVer, ct);
    }

    // ── Phase-2: Returning-lease reclaim ─────────────────────────────────────

    private async Task ReclaimExpiredLeasesAsync(DateTime now, CancellationToken ct)
    {
        var db = GetDb();

        // SELECT expired Returning instances — expiry evaluated CLIENT-SIDE on the snapshot
        // so NO DateTime appears in any UPDATE WHERE clause (portable: Oracle/DaMeng safe).
        // Issue #665: bounded per-tick sweep — see WorkFlowOptions.SweepBatchSize.
        // Any remainder beyond the cap is picked up on the next poll tick.
        var expiredLeases = await db.Set<ProcessInstance>()
            .IgnoreQueryFilters() // cross-tenant system sweep; every reclaim write is PK+RowVer CAS
            .AsNoTracking()
            .Where(i => i.State == InstanceState.Returning
                         && i.ReturningLeaseUtc != null
                         && i.ReturningLeaseUtc < now)
            .Select(i => new { i.ID, i.RowVer, i.TenantCode })
            .Take(_options.SweepBatchSize)
            .ToListAsync(ct);

        foreach (var inst in expiredLeases)
        {
            ct.ThrowIfCancellationRequested();

            // FIX #325: Set the scoped IDataContext's TenantCode to the current instance's
            // TenantCode before the CAS write so EF's HasQueryFilter (TenantCode == this.TenantCode)
            // matches the correct tenant's row. The candidate SELECT uses IgnoreQueryFilters()
            // (cross-tenant sweep) so all expired leases are found regardless of tenant; but
            // ReclaimReturningLeaseByRowVerAsync's ExecuteUpdateAsync goes through the query filter
            // and therefore needs the tenant set. PK+RowVer CAS ensures no cross-tenant write.
            // Test path: _dc is null (WfTestContext has no ITenant filters) — no action needed.
            string? previousTenantCode = null;
            if (_dc is not null)
            {
                previousTenantCode = _dc.TenantCode;
                _dc.SetTenantCode(inst.TenantCode);
            }

            try
            {
                // ReclaimReturningLeaseByRowVerAsync: NO DateTime in UPDATE WHERE (portable).
                // Expiry was already checked client-side above on the RowVer-pinned snapshot.
                var rows = await GuardedTransition.ReclaimReturningLeaseByRowVerAsync(
                    db, inst.ID, inst.RowVer, ct);

                if (rows == 1)
                {
                    _logger.LogInformation(
                        "Reclaimed expired Returning lease for ProcessInstance {InstanceId}",
                        inst.ID);
                }
                // rows==0: another host beat us — safe no-op.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Lease reclaim failed for ProcessInstance {InstanceId} — will retry next tick",
                    inst.ID);
            }
            finally
            {
                // Restore tenant context so the next row (or any subsequent scope use) does not
                // inherit this instance's tenant. Belt-and-suspenders: PK+RowVer CAS is the real guard.
                if (_dc is not null)
                    _dc.SetTenantCode(previousTenantCode);
            }
        }
    }

    // ── Escalate action (WF-20.5) ────────────────────────────────────────────

    /// <summary>
    /// Carries escalation outcome details for post-commit notification.
    /// </summary>
    private sealed record EscalateInfo(
        string OldAssigneeITCode,
        string NewAssigneeITCode,
        bool IsCollision,
        bool IsNodeScoped);

    /// <summary>
    /// Implements WF-20.5 escalation dispatch (design §5 Escalate semantics):
    /// <list type="number">
    ///   <item>Task-scoped timer: collision pre-check on (NodeInstanceId, Generation) ANY state
    ///         (WF-19 FIX-2 lesson; unique-index class) → notify-only on collision.</item>
    ///   <item>Task-scoped + no collision: <see cref="GuardedTransition.EscalateTaskAssigneeAsync"/>
    ///         + epoch bump + <c>TimeoutEscalate</c> event + follow-up reminder re-arm.</item>
    ///   <item>Both EscalateTo and AdminFallbackITCode empty → FAIL-CLOSED:
    ///         no reassign, <c>FailClosed</c> event + Warning + Remind downgrade.</item>
    ///   <item>Node-scoped timer (All/Any): notify-only (quorum reassignment deferred per design).</item>
    /// </list>
    /// <para>Lock-order: WorkflowTimer (fire CAS already held) → ApprovalTask → NodeInstance → ProcessInstance(Seq).</para>
    /// </summary>
    private async Task<(TimerFireOutcome outcome, EscalateInfo? info)> HandleEscalateAsync(
        DbContext db,
        Guid timerId,
        Guid nodeInstanceId,
        Guid? approvalTaskId,
        uint timerGeneration,
        int remindCount,
        string idempotencyKey,
        string? timerTenantCode,
        TimeoutDef? timeoutDef,
        Guid instanceId,
        uint instanceRowVer,
        string? instanceTenantCode,
        DateTime now,
        CancellationToken ct)
    {
        // Node-scoped timer (All/Any mode): notify-only — quorum reassignment deferred.
        // ApprovalTaskId==null is the structural marker for node-scoped timers.
        if (approvalTaskId is null)
        {
            _logger.LogInformation(
                "Timer {TimerId} Escalate: node-scoped timer (no task) — notify-only (quorum reassignment deferred per design §5)",
                timerId);

            // Append TimeoutEscalate event (actor NULL) for audit.
            var seqNs = await AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
            if (seqNs.rows == 1)
            {
                db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
                {
                    ID          = Guid.NewGuid(),
                    TenantCode  = instanceTenantCode,
                    InstanceId  = instanceId,
                    Seq         = seqNs.seq,
                    Action      = EventAction.TimeoutEscalate,
                    NodeKey     = null,
                    ActorITCode = null,
                    Generation  = (int?)timerGeneration,
                    Reason      = "node-scoped escalate: notify-only (quorum reassignment deferred)",
                    OccurredUtc = now,
                });
                await db.SaveChangesAsync(ct);
            }

            // Return info for post-commit notify to current Pending assignees + admin.
            return (TimerFireOutcome.Fired,
                new EscalateInfo("", _options.AdminFallbackITCode ?? "", IsCollision: false, IsNodeScoped: true));
        }

        // FIX-B1: AllowTimerAutoAction gate for task-scoped escalation.
        // The validator (check 14g) classifies Escalate under the auto-action gate because
        // it mutates authority (reassigns the task assignee).  A gate-off → notify-only path
        // must NOT perform the reassignment but MUST still notify the current assignee.
        // The notify is delivered post-commit via EscalateInfo (IsGateOff=true) → NotifyEscalateAsync
        // falls back to NotifyTimeoutRemindAsync for the current assignee.
        if (!_options.AllowTimerAutoAction)
        {
            _logger.LogWarning(
                "Timer {TimerId} Escalate (task-scoped): AllowTimerAutoAction=false — " +
                "gate-off: no assignee reassignment. Task {TaskId} stays with current assignee. " +
                "Set WorkFlowOptions.AllowTimerAutoAction=true to enable task escalation.",
                timerId, approvalTaskId);

            // Append FailClosed event noting the suppression.
            var seqGate = await AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
            if (seqGate.rows == 1)
            {
                db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
                {
                    ID          = Guid.NewGuid(),
                    TenantCode  = instanceTenantCode,
                    InstanceId  = instanceId,
                    Seq         = seqGate.seq,
                    Action      = EventAction.FailClosed,
                    NodeKey     = null,
                    ActorITCode = null,
                    Generation  = (int?)timerGeneration,
                    Reason      = "Escalate: AllowTimerAutoAction=false — reassignment suppressed (notify-only)",
                    OccurredUtc = now,
                });
                await db.SaveChangesAsync(ct);
            }

            // Return DowngradedToRemind so post-commit FIX-B4 block notifies + arms chain link.
            return (TimerFireOutcome.DowngradedToRemind, null);
        }

        // Task-scoped timer: read the task snapshot to get current assignee + RowVer.
        var taskSnap = await db.Set<ApprovalTask>()
            .IgnoreQueryFilters() // cross-tenant system sweep; write is PK+RowVer CAS
            .AsNoTracking()
            .Where(t => t.ID == approvalTaskId.Value
                         && t.State == TaskState.Pending
                         && t.Generation == timerGeneration)
            .Select(t => new { t.ID, t.RowVer, t.AssigneeITCode, t.NodeInstanceId })
            .FirstOrDefaultAsync(ct);

        if (taskSnap is null)
        {
            // Task no longer Pending or generation changed — orphan, no-op.
            _logger.LogDebug(
                "Timer {TimerId} Escalate: task {TaskId} no longer Pending/gen-matched — orphan no-op",
                timerId, approvalTaskId);
            return (TimerFireOutcome.SupersededNoOp, null);
        }

        string currentAssignee = taskSnap.AssigneeITCode ?? string.Empty;

        // FIX-A3: Resolve escalation target from the version-pinned graph (timeoutDef.EscalateTo)
        // with fallback to AdminFallbackITCode.  Per design: target = TimeoutDef.EscalateTo else
        // options.AdminFallbackITCode; both empty → FailClosed below.
        string? escalateTarget = !string.IsNullOrWhiteSpace(timeoutDef?.EscalateTo)
            ? timeoutDef.EscalateTo
            : _options.AdminFallbackITCode;

        // Both-empty fail-closed check.
        if (string.IsNullOrWhiteSpace(escalateTarget))
        {
            _logger.LogWarning(
                "Timer {TimerId} Escalate (task-scoped): EscalateTo empty and AdminFallbackITCode empty — " +
                "FAIL-CLOSED: no reassignment. Task {TaskId} stays with {Assignee}. " +
                "Configure WorkFlowOptions.AdminFallbackITCode to enable escalation.",
                timerId, approvalTaskId, currentAssignee);

            // Append FailClosed event for audit trail.
            var seqFc = await AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
            if (seqFc.rows == 1)
            {
                db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
                {
                    ID          = Guid.NewGuid(),
                    TenantCode  = instanceTenantCode,
                    InstanceId  = instanceId,
                    Seq         = seqFc.seq,
                    Action      = EventAction.FailClosed,
                    NodeKey     = null,
                    ActorITCode = null,
                    Generation  = (int?)timerGeneration,
                    Reason      = "Escalate: both EscalateTo and AdminFallbackITCode are empty — no reassignment",
                    OccurredUtc = now,
                });
                await db.SaveChangesAsync(ct);
            }

            // Downgrade: return Remind-flavored outcome so the current assignee is reminded.
            return (TimerFireOutcome.DowngradedToRemind, null);
        }

        // Collision pre-check (WF-19 FIX-2 lesson): target must not have ANY task row
        // on (NodeInstanceId, Generation) regardless of State — unique-index class prevention.
        bool hasCollision = await db.Set<ApprovalTask>()
            .IgnoreQueryFilters() // cross-tenant system sweep
            .AsNoTracking()
            .AnyAsync(t => t.NodeInstanceId == nodeInstanceId
                            && t.Generation == timerGeneration
                            && t.AssigneeITCode == escalateTarget, ct);

        if (hasCollision)
        {
            _logger.LogInformation(
                "Timer {TimerId} Escalate: target {Target} already has a task row on node {NodeId} gen {Gen} — " +
                "collision detected, downgrading to notify-only",
                timerId, escalateTarget, nodeInstanceId, timerGeneration);

            // Append FailClosed event with collision detail.
            var seqColl = await AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
            if (seqColl.rows == 1)
            {
                db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
                {
                    ID          = Guid.NewGuid(),
                    TenantCode  = instanceTenantCode,
                    InstanceId  = instanceId,
                    Seq         = seqColl.seq,
                    Action      = EventAction.FailClosed,
                    NodeKey     = null,
                    ActorITCode = null,
                    Generation  = (int?)timerGeneration,
                    Reason      = $"escalate target already participant — collision notify-only",
                    OccurredUtc = now,
                });
                await db.SaveChangesAsync(ct);
            }

            return (TimerFireOutcome.EscalateCollisionNotifyOnly,
                new EscalateInfo(currentAssignee, escalateTarget, IsCollision: true, IsNodeScoped: false));
        }

        // Attempt the assignee-bound CAS reassignment (WF-19 FIX-1 discipline).
        int escalateRows = await GuardedTransition.EscalateTaskAssigneeAsync(
            db, approvalTaskId.Value, taskSnap.RowVer, timerGeneration,
            currentAssignee, escalateTarget, ct);

        if (escalateRows == 0)
        {
            // Human claimed or generation changed between snapshot and CAS — no-op.
            _logger.LogDebug(
                "Timer {TimerId} Escalate: CAS lost for task {TaskId} — human acted or generation changed",
                timerId, approvalTaskId);
            return (TimerFireOutcome.SupersededNoOp, null);
        }

        // CAS won — bump epoch so in-flight completion CASes re-read the updated set.
        var nodeSnapForEpoch = await db.Set<NodeInstance>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(n => n.ID == nodeInstanceId && n.State == NodeState.Activated)
            .Select(n => new { n.ID, n.RowVer })
            .FirstOrDefaultAsync(ct);

        if (nodeSnapForEpoch is not null)
        {
            // Best-effort epoch bump; rows==0 (node completed concurrently) is safe.
            await GuardedTransition.AdvanceNodeApproverSetEpochAsync(
                db, nodeSnapForEpoch.ID, nodeSnapForEpoch.RowVer, ct);
        }

        // Append TimeoutEscalate event (actor NULL; OnBehalfOf = old assignee per design §5).
        var seqEsc = await AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
        if (seqEsc.rows == 1)
        {
            db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
            {
                ID                = Guid.NewGuid(),
                TenantCode        = instanceTenantCode,
                InstanceId        = instanceId,
                Seq               = seqEsc.seq,
                Action            = EventAction.TimeoutEscalate,
                NodeKey           = null,
                ActorITCode       = null,       // system action
                OnBehalfOfITCode  = currentAssignee,  // old assignee
                Generation        = (int?)timerGeneration,
                OccurredUtc       = now,
            });
        }

        // Re-arm follow-up Remind against new assignee: chain continues with the same timing.
        // This ensures the new assignee is reminded if they also don't act.
        // FIX-A3: RemindEveryHours and MaxReminders re-read from timeoutDef (not stored on row).
        int? remindEveryHours = timeoutDef?.RemindEveryHours;
        int? maxReminders     = timeoutDef?.MaxReminders;

        if (remindEveryHours.HasValue)
        {
            int effectiveCap = Math.Min(
                maxReminders ?? _options.MaxRemindersDefault,
                _options.MaxRemindersHardCap);

            if (remindCount + 1 < effectiveCap)
            {
                string nextKey = BuildNextLinkKey(idempotencyKey, remindCount);
                // FIX-A3: next-link row does NOT carry RemindEveryHours/MaxReminders (schema-delta-zero).
                db.Set<WorkflowTimer>().Add(new WorkflowTimer
                {
                    ID             = Guid.NewGuid(),
                    TenantCode     = timerTenantCode,
                    NodeInstanceId = nodeInstanceId,
                    ApprovalTaskId = approvalTaskId.Value,
                    Status         = TimerStatus.Armed,
                    RowVer         = 0,
                    IdempotencyKey = nextKey,
                    FireAtUtc      = now.AddHours(remindEveryHours.Value),
                    Action         = TimerAction.Remind, // follow-up is a Remind (not another Escalate)
                    Generation     = timerGeneration,
                    RemindCount    = remindCount + 1,
                });
            }
        }

        await db.SaveChangesAsync(ct);

        return (TimerFireOutcome.Fired,
            new EscalateInfo(currentAssignee, escalateTarget, IsCollision: false, IsNodeScoped: false));
    }

    // ── Post-commit Escalate notification (WF-20.5) ──────────────────────────

    /// <summary>
    /// Post-commit notify for Escalate: re-read node + instance, then dispatch to
    /// <see cref="IWorkflowNotifier.NotifyTimeoutEscalatedAsync"/> (task-scoped reassignment)
    /// or <see cref="IWorkflowNotifier.NotifyTimeoutRemindAsync"/> (node-scoped notify-only or collision).
    /// Null-check + try/catch + LogError; never propagates to caller.
    /// </summary>
    private async Task NotifyEscalateAsync(
        DbContext db,
        IWorkflowNotifier notifier,
        Guid nodeInstanceId,
        uint timerGeneration,
        Guid instanceId,
        EscalateInfo info,
        CancellationToken ct)
    {
        try
        {
            var freshNode = await db.Set<NodeInstance>()
                .IgnoreQueryFilters() // cross-tenant system sweep; same justification as candidate SELECT
                .AsNoTracking()
                .Where(n => n.ID == nodeInstanceId && n.Generation == timerGeneration)
                .Select(n => new { n.ID, n.State, n.Generation, n.NodeKey, n.ApproveMode, n.InstanceId })
                .FirstOrDefaultAsync(ct);

            if (freshNode is null)
            {
                _logger.LogDebug(
                    "NotifyEscalateAsync: node {NodeId} gen-mismatch — skipping notification",
                    nodeInstanceId);
                return;
            }

            var freshInstance = await db.Set<ProcessInstance>()
                .IgnoreQueryFilters() // cross-tenant system sweep
                .AsNoTracking()
                .Where(i => i.ID == instanceId)
                .FirstOrDefaultAsync(ct);

            if (freshInstance is null)
            {
                _logger.LogDebug(
                    "NotifyEscalateAsync: instance {InstanceId} not found — skipping notification",
                    instanceId);
                return;
            }

            var nodeForNotifier = new NodeInstance
            {
                ID          = freshNode.ID,
                NodeKey     = freshNode.NodeKey,
                State       = freshNode.State,
                ApproveMode = freshNode.ApproveMode,
                InstanceId  = freshNode.InstanceId,
                Generation  = freshNode.Generation,
            };

            if (!info.IsCollision && !info.IsNodeScoped && !string.IsNullOrEmpty(info.NewAssigneeITCode))
            {
                // Real reassignment: notify old and new assignees.
                await notifier.NotifyTimeoutEscalatedAsync(
                    freshInstance, nodeForNotifier,
                    info.OldAssigneeITCode, info.NewAssigneeITCode, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                // Node-scoped or collision: fall back to a Remind notification
                // so current Pending assignees (+ admin) are still alerted.
                await notifier.NotifyTimeoutRemindAsync(
                    freshInstance, nodeForNotifier, remindCount: 0, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "NotifyEscalateAsync: failed to deliver escalation notification for node {NodeId} " +
                "— notification failure ignored (engine state committed)",
                nodeInstanceId);
        }
    }

    // ── Phase-3: AtAction expired-delegation sweep (WF-20.5) ─────────────────

    /// <summary>
    /// AtAction expired-delegation sweep (R5): doubly opt-in —
    /// (a) <see cref="WorkFlowOptions.DelegationWindowMode"/> must be
    ///     <see cref="DelegationWindowMode.AtAction"/> AND
    /// (b) <see cref="WorkFlowOptions.DelegationExpiredSweep"/> must be
    ///     <see cref="DelegationExpiredSweep.RevertToPrincipal"/>.
    ///
    /// <para>AtAction mode is ctor-blocked on Oracle/DaMeng; this sweep is therefore
    /// unreachable on providers that don't support nullable-DateTime query patterns.
    /// Expiry is evaluated CLIENT-SIDE (no DateTime in UPDATE WHERE — portability preserved).</para>
    ///
    /// <para>Per-row revert shape (mirrors <see cref="GuardedTransition.RevokeDelegatedTasksAsync"/>):
    /// <c>State==Pending AND RowVer AND Generation</c> CAS;
    /// <c>AssigneeITCode=DelegatedFromITCode</c>, clear delegation fields, bump RowVer.
    /// Concurrent human claim wins cleanly (rows==0 → safe no-op; partial success valid).</para>
    ///
    /// <para>Epoch bump + <c>DelegationExpiredReverted</c> event (actor NULL) + notify principal
    /// follow each successful revert.</para>
    /// </summary>
    private async Task SweepExpiredAtActionDelegationsAsync(DateTime now, CancellationToken ct)
    {
        // Gate (a): only when DelegationWindowMode==AtAction.
        if (_options.DelegationWindowMode != DelegationWindowMode.AtAction)
            return;

        // Gate (b): only when DelegationExpiredSweep==RevertToPrincipal.
        if (_options.DelegationExpiredSweep != DelegationExpiredSweep.RevertToPrincipal)
            return;

        var db = GetDb();

        // SELECT expired delegated Pending tasks — expiry evaluated CLIENT-SIDE on the snapshot
        // (nullable-DateTime SELECT-side only; no DateTime in UPDATE WHERE — portability preserved).
        // IgnoreQueryFilters: cross-tenant system sweep; every write is PK+RowVer CAS.
        // Issue #665: bounded per-tick sweep — see WorkFlowOptions.SweepBatchSize.
        // Any remainder beyond the cap is picked up on the next poll tick.
        var expiredDelegated = await db.Set<ApprovalTask>()
            .IgnoreQueryFilters() // justified: cross-tenant system reaper sweep; all writes are PK+RowVer CAS
            .AsNoTracking()
            .Where(t => t.State == TaskState.Pending
                         && t.DelegationRuleId != null
                         && t.DelegatedFromITCode != null
                         && t.DelegationExpiresUtc != null
                         && t.DelegationExpiresUtc < now)
            .Select(t => new
            {
                t.ID,
                t.RowVer,
                t.Generation,
                t.NodeInstanceId,
                t.AssigneeITCode,
                t.DelegatedFromITCode,
                t.TenantCode,
            })
            .Take(_options.SweepBatchSize)
            .ToListAsync(ct);

        foreach (var row in expiredDelegated)
        {
            ct.ThrowIfCancellationRequested();

            // FIX #325: Set the scoped IDataContext's TenantCode to the current task's
            // TenantCode before the CAS write so EF's HasQueryFilter (TenantCode == this.TenantCode)
            // matches the correct tenant's row. The candidate SELECT uses IgnoreQueryFilters()
            // (cross-tenant sweep) so all expired delegations are found regardless of tenant; but
            // the ExecuteUpdateAsync CAS writes go through the query filter and therefore need the
            // tenant set on the context. PK+RowVer CAS ensures no cross-tenant write is possible.
            // Test path: _dc is null (WfTestContext has no ITenant filters) — no action needed.
            string? previousTenantCode = null;
            if (_dc is not null)
            {
                previousTenantCode = _dc.TenantCode;
                _dc.SetTenantCode(row.TenantCode);
            }

            try
            {
                if (string.IsNullOrWhiteSpace(row.DelegatedFromITCode))
                {
                    // Defensive: DelegatedFromITCode is null/empty — skip (principal unknown).
                    // continue inside try still executes the finally block (C# spec §13.11).
                    _logger.LogWarning(
                        "AtAction sweep: task {TaskId} has expired delegation but null DelegatedFromITCode — skipping",
                        row.ID);
                    continue;
                }

                string principal = row.DelegatedFromITCode!;

                // C13 (#327): collision pre-check — mirrors escalate-path (lines ~1308-1346) to kill infinite
                // per-tick retry when the principal was added (加签'd) onto the same node. If principal already
                // holds ANY task row on (NodeInstanceId, Generation), skip the flip and emit a FailClosed audit
                // event for operator visibility. Do NOT revert — leave the expired delegated slot as-is.
                bool hasCollision = await db.Set<ApprovalTask>()
                    .IgnoreQueryFilters() // justified: cross-tenant system sweep; all writes are PK+RowVer CAS
                    .AsNoTracking()
                    .AnyAsync(t => t.NodeInstanceId == row.NodeInstanceId
                                    && t.Generation == row.Generation
                                    && t.AssigneeITCode == principal, ct);

                if (hasCollision)
                {
                    _logger.LogInformation(
                        "AtAction sweep: principal {Principal} already has a task row on node {NodeId} gen {Gen} " +
                        "— collision detected, leaving expired delegated slot, notify-only",
                        principal, row.NodeInstanceId, (uint)row.Generation);

                    var nodeForInstColl = await db.Set<NodeInstance>()
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(n => n.ID == row.NodeInstanceId)
                        .Select(n => new { n.InstanceId })
                        .FirstOrDefaultAsync(ct);

                    if (nodeForInstColl is not null)
                    {
                        var instSnapColl = await db.Set<ProcessInstance>()
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .Where(i => i.ID == nodeForInstColl.InstanceId)
                            .Select(i => new { i.ID, i.RowVer, i.TenantCode })
                            .FirstOrDefaultAsync(ct);

                        if (instSnapColl is not null)
                        {
                            var seqColl = await AllocateSeqWithRetryAsync(db, instSnapColl.ID, instSnapColl.RowVer, ct);
                            if (seqColl.rows == 1)
                            {
                                db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
                                {
                                    ID          = Guid.NewGuid(),
                                    TenantCode  = instSnapColl.TenantCode,
                                    InstanceId  = instSnapColl.ID,
                                    Seq         = seqColl.seq,
                                    Action      = EventAction.FailClosed,
                                    NodeKey     = null,
                                    ActorITCode = null,
                                    Generation  = (int?)(uint)row.Generation,
                                    Reason      = "delegation-expiry revert: principal already participant — collision notify-only",
                                    OccurredUtc = now,
                                });
                                await db.SaveChangesAsync(ct);
                            }
                        }
                    }

                    continue;
                }

                // Per-row CAS: revert to principal, clear delegation fields, bump RowVer.
                // Concurrent human claim → rows==0 → safe no-op (partial success valid).
                int revertRows = await db.Set<ApprovalTask>()
                    .Where(t => t.ID == row.ID
                                 && t.State == TaskState.Pending
                                 && t.RowVer == row.RowVer
                                 && t.Generation == row.Generation)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.AssigneeITCode, principal)
                               .SetProperty(t => t.DelegatedFromITCode, (string?)null)
                               .SetProperty(t => t.DelegationRuleId, (Guid?)null)
                               .SetProperty(t => t.DelegationExpiresUtc, (DateTime?)null)
                               .SetProperty(t => t.WindowVerifiedUtc, (DateTime?)null)
                               .SetProperty(t => t.RowVer, t => t.RowVer + 1),
                        ct);

                if (revertRows == 0)
                {
                    // Concurrent human claim or supersede won — not an error.
                    _logger.LogDebug(
                        "AtAction sweep: task {TaskId} — revert CAS lost (human claimed or superseded) — no-op",
                        row.ID);
                    continue;
                }

                // Epoch bump: re-read node RowVer fresh after task CAS.
                var nodeSnapForEpoch = await db.Set<NodeInstance>()
                    .IgnoreQueryFilters() // cross-tenant system sweep
                    .AsNoTracking()
                    .Where(n => n.ID == row.NodeInstanceId && n.State == NodeState.Activated)
                    .Select(n => new { n.ID, n.RowVer })
                    .FirstOrDefaultAsync(ct);

                if (nodeSnapForEpoch is not null)
                {
                    // Best-effort epoch bump; rows==0 (node completed concurrently) is safe.
                    await GuardedTransition.AdvanceNodeApproverSetEpochAsync(
                        db, nodeSnapForEpoch.ID, nodeSnapForEpoch.RowVer, ct);
                }

                // Append DelegationExpiredReverted event (actor NULL = system sweep).
                // Re-read instance RowVer to get a fresh Seq allocation target.
                // We look up the instance through the node — may return null if node was removed.
                var nodeForInst = await db.Set<NodeInstance>()
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(n => n.ID == row.NodeInstanceId)
                    .Select(n => new { n.InstanceId })
                    .FirstOrDefaultAsync(ct);

                if (nodeForInst is not null)
                {
                    var instSnap = await db.Set<ProcessInstance>()
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(i => i.ID == nodeForInst.InstanceId)
                        .Select(i => new { i.ID, i.RowVer, i.TenantCode })
                        .FirstOrDefaultAsync(ct);

                    if (instSnap is not null)
                    {
                        var seqResult = await AllocateSeqWithRetryAsync(
                            db, instSnap.ID, instSnap.RowVer, ct);

                        if (seqResult.rows == 1)
                        {
                            db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
                            {
                                ID          = Guid.NewGuid(),
                                TenantCode  = instSnap.TenantCode,
                                InstanceId  = instSnap.ID,
                                Seq         = seqResult.seq,
                                Action      = EventAction.DelegationExpiredReverted,
                                NodeKey     = null,
                                ActorITCode = null, // system sweep
                                Generation  = (int?)(uint)row.Generation,
                                OccurredUtc = now,
                            });
                            await db.SaveChangesAsync(ct);
                        }

                        // Post-commit notify: alert principal that their task was reverted back to them.
                        if (_notifier is not null)
                        {
                            try
                            {
                                var freshInstance = await db.Set<ProcessInstance>()
                                    .IgnoreQueryFilters()
                                    .AsNoTracking()
                                    .Where(i => i.ID == instSnap.ID)
                                    .FirstOrDefaultAsync(ct);

                                if (freshInstance is not null)
                                {
                                    var taskForNotifier = new ApprovalTask
                                    {
                                        ID             = row.ID,
                                        AssigneeITCode = principal,
                                        NodeInstanceId = row.NodeInstanceId,
                                        State          = TaskState.Pending,
                                    };

                                    // Reuse NotifyTaskAssignedAsync to alert the reverted principal.
                                    // This is structurally equivalent to a new assignment notification.
                                    await _notifier.NotifyTaskAssignedAsync(
                                        freshInstance,
                                        new NodeInstance { ID = row.NodeInstanceId, InstanceId = instSnap.ID },
                                        taskForNotifier, ct)
                                        .ConfigureAwait(false);
                                }
                            }
                            catch (Exception notifyEx)
                            {
                                _logger.LogError(notifyEx,
                                    "AtAction sweep: notification failed for task {TaskId} principal {Principal} — ignored",
                                    row.ID, principal);
                            }
                        }
                    }
                }

                _logger.LogInformation(
                    "AtAction sweep: reverted task {TaskId} from delegate {Delegate} to principal {Principal}",
                    row.ID, row.AssigneeITCode, principal);
            }
            catch (Exception ex)
            {
                // Per-row try/catch: one bad row never blocks the rest.
                _logger.LogError(ex,
                    "AtAction sweep: failed for task {TaskId} — will retry next tick",
                    row.ID);
            }
            finally
            {
                // Restore tenant context for next iteration.
                // Belt-and-suspenders: PK+RowVer CAS is the real cross-tenant guard.
                if (_dc is not null)
                    _dc.SetTenantCode(previousTenantCode);
            }
        }
    }

    // ── Phase-4: Strand-reaper (Issue #359) ──────────────────────────────────

    /// <summary>
    /// Phase-4 strand-reaper: re-drives Sequential approval nodes where the SequencePointer
    /// was never advanced after a system auto-approve claim committed (crash window between
    /// <see cref="WorkflowEngine.SystemClaimTaskAsync"/> commit and
    /// <see cref="WorkflowEngine.SystemContinueTaskAsync"/> post-commit).
    ///
    /// <para><strong>Strand signature:</strong> ProcessInstance Running + NodeInstance Activated +
    /// ApproveMode==Sequential + task at SequenceOrder==SequencePointer is terminal
    /// (AutoApproved/AutoRejected) + SequencePointer &lt; TotalRequired.</para>
    ///
    /// <para><strong>Re-drive entry:</strong> <see cref="WorkflowEngine.SystemContinueTaskAsync"/>
    /// — the same post-commit continuation the timer normally calls after a successful claim.
    /// SystemContinueTaskAsync calls ExecuteApproveCompletionAsync which re-reads NodeInstance
    /// fresh and atomically advances SequencePointer + activates the next task (or completes
    /// the node) via RowVer-guarded CAS transactions.  Two concurrent timer hosts both calling
    /// this on the same strand produce exactly one advance and one AlreadyHandled no-op (pointer
    /// CAS returns 0 for the loser).</para>
    ///
    /// <para><strong>Concrete engine required:</strong> SystemContinueTaskAsync is on
    /// <see cref="WorkflowEngine"/> (internal); when a custom IWorkflowEngine is registered,
    /// the cast fails → <c>null</c> → reaper gate off for that host (documented limitation).</para>
    ///
    /// <para><strong>Idempotent:</strong> calling this twice on the same strand is safe because
    /// ExecuteApproveCompletionAsync uses RowVer-guarded pointer-advance CAS — the second call
    /// finds pointer already advanced → pointer CAS returns 0 → AlreadyHandled → no-op.
    /// Disabled when <see cref="WorkFlowOptions.StrandReaperBatchSize"/> == 0.</para>
    ///
    /// <para><strong>Lock order:</strong> no explicit locks acquired here; SystemContinueTaskAsync
    /// internally follows Task → Node → Instance order (canonical engine order) via
    /// GuardedTransition.</para>
    /// </summary>
    private async Task ReDriveStrandedSequentialNodesAsync(DateTime now, CancellationToken ct)
    {
        // Gate: disabled when batch size is 0.
        if (_options.StrandReaperBatchSize <= 0)
            return;

        // Gate: concrete WorkflowEngine required for SystemContinueTaskAsync (internal method).
        // Custom IWorkflowEngine registrations that aren't WorkflowEngine → null → gate off.
        var concreteEngine = _engine as WorkflowEngine;
        if (concreteEngine is null)
            return;

        var db = GetDb();

        // ── Step 1: Candidate SELECT (cross-tenant) ───────────────────────────────
        // Activated Sequential nodes with pointer < total (potential strands).
        // IgnoreQueryFilters: cross-tenant system sweep — all re-drives go through
        // SystemContinueTaskAsync which uses RowVer-guarded CAS writes.
        // No cross-tenant write is possible: each write is pinned to the node's own rows.
        // TenantCode projected so we can set _dc.TenantCode before calling the engine.
        // Issue #665: deterministic, starvation-proof ordering — oldest-Activated-first.
        //
        // Without an OrderBy, Take(StrandReaperBatchSize) is a provider-defined, effectively
        // arbitrary window. With more than batch-size candidates, a genuinely stranded node
        // outside that window could go unselected on every tick, silently starving the
        // crash-recovery backstop.
        //
        // NodeInstance.UpdateTime (BasePoco) is NOT usable here: every post-mint write to
        // NodeInstance goes through GuardedTransition's ExecuteUpdateAsync CAS helpers, which
        // bypass DataContext's ChangeTracker-based audit stamping — UpdateTime is never written
        // by the WorkFlow engine and stays permanently null. Ordering on it would collapse to a
        // null/null tie on every row, degrading to OrderBy(ID) alone — deterministic, but NOT
        // starvation-proof: it would return the exact same window forever, permanently starving
        // any node whose ID sorts after the batch cutoff.
        //
        // ActivatedAt IS reliably populated for this candidate set: the only write path that
        // transitions State -> Activated is GuardedTransition.ActivateNodeInstanceAsync, which
        // always stamps ActivatedAt in that same atomic CAS. Ordering oldest-ActivatedAt-first
        // puts the longest-waiting nodes — including genuine strands, which stop making progress
        // the instant they strand — at the front of the window. Because nodes leave the Activated
        // population as they advance/complete, the "oldest surviving" set naturally rotates tick
        // to tick, giving real coverage instead of a fixed, permanently-starved tail.
        //
        // The null-check is defensive belt-and-suspenders (ActivatedAt is not null in practice
        // for State==Activated rows today) and keeps the ordering translatable/null-safe across
        // all 7 DBTypeEnum providers if a future code path ever mints directly into Activated.
        var activatedSeqNodes = await db.Set<NodeInstance>()
            .IgnoreQueryFilters() // justified: cross-tenant system reaper sweep; all re-drives are RowVer CAS-guarded via SystemContinueTaskAsync
            .AsNoTracking()
            .Where(n => n.State == NodeState.Activated
                         && n.ApproveMode == ApproveMode.Sequential
                         && n.SequencePointer < n.TotalRequired)
            .OrderBy(n => n.ActivatedAt == null)
            .ThenBy(n => n.ActivatedAt)
            .ThenBy(n => n.ID)
            .Select(n => new { n.ID, n.RowVer, n.InstanceId, n.TenantCode, n.SequencePointer })
            .Take(_options.StrandReaperBatchSize)
            .ToListAsync(ct);

        if (activatedSeqNodes.Count == 0)
            return;

        // ── Step 2: Filter to Running instances ───────────────────────────────────
        // Only re-drive nodes whose owning instance is still Running.
        // A two-step materialize approach avoids complex correlated subqueries
        // that may not translate cleanly across all supported providers (SQLite/Oracle/DaMeng).
        var instanceIds = activatedSeqNodes.Select(n => n.InstanceId).Distinct().ToList();
        var runningInstanceIds = await db.Set<ProcessInstance>()
            .IgnoreQueryFilters() // cross-tenant system sweep (same justification)
            .AsNoTracking()
            .Where(i => instanceIds.Contains(i.ID) && i.State == InstanceState.Running)
            .Select(i => i.ID)
            .ToListAsync(ct);

        if (runningInstanceIds.Count == 0)
            return;

        var runningSet = new HashSet<Guid>(runningInstanceIds);

        // ── Step 3: Per-candidate strand-check and re-drive ───────────────────────
        foreach (var node in activatedSeqNodes)
        {
            ct.ThrowIfCancellationRequested();

            if (!runningSet.Contains(node.InstanceId))
                continue; // instance not Running — skip

            // ── Strand-check: find the terminal task at SequencePointer ───────────
            // Idempotent: if the pointer was already advanced by another host (task at
            // pointer is no longer AutoApproved/AutoRejected), AnyAsync returns false → skip.
            var terminalTask = await db.Set<ApprovalTask>()
                .IgnoreQueryFilters() // cross-tenant system sweep
                .AsNoTracking()
                .Where(t => t.NodeInstanceId == node.ID
                             && t.SequenceOrder == node.SequencePointer
                             && (t.State == TaskState.AutoApproved
                                 || t.State == TaskState.AutoRejected))
                .Select(t => new { t.ID, t.State, t.AssigneeITCode })
                .FirstOrDefaultAsync(ct);

            if (terminalTask is null)
                continue; // not a strand — task at pointer is still Pending (healthy) or pointer already advanced

            // ── Read full entity rows needed by SystemContinueTaskAsync ───────────
            // SystemContinueTaskAsync takes NodeInstance + ProcessInstance (full entities).
            // Re-reads are fresh; concurrent pointer advance on another host is safe because
            // ExecuteApproveCompletionAsync uses a RowVer-guarded CAS internally.
            var fullNode = await db.Set<NodeInstance>()
                .IgnoreQueryFilters() // cross-tenant system sweep
                .AsNoTracking()
                .SingleOrDefaultAsync(n => n.ID == node.ID, ct);

            // D1: re-validate the strand still holds at the SAME pointer we scanned.
            // A concurrent host (or normal actor) may have advanced the pointer between the
            // batch scan and this fresh read — if so, this is no longer our strand: skip it.
            if (fullNode is null
                || fullNode.State != NodeState.Activated
                || fullNode.SequencePointer != node.SequencePointer
                || fullNode.SequencePointer >= fullNode.TotalRequired)
            {
                continue;
            }

            var fullInstance = await db.Set<ProcessInstance>()
                .IgnoreQueryFilters() // cross-tenant system sweep
                .AsNoTracking()
                .SingleOrDefaultAsync(i => i.ID == node.InstanceId, ct);

            if (fullInstance is null || fullInstance.State != InstanceState.Running)
                continue; // instance no longer Running — safe skip

            // ── This node matches the strand signature — re-drive via SystemContinueTaskAsync ──
            // Tenant-scoped: set _dc.TenantCode before calling the engine so that
            // any downstream HasQueryFilter scoped writes resolve to the correct tenant.
            // Test path: _dc is null → no tenant setup needed.
            string? previousTenantCode = null;
            if (_dc is not null)
            {
                previousTenantCode = _dc.TenantCode;
                _dc.SetTenantCode(node.TenantCode);
            }

            try
            {
                // Re-drive: SystemContinueTaskAsync calls ExecuteApproveCompletionAsync (Sequential path)
                // which re-reads NodeInstance fresh and atomically advances SequencePointer + activates
                // the next task (or completes the node if pointer+1 >= TotalRequired).
                //
                // CAS-safety: ExecuteApproveCompletionAsync wraps pointer advance in a RowVer-guarded
                // transaction.  If another host or normal flow already advanced the pointer, the pointer
                // CAS returns 0 rows → AlreadyHandled → safe no-op.  No double-advance is possible.
                var ctx = new WorkflowEngine.SystemClaimContext(
                    TaskId:       terminalTask.ID,
                    AssigneeITCode: terminalTask.AssigneeITCode ?? string.Empty,
                    NextState:    terminalTask.State,
                    ApproveMode:  ApproveMode.Sequential,
                    NodeInst:     fullNode,
                    Instance:     fullInstance);

                var result = await concreteEngine.SystemContinueTaskAsync(ctx, ct);

                if (result.Code == WorkflowActionCode.Advanced
                    || result.Code == WorkflowActionCode.InstanceApproved
                    || result.Code == WorkflowActionCode.Rejected)
                {
                    _logger.LogInformation(
                        "Phase-4 strand-reaper: re-drove stranded Sequential node {NodeId} (instance {InstanceId}, " +
                        "pointer {Pointer}) — result={Result}",
                        node.ID, node.InstanceId, node.SequencePointer, result.Code);
                }
                // AlreadyHandled (another host won the CAS), Blocked (next step human), etc. are safe no-ops.
            }
            catch (Exception ex)
            {
                // Per-candidate try/catch: one bad candidate never blocks the rest.
                // The strand stays in place and will be retried on the next tick.
                _logger.LogError(ex,
                    "Phase-4 strand-reaper: re-drive failed for node {NodeId} (instance {InstanceId}) — will retry next tick",
                    node.ID, node.InstanceId);
            }
            finally
            {
                // Restore tenant context for the next candidate. Belt-and-suspenders.
                if (_dc is not null)
                    _dc.SetTenantCode(previousTenantCode);
            }
        }
    }

    // ── FIX-A3: Graph reader (version-pinned immutable graph) ────────────────

    /// <summary>
    /// Re-reads <see cref="TimeoutDef"/> for this timer's node from the version-pinned
    /// immutable <see cref="ProcessDefinitionVersion.GraphJson"/>.
    ///
    /// <para>The graph is immutable once published — this is always a consistent read.
    /// On failure (version deleted, node renamed) returns <c>null</c>; callers degrade
    /// gracefully (Remind → one-shot; Escalate → AdminFallbackITCode).</para>
    ///
    /// <para>Mirrors <c>WorkflowEngine.LoadNodeDefAsync</c> (design §7 ZERO schema/migration delta).</para>
    /// </summary>
    private async Task<TimeoutDef?> LoadTimerNodeDefAsync(
        DbContext db,
        Guid definitionVersionId,
        string? nodeKey,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(nodeKey))
            return null;

        try
        {
            var version = await db.Set<ProcessDefinitionVersion>()
                .IgnoreQueryFilters() // cross-tenant system sweep; same justification as candidate SELECT
                .AsNoTracking()
                .SingleOrDefaultAsync(v => v.ID == definitionVersionId, ct);

            if (version is null)
            {
                _logger.LogDebug(
                    "LoadTimerNodeDefAsync: ProcessDefinitionVersion {VersionId} not found — " +
                    "timeout will degrade gracefully (one-shot / AdminFallbackITCode)",
                    definitionVersionId);
                return null;
            }

            var graph = _graphProvider.GetGraph(version);
            var nodeDef = graph.Nodes.FirstOrDefault(n =>
                string.Equals(n.NodeKey, nodeKey, StringComparison.Ordinal));

            return nodeDef?.Timeout;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LoadTimerNodeDefAsync: failed to load TimeoutDef for version {VersionId} / node {NodeKey} — " +
                "timeout will degrade gracefully (one-shot / AdminFallbackITCode)",
                definitionVersionId, nodeKey);
            return null;
        }
    }

    // ── FIX-B4: DowngradedToRemind chain-link arm ────────────────────────────

    /// <summary>
    /// Post-commit helper for FIX-B4: when a timer is downgraded to Remind (gate-off auto-action,
    /// custom-engine, escalate gate-off, or escalate no-target), arm the next Remind chain link
    /// so the human assignee is reminded again after <see cref="TimeoutDef.RemindEveryHours"/>.
    ///
    /// <para>The chain-link INSERT is done outside any outer transaction (post-commit).
    /// Unique-index collision (concurrent double-fire) is silently swallowed.</para>
    ///
    /// <para>Only called when <c>timeoutDef.RemindEveryHours</c> has a value (guarded by caller).</para>
    /// </summary>
    private async Task TryArmDowngradedRemindChainLinkAsync(
        DbContext db,
        Guid nodeInstanceId,
        Guid? approvalTaskId,
        uint timerGeneration,
        int remindCount,
        string idempotencyKey,
        string? timerTenantCode,
        TimeoutDef timeoutDef,
        DateTime now,
        CancellationToken ct)
    {
        try
        {
            int remindEveryHours = timeoutDef.RemindEveryHours!.Value;
            int effectiveCap = Math.Min(
                timeoutDef.MaxReminders ?? _options.MaxRemindersDefault,
                _options.MaxRemindersHardCap);

            if (remindCount + 1 >= effectiveCap)
                return; // already at cap — no next link

            string nextKey = BuildNextLinkKey(idempotencyKey, remindCount);

            db.Set<WorkflowTimer>().Add(new WorkflowTimer
            {
                ID              = Guid.NewGuid(),
                TenantCode      = timerTenantCode,
                NodeInstanceId  = nodeInstanceId,
                ApprovalTaskId  = approvalTaskId,
                Status          = TimerStatus.Armed,
                RowVer          = 0,
                IdempotencyKey  = nextKey,
                FireAtUtc       = now.AddHours(remindEveryHours),
                Action          = TimerAction.Remind, // downgrade chain is always Remind
                Generation      = timerGeneration,
                RemindCount     = remindCount + 1,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Post-commit chain-link arm is best-effort; failure is logged but not propagated.
            _logger.LogWarning(ex,
                "TryArmDowngradedRemindChainLink: failed to insert next-link Remind for node {NodeId} — " +
                "human will still be notified this tick; chain arm will not repeat",
                nodeInstanceId);
        }
    }

    // ── Seq allocation with retry ─────────────────────────────────────────────

    private static async Task<(int rows, int seq)> AllocateSeqWithRetryAsync(
        DbContext db,
        Guid instanceId,
        uint instanceRowVer,
        CancellationToken ct,
        int maxRetries = 5)
    {
        // Re-read RowVer if the first attempt loses to a concurrent writer.
        var rowVer = instanceRowVer;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var result = await GuardedTransition.AllocateSeqAsync(db, instanceId, rowVer, ct);
            if (result.rows == 1)
                return result;

            // RowVer mismatch — re-read and retry.
            var fresh = await db.Set<ProcessInstance>()
                .AsNoTracking()
                .Where(i => i.ID == instanceId)
                .Select(i => new { i.RowVer })
                .FirstOrDefaultAsync(ct);

            if (fresh == null)
                return (0, 0); // instance disappeared — caller handles

            rowVer = fresh.RowVer;
        }
        return (0, 0);
    }
}
