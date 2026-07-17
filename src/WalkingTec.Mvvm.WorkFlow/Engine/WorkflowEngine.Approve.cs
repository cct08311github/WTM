#nullable enable
// WorkflowEngine — Approve region (ApproveTaskAsync + sequential-atomic + completion helper).
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
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

internal sealed partial class WorkflowEngine
{
    // ── ApproveTaskAsync ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<WorkflowActionResult> ApproveTaskAsync(
        Guid taskId,
        string actorITCode,
        string? comment = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorITCode))
            throw new ArgumentException("actorITCode must not be empty.", nameof(actorITCode));

        // 1. Load task (AsNoTracking — RowVer is re-checked at CAS time via GuardedTransition,
        //    not cached in the ChangeTracker; a fresh read here would still be stale by the
        //    time the CAS runs, so tracking would not help).
        var task = await Db.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.ID == taskId && t.IsValid == true, ct);

        if (task is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"ApprovalTask {taskId} not found.");

        // 2. Load the owning node instance.
        var nodeInst = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == task.NodeInstanceId, ct);

        if (nodeInst is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"NodeInstance {task.NodeInstanceId} not found.");

        // 3. Load the owning process instance.
        var instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.ID == nodeInst.InstanceId && p.IsValid == true, ct);

        if (instance is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"ProcessInstance for NodeInstance {nodeInst.ID} not found.");

        // 4. Guard: node must be Activated.
        //    Special case: concurrent All/Any races can result in the node being completed
        //    (CompletedApproved) and the instance Approved by a concurrent winner before
        //    this caller even checks.  Return AlreadyHandled rather than NodeClosed so that
        //    the caller knows the workflow completed successfully.
        if (nodeInst.State != NodeState.Activated)
        {
            var approveModePre = nodeInst.ApproveMode ?? ApproveMode.Sequential;
            if ((approveModePre == ApproveMode.All || approveModePre == ApproveMode.Any)
                && nodeInst.State == NodeState.CompletedApproved
                && instance.State == InstanceState.Approved)
            {
                return WorkflowActionResult.AlreadyHandled;
            }

            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"NodeInstance {nodeInst.ID} is in state {nodeInst.State}, not Activated.");
        }

        // 5. Early-act guard: actor must be the assignee of the task.
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

        // 7. Sequential-only guard: task.SequenceOrder must match nodeInst.SequencePointer (early-act).
        //    All/Any modes have all tasks Pending simultaneously — skip this guard for them.
        var approveMode = nodeInst.ApproveMode ?? ApproveMode.Sequential;
        if (approveMode == ApproveMode.Sequential)
        {
            if (task.SequenceOrder != nodeInst.SequencePointer)
            {
                return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                    $"Task {taskId} SequenceOrder {task.SequenceOrder} does not match " +
                    $"node pointer {nodeInst.SequencePointer}. Early-act rejected.");
            }
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // 8+10. Sequential mode: atomic claim + pointer-advance in ONE transaction (WF-373).
        //        All/Any mode: standalone claim CAS below (unchanged pre-WF-373 path).
        if (approveMode == ApproveMode.Sequential)
        {
            // WF-373: claim + mid-chain pointer-advance are now ONE atomic unit.
            var (atomicResult, atomicExtra) = await ExecuteSequentialApproveAtomicAsync(
                taskId,
                nextTaskState: TaskState.Approved,
                isAtActionDelegated: _options.DelegationWindowMode == DelegationWindowMode.AtAction && task.DelegationExpiresUtc.HasValue,
                now: now,
                comment: comment,
                nodeInst: nodeInst,
                instance: instance,
                ct: ct);

            if (atomicResult.Code == WorkflowActionCode.DeadlockRetryExhausted)
                return atomicResult;

            if (atomicResult.IsAlreadyHandled)
                return atomicResult;

            // Atomic helper committed — write Approve event post-commit (standalone).
            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.Approve,
                nodeKey: nodeInst.NodeKey,
                actorITCode: actorITCode,
                beforeState: TaskState.Pending.ToString(),
                afterState: TaskState.Approved.ToString(),
                reason: comment,
                timeProvider: _timeProvider,
                ct: ct);

            // WF-15 — Notify approved (post-commit, best-effort).
            if (_notifier is not null)
            {
                try { await _notifier.NotifyApprovedAsync(instance, nodeInst, task, actorITCode, ct); }
                catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyApprovedAsync (Sequential WF-373) failed for task {TaskId}.", taskId); }
            }

            if (atomicExtra.IsLastStep)
            {
                // Last step committed — call AdvanceAsync to route the engine onward.
                var seqResult = await AdvanceAsync(instance.ID, ct);
                // WF-15 — post-advance instance completion notification (best-effort).
                if (_notifier is not null)
                {
                    try
                    {
                        if (seqResult.Code == WorkflowActionCode.InstanceApproved)
                        {
                            var freshInst = await Db.Set<ProcessInstance>().AsNoTracking().SingleAsync(x => x.ID == instance.ID, CancellationToken.None);
                            await _notifier.NotifyInstanceCompletedAsync(freshInst, ct);
                        }
                        else
                        {
                            await NotifyFirstPendingTasksAsync(instance.ID, instance, ct);
                        }
                    }
                    catch (Exception ex) { _logger.LogError(ex, "WF-15 post-advance notification (Sequential WF-373) failed for instance {InstanceId}.", instance.ID); }
                }
                return seqResult;
            }
            else
            {
                // Mid-chain: pointer advanced, next step activated (or AutoApproved).

                // WF-20.2: cancel completing step's task timer; re-arm next step if configured.
                await GuardedTransition.CancelTimerForTaskAsync(Db, taskId, ct);
                if (_businessCalendar is not null)
                {
                    var freshNodeForPointer = await Db.Set<NodeInstance>()
                        .AsNoTracking()
                        .SingleAsync(n => n.ID == nodeInst.ID, ct);
                    var nextPointer = freshNodeForPointer.SequencePointer;
                    var stepBNodeDef = await LoadNodeDefAsync(instance.DefinitionVersionId, nodeInst.NodeKey, ct);
                    if (stepBNodeDef?.Timeout is not null)
                    {
                        var nextPendingForArm = await Db.Set<ApprovalTask>()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                t => t.NodeInstanceId == nodeInst.ID
                                     && t.SequenceOrder == nextPointer
                                     && t.State == TaskState.Pending,
                                ct);
                        if (nextPendingForArm is not null)
                        {
                            await ArmTaskTimerIfConfiguredAsync(
                                nextPendingForArm, freshNodeForPointer, stepBNodeDef, _timeProvider.GetUtcNow().UtcDateTime, ct);
                        }
                    }
                }

                if (atomicExtra.NextIsAutoApproved)
                {
                    // #361: bounded loop — advance pointer past consecutive mid-chain AutoApproved slots.
                    // The atomic helper advanced the pointer to an AutoApproved position; we must keep
                    // consuming AutoApproved slots until the pointer reaches a Pending task or the last step.
                    // Each iteration: if the current-pointer task is AutoApproved, the two writes (Step 1
                    // activate-next-task + Step 2 pointer CAS) run inside a per-iteration BeginTransactionAsync
                    // block (mirrors txSeqApproveMidChain pattern in ExecuteApproveCompletionAsync). If the
                    // CAS returns 0 rows, the tx is rolled back so the activate does not leave an orphan-Pending
                    // task at nextPtr. Bounded to MaxSteps=200 to prevent livelock on malformed data or
                    // TotalRequired holding an int.MaxValue "FailClose" sentinel.
                    const int MaxSteps = 200;
                    var freshNodeFor361 = await Db.Set<NodeInstance>()
                        .AsNoTracking()
                        .SingleAsync(n => n.ID == nodeInst.ID, ct);
                    int boundFor361 = Math.Min(Math.Max(freshNodeFor361.TotalRequired, 1), MaxSteps);

                    for (int iter361 = 0; iter361 < boundFor361; iter361++)
                    {
                        var freshNodeLoop = await Db.Set<NodeInstance>()
                            .AsNoTracking()
                            .SingleAsync(n => n.ID == nodeInst.ID, ct);

                        if (freshNodeLoop.SequencePointer >= freshNodeLoop.TotalRequired)
                        {
                            // Pointer reached the end — all steps done. Let AdvanceCoreAsync route onward.
                            return await AdvanceAsync(instance.ID, ct);
                        }

                        var loopTask = await Db.Set<ApprovalTask>()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                t => t.NodeInstanceId == freshNodeLoop.ID
                                     && t.SequenceOrder == freshNodeLoop.SequencePointer,
                                ct);

                        if (loopTask is null)
                        {
                            _logger.LogWarning(
                                "#361: no task found at SequencePointer {Ptr} for node {NodeId}. Breaking loop.",
                                freshNodeLoop.SequencePointer, freshNodeLoop.ID);
                            break;
                        }

                        if (loopTask.State == TaskState.Pending)
                        {
                            // Real approver task is Pending — Blocked is the correct result.
                            return WorkflowActionResult.Blocked;
                        }

                        if (loopTask.State != TaskState.AutoApproved)
                        {
                            _logger.LogWarning(
                                "#361: unexpected task state {State} at SequencePointer {Ptr} for node {NodeId}.",
                                loopTask.State, freshNodeLoop.SequencePointer, freshNodeLoop.ID);
                            break;
                        }

                        // Task is AutoApproved — activate the next task and advance the pointer by 1.
                        // Both writes run inside a per-iteration tx (mirrors txSeqApproveMidChain pattern).
                        // If the pointer CAS loses, the tx rolls back the activate → no orphan-Pending task.
                        // #667: routed through ExecuteInTransactionAsync — was a raw, unretried
                        // BeginTransactionAsync; a deadlock/transient victim here previously threw a
                        // raw provider exception to the caller.
                        var nextPtr361 = freshNodeLoop.SequencePointer + 1;
                        bool isLastStep361 = nextPtr361 >= freshNodeLoop.TotalRequired;

                        var tx361Result = await ExecuteInTransactionAsync(async innerCt361 =>
                        {
                            await using var tx361 = await Db.Database.BeginTransactionAsync(innerCt361);
                            try
                            {
                                // Step 1 (Task FIRST — canonical order): activate next task.
                                if (!isLastStep361)
                                {
                                    await Db.Set<ApprovalTask>()
                                        .Where(t => t.NodeInstanceId == freshNodeLoop.ID
                                                     && t.SequenceOrder == nextPtr361
                                                     && (t.State == TaskState.NotYetActive || t.State == TaskState.AddedPending))
                                        .ExecuteUpdateAsync(
                                            s => s.SetProperty(t => t.State, TaskState.Pending),
                                            innerCt361);
                                }

                                // Step 2 (Node SECOND): CAS pointer advance.
                                var advRows361 = await Db.Set<NodeInstance>()
                                    .Where(n => n.ID == freshNodeLoop.ID
                                                 && n.State == NodeState.Activated
                                                 && n.RowVer == freshNodeLoop.RowVer
                                                 && n.SequencePointer == freshNodeLoop.SequencePointer
                                                 && n.ApproverSetEpoch == freshNodeLoop.ApproverSetEpoch)
                                    .ExecuteUpdateAsync(
                                        s => s.SetProperty(n => n.SequencePointer, nextPtr361)
                                               .SetProperty(n => n.RowVer, x => x.RowVer + 1),
                                        innerCt361);

                                if (advRows361 == 0)
                                {
                                    // CAS lost — roll back the activate (no orphan-Pending task).
                                    await tx361.RollbackAsync(CancellationToken.None);
                                    _logger.LogDebug(
                                        "#361: pointer CAS returned 0 for node {NodeId} at AutoApproved step {Ptr} — rolling back activate, returning AlreadyHandled.",
                                        freshNodeLoop.ID, freshNodeLoop.SequencePointer);
                                    return WorkflowActionResult.AlreadyHandled;
                                }

                                await tx361.CommitAsync(innerCt361);
                                return WorkflowActionResult.NodeCompleted;
                            }
                            catch
                            {
                                await tx361.RollbackAsync(CancellationToken.None);
                                throw;
                            }
                        }, ct);

                        if (tx361Result.Code != WorkflowActionCode.NodeCompleted)
                            return tx361Result;

                        if (isLastStep361)
                        {
                            // All steps done — call AdvanceAsync to complete the node and route onward.
                            return await AdvanceAsync(instance.ID, ct);
                        }

                        // Pointer advanced — loop again to check if the new position is also AutoApproved.
                    }

                    // Loop exhausted without finding a Pending task or last step — call AdvanceAsync as fallback.
                    return await AdvanceAsync(instance.ID, ct);
                }

                // WF-15 — Notify the newly activated task assignee (post-commit, best-effort).
                if (_notifier is not null)
                {
                    try
                    {
                        var freshNodeForNotify = await Db.Set<NodeInstance>()
                            .AsNoTracking()
                            .SingleAsync(n => n.ID == nodeInst.ID, ct);
                        var nextPointerForNotify = freshNodeForNotify.SequencePointer;
                        var nextPendingTask = await Db.Set<ApprovalTask>()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == nextPointerForNotify && t.State == TaskState.Pending,
                                ct);
                        if (nextPendingTask is not null)
                            await _notifier.NotifyTaskAssignedAsync(instance, freshNodeForNotify, nextPendingTask, ct);
                    }
                    catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyTaskAssignedAsync (Sequential WF-373) failed for instance {InstanceId}.", instance.ID); }
                }
                return WorkflowActionResult.Advanced;
            }
        }

        // 8. CAS: claim the task as Approved.
        //    WF-19 #284.4 — AtAction routing:
        //    When DelegationWindowMode==AtAction AND the task has a delegation window, route
        //    through ClaimDelegatedTaskAsync which folds the window into the CAS predicate.
        //    This is the ONLY code path that should use ClaimDelegatedTaskAsync — do NOT touch
        //    any other claim site (RejectTaskAsync, ReturnToPrevAsync, etc.) for AtAction.
        int claimedRows;
        bool isAtActionDelegated = _options.DelegationWindowMode == DelegationWindowMode.AtAction
                                   && task.DelegationExpiresUtc.HasValue;

        if (isAtActionDelegated)
        {
            // AtAction path: window check + state flip are atomic (FIX-D).
            claimedRows = await GuardedTransition.ClaimDelegatedTaskAsync(
                Db, taskId,
                expectedRowVer: task.RowVer,
                nextState: TaskState.Approved,
                actedAtUtc: now,
                comment: comment,
                ct: ct);

            if (claimedRows == 0)
            {
                // Disambiguate: expired window vs. already handled by concurrent actor.
                // Follow-up no-side-effect read to check why (design §4 R3).
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
                        "ApproveTaskAsync: task {TaskId} AtAction window expired at {Expiry} (now={Now}). " +
                        "Task stays Pending; manual reassignment or revoke required.",
                        taskId, freshTask.DelegationExpiresUtc.Value, now);
                    return WorkflowActionResult.DelegationExpired;
                }

                _logger.LogDebug(
                    "ApproveTaskAsync: task {TaskId} AtAction CAS returned 0 rows — already handled by concurrent actor.",
                    taskId);
                return WorkflowActionResult.AlreadyHandled;
            }

            // AtAction claim succeeded — stamp WindowVerifiedUtc for audit (non-guarded, audit-only).
            // The spec says this field is NEVER used in any predicate; a separate non-guarded update is acceptable.
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
                nextState: TaskState.Approved,
                actedAtUtc: now,
                comment: comment,
                ct: ct);

            if (claimedRows == 0)
            {
                _logger.LogDebug(
                    "ApproveTaskAsync: task {TaskId} CAS returned 0 rows — already handled by concurrent actor.",
                    taskId);
                return WorkflowActionResult.AlreadyHandled;
            }
        }

        // 9. T5 fix (W-ALL-CLAIM-TO-INCREMENT): for All/Any modes, wrap the claim +
        //    IncrementNodeApprovedCount + Approve event in ONE transaction so a crash cannot
        //    leave the task Approved with the advisory count un-incremented (threshold strand).
        //    The tx COMMITS here, before AdvanceWithActorAsync, so no nested Begin is possible
        //    through the drain. Sequential mode is unchanged (no increment, no tx wrapper needed).
        //    Nested-tx guard: if an ambient tx exists (e.g. AutoApprove recursion), enlist;
        //    otherwise open+own+commit — mirroring WorkflowEventLogWriter.cs:99.
        //    #667 fix: this used to be a BARE, unretried BeginTransactionAsync — NOT routed
        //    through ExecuteInTransactionAsync and NOT in the documented exclusion set
        //    (Return txA/txB, lease reaper). Under a host-configured retrying execution
        //    strategy (EnableRetryOnFailure), that bare call threw
        //    InvalidOperationException("...does not support user-initiated transactions...")
        //    on every All/Any approval — AFTER the claim CAS above had already committed the
        //    task as Approved, so the throw skipped IncrementNodeApprovedCountAsync and left
        //    the node's advisory ApprovedCount under-incremented (会签/或签 quorum corrupted).
        //    Routed through ExecuteInTransactionAsync so the transaction boundary is always
        //    sanctioned. The own-or-enlist body below is UNCHANGED: per EF Core's
        //    ExecutionStrategy.ExecuteAsync (AsyncLocal `Current` marker), a call nested
        //    inside an already-running ExecuteInTransactionAsync/strategy.ExecuteAsync
        //    skips the existing-transaction guard and runs the body once, in-line, deferring
        //    retry to the outer envelope — so the AutoApprove-recursion enlist case keeps
        //    working exactly as before.
        if (approveMode == ApproveMode.All || approveMode == ApproveMode.Any)
        {
            var claimIncrementResult = await ExecuteInTransactionAsync(async innerCt =>
            {
                bool ownsTx = Db.Database.CurrentTransaction is null;
                IDbContextTransaction? claimTx = ownsTx
                    ? await Db.Database.BeginTransactionAsync(innerCt)
                    : null;
                try
                {
                    // Increment advisory count (Task→Node canonical order; claim already committed above).
                    await GuardedTransition.IncrementNodeApprovedCountAsync(Db, nodeInst.ID, innerCt);

                    // Approve event enlists in the ambient tx (WorkflowEventLogWriter nested-tx guard).
                    await WorkflowEventLogWriter.AppendAsync(
                        Db, instance.ID, instance.TenantCode,
                        EventAction.Approve,
                        nodeKey: nodeInst.NodeKey,
                        actorITCode: actorITCode,
                        beforeState: TaskState.Pending.ToString(),
                        afterState: TaskState.Approved.ToString(),
                        reason: comment,
                        timeProvider: _timeProvider,
                        ct: innerCt);

                    if (ownsTx)
                        await claimTx!.CommitAsync(innerCt);

                    return WorkflowActionResult.Advanced;
                }
                catch when (ownsTx && claimTx is not null)
                {
                    await claimTx.RollbackAsync(CancellationToken.None);
                    throw;
                }
                finally
                {
                    if (ownsTx)
                        claimTx?.Dispose();
                }
            }, ct);

            // Deadlock retry exhausted — surface the closed result code instead of continuing
            // into completion logic with an under-incremented (or double-incremented) count.
            if (claimIncrementResult.Code == WorkflowActionCode.DeadlockRetryExhausted)
                return claimIncrementResult;
        }
        else
        {
            // Sequential: write Approve event standalone (unchanged pre-T5 behaviour).
            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.Approve,
                nodeKey: nodeInst.NodeKey,
                actorITCode: actorITCode,
                beforeState: TaskState.Pending.ToString(),
                afterState: TaskState.Approved.ToString(),
                reason: comment,
                timeProvider: _timeProvider,
                ct: ct);
        }

        // WF-15 — Notify approved (post-commit, best-effort).  Fires for every successful
        // approval regardless of mode; the node/instance-completion notification fires later
        // based on the final result of mode-specific processing below.
        if (_notifier is not null)
        {
            try { await _notifier.NotifyApprovedAsync(instance, nodeInst, task, actorITCode, ct); }
            catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyApprovedAsync failed for task {TaskId}.", taskId); }
        }

        // 10. Mode-specific completion logic — shared with SystemContinueTaskAsync (WF-20.4).
        // C10 fix: pass actorITCode so All/Any completion can stamp DecidedBy on NodeInstance.
        // T5 fix: pass incrementAlreadyDone=true for All/Any (increment is already committed above);
        //         the system path (SystemContinueTaskAsync) always passes default false.
        return await ExecuteApproveCompletionAsync(
            task.ID, nodeInst, instance, approveMode, ct, actorITCode,
            incrementAlreadyDone: approveMode == ApproveMode.All || approveMode == ApproveMode.Any);
    }

    // ── WF-373: Sequential approve atomic helper (claim + pointer-advance in one tx) ──

    /// <summary>
    /// Atomic helper for the Sequential approve path: claim the task AND advance the
    /// SequencePointer in ONE transaction, eliminating the crash window between a
    /// standalone claim CAS and the separate pointer-advance tx (WF-373).
    /// </summary>
    private async Task<(WorkflowActionResult result, SequentialAtomicExtra extra)>
        ExecuteSequentialApproveAtomicAsync(
            Guid taskId,
            TaskState nextTaskState,
            bool isAtActionDelegated,
            DateTime now,
            string? comment,
            NodeInstance nodeInst,
            ProcessInstance instance,
            CancellationToken ct)
    {
        var defaultExtra = new SequentialAtomicExtra(IsLastStep: false, NextIsAutoApproved: false);

        return await ExecuteInTransactionAsync(async innerCt =>
        {
            // #667: this helper is never called while an ambient transaction is open (verified:
            // no call site nests it) so the pre-#667 assert-no-ambient invariant is kept as-is
            // rather than retrofitted to own-or-enlist — doing so would require guarding every
            // tx.CommitAsync/RollbackAsync call in this body with "if (ownsTx)", and getting even
            // one of those guards wrong would silently commit/rollback a caller's ambient
            // transaction. Not worth the risk for a path that is unreachable today. If a future
            // change genuinely needs to nest this helper, convert it deliberately then, with a
            // dedicated test proving every Commit/Rollback branch respects ownership.
            if (Db.Database.CurrentTransaction is not null)
                throw new InvalidOperationException(
                    "WF-373 atomic helper must not be nested inside an ambient transaction.");

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
                    "ExecuteSequentialApproveAtomicAsync: task {TaskId} re-read state={State} (expected Pending) — AlreadyHandled.",
                    taskId, freshTask?.State);
                await tx.RollbackAsync(CancellationToken.None);
                return (WorkflowActionResult.AlreadyHandled, defaultExtra);
            }

            // Step 0 — CLAIM (ApprovalTask write, INSIDE the tx — this is the key fix).
            int claimedRows;
            if (isAtActionDelegated)
            {
                claimedRows = await GuardedTransition.ClaimDelegatedTaskAsync(
                    Db, taskId,
                    expectedRowVer: freshTask.RowVer,
                    nextState: nextTaskState,
                    actedAtUtc: now,
                    comment: comment,
                    ct: innerCt);
            }
            else
            {
                claimedRows = await GuardedTransition.ClaimApprovalTaskAsync(
                    Db, taskId,
                    expectedRowVer: freshTask.RowVer,
                    nextState: nextTaskState,
                    actedAtUtc: now,
                    comment: comment,
                    ct: innerCt);
            }

            if (claimedRows == 0)
            {
                _logger.LogDebug(
                    "ExecuteSequentialApproveAtomicAsync: task {TaskId} claim CAS returned 0 — AlreadyHandled.",
                    taskId);
                await tx.RollbackAsync(CancellationToken.None);
                return (WorkflowActionResult.AlreadyHandled, defaultExtra);
            }

            // HIGH-2: stamp WindowVerifiedUtc for AtAction claims (audit parity with All/Any path).
            // Non-guarded audit-only update; mirrors the post-claim stamp in ApproveTaskAsync ~1447-1451.
            // Task-before-Node order: this is still an ApprovalTask write, before any NodeInstance write.
            if (isAtActionDelegated)
            {
                await Db.Set<ApprovalTask>()
                    .Where(t => t.ID == taskId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.WindowVerifiedUtc, now),
                        innerCt);
            }

            // Read the node's current pointer state (inside tx).
            var freshNodeForPointer = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, innerCt);

            if (freshNodeForPointer.State != NodeState.Activated)
            {
                _logger.LogDebug(
                    "ExecuteSequentialApproveAtomicAsync: NodeInstance {NodeId} state={State} (expected Activated) — AlreadyHandled.",
                    nodeInst.ID, freshNodeForPointer.State);
                await tx.RollbackAsync(CancellationToken.None);
                return (WorkflowActionResult.AlreadyHandled, defaultExtra);
            }

            var nextPointer = freshNodeForPointer.SequencePointer + 1;
            var totalRequired = freshNodeForPointer.TotalRequired;

            if (nextPointer < totalRequired)
            {
                // Mid-chain: more steps remain.

                // Step 1 — ApprovalTask write (Task-before-Node lock order).
                var activateRows = await Db.Set<ApprovalTask>()
                    .Where(t => t.NodeInstanceId == nodeInst.ID
                                 && t.SequenceOrder == nextPointer
                                 && (t.State == TaskState.NotYetActive || t.State == TaskState.AddedPending))
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.State, TaskState.Pending),
                        innerCt);

                // Re-read node to get fresh RowVer for the CAS (inside tx).
                var freshNodeForCas = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleAsync(n => n.ID == nodeInst.ID, innerCt);

                if (freshNodeForCas.State != NodeState.Activated)
                {
                    _logger.LogDebug(
                        "ExecuteSequentialApproveAtomicAsync: NodeInstance {NodeId} became {State} before pointer advance — AlreadyHandled.",
                        nodeInst.ID, freshNodeForCas.State);
                    await tx.RollbackAsync(CancellationToken.None);
                    return (WorkflowActionResult.AlreadyHandled, defaultExtra);
                }

                // Step 2 — NodeInstance pointer CAS (AFTER the task write — Task-before-Node order).
                // WF-18 FIX-F: carry ApproverSetEpoch guard VERBATIM.
                var advanceRows = await Db.Set<NodeInstance>()
                    .Where(n => n.ID == nodeInst.ID
                                 && n.State == NodeState.Activated
                                 && n.RowVer == freshNodeForCas.RowVer
                                 && n.SequencePointer == freshNodeForCas.SequencePointer
                                 && n.ApproverSetEpoch == freshNodeForCas.ApproverSetEpoch)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(n => n.SequencePointer, nextPointer)
                               .SetProperty(n => n.RowVer, x => x.RowVer + 1),
                        innerCt);

                if (advanceRows == 0)
                {
                    _logger.LogDebug(
                        "ExecuteSequentialApproveAtomicAsync: NodeInstance {NodeId} pointer advance CAS returned 0 — AlreadyHandled.",
                        nodeInst.ID);
                    await tx.RollbackAsync(CancellationToken.None);
                    return (WorkflowActionResult.AlreadyHandled, defaultExtra);
                }

                // Check for AutoApproved mid-chain.
                bool nextIsAutoApproved = false;
                if (activateRows == 0)
                {
                    var nextTask = await Db.Set<ApprovalTask>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.NodeInstanceId == nodeInst.ID
                                                   && t.SequenceOrder == nextPointer, innerCt);

                    if (nextTask?.State == TaskState.AutoApproved)
                    {
                        nextIsAutoApproved = true;
                        _logger.LogDebug(
                            "ExecuteSequentialApproveAtomicAsync: next task (order {Order}) is AutoApproved — will recurse post-commit.",
                            nextPointer);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "ExecuteSequentialApproveAtomicAsync: could not activate next task at order {Order} for node {NodeId}.",
                            nextPointer, nodeInst.ID);
                    }
                    // DO NOT roll back — the pointer advance MUST commit.
                }

                await tx.CommitAsync(innerCt);
                return (WorkflowActionResult.Advanced,
                        new SequentialAtomicExtra(IsLastStep: false, NextIsAutoApproved: nextIsAutoApproved));
            }
            else
            {
                // Last step: advance pointer to totalRequired so CanCompleteAsync returns true.

                // Re-read for fresh RowVer (inside tx).
                var freshNodeLast = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleAsync(n => n.ID == nodeInst.ID, innerCt);

                if (freshNodeLast.State != NodeState.Activated)
                {
                    _logger.LogDebug(
                        "ExecuteSequentialApproveAtomicAsync: NodeInstance {NodeId} became {State} before last-step pointer CAS — AlreadyHandled.",
                        nodeInst.ID, freshNodeLast.State);
                    await tx.RollbackAsync(CancellationToken.None);
                    return (WorkflowActionResult.AlreadyHandled, defaultExtra);
                }

                // WF-18 FIX-F: ApproverSetEpoch guard VERBATIM.
                var advanceRows = await Db.Set<NodeInstance>()
                    .Where(n => n.ID == nodeInst.ID
                                 && n.State == NodeState.Activated
                                 && n.RowVer == freshNodeLast.RowVer
                                 && n.ApproverSetEpoch == freshNodeLast.ApproverSetEpoch)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(n => n.SequencePointer, totalRequired)
                               .SetProperty(n => n.RowVer, x => x.RowVer + 1),
                        innerCt);

                if (advanceRows == 0)
                {
                    _logger.LogDebug(
                        "ExecuteSequentialApproveAtomicAsync: NodeInstance {NodeId} last-step pointer CAS returned 0 — AlreadyHandled.",
                        nodeInst.ID);
                    await tx.RollbackAsync(CancellationToken.None);
                    return (WorkflowActionResult.AlreadyHandled, defaultExtra);
                }

                await tx.CommitAsync(innerCt);
                return (WorkflowActionResult.Advanced,
                        new SequentialAtomicExtra(IsLastStep: true, NextIsAutoApproved: false));
            }

            } // end try
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, defaultExtra, ct);
    }

    // ── WF-20.4: shared approve post-claim completion helper ──────────────────

    /// <summary>
    /// Mode-specific completion logic run after the ApprovalTask CAS claim succeeds for
    /// either a human approve or a system AutoApprove.  Human path is byte-identical to
    /// the pre-WF-20.4 code (locked by the event-sequence snapshot test).
    /// </summary>
    private async Task<WorkflowActionResult> ExecuteApproveCompletionAsync(
        Guid claimedTaskId,
        NodeInstance nodeInst,
        ProcessInstance instance,
        ApproveMode approveMode,
        CancellationToken ct,
        string? actorITCode = null,
        bool incrementAlreadyDone = false)
    {
        if (approveMode == ApproveMode.All)
        {
            // 会签: increment advisory ApprovedCount, then check if threshold is reached.
            // For non-final approvals (count < threshold) return Advanced immediately —
            // AdvanceCoreAsync would only see Blocked (node not yet complete).
            // For the final approval(s) that cross the threshold, call AdvanceAsync so that
            // AdvanceCoreAsync performs the authoritative node-completion CAS (W1 fix, spec §7.4).
            // Concurrent final approvers both reach this path; exactly one wins the CAS;
            // the other gets AlreadyHandled from CompleteNodeInstanceAsync returning 0 rows.
            // T5 (W-ALL-CLAIM-TO-INCREMENT): when called from the human path (ApproveTaskAsync),
            // the increment was already committed atomically with the claim CAS and the Approve
            // event (see claim+increment tx in ApproveTaskAsync). Skip here to avoid double-count.
            // System/timeout path (SystemContinueTaskAsync, incrementAlreadyDone=false): still
            // increments here as before (post-commit, no ambient tx).
            if (!incrementAlreadyDone)
                await GuardedTransition.IncrementNodeApprovedCountAsync(Db, nodeInst.ID, ct);

            var freshNodeAll = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, ct);

            int thresholdAll = AllApprovalHandler.ComputeThreshold(
                freshNodeAll.TotalRequired, freshNodeAll.ApprovePercent, freshNodeAll.NodeKey);

            if (freshNodeAll.ApprovedCount < thresholdAll)
            {
                // Threshold not yet met — node is still waiting for more approvals.
                return WorkflowActionResult.Advanced;
            }

            // Threshold met (or exceeded by concurrent racing approvers): try to complete.
            // C10 fix: pass actorITCode so AdvanceCoreAsync can stamp DecidedBy on the node.
            var allResult = await AdvanceWithActorAsync(instance, ct, actorITCode);
            // WF-15 — post-advance instance completion notification (best-effort).
            if (_notifier is not null && allResult.Code == WorkflowActionCode.InstanceApproved)
            {
                try
                {
                    var freshInst = await Db.Set<ProcessInstance>().AsNoTracking().SingleAsync(x => x.ID == instance.ID, CancellationToken.None);
                    await _notifier.NotifyInstanceCompletedAsync(freshInst, ct);
                }
                catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyInstanceCompletedAsync (All) failed for instance {InstanceId}.", instance.ID); }
            }
            return allResult;
        }

        if (approveMode == ApproveMode.Any)
        {
            // 或签: first approve always crosses the threshold (Any = 1 needed).
            // Increment advisory count, then call AdvanceAsync so AdvanceCoreAsync runs
            // CanCompleteAsync (ApprovedCount >= 1 → true) and performs the node CAS.
            // OnCompleteAsync cancels sibling Pending tasks.
            // Concurrent approvers both increment and both call AdvanceAsync; the node
            // CAS in AdvanceCoreAsync ensures exactly one caller completes the node.
            // T5 (W-ALL-CLAIM-TO-INCREMENT): see All branch comment above.
            if (!incrementAlreadyDone)
                await GuardedTransition.IncrementNodeApprovedCountAsync(Db, nodeInst.ID, ct);
            // C10 fix: pass actorITCode so AdvanceCoreAsync can stamp DecidedBy on the node.
            var anyResult = await AdvanceWithActorAsync(instance, ct, actorITCode);
            // WF-15 — post-advance instance completion notification (best-effort).
            if (_notifier is not null && anyResult.Code == WorkflowActionCode.InstanceApproved)
            {
                try
                {
                    var freshInst = await Db.Set<ProcessInstance>().AsNoTracking().SingleAsync(x => x.ID == instance.ID, CancellationToken.None);
                    await _notifier.NotifyInstanceCompletedAsync(freshInst, ct);
                }
                catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyInstanceCompletedAsync (Any) failed for instance {InstanceId}.", instance.ID); }
            }
            return anyResult;
        }

        // Sequential path (default) ─────────────────────────────────────────────

        var nextPointer = nodeInst.SequencePointer + 1;
        var totalRequired = nodeInst.TotalRequired;

        if (nextPointer < totalRequired)
        {
            // More steps remain: atomically activate the next task AND advance the pointer.
            // txSeqApproveMidChain (#320 PR C): wrap both writes in one transaction.
            // Canonical Task→Node order so a pointer-CAS loss rolls back the activate too —
            // no half-activated state (W-SEQ-MIDCHAIN permanent-strand window eliminated).
            var freshNode = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, ct);

            // #667: routed through ExecuteInTransactionAsync — was a raw, unretried
            // BeginTransactionAsync; a deadlock/transient victim here previously threw a raw
            // provider exception to the caller.
            int activateRows = 0;
            var txSeqApproveMidChainResult = await ExecuteInTransactionAsync(async innerCt =>
            {
                await using var txSeqApproveMidChain = await Db.Database.BeginTransactionAsync(innerCt);
                try
                {
                    // Step B first (Task): activate the next-step task.
                    // FIX-B5a: also match AddedPending (injected steps from WF-18 AddApproverAsync).
                    activateRows = await Db.Set<ApprovalTask>()
                        .Where(t => t.NodeInstanceId == nodeInst.ID
                                     && t.SequenceOrder == nextPointer
                                     && (t.State == TaskState.NotYetActive || t.State == TaskState.AddedPending))
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(t => t.State, TaskState.Pending),
                            innerCt);

                    // Step A second (Node): advance the SequencePointer CAS.
                    // WF-18 FIX-F: assert ApproverSetEpoch alongside RowVer so a concurrent Before-加签
                    // that inserts a task ahead of nextPointer invalidates this pointer advance (rows→0).
                    var advanceRows = await Db.Set<NodeInstance>()
                        .Where(n => n.ID == nodeInst.ID
                                     && n.State == NodeState.Activated
                                     && n.RowVer == freshNode.RowVer
                                     && n.SequencePointer == nodeInst.SequencePointer
                                     && n.ApproverSetEpoch == freshNode.ApproverSetEpoch)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(n => n.SequencePointer, nextPointer)
                                   .SetProperty(n => n.RowVer, x => x.RowVer + 1),
                            innerCt);

                    if (advanceRows == 0)
                    {
                        // Concurrent actor already advanced the pointer (or epoch changed).
                        // Roll back the activate — no orphan Pending task at nextPointer.
                        await txSeqApproveMidChain.RollbackAsync(CancellationToken.None);
                        _logger.LogDebug(
                            "ExecuteApproveCompletionAsync: NodeInstance {NodeId} pointer advance CAS returned 0 — " +
                            "concurrent actor already advanced. Instance proceeds as AlreadyHandled.",
                            nodeInst.ID);
                        return WorkflowActionResult.AlreadyHandled;
                    }

                    await txSeqApproveMidChain.CommitAsync(innerCt);
                    return WorkflowActionResult.NodeCompleted;
                }
                catch
                {
                    await txSeqApproveMidChain.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }, ct);

            if (txSeqApproveMidChainResult.Code != WorkflowActionCode.NodeCompleted)
                return txSeqApproveMidChainResult;

            // Post-commit: AutoApprove disambiguation, timers, notify — all best-effort, outside tx.

            if (activateRows == 0)
            {
                // Check if it was already auto-approved (InitiatorAutoApprove path).
                // The activate CAS matches only NotYetActive/AddedPending, so AutoApproved
                // tasks naturally yield activateRows==0 — the disambiguation still fires.
                var nextTask = await Db.Set<ApprovalTask>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.NodeInstanceId == nodeInst.ID
                                               && t.SequenceOrder == nextPointer, ct);

                if (nextTask?.State == TaskState.AutoApproved)
                {
                    // Auto-approved step: recursively advance until a real pending step or completion.
                    _logger.LogDebug(
                        "ExecuteApproveCompletionAsync: next task (order {Order}) is AutoApproved — continuing pointer advance.",
                        nextPointer);
                    // Re-read nodeInst with updated pointer and recurse via AdvanceAsync.
                    return await AdvanceAsync(instance.ID, ct);
                }

                _logger.LogWarning(
                    "ExecuteApproveCompletionAsync: could not activate next task at order {Order} for node {NodeId}.",
                    nextPointer, nodeInst.ID);
            }

            // Node is still active — waiting for the next approver.

            // WF-20.2: Step B timer cancel/re-arm.
            // Cancel the completing step's task timer (hygiene — always safe, no-op when no timer exists).
            await GuardedTransition.CancelTimerForTaskAsync(Db, claimedTaskId, ct);
            // Arm the next step's timer only when the business-calendar seam is present (opt-in).
            if (_businessCalendar is not null)
            {
                var stepBNodeDef = await LoadNodeDefAsync(instance.DefinitionVersionId, nodeInst.NodeKey, ct);
                if (stepBNodeDef?.Timeout is not null)
                {
                    var nextPendingForArm = await Db.Set<ApprovalTask>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            t => t.NodeInstanceId == nodeInst.ID
                                 && t.SequenceOrder == nextPointer
                                 && t.State == TaskState.Pending,
                            ct);
                    if (nextPendingForArm is not null)
                    {
                        var freshNodeForArm = await Db.Set<NodeInstance>()
                            .AsNoTracking().SingleAsync(n => n.ID == nodeInst.ID, ct);
                        await ArmTaskTimerIfConfiguredAsync(
                            nextPendingForArm, freshNodeForArm, stepBNodeDef, _timeProvider.GetUtcNow().UtcDateTime, ct);
                    }
                }
            }

            // WF-15 — Notify the newly activated task assignee (post-commit, best-effort).
            if (_notifier is not null)
            {
                try
                {
                    // Read the next-step task that was just activated for notification.
                    var nextPendingTask = await Db.Set<ApprovalTask>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            t => t.NodeInstanceId == nodeInst.ID && t.SequenceOrder == nextPointer && t.State == TaskState.Pending,
                            ct);
                    if (nextPendingTask is not null)
                    {
                        var freshNodeForNotify = await Db.Set<NodeInstance>().AsNoTracking().SingleAsync(n => n.ID == nodeInst.ID, ct);
                        await _notifier.NotifyTaskAssignedAsync(instance, freshNodeForNotify, nextPendingTask, ct);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyTaskAssignedAsync (Sequential mid-chain) failed for instance {InstanceId}.", instance.ID); }
            }
            return WorkflowActionResult.Advanced;
        }
        else
        {
            // Last step completed — advance the SequencePointer to totalRequired so that
            // SequentialApprovalHandler.CanCompleteAsync (pointer >= totalRequired) returns true,
            // then call AdvanceAsync to route the engine onward (to End → Approved).
            var freshNodeLast = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, ct);

            // WF-18 FIX-F: assert ApproverSetEpoch on the final pointer advance too —
            // guards against a concurrent After-加签 that extends the chain after the
            // last task was approved but before the pointer is advanced to completion.
            // D2: also guard on SequencePointer — a concurrent loser whose pointer already
            // moved must fail this CAS (rows==0 → AlreadyHandled) instead of advancing
            // a step early. Mirrors the mid-chain CAS guard (see txSeqApproveMidChain above).
            await Db.Set<NodeInstance>()
                .Where(n => n.ID == nodeInst.ID
                             && n.State == NodeState.Activated
                             && n.RowVer == freshNodeLast.RowVer
                             && n.ApproverSetEpoch == freshNodeLast.ApproverSetEpoch
                             && n.SequencePointer == nodeInst.SequencePointer)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(n => n.SequencePointer, totalRequired)
                           .SetProperty(n => n.RowVer, x => x.RowVer + 1),
                    ct);

            var seqResult = await AdvanceAsync(instance.ID, ct);
            // WF-15 — post-advance instance completion notification (best-effort).
            if (_notifier is not null)
            {
                try
                {
                    if (seqResult.Code == WorkflowActionCode.InstanceApproved)
                    {
                        var freshInst = await Db.Set<ProcessInstance>().AsNoTracking().SingleAsync(x => x.ID == instance.ID, CancellationToken.None);
                        await _notifier.NotifyInstanceCompletedAsync(freshInst, ct);
                    }
                    else
                    {
                        // Advance moved to another node — notify first pending task at next node.
                        await NotifyFirstPendingTasksAsync(instance.ID, instance, ct);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "WF-15 post-advance notification failed for instance {InstanceId}.", instance.ID); }
            }
            return seqResult;
        }
    }

    // #668: IsUniqueConstraintViolation consolidated onto GuardedTransition.IsUniqueConstraintViolation
    // (was one of three divergent unique-violation detectors) — see GuardedTransition.cs.

    /// <summary>
    /// Load the <see cref="Definition.NodeDef"/> for <paramref name="nodeKey"/> from the
    /// published graph stored in <paramref name="definitionVersionId"/>.
    ///
    /// <para>Used by WF-20.2 Step B re-arm in <c>ApproveTaskAsync</c> — the arm site needs
    /// the TimeoutDef of the node whose next sequential step just became Pending, but
    /// <c>ApproveTaskAsync</c> does not have the graph loaded at that call site.
    /// This helper loads and deserializes on demand; callers must gate on
    /// <see cref="_businessCalendar"/> being non-null to avoid the I/O cost in tests.</para>
    ///
    /// <para>Returns <c>null</c> when the version row or the nodeKey cannot be found
    /// (fail-closed: caller skips re-arm rather than throwing).</para>
    /// </summary>
    private async Task<Definition.NodeDef?> LoadNodeDefAsync(
        Guid definitionVersionId,
        string nodeKey,
        CancellationToken ct)
    {
        var version = await Db.Set<ProcessDefinitionVersion>()
            .AsNoTracking()
            .SingleOrDefaultAsync(v => v.ID == definitionVersionId, ct);

        if (version is null) return null;

        var graph = _graphProvider.GetGraph(version);
        return graph.Nodes.FirstOrDefault(n =>
            string.Equals(n.NodeKey, nodeKey, StringComparison.Ordinal));
    }

    private sealed record SequentialAtomicExtra(bool IsLastStep, bool NextIsAutoApproved);
}
