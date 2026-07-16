#nullable enable
// WorkflowEngine — Reject region (RejectTaskAsync + sequential-atomic + completion helpers).
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
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

internal sealed partial class WorkflowEngine
{
    // ── RejectTaskAsync ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<WorkflowActionResult> RejectTaskAsync(
        Guid taskId,
        string actorITCode,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorITCode))
            throw new ArgumentException("actorITCode must not be empty.", nameof(actorITCode));

        // 1. Load task.
        var task = await Db.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.ID == taskId && t.IsValid == true, ct);

        if (task is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"ApprovalTask {taskId} not found.");

        // 2. Load node instance.
        var nodeInst = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == task.NodeInstanceId, ct);

        if (nodeInst is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"NodeInstance {task.NodeInstanceId} not found.");

        // 3. Load process instance.
        var instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.ID == nodeInst.InstanceId && p.IsValid == true, ct);

        if (instance is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"ProcessInstance for NodeInstance {nodeInst.ID} not found.");

        // 4. Guard: node must be Activated.
        if (nodeInst.State != NodeState.Activated)
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"NodeInstance {nodeInst.ID} is in state {nodeInst.State}, not Activated.");
        }

        // 5. Early-act guard: actor must be the assignee.
        if (!string.Equals(task.AssigneeITCode, actorITCode, StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"Actor '{actorITCode}' is not the assignee '{task.AssigneeITCode}' of task {taskId}.");
        }

        // 6. Guard: task must be Pending.
        if (task.State != TaskState.Pending)
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.AlreadyHandled,
                $"Task {taskId} is in state {task.State}, not Pending.");
        }

        // 7. Sequential-only guard: SequencePointer match (All/Any tasks are all Pending in parallel).
        var rejectMode = nodeInst.ApproveMode ?? ApproveMode.Sequential;
        if (rejectMode == ApproveMode.Sequential)
        {
            if (task.SequenceOrder != nodeInst.SequencePointer)
            {
                return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                    $"Task {taskId} SequenceOrder {task.SequenceOrder} does not match " +
                    $"node pointer {nodeInst.SequencePointer}. Early-act rejected.");
            }
        }

        var now = DateTime.UtcNow;

        // 8+9. Sequential mode: atomic claim + node/instance rejection in ONE transaction (WF-373).
        //      All/Any mode: standalone claim CAS below (unchanged pre-WF-373 path).
        if (rejectMode == ApproveMode.Sequential)
        {
            bool isAtActionDelegatedSeqReject = _options.DelegationWindowMode == DelegationWindowMode.AtAction
                                                && task.DelegationExpiresUtc.HasValue;
            var seqRejectResult = await ExecuteSequentialRejectAtomicAsync(
                taskId,
                isAtActionDelegated: isAtActionDelegatedSeqReject,
                now: now,
                reason: reason,
                nodeInst: nodeInst,
                instance: instance,
                task: task,
                actorITCode: actorITCode,
                ct: ct);

            if (seqRejectResult.Code == WorkflowActionCode.DeadlockRetryExhausted)
                return seqRejectResult;

            // AtAction disambiguate: if the atomic helper returns AlreadyHandled and we
            // were in AtAction mode, check if the real reason is DelegationExpired.
            // Mirrors the original standalone-CAS disambiguation in the All/Any path below.
            if (seqRejectResult.IsAlreadyHandled && isAtActionDelegatedSeqReject)
            {
                var freshForExpiry = await Db.Set<ApprovalTask>()
                    .AsNoTracking()
                    .Select(t => new { t.ID, t.State, t.DelegationExpiresUtc })
                    .SingleOrDefaultAsync(t => t.ID == taskId, ct);

                if (freshForExpiry is not null
                    && freshForExpiry.State == TaskState.Pending
                    && freshForExpiry.DelegationExpiresUtc.HasValue
                    && now > freshForExpiry.DelegationExpiresUtc.Value)
                {
                    _logger.LogWarning(
                        "RejectTaskAsync (Sequential WF-373): task {TaskId} AtAction window expired at {Expiry} (now={Now}). " +
                        "Task stays Pending; manual reassignment or revoke required.",
                        taskId, freshForExpiry.DelegationExpiresUtc.Value, now);
                    return WorkflowActionResult.DelegationExpired;
                }
            }

            return seqRejectResult;
        }

        // 8. CAS: claim the task as Rejected.
        // FIX-3: AtAction window applies to ALL actions by the delegatee, not just Approve.
        // Under AtAction, an expired delegatee must NOT be able to reject (which in 会签 can
        // complete-reject the node) — the delegation window is the delegatee's authority to act.
        // Mirror the ApproveTaskAsync AtAction routing pattern exactly.
        int claimedRows;
        bool isAtActionDelegatedReject = _options.DelegationWindowMode == DelegationWindowMode.AtAction
                                         && task.DelegationExpiresUtc.HasValue;

        if (isAtActionDelegatedReject)
        {
            // AtAction path: window check + state flip are atomic (same as Approve).
            claimedRows = await GuardedTransition.ClaimDelegatedTaskAsync(
                Db, taskId,
                expectedRowVer: task.RowVer,
                nextState: TaskState.Rejected,
                actedAtUtc: now,
                comment: reason,
                ct: ct);

            if (claimedRows == 0)
            {
                var freshTask = await Db.Set<ApprovalTask>()
                    .AsNoTracking()
                    .Select(t => new { t.ID, t.State, t.DelegationExpiresUtc })
                    .SingleOrDefaultAsync(t => t.ID == taskId, ct);

                if (freshTask is not null
                    && freshTask.State == TaskState.Pending
                    && freshTask.DelegationExpiresUtc.HasValue
                    && now > freshTask.DelegationExpiresUtc.Value)
                {
                    _logger.LogWarning(
                        "RejectTaskAsync: task {TaskId} AtAction window expired at {Expiry} (now={Now}). " +
                        "Task stays Pending; manual reassignment or revoke required.",
                        taskId, freshTask.DelegationExpiresUtc.Value, now);
                    return WorkflowActionResult.DelegationExpired;
                }

                _logger.LogDebug(
                    "RejectTaskAsync: task {TaskId} AtAction CAS returned 0 rows — already handled by concurrent actor.",
                    taskId);
                return WorkflowActionResult.AlreadyHandled;
            }

            // AtAction claim succeeded — stamp WindowVerifiedUtc for audit (non-guarded, audit-only).
            await Db.Set<ApprovalTask>()
                .Where(t => t.ID == taskId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.WindowVerifiedUtc, now),
                    ct);
        }
        else
        {
            // Standard path (AtAssignment default or no delegation window) — byte-identical to pre-Wave-4.
            claimedRows = await GuardedTransition.ClaimApprovalTaskAsync(
                Db, taskId,
                expectedRowVer: task.RowVer,
                nextState: TaskState.Rejected,
                actedAtUtc: now,
                comment: reason,
                ct: ct);

            if (claimedRows == 0)
            {
                _logger.LogDebug(
                    "RejectTaskAsync: task {TaskId} CAS returned 0 rows — already handled by concurrent actor.",
                    taskId);
                return WorkflowActionResult.AlreadyHandled;
            }
        }

        // 9. Mode-specific rejection logic — shared with SystemContinueTaskAsync (WF-20.4).
        return await ExecuteRejectCompletionAsync(task.ID, nodeInst, instance, task, actorITCode, reason, rejectMode, writeNodeCompletionEvent: true, ct);
    }

    // ── WF-373: Sequential reject atomic helper (claim + node completion in one tx) ──

    /// <summary>
    /// Atomic helper for the Sequential reject path: claim the task AND complete the
    /// node/instance rejection in ONE transaction (WF-373).
    /// </summary>
    private async Task<WorkflowActionResult> ExecuteSequentialRejectAtomicAsync(
        Guid taskId,
        bool isAtActionDelegated,
        DateTime now,
        string? reason,
        NodeInstance nodeInst,
        ProcessInstance instance,
        ApprovalTask task,
        string? actorITCode,
        CancellationToken ct)
    {
        return await ExecuteInTransactionAsync(async innerCt =>
        {
            // #667: see the equivalent comment in ExecuteSequentialApproveAtomicAsync — kept
            // as assert-no-ambient (never nested today) rather than retrofitted to own-or-enlist.
            if (Db.Database.CurrentTransaction is not null)
                throw new InvalidOperationException(
                    "WF-373 atomic reject helper must not be nested inside an ambient transaction.");

            await using var tx = await Db.Database.BeginTransactionAsync(innerCt);
            try
            {

            // Re-read task inside tx for idempotent re-entry guard.
            var freshTask = await Db.Set<ApprovalTask>()
                .AsNoTracking()
                .SingleOrDefaultAsync(t => t.ID == taskId, innerCt);

            if (freshTask is null || freshTask.State != TaskState.Pending)
            {
                _logger.LogDebug(
                    "ExecuteSequentialRejectAtomicAsync: task {TaskId} re-read state={State} (expected Pending) — AlreadyHandled.",
                    taskId, freshTask?.State);
                await tx.RollbackAsync(CancellationToken.None);
                return WorkflowActionResult.AlreadyHandled;
            }

            // Step 0 — CLAIM (ApprovalTask write INSIDE the tx — this is the key WF-373 fix).
            int claimedRows;
            if (isAtActionDelegated)
            {
                claimedRows = await GuardedTransition.ClaimDelegatedTaskAsync(
                    Db, taskId,
                    expectedRowVer: freshTask.RowVer,
                    nextState: TaskState.Rejected,
                    actedAtUtc: now,
                    comment: reason,
                    ct: innerCt);
            }
            else
            {
                claimedRows = await GuardedTransition.ClaimApprovalTaskAsync(
                    Db, taskId,
                    expectedRowVer: freshTask.RowVer,
                    nextState: TaskState.Rejected,
                    actedAtUtc: now,
                    comment: reason,
                    ct: innerCt);
            }

            if (claimedRows == 0)
            {
                _logger.LogDebug(
                    "ExecuteSequentialRejectAtomicAsync: task {TaskId} claim CAS returned 0 — AlreadyHandled.",
                    taskId);
                await tx.RollbackAsync(CancellationToken.None);
                return WorkflowActionResult.AlreadyHandled;
            }

            // HIGH-2: stamp WindowVerifiedUtc for AtAction claims (audit parity with All/Any path).
            // Non-guarded audit-only update; mirrors the post-claim stamp in RejectTaskAsync ~2194-2199.
            // Task-before-Node order: still an ApprovalTask write, before cancel and node-completion writes.
            if (isAtActionDelegated)
            {
                await Db.Set<ApprovalTask>()
                    .Where(t => t.ID == taskId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.WindowVerifiedUtc, now),
                        innerCt);
            }

            // Cancel remaining NotYetActive and AddedPending tasks (Task write — canonical order).
            await Db.Set<ApprovalTask>()
                .Where(t => t.NodeInstanceId == nodeInst.ID
                             && (t.State == TaskState.NotYetActive || t.State == TaskState.AddedPending))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.State, TaskState.Cancelled),
                    innerCt);

            // Complete the node as CompletedRejected (Node write, CAS on RowVer).
            var freshNode = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, innerCt);

            var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
                Db, nodeInst.ID,
                expectedRowVer: freshNode.RowVer,
                completedState: NodeState.CompletedRejected,
                decidedBy: actorITCode,
                ct: innerCt);

            if (completeRows == 0)
            {
                _logger.LogDebug(
                    "ExecuteSequentialRejectAtomicAsync: NodeInstance {NodeId} completion CAS returned 0 — AlreadyHandled.",
                    nodeInst.ID);
                await tx.RollbackAsync(CancellationToken.None);
                return WorkflowActionResult.AlreadyHandled;
            }

            // Node is CompletedRejected — continue in same tx to instance flip.
            // MEDIUM-3: pass freshTask (the in-tx re-read snapshot) rather than the stale outer `task`.
            // freshTask is AsNoTracking and carries task.ID which is all CompleteInstanceRejectionInTxAsync
            // needs for timer cancel and notify; using freshTask avoids any stale pre-tx field values.
            return await CompleteInstanceRejectionInTxAsync(
                tx, nodeInst, instance, freshTask, actorITCode, reason, writeNodeCompletionEvent: true, innerCt);

            } // end try
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, ct);
    }

    // ── WF-20.4: shared reject post-claim completion helper ───────────────────

    /// <summary>
    /// Mode-specific completion logic run after the ApprovalTask CAS claim succeeds for
    /// either a human reject or a system AutoReject.  Human path is byte-identical to
    /// the pre-WF-20.4 code (locked by the event-sequence snapshot test).
    ///
    /// <para><paramref name="writeNodeCompletionEvent"/> is <c>true</c> on the human path
    /// (writes the node-completion Reject event) and <c>false</c> on the system path
    /// where a <c>TimeoutFire</c> event was already written per claimed task.</para>
    ///
    /// <para>Issue #320 PR B: for the failing path (nodeFailed==true) each mode wraps
    /// node-completion writes AND the instance-flip in ONE transaction in canonical
    /// Task→Node→Instance order, eliminating the gap between NodeState.CompletedRejected
    /// and InstanceState.Rejected that could strand an instance if the process crashed
    /// between the two separate commits.</para>
    /// </summary>
    private async Task<WorkflowActionResult> ExecuteRejectCompletionAsync(
        Guid claimedTaskId,
        NodeInstance nodeInst,
        ProcessInstance instance,
        ApprovalTask task,
        string? actorITCode,
        string? reason,
        ApproveMode rejectMode,
        bool writeNodeCompletionEvent,
        CancellationToken ct)
    {
        if (rejectMode == ApproveMode.All)
        {
            // 会签: increment advisory rejected count STANDALONE (must survive non-failing returns).
            await GuardedTransition.IncrementNodeRejectedCountAsync(Db, nodeInst.ID, ct);
            var freshNodeAll = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, ct);

            // Open one transaction for the failing path.
            // TryCompleteRejectedAsync is called INSIDE the tx: if it returns false (gate not met
            // or CAS lost), we rollback the tx (reverting any task cancels it may have written).
            // #667: routed through ExecuteInTransactionAsync — was a raw, unretried
            // BeginTransactionAsync; a deadlock/transient victim here previously threw a raw
            // provider exception to the caller.
            return await ExecuteInTransactionAsync(async innerCt =>
            {
                await using var txRejectAll = await Db.Database.BeginTransactionAsync(innerCt);
                try
                {
                    var nodeFailed = await AllApprovalHandler.TryCompleteRejectedAsync(
                        Db, freshNodeAll, actorITCode ?? string.Empty, _logger, innerCt);

                    if (!nodeFailed)
                    {
                        // Gate not met (AfterAll) or CAS lost: rollback (nothing harmful committed).
                        await txRejectAll.RollbackAsync(CancellationToken.None);

                        // Write standalone Reject event (node continues, not failing).
                        if (writeNodeCompletionEvent)
                        {
                            await WorkflowEventLogWriter.AppendAsync(
                                Db, instance.ID, instance.TenantCode,
                                EventAction.Reject,
                                nodeKey: nodeInst.NodeKey,
                                actorITCode: actorITCode,
                                beforeState: TaskState.Pending.ToString(),
                                afterState: TaskState.Rejected.ToString(),
                                reason: reason,
                                ct: innerCt);
                        }
                        return WorkflowActionResult.Advanced;
                    }

                    // Node is now CompletedRejected inside this tx — continue to instance flip.
                    return await CompleteInstanceRejectionInTxAsync(
                        txRejectAll, nodeInst, instance, task, actorITCode, reason, writeNodeCompletionEvent, innerCt);
                }
                catch
                {
                    await txRejectAll.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }, ct);
        }
        else if (rejectMode == ApproveMode.Any)
        {
            // 或签: single reject does NOT fail node; only last-reject does.
            await GuardedTransition.IncrementNodeRejectedCountAsync(Db, nodeInst.ID, ct);
            var freshNodeAny = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, ct);

            // #667: routed through ExecuteInTransactionAsync — was a raw, unretried
            // BeginTransactionAsync; a deadlock/transient victim here previously threw a raw
            // provider exception to the caller.
            return await ExecuteInTransactionAsync(async innerCt =>
            {
                await using var txRejectAny = await Db.Database.BeginTransactionAsync(innerCt);
                try
                {
                    var nodeFailed = await AnyApprovalHandler.TryCompleteRejectedAsync(
                        Db, freshNodeAny, actorITCode ?? string.Empty, _logger, innerCt);

                    if (!nodeFailed)
                    {
                        // More approvers still pending — rollback and let node continue.
                        await txRejectAny.RollbackAsync(CancellationToken.None);

                        if (writeNodeCompletionEvent)
                        {
                            await WorkflowEventLogWriter.AppendAsync(
                                Db, instance.ID, instance.TenantCode,
                                EventAction.Reject,
                                nodeKey: nodeInst.NodeKey,
                                actorITCode: actorITCode,
                                beforeState: TaskState.Pending.ToString(),
                                afterState: TaskState.Rejected.ToString(),
                                reason: reason,
                                ct: innerCt);
                        }
                        return WorkflowActionResult.Advanced;
                    }

                    // All have rejected — continue in same tx to instance flip.
                    return await CompleteInstanceRejectionInTxAsync(
                        txRejectAny, nodeInst, instance, task, actorITCode, reason, writeNodeCompletionEvent, innerCt);
                }
                catch
                {
                    await txRejectAny.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }, ct);
        }
        else
        {
            // Sequential path ─────────────────────────────────────────────────────
            // Canonical order inside the tx: Task cancels → Node CAS → Instance flip.

            // #667: routed through ExecuteInTransactionAsync — was a raw, unretried
            // BeginTransactionAsync; a deadlock/transient victim here previously threw a raw
            // provider exception to the caller.
            return await ExecuteInTransactionAsync(async innerCt =>
            {
                await using var txRejectSeq = await Db.Database.BeginTransactionAsync(innerCt);
                try
                {
                    // Cancel remaining NotYetActive and AddedPending tasks (Task write FIRST).
                    // AddedPending (加签-injected tasks not yet reached by the SequencePointer)
                    // must also be cancelled; omitting them leaves un-actionable dangling
                    // rows on a CompletedRejected node (C11 fix).
                    await Db.Set<ApprovalTask>()
                        .Where(t => t.NodeInstanceId == nodeInst.ID
                                     && (t.State == TaskState.NotYetActive || t.State == TaskState.AddedPending))
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(t => t.State, TaskState.Cancelled),
                            innerCt);

                    // Complete the node as CompletedRejected (Node write SECOND, CAS on RowVer).
                    var freshNode = await Db.Set<NodeInstance>()
                        .AsNoTracking()
                        .SingleAsync(n => n.ID == nodeInst.ID, innerCt);

                    var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
                        Db, nodeInst.ID,
                        expectedRowVer: freshNode.RowVer,
                        completedState: NodeState.CompletedRejected,
                        decidedBy: actorITCode,
                        ct: innerCt);

                    if (completeRows == 0)
                    {
                        _logger.LogDebug(
                            "ExecuteRejectCompletionAsync: NodeInstance {NodeId} completion CAS returned 0 — concurrent actor already completed.",
                            nodeInst.ID);
                        await txRejectSeq.RollbackAsync(CancellationToken.None);
                        return WorkflowActionResult.AlreadyHandled;
                    }

                    // Node is CompletedRejected — continue in same tx to instance flip.
                    return await CompleteInstanceRejectionInTxAsync(
                        txRejectSeq, nodeInst, instance, task, actorITCode, reason, writeNodeCompletionEvent, innerCt);
                }
                catch
                {
                    await txRejectSeq.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }, ct);
        }
    }

    /// <summary>
    /// Shared instance-rejection block: runs INSIDE an open transaction (<paramref name="tx"/>).
    /// Writes the node-completion event (when <paramref name="writeNodeCompletionEvent"/> is
    /// <c>true</c>), re-reads the instance for a fresh <c>RowVer</c>, flips
    /// <c>Running→Rejected</c> via CAS, writes the instance-reject event, commits, then
    /// runs post-commit timer cancels and the WF-15 notifier.
    ///
    /// <para>Canonical write order inside the tx: Node-completion Append → Instance CAS →
    /// Instance Append → Commit.  The instance re-read sits immediately before the CAS with
    /// no Append between them (avoids stale <c>RowVer</c> from NextSeq bump).</para>
    ///
    /// <para>Lock-order (§0 / Issue #320 PR B): Task→Node→Instance — all task and node
    /// writes are already in-flight inside the same transaction before this method is
    /// called.</para>
    /// </summary>
    private async Task<WorkflowActionResult> CompleteInstanceRejectionInTxAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        NodeInstance nodeInst,
        ProcessInstance instance,
        ApprovalTask task,
        string? actorITCode,
        string? reason,
        bool writeNodeCompletionEvent,
        CancellationToken ct)
    {
        var rejectPolicy = nodeInst.RejectPolicy;

        // Write node-completion Reject event (inside tx — rolled back if instance CAS fails).
        if (writeNodeCompletionEvent)
        {
            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.Reject,
                nodeKey: nodeInst.NodeKey,
                actorITCode: actorITCode,
                beforeState: NodeState.Activated.ToString(),
                afterState: NodeState.CompletedRejected.ToString(),
                reason: reason,
                ct: ct);
        }

        // Re-read instance for fresh RowVer immediately before the CAS.
        // NO AppendAsync between this re-read and the CAS (NextSeq bump would make RowVer stale).
        instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(x => x.ID == instance.ID, ct);

        // Instance Running→Rejected CAS (Instance write — LAST in canonical Task→Node→Instance order).
        var rejectRows = await GuardedTransition.AdvanceProcessInstanceAsync(
            Db, instance.ID,
            expectedState: InstanceState.Running,
            expectedRowVer: instance.RowVer,
            nextState: InstanceState.Rejected,
            ct);

        if (rejectRows == 0)
        {
            // CAS lost — another actor already flipped the instance.
            await tx.RollbackAsync(CancellationToken.None);
            return WorkflowActionResult.AlreadyHandled;
        }

        // Write instance-reject event (inside tx).
        await WorkflowEventLogWriter.AppendAsync(
            Db, instance.ID, instance.TenantCode,
            EventAction.Reject,
            nodeKey: nodeInst.NodeKey,
            actorITCode: actorITCode,
            beforeState: InstanceState.Running.ToString(),
            afterState: InstanceState.Rejected.ToString(),
            reason: $"Rejected by '{actorITCode}'. RejectPolicy={rejectPolicy}. {reason}",
            ct: ct);

        // Commit the transaction (Task cancels + Node CAS + events + Instance CAS all atomic).
        await tx.CommitAsync(ct);

        // ── Post-commit: timer cancels (WF-20.2) ─────────────────────────────────
        // WF-20.2: cancel task timer for the rejecting step (Sequential) + instance-wide hygiene.
        // For Sequential path: also cancel task-specific timer for the rejecting step.
        await GuardedTransition.CancelTimerForTaskAsync(Db, task.ID, ct);

        // Instance-wide timer cancel on terminal Rejected state (best-effort, post-commit).
        var rejectedNodeIds = await Db.Set<NodeInstance>()
            .Where(n => n.InstanceId == instance.ID)
            .Select(n => n.ID)
            .ToListAsync(ct);
        foreach (var nid in rejectedNodeIds)
            await GuardedTransition.CancelTimersForNodeAsync(Db, nid, ct);

        // WF-15 — Notify rejected (post-commit, best-effort).
        if (_notifier is not null)
        {
            try { await _notifier.NotifyRejectedAsync(instance, nodeInst, task, actorITCode ?? string.Empty, reason, ct); }
            catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyRejectedAsync failed for task {TaskId}.", task.ID); }
        }

        return WorkflowActionResult.Rejected;
    }
}
