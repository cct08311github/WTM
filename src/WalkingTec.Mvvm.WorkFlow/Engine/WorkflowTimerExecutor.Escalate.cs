#nullable enable
// WorkflowTimerExecutor — Escalate region (HandleEscalateAsync + post-commit notify).
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
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

internal sealed partial class WorkflowTimerExecutor
{
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
            var seqNs = await GuardedTransition.AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
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
            var seqGate = await GuardedTransition.AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
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
            var seqFc = await GuardedTransition.AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
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
            var seqColl = await GuardedTransition.AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
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
        var seqEsc = await GuardedTransition.AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
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
}
