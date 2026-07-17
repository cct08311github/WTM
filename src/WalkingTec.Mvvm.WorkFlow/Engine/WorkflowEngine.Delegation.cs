#nullable enable
// WorkflowEngine — Delegation region (AddApproverAsync 加签, DelegateTaskAsync, RevokeDelegationAsync).
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
    // ── WF-18: AddApproverAsync (加签) ──────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<WorkflowActionResult> AddApproverAsync(
        Guid taskId,
        string actorITCode,
        IReadOnlyList<string> newApproverITCodes,
        AddPosition position = AddPosition.After,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorITCode))
            throw new ArgumentException("actorITCode must not be empty.", nameof(actorITCode));
        if (newApproverITCodes is null || newApproverITCodes.Count == 0)
            throw new ArgumentException("newApproverITCodes must not be empty.", nameof(newApproverITCodes));

        // 1. Load the actor's task (RBAC guard: actor must have an active Pending task on the node).
        var task = await Db.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.ID == taskId && t.IsValid == true, ct);

        if (task is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"ApprovalTask {taskId} not found.");

        if (task.AssigneeITCode != actorITCode)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NotAuthorized,
                $"Actor '{actorITCode}' is not the assignee of task {taskId}.");

        if (task.State != TaskState.Pending)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"Task {taskId} is in state {task.State}, not Pending.");

        // 2. Load node instance.
        var nodeInst = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == task.NodeInstanceId, ct);

        if (nodeInst is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"NodeInstance {task.NodeInstanceId} not found.");

        if (nodeInst.State != NodeState.Activated)
        {
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeAlreadyDecided,
                $"NodeInstance {nodeInst.ID} is in state {nodeInst.State}, not Activated.");
        }

        // 3. Load process instance for tenant isolation and event log.
        var instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.ID == nodeInst.InstanceId, ct);

        if (instance is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"ProcessInstance {nodeInst.InstanceId} not found.");

        // 4. AddDepth guard (O(1) — no chain walk needed; AddDepth stamped at inject time).
        int newDepth = task.AddDepth + 1;
        if (newDepth > _options.MaxAddDepth)
        {
            _logger.LogWarning(
                "AddApproverAsync: MaxAddDepth ({Max}) would be exceeded for task {TaskId} " +
                "(sourceTask.AddDepth={Depth}).",
                _options.MaxAddDepth, taskId, task.AddDepth);
            return WorkflowActionResult.MaxAddDepthExceeded;
        }

        // 5. Deduplicate and validate new approver ITCodes.
        // Exclude the actor themselves and any already-assigned approver at this generation.
        var existingITCodes = await Db.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.NodeInstanceId == nodeInst.ID
                         && t.Generation == nodeInst.Generation
                         && (t.State == TaskState.Pending
                             || t.State == TaskState.NotYetActive
                             || t.State == TaskState.AddedPending))
            .Select(t => t.AssigneeITCode)
            .ToListAsync(ct);

        var existingSet = new HashSet<string>(existingITCodes, StringComparer.OrdinalIgnoreCase);
        var toInject = newApproverITCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => !existingSet.Contains(c))
            .ToList();

        if (toInject.Count == 0)
        {
            // All requested approvers are already on the node — treat as already handled.
            _logger.LogDebug(
                "AddApproverAsync: all requested approvers are already on node {NodeId}. No-op.",
                nodeInst.ID);
            return WorkflowActionResult.AlreadyHandled;
        }

        int delta = toInject.Count;

        // 6. Engine-owned explicit transaction: guarded UPDATE + k INSERT + event log.
        // #310 (WF-290.2): AddApproverAsync now acquires ApprovalTask (shift+INSERT) BEFORE
        // NodeInstance (AddApproversToNodeAsync), matching DelegateTaskAsync's Task→Node order.
        // All human multi-row txns now share one total lock order: ApprovalTask → NodeInstance
        // → ProcessInstance(Seq). The C-backstop (ExecuteInTransactionAsync) is retained as
        // pure defense-in-depth; the (Node,Task) ABBA cycle is now structurally eliminated.
        // On SQLite (unit tests) the classifier never fires — transparent pass-through.
        var addResult = await ExecuteInTransactionAsync(async innerCt =>
        {
            await using var tx = await Db.Database.BeginTransactionAsync(innerCt);
            try
            {
                // Re-read fresh node snapshot inside the transaction for current RowVer + ApproverSetEpoch.
                var freshNode = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(n => n.ID == nodeInst.ID, innerCt)
                    ?? nodeInst; // keep stale as fallback (CAS will fail safely below)

                if (freshNode.State != NodeState.Activated)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeAlreadyDecided,
                        $"NodeInstance {freshNode.ID} left Activated state before transaction started.");
                }

                // #310 (WF-290.2): ApprovalTask writes (shift UPDATE + INSERT) now come BEFORE
                // the NodeInstance CAS (AddApproversToNodeAsync), matching DelegateTaskAsync's
                // Task→Node lock order. This eliminates the (Node,Task) ABBA deadlock cycle.
                // If the NodeInstance CAS fails (casRows==0), the whole txn rolls back atomically —
                // no orphaned task INSERTs escape.

                // 6a. Insert k new tasks (moved before NodeInstance CAS per WF-310 lock-order fix).
                // Sequential mode: compute insertion point based on position.
                // All/Any mode: position ignored; tasks are parallel inboxes.
                var approveMode = freshNode.ApproveMode ?? ApproveMode.Sequential;

                int insertionOrder;
                if (approveMode == ApproveMode.Sequential)
                {
                    if (position == AddPosition.Before)
                    {
                        // Insert before current pointer: shift existing tasks >= pointer up by delta.
                        int pointer = task.SequenceOrder; // pointer == task's order == current active
                        await Db.Set<ApprovalTask>()
                            .Where(t => t.NodeInstanceId == freshNode.ID
                                         && t.Generation == freshNode.Generation
                                         && t.SequenceOrder >= pointer
                                         && (t.State == TaskState.Pending
                                             || t.State == TaskState.NotYetActive
                                             || t.State == TaskState.AddedPending))
                            .ExecuteUpdateAsync(
                                s => s.SetProperty(t => t.SequenceOrder, t => t.SequenceOrder + delta),
                                innerCt);

                        insertionOrder = pointer;
                    }
                    else // After
                    {
                        // Insert after current pointer: shift tasks > pointer up by delta.
                        int pointer = task.SequenceOrder;
                        await Db.Set<ApprovalTask>()
                            .Where(t => t.NodeInstanceId == freshNode.ID
                                         && t.Generation == freshNode.Generation
                                         && t.SequenceOrder > pointer
                                         && (t.State == TaskState.Pending
                                             || t.State == TaskState.NotYetActive
                                             || t.State == TaskState.AddedPending))
                            .ExecuteUpdateAsync(
                                s => s.SetProperty(t => t.SequenceOrder, t => t.SequenceOrder + delta),
                                innerCt);

                        insertionOrder = pointer + 1;
                    }
                }
                else
                {
                    // All/Any: append in parallel (SequenceOrder for non-Sequential is unused for ordering,
                    // but we still assign monotonically increasing values for uniqueness).
                    // freshNode.TotalRequired is the PRE-BUMP value (AddApproversToNodeAsync not yet called).
                    insertionOrder = freshNode.TotalRequired; // append at end (pre-bump value)
                }

                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var newTasks = new List<ApprovalTask>(delta);
                for (int i = 0; i < delta; i++)
                {
                    newTasks.Add(new ApprovalTask
                    {
                        ID              = Guid.NewGuid(),
                        TenantCode      = instance.TenantCode,
                        NodeInstanceId  = freshNode.ID,
                        AssigneeITCode  = toInject[i],
                        // #324: For All/Any modes the node is already Activated; injected tasks
                        // must be immediately actionable (Pending). Sequential uses AddedPending
                        // so the pointer-advance path in ExecuteApproveCompletionAsync activates them.
                        State           = approveMode == ApproveMode.Sequential
                                              ? TaskState.AddedPending
                                              : TaskState.Pending,
                        SequenceOrder   = insertionOrder + i,
                        AddDepth        = newDepth,
                        AddedByITCode   = actorITCode,
                        IsRuntimeInjected = true,
                        Generation      = freshNode.Generation,
                        RowVer          = 0,
                        IsValid         = true,
                    });
                }

                Db.Set<ApprovalTask>().AddRange(newTasks);
                await Db.SaveChangesAsync(innerCt);

                // 6b. Guarded UPDATE: TotalRequired+=delta, ApproverSetEpoch+=1, RowVer+=1.
                // Atomically binds the threshold bump to the epoch guard (FIX-A/B, FIX-G).
                // Comes AFTER task writes per WF-310 lock-order fix (Task→Node).
                var casRows = await GuardedTransition.AddApproversToNodeAsync(
                    Db,
                    freshNode.ID,
                    expectedRowVer: freshNode.RowVer,
                    generation: freshNode.Generation,
                    expectedApproverSetEpoch: freshNode.ApproverSetEpoch,
                    delta: delta,
                    ct: innerCt);

                if (casRows == 0)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    _logger.LogDebug(
                        "AddApproverAsync: AddApproversToNodeAsync CAS returned 0 for node {NodeId} — " +
                        "concurrent actor already modified the approver set.",
                        freshNode.ID);
                    return WorkflowActionResult.AlreadyHandled;
                }

                // 6c. Append event log row.
                var addedCodes = string.Join(",", toInject);
                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, instance.TenantCode,
                    EventAction.AddApprover,
                    nodeKey: freshNode.NodeKey,
                    actorITCode: actorITCode,
                    beforeState: freshNode.State.ToString(),
                    afterState: freshNode.State.ToString(),
                    reason: reason is not null
                        ? $"[加签:{position}→{addedCodes}] {reason}"
                        : $"加签:{position}→{addedCodes}",
                    timeProvider: _timeProvider,
                    ct: innerCt);

                await tx.CommitAsync(innerCt);
                return WorkflowActionResult.Advanced;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, ct);

        if (addResult.Code == WorkflowActionCode.Advanced)
        {
            _logger.LogInformation(
                "AddApproverAsync: injected {Count} task(s) ({ITCodes}) onto node '{NodeKey}' " +
                "(id={NodeId}, position={Position}, depth={Depth}).",
                delta, LogSanitizer.Sanitize(string.Join(",", toInject)), LogSanitizer.Sanitize(nodeInst.NodeKey), nodeInst.ID, position, newDepth);
        }

        return addResult;
    }

    // ── WF-19: DelegateTaskAsync (mid-flight 转办/委托-now) ──────────────────

    /// <inheritdoc/>
    public async Task<WorkflowActionResult> DelegateTaskAsync(
        Guid taskId,
        string actorITCode,
        string delegateeITCode,
        Guid? delegationRuleId = null,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorITCode))
            throw new ArgumentException("actorITCode must not be empty.", nameof(actorITCode));
        if (string.IsNullOrWhiteSpace(delegateeITCode))
            throw new ArgumentException("delegateeITCode must not be empty.", nameof(delegateeITCode));

        // 1. Load the actor's task (RBAC guard: actor must be the current Pending assignee).
        var task = await Db.Set<ApprovalTask>()
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.ID == taskId && t.IsValid == true, ct);

        if (task is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"ApprovalTask {taskId} not found.");

        if (task.AssigneeITCode != actorITCode)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NotAuthorized,
                $"Actor '{actorITCode}' is not the assignee of task {taskId}.");

        if (task.State != TaskState.Pending)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                $"Task {taskId} is in state {task.State}, not Pending.");

        // 2. Load node instance.
        var nodeInst = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == task.NodeInstanceId, ct);

        if (nodeInst is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"NodeInstance {task.NodeInstanceId} not found.");

        if (nodeInst.State != NodeState.Activated)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeAlreadyDecided,
                $"NodeInstance {nodeInst.ID} is in state {nodeInst.State}, not Activated.");

        // 3. Load process instance for tenant isolation and event log.
        var instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.ID == nodeInst.InstanceId, ct);

        if (instance is null)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeClosed,
                $"ProcessInstance {nodeInst.InstanceId} not found.");

        // 4. Collision guard (pre-check): delegatee already holds ANY slot on this node generation.
        // FIX-2: widen from active-only (Pending/AddedPending/NotYetActive) to ANY state.
        // Rationale: the UNIQUE index IX_Wf_ApprovalTask_Node_Assignee_Gen is non-filtered.
        // If the delegatee already VOTED (Approved/Rejected) in this generation and we attempt
        // to reassign their slot to them again, the CAS would collide with the voted row and
        // surface a raw DbUpdateException (HTTP 500) instead of a clean DelegateAlreadyParticipant.
        // Semantically, a human who already voted MUST NOT regain a Pending slot (one human =
        // one slot = one vote per generation).  Widen the pre-check to catch this early.
        var alreadyParticipant = await Db.Set<ApprovalTask>()
            .AsNoTracking()
            .AnyAsync(t => t.NodeInstanceId == nodeInst.ID
                            && t.Generation == nodeInst.Generation
                            && t.AssigneeITCode == delegateeITCode, ct);

        if (alreadyParticipant)
        {
            _logger.LogWarning(
                "DelegateTaskAsync: delegatee '{Delegatee}' already has an active slot on node {NodeId}. " +
                "Refusing mid-flight merge; TotalRequired unchanged.",
                LogSanitizer.Sanitize(delegateeITCode), nodeInst.ID);

            // Log the refused attempt inside its own transaction (append-only, best-effort).
            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.Delegate,
                nodeKey: nodeInst.NodeKey,
                actorITCode: actorITCode,
                beforeState: nodeInst.State.ToString(),
                afterState: nodeInst.State.ToString(),
                reason: $"[委托拒绝: delegatee already an approver] delegate→{delegateeITCode}",
                timeProvider: _timeProvider,
                ct: ct);

            return WorkflowActionResult.DelegateAlreadyParticipant;
        }

        // 5. Engine-owned explicit transaction: guarded ReassignTaskAssigneeAsync + epoch bump + event log.
        // C-backstop (defense-in-depth): DelegateTaskAsync is wrapped in ExecuteInTransactionAsync.
        // #310 (WF-290.2) eliminated the Delegate-vs-AddApprover (Node,Task) ABBA cycle by
        // unifying AddApprover to Task-before-Node lock order. The retry envelope is retained
        // as belt-and-suspenders. On SQLite (unit tests) the classifier never fires.
        // Capture locals for the lambda (task/nodeInst are already captured by ref in the lambda).
        var delegateResult = await ExecuteInTransactionAsync(async innerCt =>
        {
            await using var tx = await Db.Database.BeginTransactionAsync(innerCt);
            try
            {
                // Re-read fresh task snapshot inside the transaction to get current RowVer.
                var freshTask = await Db.Set<ApprovalTask>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(t => t.ID == taskId && t.IsValid == true, innerCt)
                    ?? task; // keep stale as CAS-will-fail-safely fallback

                if (freshTask.State != TaskState.Pending)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    return WorkflowActionResult.WithDetail(WorkflowActionCode.TaskNotActive,
                        $"Task {taskId} is no longer Pending inside transaction.");
                }

                // FIX-1b: re-verify assignee inside the transaction.
                // After the initial RBAC check, a concurrent actor may have reassigned this slot
                // (bumping RowVer).  The ReassignTaskAssigneeAsync CAS now also guards on
                // AssigneeITCode==actorITCode (FIX-1 in GuardedTransition), but returning
                // NotAuthorized here gives a clearer diagnostic than a silent rows==0 → AlreadyHandled.
                if (!string.Equals(freshTask.AssigneeITCode, actorITCode, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    _logger.LogWarning(
                        "DelegateTaskAsync: task {TaskId} assignee changed to '{NewAssignee}' inside " +
                        "transaction (was '{Actor}'). Concurrent reassign won; returning NotAuthorized.",
                        taskId, freshTask.AssigneeITCode, actorITCode);
                    return WorkflowActionResult.WithDetail(WorkflowActionCode.NotAuthorized,
                        $"Task {taskId} was reassigned to '{freshTask.AssigneeITCode}' by a concurrent actor; " +
                        $"'{actorITCode}' is no longer the assignee.");
                }

                // Re-read node inside transaction for current RowVer + ApproverSetEpoch.
                var freshNode = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(n => n.ID == nodeInst.ID, innerCt)
                    ?? nodeInst;

                if (freshNode.State != NodeState.Activated)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeAlreadyDecided,
                        $"NodeInstance {freshNode.ID} left Activated state before transaction.");
                }

                // 5a. Single-statement CAS: reassign slot (FIX-C — TotalRequired NOT touched).
                // FIX-6: pass freshNode.Generation (the in-tx node snapshot), NOT task.Generation.
                // freshNode.Generation is the generation on the node; if a concurrent 回退 bumped
                // the instance generation between our pre-tx check and this CAS,
                // freshNode.Generation > task.Generation and the predicate correctly rejects.
                var casRows = await GuardedTransition.ReassignTaskAssigneeAsync(
                    Db,
                    taskId: taskId,
                    expectedRowVer: freshTask.RowVer,
                    generation: freshNode.Generation,
                    delegateeITCode: delegateeITCode,
                    principalITCode: actorITCode,
                    delegationRuleId: delegationRuleId,
                    delegationExpiresUtc: null, // explicit 转办-now; no window expiry
                    ct: innerCt);

                if (casRows == 0)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    _logger.LogDebug(
                        "DelegateTaskAsync: ReassignTaskAssigneeAsync CAS returned 0 for task {TaskId} — " +
                        "concurrent actor already acted on this task.",
                        taskId);
                    return WorkflowActionResult.AlreadyHandled;
                }

                // FIX-2 note: the unique index IX_Wf_ApprovalTask_Node_Assignee_Gen is non-filtered.
                // The pre-check above now covers voted (Approved/Rejected) delegatees, so the CAS
                // reaching here should never collide with a voted row.  However, between the pre-check
                // and the CAS a second concurrent delegate call could race to the same delegatee.
                // The catch block below handles the DbUpdateException from that narrow window.

                // 5b. Bump node ApproverSetEpoch so in-flight completion CAS re-evaluates eligible actors.
                // Re-read node RowVer after the task CAS to avoid stale-RowVer rejection here.
                var nodeInstFresh = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(n => n.ID == freshNode.ID, innerCt);

                if (nodeInstFresh is not null)
                {
                    await GuardedTransition.AdvanceNodeApproverSetEpochAsync(
                        Db,
                        freshNode.ID,
                        expectedRowVer: nodeInstFresh.RowVer,
                        ct: innerCt);
                    // Epoch bump uses its own guard — rows==0 is benign (completion CAS already committed).
                }

                // 5c. Append event log row (inside the transaction; AppendAsync participates in ambient tx).
                var delegateDetail = reason is not null
                    ? $"[委托→{delegateeITCode}] {reason}"
                    : $"委托→{delegateeITCode}";

                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, instance.TenantCode,
                    EventAction.Delegate,
                    nodeKey: freshNode.NodeKey,
                    actorITCode: actorITCode,
                    beforeState: freshNode.State.ToString(),
                    afterState: freshNode.State.ToString(),
                    reason: delegateDetail,
                    generation: (int)freshNode.Generation,
                    timeProvider: _timeProvider,
                    ct: innerCt);

                await tx.CommitAsync(innerCt);
                return WorkflowActionResult.Advanced;
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
                when (!WorkflowDeadlockClassifier.IsDeadlockVictim(dbEx))
            {
                // FIX-2: race-safe backstop for UNIQUE-index collision (not a deadlock).
                // A second concurrent DelegateTaskAsync call between our pre-check and the CAS could
                // attempt to assign the same delegatee on the same (NodeInstanceId, Generation) pair,
                // colliding with the UNIQUE index IX_Wf_ApprovalTask_Node_Assignee_Gen.
                // Roll back and surface DelegateAlreadyParticipant instead of a raw HTTP 500.
                // Deadlock DbUpdateException is let through to the retry envelope above.
                await tx.RollbackAsync(CancellationToken.None);
                _logger.LogWarning(
                    "DelegateTaskAsync: unique-index collision on task {TaskId} → delegatee '{Delegatee}' " +
                    "already has a row on (NodeInstanceId={NodeId}, Generation={Gen}). " +
                    "Concurrent delegate call won; returning DelegateAlreadyParticipant.",
                    taskId, LogSanitizer.Sanitize(delegateeITCode), task.NodeInstanceId, task.Generation);
                return WorkflowActionResult.DelegateAlreadyParticipant;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, ct);

        if (delegateResult.Code == WorkflowActionCode.Advanced)
        {
            _logger.LogInformation(
                "DelegateTaskAsync: task {TaskId} reassigned from '{Principal}' to '{Delegatee}' " +
                "on node '{NodeKey}' (id={NodeId}).",
                taskId, LogSanitizer.Sanitize(actorITCode), LogSanitizer.Sanitize(delegateeITCode), LogSanitizer.Sanitize(nodeInst.NodeKey), nodeInst.ID);
        }

        return delegateResult;
    }

    // ── WF-19 #284.5: RevokeDelegationAsync (admin revocation sweep) ─────────

    /// <inheritdoc/>
    public async Task<int> RevokeDelegationAsync(
        Guid delegationRuleId,
        string actorITCode,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorITCode))
            throw new ArgumentException("actorITCode must not be empty.", nameof(actorITCode));

        // RBAC guard: caller has already validated admin-level access before calling this.
        // The engine enforces parameter integrity only.

        int revokedCount = 0;
        var outcomeBuffer = new System.Collections.Generic.List<GuardedTransition.RevokeDelegatedTaskOutcome>();

        // Collect revocation outcomes from the sweep (IAsyncEnumerable — one iteration).
        await foreach (var outcome in GuardedTransition.RevokeDelegatedTasksAsync(Db, delegationRuleId, ct))
        {
            outcomeBuffer.Add(outcome);
            if (outcome.Result == GuardedTransition.RevokeSingleTaskResult.Revoked)
                revokedCount++;
        }

        // Write event log rows for each successfully revoked task.
        // Append inside the same DbContext session (no explicit transaction — each event-log
        // append is idempotent and best-effort; partial failure is logged at Warning).
        foreach (var outcome in outcomeBuffer)
        {
            if (outcome.Result != GuardedTransition.RevokeSingleTaskResult.Revoked)
                continue;

            // Minimal event-log data: load instance id + tenant from the task (no join needed
            // because WorkflowEventLogWriter.AppendAsync takes instanceId directly).
            // We read the task here because it was already mutated — we only need NodeInstanceId
            // to find the ProcessInstance for the event log's instanceId field.
            var taskSnap = await Db.Set<ApprovalTask>()
                .AsNoTracking()
                .Where(t => t.ID == outcome.TaskId)
                .Select(t => new { t.NodeInstanceId, t.TenantCode })
                .FirstOrDefaultAsync(ct);

            if (taskSnap is null) continue;

            var nodeSnap = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .Where(n => n.ID == taskSnap.NodeInstanceId)
                .Select(n => new { n.InstanceId, n.NodeKey, n.State, n.Generation })
                .FirstOrDefaultAsync(ct);

            if (nodeSnap is null) continue;

            var revokeDetail = reason is not null
                ? $"[委托撤销 rule={delegationRuleId}] {reason}"
                : $"[委托撤销 rule={delegationRuleId}]";

            try
            {
                await WorkflowEventLogWriter.AppendAsync(
                    Db, nodeSnap.InstanceId, taskSnap.TenantCode,
                    EventAction.Delegate,
                    nodeKey: nodeSnap.NodeKey,
                    actorITCode: actorITCode,
                    beforeState: nodeSnap.State.ToString(),
                    afterState: nodeSnap.State.ToString(),
                    reason: revokeDetail,
                    generation: (int)nodeSnap.Generation,
                    timeProvider: _timeProvider,
                    ct: ct);
            }
            catch (Exception ex)
            {
                // Best-effort audit log — do not fail the revocation on log failure.
                _logger.LogWarning(ex,
                    "RevokeDelegationAsync: event log append failed for task {TaskId} " +
                    "(rule={RuleId}). Revocation itself was successful.",
                    outcome.TaskId, delegationRuleId);
            }
        }

        _logger.LogInformation(
            "RevokeDelegationAsync: rule {RuleId} revoked {Count} task(s) (actor='{Actor}', " +
            "total swept={Total}).",
            delegationRuleId, revokedCount, actorITCode, outcomeBuffer.Count);

        return revokedCount;
    }
}
