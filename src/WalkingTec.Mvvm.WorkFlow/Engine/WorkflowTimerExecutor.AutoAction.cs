#nullable enable
// WorkflowTimerExecutor — AutoAction region (HandleAutoActionAsync + post-commit notify).
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
            var seqFc = await GuardedTransition.AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
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

            var seqFc2 = await GuardedTransition.AllocateSeqWithRetryAsync(db, instanceId, instanceRowVer, ct);
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
}
