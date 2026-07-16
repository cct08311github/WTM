#nullable enable
// WorkflowTimerExecutor — Fire region (tick dispatch: FireDueTimersAsync + ProcessTimerAsync + its private helpers).
//
// #668: partial-class split of WorkflowTimerExecutor.cs — pure code motion (see
// WorkflowTimerExecutor.cs for the shared design notes and invariants). Members below were
// cut verbatim (including their original doc comments) from WorkflowTimerExecutor.cs; no
// signature, accessibility, or logic changes were made during the move.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

internal sealed partial class WorkflowTimerExecutor
{
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

    private TimerFireOutcome HandleUnknownAction(Guid timerId, TimerAction action)
    {
        _logger.LogError(
            "Timer {TimerId} has unknown TimerAction={Action} — treating as FailClosed",
            timerId, action);
        return TimerFireOutcome.FailClosed;
    }

    // ── Orphan retirement ─────────────────────────────────────────────────────

    private async Task RetireOrphanAsync(DbContext db, Guid timerId, uint timerRowVer, CancellationToken ct)
    {
        // Minimal fire CAS — flips timer to Fired, zero downstream side-effects.
        // rows==0 is fine: another host already retired it.
        await GuardedTransition.FireTimerAsync(db, timerId, timerRowVer, ct);
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
}
