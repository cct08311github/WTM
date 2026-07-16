#nullable enable
// WorkflowEngine — System region (SystemClaimTaskAsync / SystemContinueTaskAsync for timer auto-actions).
//
// #668: partial-class split of WorkflowEngine.cs — pure code motion (see WorkflowEngine.cs
// for the shared design notes, invariants, and race-condition catalogue). Members below were
// cut verbatim (including their original doc comments) from WorkflowEngine.cs; no signature,
// accessibility, or logic changes were made during the move.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

internal sealed partial class WorkflowEngine
{
    // ── WF-20.4: SystemClaimTaskAsync / SystemContinueTaskAsync ──────────────────
    //
    // FIX-C: split the original monolithic auto-action into two seams so the
    // per-timer fire transaction contains ONLY the claims + event rows (fast, bounded,
    // no HTTP, no long lock spans), and the continuation (AdvanceAsync recursion, timer
    // re-arm, WF-15 notifier calls) runs POST-COMMIT.
    //
    // Lock-order rule (§0 invariant, new for every timer-path transaction):
    //   WorkflowTimer → ApprovalTask → NodeInstance → ProcessInstance(Seq)
    // Inside the fire txn, task-claim CASes (ApprovalTask writes) come before the Seq
    // counter increment (ProcessInstance write) — this matches the Wave-4 DelegateTaskAsync
    // order (task → node-epoch → instance-Seq) and avoids ABBA deadlock with that path.

    /// <summary>
    /// Context returned by <see cref="SystemClaimTaskAsync"/> when the claim CAS succeeds.
    /// Carries the snapshots needed by <see cref="SystemContinueTaskAsync"/> to drive the
    /// post-commit continuation without re-reading (already locked by the committed claim).
    /// </summary>
    internal sealed record SystemClaimContext(
        Guid TaskId,
        string AssigneeITCode,
        TaskState NextState,
        ApproveMode ApproveMode,
        NodeInstance NodeInst,
        ProcessInstance Instance);

    /// <summary>
    /// IN-TXN part of the system auto-action: claims the task via the byte-identical
    /// <see cref="GuardedTransition.ClaimApprovalTaskAsync"/> predicate
    /// (State==Pending + RowVer + Generation==timerGeneration) and appends the
    /// <c>TimeoutFire</c> event row.
    ///
    /// <para>MUST be called inside an open transaction.  The caller (WorkflowTimerExecutor)
    /// commits the transaction AFTER all per-drain-iteration claims are complete, then
    /// calls <see cref="SystemContinueTaskAsync"/> post-commit per claimed task.</para>
    ///
    /// <para>Lock-order: ApprovalTask (this CAS) → ProcessInstance (Seq counter).
    /// WorkflowTimer is already held by the executor's outer fire CAS (first in order §0).</para>
    /// </summary>
    /// <returns>
    /// <c>null</c> when rows==0 (human or concurrent auto-action won — claim lost; no event written).
    /// Otherwise a <see cref="SystemClaimContext"/> that must be passed to
    /// <see cref="SystemContinueTaskAsync"/> after the transaction commits.
    /// </returns>
    internal async Task<SystemClaimContext?> SystemClaimTaskAsync(
        Guid taskId,
        uint taskRowVer,
        TaskState nextState,
        uint timerGeneration,
        string assigneeITCode,
        DateTime now,
        NodeInstance nodeInst,
        ProcessInstance instance,
        CancellationToken ct)
    {
        // CAS: claim the task as AutoApproved or AutoRejected.
        // Predicate is byte-identical to the human standard path: State==Pending + RowVer + Generation.
        // (No AtAction delegation window — system actors bypass delegation semantics by design.)
        var comment = nextState == TaskState.AutoApproved
            ? "timeout:auto-approve"
            : "timeout:auto-reject";

        var claimedRows = await GuardedTransition.ClaimApprovalTaskAsync(
            Db, taskId,
            expectedRowVer: taskRowVer,
            nextState: nextState,
            actedAtUtc: now,
            comment: comment,
            generation: timerGeneration,
            ct: ct);

        if (claimedRows == 0)
        {
            // Human actor (or concurrent auto-action) already claimed this task — silent no-op.
            _logger.LogDebug(
                "SystemClaimTaskAsync: task {TaskId} CAS returned 0 rows — already handled (human or concurrent auto-action won).",
                taskId);
            return null;
        }

        // Write TimeoutFire event: ActorITCode=NULL (system), OnBehalfOf=assignee.
        // One row per claimed task (design §5 §6 R6 "no double-count").
        // Lock-order §0: ApprovalTask claim (above) → ProcessInstance Seq (here) — matches Wave-4 delegate order.
        var seqResult = await GuardedTransition.AllocateSeqWithRetryAsync(Db, instance.ID, instance.RowVer, ct);
        if (seqResult.rows == 1)
        {
            Db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
            {
                ID               = Guid.NewGuid(),
                TenantCode       = instance.TenantCode,
                InstanceId       = instance.ID,
                Seq              = seqResult.seq,
                Action           = EventAction.TimeoutFire,
                NodeKey          = nodeInst.NodeKey,
                ActorITCode      = null,            // system action (no human actor)
                OnBehalfOfITCode = assigneeITCode,  // the assignee on whose behalf the system acts
                BeforeState      = TaskState.Pending.ToString(),
                AfterState       = nextState.ToString(),
                Reason           = comment,
                Generation       = (int?)timerGeneration,
                OccurredUtc      = now,
            });
            await Db.SaveChangesAsync(ct);
        }
        else
        {
            _logger.LogWarning(
                "SystemClaimTaskAsync: Seq allocation failed for instance {InstanceId} (task {TaskId}) — " +
                "TimeoutFire event not appended (audit gap, not a correctness issue).",
                instance.ID, taskId);
        }

