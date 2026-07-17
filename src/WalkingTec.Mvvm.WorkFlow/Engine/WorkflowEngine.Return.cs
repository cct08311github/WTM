#nullable enable
// WorkflowEngine — Return region (WithdrawAsync + ReturnToInitiator/Prev/Node + WF-16 return execution).
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
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

internal sealed partial class WorkflowEngine
{
    // #668: AllocateSeqWithRetryAsync consolidated onto GuardedTransition.AllocateSeqWithRetryAsync
    // (was verbatim-duplicated here and in WorkflowTimerExecutor) — see GuardedTransition.cs.

    // ── WF-12: WithdrawAsync ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<WorkflowActionResult> WithdrawAsync(
        Guid instanceId,
        string actorITCode,
        string? reason = null,
        bool isAdmin = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorITCode))
            throw new ArgumentException("actorITCode must not be empty.", nameof(actorITCode));

        // 1. Load instance.
        var instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ID == instanceId && x.IsValid == true, ct);

        if (instance is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"ProcessInstance {instanceId} not found.");

        // 2. Initiator check (bypass for admin).
        if (!isAdmin
            && !string.Equals(instance.InitiatorITCode, actorITCode, StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NotInitiator,
                $"Actor '{actorITCode}' is not the initiator '{instance.InitiatorITCode}' of instance {instanceId}.");
        }

        // 3. WithdrawPolicy check.
        var policy = _options.WithdrawPolicy;

        if (policy == WithdrawPolicy.Disabled)
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NotAuthorized,
                $"Withdrawal is disabled (WithdrawPolicy={policy}).");
        }

        // 4. Current-state check: only Running instances can be withdrawn.
        if (instance.State != InstanceState.Running)
        {
            // Already terminal — T-CONC-2 loser or user re-tries after completion.
            return WorkflowActionResult.WithDetail(WorkflowActionCode.CannotWithdrawAlreadyFinal,
                $"Instance {instanceId} is in state {instance.State}, not Running. Cannot withdraw.");
        }

        // 5. BeforeAnyAction (L0): withdrawal only allowed while no approver has acted yet.
        //    "Acted" = at least one ApprovalTask in a terminal state (not Pending/NotYetActive/Cancelled).
        //    Two-step: materialize node IDs first (avoids nested subquery translation issues on SQLite).
        if (policy == WithdrawPolicy.BeforeAnyAction)
        {
            var nodeIdsForPolicy = await Db.Set<NodeInstance>()
                .Where(n => n.InstanceId == instanceId)
                .Select(n => n.ID)
                .ToListAsync(ct);

            bool anyActed = nodeIdsForPolicy.Count > 0
                && await Db.Set<ApprovalTask>()
                    .AnyAsync(t => nodeIdsForPolicy.Contains(t.NodeInstanceId)
                                    && t.State != TaskState.Pending
                                    && t.State != TaskState.NotYetActive
                                    && t.State != TaskState.Cancelled,
                              ct);

            if (anyActed)
            {
                return WorkflowActionResult.WithDetail(WorkflowActionCode.CannotWithdrawAlreadyFinal,
                    $"Withdrawal rejected: WithdrawPolicy=BeforeAnyAction and an approver has already acted on instance {instanceId}.");
            }
        }

        // 6-7 (reordered per #358): atomic Withdraw — Task cancels BEFORE ProcessInstance flip.
        // Original order was: ProcessInstance flip → Task cancel (violated canonical Task→Node→ProcessInstance order).
        // #358 fix: (1) reorder to canonical — cancel Tasks FIRST, flip ProcessInstance LAST;
        //           (2) wrap in ExecuteInTransactionAsync + explicit tx so a crash mid-sequence rolls back fully.
        // CAS-loser (rows==0 on ProcessInstance flip): the instance was concurrently finalized —
        // return CannotWithdrawAlreadyFinal immediately (not retried; idempotent re-read on replay).
        // #667: kept as assert-no-ambient (never nested today) rather than retrofitted to
        // own-or-enlist — see the equivalent comment in ExecuteSequentialApproveAtomicAsync.
        if (Db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "WithdrawAsync: unexpected ambient transaction (#358).");

        var withdrawResult = await ExecuteInTransactionAsync(async innerCt =>
        {
            await using var txWithdraw = await Db.Database.BeginTransactionAsync(innerCt);
            try
            {
                // Re-read fresh instance snapshot inside the tx (idempotent on deadlock retry).
                var freshInst = await Db.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ID == instanceId && x.IsValid == true, innerCt);

                if (freshInst is null || freshInst.State != InstanceState.Running)
                {
                    await txWithdraw.RollbackAsync(CancellationToken.None);
                    _logger.LogDebug(
                        "WithdrawAsync: re-read state={State} (expected Running) for {InstanceId} — CannotWithdrawAlreadyFinal.",
                        freshInst?.State, instanceId);
                    return WorkflowActionResult.CannotWithdrawAlreadyFinal;
                }

                // Step 1 (ApprovalTask — FIRST): cancel all Pending/NotYetActive tasks.
                // Two-step: fetch node IDs then bulk-cancel tasks (avoids correlated subquery issues on SQLite).
                var nodeIdsForWithdraw = await Db.Set<NodeInstance>()
                    .Where(n => n.InstanceId == instanceId)
                    .Select(n => n.ID)
                    .ToListAsync(innerCt);

                if (nodeIdsForWithdraw.Count > 0)
                {
                    await Db.Set<ApprovalTask>()
                        .Where(t => nodeIdsForWithdraw.Contains(t.NodeInstanceId)
                                     && (t.State == TaskState.Pending || t.State == TaskState.NotYetActive))
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(t => t.State, TaskState.Cancelled),
                            innerCt);
                }

                // Step 2 (ProcessInstance — LAST): CAS flip Running → Withdrawn.
                // T-CONC-2: if the final approver won the race first (Approved/Rejected), this returns 0.
                var withdrawRows = await GuardedTransition.AdvanceProcessInstanceAsync(
                    Db, instanceId,
                    expectedState: InstanceState.Running,
                    expectedRowVer: freshInst.RowVer,
                    nextState: InstanceState.Withdrawn,
                    innerCt);

                if (withdrawRows == 0)
                {
                    // CAS loser — concurrent actor finalized the instance; return without retry.
                    await txWithdraw.RollbackAsync(CancellationToken.None);
                    _logger.LogDebug(
                        "WithdrawAsync: CAS returned 0 rows for instance {InstanceId} — " +
                        "concurrent actor already changed state. Treating as CannotWithdrawAlreadyFinal.",
                        instanceId);
                    return WorkflowActionResult.CannotWithdrawAlreadyFinal;
                }

                // Step 3 (Audit): event log AFTER both Task + ProcessInstance writes.
                await WorkflowEventLogWriter.AppendAsync(
                    Db, instanceId, freshInst.TenantCode,
                    EventAction.Withdraw,
                    nodeKey: null,
                    actorITCode: actorITCode,
                    beforeState: InstanceState.Running.ToString(),
                    afterState: InstanceState.Withdrawn.ToString(),
                    reason: reason,
                    timeProvider: _timeProvider,
                    ct: innerCt);

                await txWithdraw.CommitAsync(innerCt);
                return WorkflowActionResult.Withdrawn;
            }
            catch
            {
                await txWithdraw.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, ct);

        if (withdrawResult.Code != WorkflowActionCode.Withdrawn)
            return withdrawResult;

        // Post-commit: timer cancels are best-effort (Armed timers fire-and-no-op on gen-gated CAS).
        // WF-20.2: instance-wide timer cancel on Withdrawn (§6 R4 spec §5.6 gap close).
        var nodeIdsPostCommit = await Db.Set<NodeInstance>()
            .Where(n => n.InstanceId == instanceId)
            .Select(n => n.ID)
            .ToListAsync(ct);
        foreach (var nid in nodeIdsPostCommit)
            await GuardedTransition.CancelTimersForNodeAsync(Db, nid, ct);

        _logger.LogInformation(
            "WithdrawAsync: instance {InstanceId} withdrawn by '{ActorITCode}' (isAdmin={IsAdmin}).",
            instanceId, actorITCode, isAdmin);

        // WF-15 — Notify withdrawn (post-commit, best-effort).
        if (_notifier is not null)
        {
            // Re-read fresh instance for notification (state is now Withdrawn).
            var freshInst = await Db.Set<ProcessInstance>().AsNoTracking().SingleAsync(x => x.ID == instanceId, CancellationToken.None);
            try { await _notifier.NotifyWithdrawnAsync(freshInst, actorITCode, ct); }
            catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyWithdrawnAsync failed for instance {InstanceId}.", instanceId); }
        }

        return WorkflowActionResult.Withdrawn;
    }

    // ── WF-12: ReturnToInitiatorAsync ─────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<WorkflowActionResult> ReturnToInitiatorAsync(
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

        // 5. Actor must be the assignee.
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

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // 7. CAS: claim the trigger task as Rejected (the task that triggered the return).
        // FIX-3: AtAction window applies to ALL actions by the delegatee, not just Approve.
        // Under AtAction, an expired delegatee must NOT be able to trigger a return-to-initiator —
        // the delegation window is the delegatee's authority to act.
        // Mirror the ApproveTaskAsync AtAction routing pattern exactly.
        int claimedRows;
        bool isAtActionDelegatedReturn = _options.DelegationWindowMode == DelegationWindowMode.AtAction
                                         && task.DelegationExpiresUtc.HasValue;

        if (isAtActionDelegatedReturn)
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
                        "ReturnToInitiatorAsync: task {TaskId} AtAction window expired at {Expiry} (now={Now}). " +
                        "Task stays Pending; manual reassignment or revoke required.",
                        taskId, freshTask.DelegationExpiresUtc.Value, now);
                    return WorkflowActionResult.DelegationExpired;
                }

                _logger.LogDebug(
                    "ReturnToInitiatorAsync: task {TaskId} AtAction CAS returned 0 rows — already handled by concurrent actor.",
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
                    "ReturnToInitiatorAsync: task {TaskId} CAS returned 0 rows — already handled.",
                    taskId);
                return WorkflowActionResult.AlreadyHandled;
            }
        }

        // T7 (#320 PR D): wrap {sibling-cancel → CompleteNode → instance re-read → instance-flip CAS → event Append}
        // in one canonical-order (Task→Node→Instance) transaction. A crash between node completion and
        // instance flip can no longer strand a terminal node on a Running instance, and sibling cancellation
        // is now atomic with node completion so a rollback reverts both.
        // Timer cancels (WF-20.2) and WF-15 notifier run post-commit, best-effort.
        // Lock-order: Task (sibling cancel, step 8) → Node (CompleteNode, step 9) → Instance (AdvanceProcessInstance, step 11).
        //
        // #667 fix: this used to be a BARE, unretried BeginTransactionAsync, deliberately excluded
        // from BOTH the deadlock-retry loop AND the execution-strategy legality wrap (the exclusion
        // comment only justified skipping the RETRY, not skipping legality). Under a host-configured
        // retrying execution strategy (EnableRetryOnFailure), that bare call threw
        // InvalidOperationException("...does not support user-initiated transactions...") on every
        // ReturnToInitiator, AFTER the claim CAS above had already committed the task as Rejected —
        // stranding the node/instance mid-transition with no compensating action. Now strategy-wrapped
        // via ExecuteInTransactionAsync for legality; retryOnDeadlock: false is unchanged and correct —
        // this unit has no independent self-healing backstop analogous to the Returning-lease reaper
        // (ReturnToInitiator does not enter the Returning sub-state), so the ORIGINAL semantics were
        // "fail closed, no retry, caller/reaper-adjacent recovery" and that is preserved; only the
        // failure MODE changes (DeadlockRetryExhausted result instead of a raw thrown exception).
        var (txReturnResult, instanceRows) = await ExecuteInTransactionAsync<int>(async innerCt =>
        {
            await using var txReturn = await Db.Database.BeginTransactionAsync(innerCt);
            try
            {
                // 8. Cancel all remaining Pending/NotYetActive tasks on this node (first write, inside tx).
                await Db.Set<ApprovalTask>()
                    .Where(t => t.NodeInstanceId == nodeInst.ID
                                 && (t.State == TaskState.Pending || t.State == TaskState.NotYetActive)
                                 && t.ID != taskId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.State, TaskState.Cancelled),
                        innerCt);

                // 9. Complete node as Returned (CAS on fresh RowVer).
                var freshNode = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleAsync(n => n.ID == nodeInst.ID, innerCt);

                var nodeCompleteRows = await GuardedTransition.CompleteNodeInstanceAsync(
                    Db, nodeInst.ID,
                    expectedRowVer: freshNode.RowVer,
                    completedState: NodeState.Returned,
                    decidedBy: actorITCode,
                    ct: innerCt);

                if (nodeCompleteRows == 0)
                {
                    _logger.LogDebug(
                        "ReturnToInitiatorAsync: NodeInstance {NodeId} completion CAS returned 0 — concurrent actor already completed.",
                        nodeInst.ID);
                    await txReturn.RollbackAsync(CancellationToken.None);
                    return (WorkflowActionResult.AlreadyHandled, 0);
                }

                // 10. Re-read instance for fresh RowVer (no AllocateSeq has bumped it yet — safe).
                //     The CAS must be immediately after this re-read with no Append between.
                instance = await Db.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleAsync(x => x.ID == instance.ID, innerCt);

                // 11. Flip instance Running→Draft (CAS — immediately after re-read).
                var localInstanceRows = await GuardedTransition.AdvanceProcessInstanceAsync(
                    Db, instance.ID,
                    expectedState: InstanceState.Running,
                    expectedRowVer: instance.RowVer,
                    nextState: InstanceState.Draft,
                    innerCt);

                if (localInstanceRows == 1)
                {
                    // 12. Write the Return event (enlists in ambient txReturn).
                    // AppendAsync enlists in the ambient txReturn (db.Database.CurrentTransaction is not null).
                    await WorkflowEventLogWriter.AppendAsync(
                        Db, instance.ID, instance.TenantCode,
                        EventAction.Return,
                        nodeKey: nodeInst.NodeKey,
                        actorITCode: actorITCode,
                        beforeState: InstanceState.Running.ToString(),
                        afterState: InstanceState.Draft.ToString(),
                        reason: reason,
                        timeProvider: _timeProvider,
                        ct: innerCt);
                }

                await txReturn.CommitAsync(innerCt);
                return (WorkflowActionResult.Advanced, localInstanceRows);
            }
            catch
            {
                await txReturn.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, defaultExtra: 0, ct, retryOnDeadlock: false);

        // Early-exit codes from the body above (concurrent actor already completed the node,
        // or the deadlock-classified single attempt failed) return directly — nothing further
        // to compensate; the tx rolled back cleanly in both cases.
        if (txReturnResult.Code is WorkflowActionCode.AlreadyHandled or WorkflowActionCode.DeadlockRetryExhausted)
            return txReturnResult;

        if (instanceRows == 0)
        {
            _logger.LogWarning(
                "ReturnToInitiatorAsync: instance {InstanceId} CAS Running→Draft returned 0 — " +
                "concurrent actor already changed state.",
                instance.ID);
            // Node was completed but instance flip failed — rolled back; return AlreadyHandled.
            return WorkflowActionResult.AlreadyHandled;
        }

        _logger.LogInformation(
            "ReturnToInitiatorAsync: task {TaskId} returned to initiator by '{ActorITCode}'. Instance {InstanceId} now Draft.",
            taskId, actorITCode, instance.ID);

        // WF-20.2: cancel all Armed timers for this node (post-commit, best-effort).
        await GuardedTransition.CancelTimersForNodeAsync(Db, nodeInst.ID, ct);

        // WF-15 — Notify returned to initiator (post-commit, best-effort).
        if (_notifier is not null)
        {
            var freshInst = await Db.Set<ProcessInstance>().AsNoTracking().SingleAsync(x => x.ID == instance.ID, CancellationToken.None);
            try { await _notifier.NotifyReturnedToInitiatorAsync(freshInst, nodeInst, task, actorITCode, reason, ct); }
            catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyReturnedToInitiatorAsync failed for task {TaskId}.", taskId); }
        }

        return WorkflowActionResult.ReturnedToInitiator;
    }

    // ── WF-16: ReturnToPrevAsync / ReturnToNodeAsync ───────────────────────────

    /// <inheritdoc/>
    public async Task<WorkflowActionResult> ReturnToPrevAsync(
        Guid taskId,
        string actorITCode,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorITCode))
            throw new ArgumentException("actorITCode must not be empty.", nameof(actorITCode));

        // STEP-0: Load trigger task + node + instance to resolve the graph and auto-determine
        // the target.  (Heavy object loading deferred to ExecuteReturnToNodeAsync.)
        var task = await Db.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.ID == taskId && t.IsValid == true, ct);

        if (task is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"ApprovalTask {taskId} not found.");

        var nodeInst = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == task.NodeInstanceId, ct);

        if (nodeInst is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"NodeInstance {task.NodeInstanceId} not found.");

        var instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.ID == nodeInst.InstanceId && p.IsValid == true, ct);

        if (instance is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"ProcessInstance for NodeInstance {nodeInst.ID} not found.");

        var version = await Db.Set<ProcessDefinitionVersion>()
            .AsNoTracking()
            .SingleAsync(v => v.ID == instance.DefinitionVersionId, ct);

        var graph = _graphProvider.GetGraph(version);

        // Resolve the closest dominating Approval node (ReturnToPrev semantics).
        var targetNodeKey = WorkflowGraphValidator.GetPrevApprovalNode(graph, nodeInst.NodeKey);

        if (targetNodeKey is null)
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NoDominatorTarget,
                $"No preceding Approval dominator found for node '{nodeInst.NodeKey}' " +
                $"in graph '{graph.Key}'. Cannot ReturnToPrev.");
        }

        return await ExecuteReturnToNodeAsync(
            taskId, task, nodeInst, instance, graph, targetNodeKey, actorITCode, reason, ct);
    }

    /// <inheritdoc/>
    public async Task<WorkflowActionResult> ReturnToNodeAsync(
        Guid taskId,
        string targetNodeKey,
        string actorITCode,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorITCode))
            throw new ArgumentException("actorITCode must not be empty.", nameof(actorITCode));

        if (string.IsNullOrWhiteSpace(targetNodeKey))
            throw new ArgumentException("targetNodeKey must not be empty.", nameof(targetNodeKey));

        // STEP-0: Load and validate.
        var task = await Db.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.ID == taskId && t.IsValid == true, ct);

        if (task is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"ApprovalTask {taskId} not found.");

        var nodeInst = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == task.NodeInstanceId, ct);

        if (nodeInst is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"NodeInstance {task.NodeInstanceId} not found.");

        var instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.ID == nodeInst.InstanceId && p.IsValid == true, ct);

        if (instance is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"ProcessInstance for NodeInstance {nodeInst.ID} not found.");

        var version = await Db.Set<ProcessDefinitionVersion>()
            .AsNoTracking()
            .SingleAsync(v => v.ID == instance.DefinitionVersionId, ct);

        var graph = _graphProvider.GetGraph(version);

        // Validate that targetNodeKey is a dominator of the trigger node.
        var validTargets = WorkflowGraphValidator.GetValidReturnTargets(graph, nodeInst.NodeKey);
        if (!validTargets.Contains(targetNodeKey))
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NoDominatorTarget,
                $"Node '{targetNodeKey}' is not a valid return target for trigger node '{nodeInst.NodeKey}' " +
                $"in graph '{graph.Key}'. Only dominating Approval nodes are valid targets.");
        }

        return await ExecuteReturnToNodeAsync(
            taskId, task, nodeInst, instance, graph, targetNodeKey, actorITCode, reason, ct);
    }

    /// <summary>
    /// Core Wave-3 return-to-node implementation (STEP 0-6 + STEP-6-FC).
    ///
    /// <para><strong>#290 primary fix (Proposal B) — split STEP-1 into its own committed
    /// transaction.</strong>  The original code held <c>ProcessInstance</c> from STEP-1
    /// through STEP-6, making the return the sole multi-row transaction that acquired the
    /// instance FIRST.  Every other multi-row transaction (Delegate, AddApprover, reaper-
    /// escalate) acquires the instance LAST (via the Seq counter in AllocateSeqAsync).
    /// That ordering inversion was the root cause of three ABBA deadlock cycles.</para>
    ///
    /// <para>Fix: STEP-1 (<c>BeginReturnAsync</c>, the Running→Returning CAS) is committed
    /// in its own transaction (<c>txA</c>).  After txA commits, the instance write-lock is
    /// released before touching any timer/task/node rows.  STEP-2..6 run in a fresh
    /// transaction (<c>txB</c>) whose held-lock order is:
    /// <c>WorkflowTimer → ApprovalTask → NodeInstance → ProcessInstance(Seq)</c> —
    /// the wave-5 §0 canonical order, matching Delegate, AddApprover, and reaper-escalate.</para>
    ///
    /// <para>Non-atomic boundary: txA-commit leaves State=Returning+lease durable.  A
    /// failing txB is handled by the widened catch below — it attempts a prompt
    /// compensating Returning→Running roll-forward via
    /// <c>ReclaimReturningLeaseByRowVerAsync</c>.  If compensation itself fails, the Wave-5
    /// lease reaper (<c>ReclaimExpiredLeasesAsync</c>) recovers the stuck instance at lease
    /// expiry.  No new recovery machinery is introduced.</para>
    ///
    /// <para>Race A (span-discard vs in-flight approve): SupersedeNodeAsync shares the
    /// same RowVer as CompleteNodeInstanceAsync — exactly one wins.</para>
    ///
    /// <para>Race D: MaxReturnLoops cap enforced in BeginReturnAsync predicate.</para>
    /// </summary>
    private async Task<WorkflowActionResult> ExecuteReturnToNodeAsync(
        Guid taskId,
        ApprovalTask task,
        NodeInstance triggerNode,
        ProcessInstance instance,
        WorkflowGraph graph,
        string targetNodeKey,
        string actorITCode,
        string? reason,
        CancellationToken ct)
    {
        // Guard: node must be Activated.
        if (triggerNode.State != NodeState.Activated)
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"NodeInstance {triggerNode.ID} is in state {triggerNode.State}, not Activated.");
        }

        // Guard: actor must be the assignee.
        if (!string.Equals(task.AssigneeITCode, actorITCode, StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"Actor '{actorITCode}' is not the assignee '{task.AssigneeITCode}' of task {taskId}.");
        }

        // Guard: task must be Pending.
        if (task.State != TaskState.Pending)
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.AlreadyHandled,
                $"Task {taskId} is in state {task.State}, not Pending.");
        }

        int maxReturnLoops = _options.MaxReturnLoops;
        // WF-20.2: use configurable ReturningLeaseTtl (default 30 min) — zero behavior change.
        var leaseExpiry = _timeProvider.GetUtcNow().UtcDateTime.Add(_options.ReturningLeaseTtl);

        // Compute the span of node keys that must be superseded.
        var spanNodeKeys = WorkflowGraphValidator.ComputeReturnSpan(graph, targetNodeKey, triggerNode.NodeKey);

        // Resolve span NodeInstance IDs for STEP-2/3/4.
        // We need all Activated/Pending NodeInstances for the current generation that belong to span keys.
        var spanNodeInstances = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .Where(n => n.InstanceId == instance.ID
                         && n.Generation == instance.Generation
                         && (n.State == NodeState.Activated || n.State == NodeState.Pending))
            .ToListAsync(ct);

        // Filter to span keys only.
        var spanNodes = spanNodeInstances
            .Where(n => spanNodeKeys.Contains(n.NodeKey))
            .ToList();

        var spanNodeIds = spanNodes.Select(n => n.ID).ToList();

        // ── #290 primary fix: txA — STEP-1 only (single-row CAS, never an ABBA party) ──
        // Commit the linearization point (Running→Returning) in its own transaction so the
        // ProcessInstance write-lock is released BEFORE any timer/task/node rows are touched.
        // After txA commits, State=Returning + Generation=gNew + lease are all durable.
        //
        // #667 fix: this used to be a BARE, unretried BeginTransactionAsync — legality-only
        // exclusion was never justified (only the retry-loop exclusion was, via the Wave-5
        // Returning-lease reaper backstop). Strategy-wrapped now via ExecuteInTransactionAsync
        // for legality under a host-configured retrying execution strategy; retryOnDeadlock:
        // false is unchanged/correct — a failed single attempt here leaves the instance Running
        // (txA never committed), so there is nothing for the reaper to reclaim and nothing lost;
        // the caller can simply retry the ReturnToNode call.
        // gNewOrNull is null for every early-exit branch (concurrent actor won, MaxReturnLoops,
        // or DeadlockRetryExhausted) — the caller returns txAResult directly in that case.
        var (txAResult, gNewOrNull) = await ExecuteInTransactionAsync<uint?>(async innerCt =>
        {
            await using var txA = await Db.Database.BeginTransactionAsync(innerCt);
            try
            {
                // ── STEP-1: BeginReturnAsync — linearization point ─────────────────
                // Atomically: Running → Returning, Generation+1, ReturnLoops+1, stamp lease.
                // Re-read instance for current RowVer before the CAS.
                instance = await Db.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleAsync(p => p.ID == instance.ID, innerCt);

                if (instance.State != InstanceState.Running)
                {
                    await txA.RollbackAsync(CancellationToken.None);
                    return (WorkflowActionResult.WithDetail(WorkflowActionCode.AlreadyHandled,
                        $"Instance {instance.ID} is in state {instance.State}, not Running. Concurrent actor won."),
                        (uint?)null);
                }

                // Check MaxReturnLoops before the CAS (early-exit; CAS also enforces it atomically).
                if ((int)instance.ReturnLoops >= maxReturnLoops)
                {
                    await txA.RollbackAsync(CancellationToken.None);
                    _logger.LogWarning(
                        "ExecuteReturnToNodeAsync: instance {InstanceId} has reached MaxReturnLoops ({Max}). Fail-closed.",
                        instance.ID, maxReturnLoops);
                    return (WorkflowActionResult.MaxReturnLoopsExceeded, (uint?)null);
                }

                var (beginRows, _) = await GuardedTransition.BeginReturnAsync(
                    Db, instance.ID,
                    expectedRowVer: instance.RowVer,
                    expectedGeneration: instance.Generation,
                    maxReturnLoops: maxReturnLoops,
                    leaseExpiry: leaseExpiry,
                    ct: innerCt);

                if (beginRows == 0)
                {
                    await txA.RollbackAsync(CancellationToken.None);
                    _logger.LogDebug(
                        "ExecuteReturnToNodeAsync: BeginReturnAsync CAS returned 0 for instance {InstanceId} — " +
                        "concurrent actor won or MaxReturnLoops reached.",
                        instance.ID);
                    return (WorkflowActionResult.AlreadyHandled, (uint?)null);
                }

                // Re-read instance to get the new Generation (gNew = gOld+1 after BeginReturnAsync).
                instance = await Db.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleAsync(p => p.ID == instance.ID, innerCt);

                var gNewInner = instance.Generation;

                // ── txA COMMIT ── State=Returning + gNew + lease are now durable.
                // ProcessInstance write-lock released here — txB acquires it LAST (canonical order).
                await txA.CommitAsync(innerCt);
                return (WorkflowActionResult.Advanced, (uint?)gNewInner);
            }
            catch
            {
                await txA.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, defaultExtra: null, ct, retryOnDeadlock: false);

        if (gNewOrNull is null)
            return txAResult;

        uint gNew = gNewOrNull.Value;

        // ── txB — STEP-2 through STEP-6 (canonical lock order: Timer → Task → Node → Instance) ──
        // txB re-acquires ProcessInstance only at STEP-6 + AllocateSeq (instance LAST).
        // A failed txB leaves the instance in Returning with the lease stamped by txA.
        // The widened catch below attempts a prompt compensating Returning→Running flip;
        // if that also fails, the Wave-5 lease reaper recovers it at lease expiry.
        //
        // #667 fix: this used to be a BARE, unretried BeginTransactionAsync — same legality gap
        // as txA above. Strategy-wrapped now via ExecuteInTransactionAsync; retryOnDeadlock:
        // false is unchanged/correct — a failed attempt here already has a dedicated compensating
        // Returning→Running flip in the catch below (best-effort), with the Wave-5 lease reaper
        // as the final backstop if even that compensation fails. Inline classifier retry would
        // duplicate what the compensation + reaper already handle safely.
        // The body returns WorkflowActionResult.Advanced as an internal "committed, continue
        // post-processing" sentinel on the success path — the outer method only branches on the
        // two abort codes (STEP-6-FC AlreadyHandled, DeadlockRetryExhausted) below.
        var txBResult = await ExecuteInTransactionAsync(async innerCt =>
        {
            await using var txB = await Db.Database.BeginTransactionAsync(innerCt);
            try
            {
                // ── STEP-2: Cancel timers for all span NodeInstances ──────────────
                // First lock acquired in txB: WorkflowTimer (canonical order position 1).
                if (spanNodeIds.Count > 0)
                {
                    await GuardedTransition.CancelTimersForReturnAsync(Db, spanNodeIds, innerCt);
                }

                // ── STEP-3: Discard tasks on span nodes (current generation) ──────
                // Second lock order: ApprovalTask (canonical order position 2).
                if (spanNodeIds.Count > 0)
                {
                    // Exclude the trigger task — it is claimed in STEP-3b below.
                    await GuardedTransition.DiscardTasksForReturnAsync(
                        Db, spanNodeIds, excludeTaskId: taskId, innerCt);
                }

                // STEP-3b: Claim the trigger task itself as Rejected (the task that triggered return).
                // FIX-3: AtAction window applies to ALL actions by the delegatee, not just Approve.
                // Under AtAction, route through ClaimDelegatedTaskAsync so that the window check and
                // state flip are atomic.  If the window has expired, we still proceed with the return
                // (BeginReturnAsync already won the linearization point in txA), but we log the anomaly and
                // stamp WindowVerifiedUtc only on success.
                var stepNow = _timeProvider.GetUtcNow().UtcDateTime;
                bool isAtActionDelegatedStep3b = _options.DelegationWindowMode == DelegationWindowMode.AtAction
                                                 && task.DelegationExpiresUtc.HasValue;
                int claimedRows;

                if (isAtActionDelegatedStep3b)
                {
                    claimedRows = await GuardedTransition.ClaimDelegatedTaskAsync(
                        Db, taskId,
                        expectedRowVer: task.RowVer,
                        nextState: TaskState.Rejected,
                        actedAtUtc: stepNow,
                        comment: reason,
                        ct: innerCt);

                    // If CAS returned 0, the delegation window check in ClaimDelegatedTaskAsync folded
                    // out the predicate (expired) or a concurrent actor already claimed it.
                    // Either way, BeginReturnAsync already won the instance transition (txA committed) —
                    // the return proceeds.
                    if (claimedRows == 0)
                    {
                        var freshTask3b = await Db.Set<ApprovalTask>()
                            .AsNoTracking()
                            .Select(t => new { t.ID, t.State, t.DelegationExpiresUtc })
                            .SingleOrDefaultAsync(t => t.ID == taskId, innerCt);

                        if (freshTask3b is not null
                            && freshTask3b.State == TaskState.Pending
                            && freshTask3b.DelegationExpiresUtc.HasValue
                            && stepNow > freshTask3b.DelegationExpiresUtc.Value)
                        {
                            _logger.LogWarning(
                                "ExecuteReturnToNodeAsync: STEP-3b trigger task {TaskId} AtAction window expired at {Expiry} " +
                                "(now={Now}). Return proceeding (instance already in Returning state); task left Pending for revoke sweep.",
                                taskId, freshTask3b.DelegationExpiresUtc.Value, stepNow);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "ExecuteReturnToNodeAsync: STEP-3b trigger task {TaskId} AtAction CAS returned 0 — " +
                                "task already claimed by concurrent actor. Proceeding with return (instance already in Returning state).",
                                taskId);
                        }
                    }
                    else
                    {
                        // AtAction claim succeeded — stamp WindowVerifiedUtc for audit (non-guarded, audit-only).
                        await Db.Set<ApprovalTask>()
                            .Where(t => t.ID == taskId)
                            .ExecuteUpdateAsync(
                                s => s.SetProperty(t => t.WindowVerifiedUtc, stepNow),
                                innerCt);
                    }
                }
                else
                {
                    // Standard path (AtAssignment default or no delegation window) — byte-identical to pre-Wave-4.
                    claimedRows = await GuardedTransition.ClaimApprovalTaskAsync(
                        Db, taskId,
                        expectedRowVer: task.RowVer,
                        nextState: TaskState.Rejected,
                        actedAtUtc: stepNow,
                        comment: reason,
                        ct: innerCt);

                    // If another actor already claimed it — we already atomically won the instance
                    // state transition (BeginReturnAsync in txA), so treat this as an idempotent no-op;
                    // the return path proceeds.  Log a warning for diagnosis.
                    if (claimedRows == 0)
                    {
                        _logger.LogWarning(
                            "ExecuteReturnToNodeAsync: trigger task {TaskId} ClaimApprovalTask CAS returned 0 — " +
                            "task already claimed by concurrent actor. Proceeding with return (instance already in Returning state).",
                            taskId);
                    }
                }

                // ── STEP-4: Supersede span NodeInstances ──────────────────────────
                // Third lock order: NodeInstance (canonical order position 3).
                foreach (var spanNode in spanNodes)
                {
                    // Re-read fresh RowVer for each span node (other steps may have bumped it).
                    var freshSpanNode = await Db.Set<NodeInstance>()
                        .AsNoTracking()
                        .SingleOrDefaultAsync(n => n.ID == spanNode.ID, innerCt);

                    if (freshSpanNode is null) continue; // already gone (edge case)

                    // Skip if already superseded (concurrent twin return path).
                    if (freshSpanNode.State == NodeState.Superseded) continue;

                    var supersedeRows = await GuardedTransition.SupersedeNodeAsync(
                        Db, spanNode.ID,
                        expectedRowVer: freshSpanNode.RowVer,
                        supersededAtGen: gNew,
                        ct: innerCt);

                    if (supersedeRows == 0)
                    {
                        _logger.LogDebug(
                            "ExecuteReturnToNodeAsync: SupersedeNodeAsync CAS returned 0 for span node {NodeId} — " +
                            "concurrent actor won this node. Continuing span supersede.",
                            spanNode.ID);
                    }
                }

                // ── STEP-5: Mint fresh NodeInstance at target node ────────────────
                // Generation is gNew (stamped at mint time, durable since txA committed).
                var targetNodeDef = graph.Nodes.FirstOrDefault(n => n.NodeKey == targetNodeKey)
                    ?? throw new InvalidOperationException(
                           $"Target node '{targetNodeKey}' not found in graph '{graph.Key}'.");

                // Build the NodeInstance entity for the target node at generation gNew.
                var targetNodeInst = new NodeInstance
                {
                    ID = Guid.NewGuid(),
                    TenantCode = instance.TenantCode,
                    InstanceId = instance.ID,
                    NodeKey = targetNodeDef.NodeKey,
                    NodeKind = targetNodeDef.Kind,
                    State = NodeState.Pending,
                    ApproveMode = targetNodeDef.ApproveMode,
                    ApprovePercent = targetNodeDef.ApprovePercent,
                    RejectGate = targetNodeDef.RejectGate ?? RejectGate.Immediate,
                    RejectPolicy = targetNodeDef.RejectPolicy ?? RejectPolicy.ReturnToInitiator,
                    RowVer = 0,
                    Generation = gNew,
                    // FIX-5: DefinitionCode must be carried from the graph key so that
                    // DelegationResolvingDecorator can scope-filter DelegationRules to this
                    // node after a 回退 (return-to-node).  Without it, scope-restricted rules
                    // created for this node would be invisible to the post-回退 resolver call.
                    DefinitionCode = graph.Key,
                };
                var mintOk = await GuardedTransition.MintNodeInstanceGuardedAsync(
                    Db, targetNodeInst, innerCt);

                if (!mintOk)
                {
                    // UNIQUE constraint: another concurrent call already minted it — idempotent.
                    _logger.LogDebug(
                        "ExecuteReturnToNodeAsync: MintNodeInstanceGuardedAsync for '{TargetNodeKey}' gen={Gen} " +
                        "already exists (UNIQUE constraint) — idempotent, proceeding.",
                        LogSanitizer.Sanitize(targetNodeKey), gNew);
                }

                // ── STEP-6: Set instance Running ─────────────────────────────────
                // Fourth (last) lock order: ProcessInstance (canonical order position 4).
                // This is the canonical "instance LAST" acquisition — matching Delegate/AddApprover/reaper.
                instance = await Db.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleAsync(p => p.ID == instance.ID, innerCt);

                var runningRows = await GuardedTransition.AdvanceProcessInstanceAsync(
                    Db, instance.ID,
                    expectedState: InstanceState.Returning,
                    expectedRowVer: instance.RowVer,
                    nextState: InstanceState.Running,
                    innerCt);

                if (runningRows == 0)
                {
                    // ── STEP-6-FC: fail-closed ────────────────────────────────────
                    // We already incremented ReturnLoops (txA) and minted the target node.
                    // The only reason STEP-6 can fail is if the lease reaper (or a concurrent
                    // caller) already changed the instance state from Returning.
                    // Roll back txB and attempt the same compensating Returning→Running flip
                    // we use in the catch block, so the instance is not left stranded in
                    // Returning waiting for lease expiry (#290 FIX-4b).
                    _logger.LogError(
                        "ExecuteReturnToNodeAsync: STEP-6 Returning→Running CAS returned 0 for " +
                        "instance {InstanceId}. txA already committed (State=Returning). Rolled back txB. " +
                        "This indicates a rare concurrent reaper race — investigate lease config.",
                        instance.ID);

                    await txB.RollbackAsync(CancellationToken.None);

                    // #290 FIX-4b: proactive Returning→Running flip on the STEP-6-FC path.
                    // Mirrors the compensation in the catch block — prevents the instance from
                    // relying solely on the Wave-5 lease reaper when STEP-6 returns 0 rows
                    // (possible if a concurrent reaper or actor already reclaimed the lease).
                    try
                    {
                        var freshInstFc = await Db.Set<ProcessInstance>().AsNoTracking()
                            .SingleAsync(p => p.ID == instance.ID, CancellationToken.None);
                        if (freshInstFc.State == InstanceState.Returning)
                        {
                            await GuardedTransition.ReclaimReturningLeaseByRowVerAsync(
                                Db, freshInstFc.ID, freshInstFc.RowVer, CancellationToken.None);
                        }
                    }
                    catch (Exception fcCompEx)
                    {
                        _logger.LogError(fcCompEx,
                            "ExecuteReturnToNodeAsync: STEP-6-FC compensating Returning→Running failed " +
                            "for instance {InstanceId}; deferring to Returning-lease reaper.", instance.ID);
                    }

                    return WorkflowActionResult.WithDetail(WorkflowActionCode.AlreadyHandled,
                        $"Instance {instance.ID} STEP-6 CAS missed (txB rolled back). " +
                        "The return operation may have been superseded by a concurrent caller.");
                }

                // ── Write event log (inside txB, after STEP-6) ───────────────────
                // AppendAsync calls AllocateSeqAsync → ExecuteUpdate on ProcessInstance row —
                // this is the only Seq bump in the entire return operation (txA writes none).
                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, instance.TenantCode,
                    EventAction.Return,
                    nodeKey: triggerNode.NodeKey,
                    actorITCode: actorITCode,
                    beforeState: InstanceState.Running.ToString(),
                    afterState: InstanceState.Running.ToString(),
                    reason: $"ReturnToNode '{targetNodeKey}'. {reason}",
                    generation: (int)gNew,
                    timeProvider: _timeProvider,
                    ct: innerCt);

                await txB.CommitAsync(innerCt);

                _logger.LogInformation(
                    "ExecuteReturnToNodeAsync: instance {InstanceId} returned to '{TargetNodeKey}' " +
                    "by '{ActorITCode}'. Generation={Gen}, ReturnLoops={Loops}.",
                    instance.ID, LogSanitizer.Sanitize(targetNodeKey), LogSanitizer.Sanitize(actorITCode), gNew, instance.ReturnLoops);

                return WorkflowActionResult.Advanced;
            }
            catch
            {
                await txB.RollbackAsync(CancellationToken.None);

                // txA already durably committed State=Returning + lease.  A failed txB leaves the
                // instance in Returning with no in-flight engine owner.  Roll it forward to Running
                // promptly (idempotent), instead of waiting for lease expiry.
                try
                {
                    var freshInst = await Db.Set<ProcessInstance>().AsNoTracking()
                        .SingleAsync(p => p.ID == instance.ID, CancellationToken.None);
                    if (freshInst.State == InstanceState.Returning)
                    {
                        // ReclaimReturningLeaseByRowVerAsync: portable Returning→Running CAS (no DateTime
                        // in WHERE). rows==0 is benign — the Wave-5 reaper already reclaimed it.
                        await GuardedTransition.ReclaimReturningLeaseByRowVerAsync(
                            Db, freshInst.ID, freshInst.RowVer, CancellationToken.None);
                    }
                }
                catch (Exception compEx)
                {
                    // Compensation is best-effort.  If it fails, the Wave-5 reaper
                    // (ReclaimExpiredLeasesAsync) flips it back to Running at lease expiry.
                    _logger.LogError(compEx,
                        "ExecuteReturnToNodeAsync: compensating Returning→Running roll-forward failed for " +
                        "instance {InstanceId}; deferring to Returning-lease reaper.", instance.ID);
                }
                throw;
            }
        }, ct, retryOnDeadlock: false);

        // Abort codes from the body above return directly:
        //  - STEP-6-FC AlreadyHandled: txB rolled back, compensation already attempted inline.
        //  - DeadlockRetryExhausted: the single strategy-wrapped attempt failed on a
        //    deadlock-classified exception; the body's own catch already ran the compensating
        //    Returning→Running flip before ExecuteInTransactionAsync classified/swallowed it.
        if (txBResult.Code is WorkflowActionCode.AlreadyHandled or WorkflowActionCode.DeadlockRetryExhausted)
            return txBResult;

        // ── #322: Drive the freshly minted Pending target node through activation ──
        // After txB commits, the target NodeInstance is Pending with no ApprovalTasks.
        // AdvanceCoreAsync picks up all Pending/Activated tokens for the current generation,
        // activates the target node (Pending → Activated) and calls handler.OnEnterAsync
        // which materialises ApprovalTasks.  This MUST run post-commit (outside any txB scope)
        // so the minted node is visible to AdvanceCoreAsync's DB reads.
        var freshInstForAdvance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(x => x.ID == instance.ID, ct);
        try
        {
            await AdvanceCoreAsync(freshInstForAdvance, graph, ct);
        }
        catch (Exception advEx)
        {
            // Non-fatal: the return itself succeeded (txB committed).  Log and continue.
            // The target node remains Pending; the caller / reaper can retry AdvanceAsync.
            _logger.LogError(advEx,
                "ExecuteReturnToNodeAsync: #322 post-return AdvanceCoreAsync failed for instance {InstanceId}. " +
                "Return committed; target node activation deferred.", instance.ID);
        }

        // WF-15 — Notify return (post-commit, post-advance, best-effort).
        if (_notifier is not null)
        {
            var freshInst = await Db.Set<ProcessInstance>().AsNoTracking().SingleAsync(x => x.ID == instance.ID, CancellationToken.None);
            try { await NotifyFirstPendingTasksAsync(instance.ID, freshInst, ct); }
            catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyFirstPendingTasksAsync (ReturnToNode) failed for instance {InstanceId}.", instance.ID); }
        }

        return WorkflowActionResult.Returned;
    }
}
