#nullable enable
// WorkflowTimerExecutor — Remind region (HandleRemindAsync + post-commit notify).
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
        var seqResult = await GuardedTransition.AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
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
}