        var approveMode = nodeInst.ApproveMode ?? ApproveMode.Sequential;
        return new SystemClaimContext(taskId, assigneeITCode, nextState, approveMode, nodeInst, instance);
    }

    /// <summary>
    /// POST-COMMIT continuation for a successful <see cref="SystemClaimTaskAsync"/> claim.
    /// Runs the same post-claim completion logic as the human approve/reject path
    /// (increment → handler TryComplete → CompleteNodeInstanceAsync → AdvanceAsync recursion).
    ///
    /// <para>MUST be called after the fire transaction has been committed.
    /// A failure here does NOT undo the committed claim — the tasks are already in
    /// AutoApproved/AutoRejected state.  The caller wraps each invocation in a per-task
    /// try/catch + LogError so one continuation failure never blocks the rest of the batch
    /// (documented crash-profile limitation: an Activated node with zero Pending tasks
    /// is re-driven by Phase-4 (ReDriveStrandedSequentialNodesAsync)).</para>
    ///
    /// <para>writeNodeCompletionEvent=false: the TimeoutFire event written by
    /// <see cref="SystemClaimTaskAsync"/> IS the per-task audit record; per-task Reject
    /// events in non-final All/Any paths would double-count.</para>
    /// </summary>
    internal async Task<WorkflowActionResult> SystemContinueTaskAsync(
        SystemClaimContext ctx,
        CancellationToken ct)
    {
        if (ctx.NextState == TaskState.AutoApproved)
        {
            return await ExecuteApproveCompletionAsync(ctx.TaskId, ctx.NodeInst, ctx.Instance, ctx.ApproveMode, ct);
        }
        else
        {
            // AutoReject: routes through handlers — Any-unanimity and All-RejectGate respected.
            return await ExecuteRejectCompletionAsync(
                ctx.TaskId, ctx.NodeInst, ctx.Instance,
                new ApprovalTask { ID = ctx.TaskId, AssigneeITCode = ctx.AssigneeITCode },
                actorITCode: null,
                reason: ctx.NextState == TaskState.AutoRejected ? "timeout:auto-reject" : "timeout:auto-approve",
                rejectMode: ctx.ApproveMode,
                writeNodeCompletionEvent: false,
                ct: ct);
        }
    }
}
