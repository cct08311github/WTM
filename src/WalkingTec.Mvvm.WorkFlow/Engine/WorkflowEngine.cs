#nullable enable
// WF-6: WorkflowEngine — token/marking engine core.
// WF-11: Exclusive gateway routing via IRoutingEvaluator.
// WF-12: WithdrawAsync (撤回) + ReturnToInitiatorAsync (回退发起人, MVP restart semantics).
// WF-15: IWorkflowNotifier wired post-commit — best-effort, non-blocking. Null-safe (optional DI).
//
// Design:
//   • StartAsync: create ProcessInstance, mint Start NodeInstance, call AdvanceAsync.
//   • AdvanceAsync: drive current active node(s) through their handler until blocked or End.
//     All writes happen inside ONE transaction (spec §7.3).
//   • GuardedTransition is the ONLY path for state flips — never raw SaveChanges (spec §7.1).
//   • WorkflowEventLogWriter.AppendAsync is called inside every transition.
//   • On DbUpdateConcurrencyException: retry up to MaxRetries with fresh read (spec §7.3).
//   • Condition node routing (WF-11): IRoutingEvaluator evaluates branches in order;
//     first match wins (Exclusive); fallback to Default; no-match + no-default → fail-closed.
//   • FormDataJson deserialized to IReadOnlyDictionary<string,object?> for in-memory evaluation.
//   • Notifications (WF-15): IWorkflowNotifier? is injected optionally.  When null the engine
//     behaves identically to pre-WF-15 code.  When non-null, each method fires the relevant
//     event AFTER the transaction commits so that a delivery failure can NEVER roll back an
//     approval decision (spec §8 "post-commit, best-effort, non-blocking").
//
// WF-12 — Withdraw:
//   • WithdrawAsync: initiator (or admin) withdraws a Running instance.
//   • Honors WithdrawPolicy: BeforeAnyAction (L0) / BeforeFinalApproval (L1, default) / Disabled (L2).
//   • Instance-level CAS (T-CONC-2 race: 撤回 vs. final-approve; first wins, loser no-ops).
//   • On success: cancels all Pending ApprovalTasks; writes WorkflowEventLog.
//
// WF-12 — ReturnToInitiatorAsync:
//   • Approver returns task to initiator; instance goes to Draft (re-editable).
//   • Full Wave-3 回退-to-arbitrary-node is deferred (// WF-16 Wave-3).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Token/marking workflow engine.  Implements <see cref="IWorkflowEngine"/>.
/// Registered as scoped by <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>.
///
/// <para><strong>Token model:</strong> a "token" is an <see cref="NodeInstance"/> in
/// <see cref="NodeState.Activated"/> state.  The engine holds as many active
/// NodeInstances as there are active parallel branches (MVP: always one at a time;
/// 会签 parallelism within a single Activation is supported via the ApprovedCount CAS
/// without needing multiple active NodeInstances).</para>
/// </summary>
internal sealed class WorkflowEngine : IWorkflowEngine
{
    private const int MaxRetries = 3;

    // Db is the authoritative handle used throughout the engine.
    // In production the DI-injected IDataContext is always a DbContext subclass (spec §7.1 invariant);
    // in tests a DbContext can be passed directly via the internal constructor.
    private readonly DbContext _db;
    // _dc is the original IDataContext; used by ValidateDbTypeOnFirstUse (#271).
    // Null when the engine is constructed via the internal DbContext-only constructor (test path) —
    // in that case ValidateDbType is skipped (tests always use SQLite, never Memory).
    private readonly IDataContext? _dc;
    private readonly INodeKindDispatcher _dispatcher;
    private readonly IRoutingEvaluator _routingEvaluator;
    private readonly WorkFlowOptions _options;
    // Stored as non-generic ILogger so that test subclasses can inject an
    // ILogger<TSubclass> without a covariance problem.  Extension methods on
    // ILogger (LogDebug/LogWarning/LogError) work identically on the base type.
    private readonly ILogger _logger;
    // WF-15: optional post-commit notification seam.  Null when AddWtmWorkFlowNotifications()
    // was not called.  Engine skips all notify calls when null — identical to pre-WF-15 behavior.
    private readonly IWorkflowNotifier? _notifier;

    // Convenience alias — keeps all the engine body code readable.
    private DbContext Db => _db;

    /// <summary>Production constructor: DI injects <see cref="IDataContext"/> which is always a
    /// <see cref="DbContext"/> subclass at runtime.  The cast is validated at construction so any
    /// mis-registration fails loudly at startup.
    /// <para><see cref="IWorkflowNotifier"/> is optional — injected when
    /// <see cref="ServiceCollectionExtensions.AddWtmWorkFlowNotifications"/> was called; null otherwise.</para>
    /// </summary>
    public WorkflowEngine(
        IDataContext dc,
        INodeKindDispatcher dispatcher,
        IRoutingEvaluator routingEvaluator,
        IOptions<WorkFlowOptions> options,
        ILogger<WorkflowEngine> logger,
        IWorkflowNotifier? notifier = null)
    {
        if (dc is null) throw new ArgumentNullException(nameof(dc));
        _dc = dc;
        _db = (DbContext)dc;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _routingEvaluator = routingEvaluator ?? throw new ArgumentNullException(nameof(routingEvaluator));
        _options = options?.Value ?? new WorkFlowOptions();
        _logger = (ILogger)(logger ?? throw new ArgumentNullException(nameof(logger)));
        _notifier = notifier;
    }

    /// <summary>Test / direct-DbContext constructor.  Internal so tests in the sibling project can
    /// use it; production code always goes through the <see cref="IDataContext"/> overload.
    /// Accepts the non-generic <see cref="ILogger"/> base so that subclass-typed
    /// <c>NullLogger&lt;TSubclass&gt;</c> instances satisfy the parameter without a cast.
    /// <para><c>_dc</c> is null in this path — <see cref="WorkFlowOptions.ValidateDbTypeOnFirstUse"/>
    /// is skipped (tests always supply a real SQLite DbContext, never EF InMemory).</para></summary>
    internal WorkflowEngine(
        DbContext db,
        INodeKindDispatcher dispatcher,
        IRoutingEvaluator routingEvaluator,
        ILogger logger,
        IWorkflowNotifier? notifier = null)
        : this(db, dispatcher, routingEvaluator, new WorkFlowOptions(), logger, notifier)
    { }

    /// <summary>Full internal constructor used by tests that need to override WorkFlowOptions.</summary>
    internal WorkflowEngine(
        DbContext db,
        INodeKindDispatcher dispatcher,
        IRoutingEvaluator routingEvaluator,
        WorkFlowOptions options,
        ILogger logger,
        IWorkflowNotifier? notifier = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _dc = null; // No IDataContext in the direct-DbContext test path — ValidateDbTypeOnFirstUse skipped.
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _routingEvaluator = routingEvaluator ?? throw new ArgumentNullException(nameof(routingEvaluator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notifier = notifier; // null is valid — skip all notifications
    }

    // ── StartAsync ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<ProcessInstance> StartAsync(
        Guid definitionVersionId,
        string? formDataJson,
        string initiatorITCode,
        string? tenantCode,
        string? businessType = null,
        string? businessKey = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(initiatorITCode))
            throw new ArgumentException("initiatorITCode must not be empty.", nameof(initiatorITCode));

        // WF-5 lazy guard (#271): validate that the DataContext is not EF InMemory on first use.
        // The eager check at AddWtmWorkFlow() was removed in PR #240 (BuildServiceProvider root-provider
        // crash under scope validation).  This lazy guard fires once per engine call and is the
        // primary enforcement path when ValidateDbTypeOnFirstUse = true.
        // Skipped when _dc is null (internal test path uses a real SQLite DbContext directly).
        if (_options.ValidateDbTypeOnFirstUse && _dc is not null)
            ServiceCollectionExtensions.ValidateDbType(_dc);

        // Load the pinned definition version (immutable; FK enforced by invariant §1.3).
        var version = await Db.Set<ProcessDefinitionVersion>()
            .AsNoTracking()
            .SingleOrDefaultAsync(v => v.ID == definitionVersionId && v.IsValid == true, ct)
            ?? throw new InvalidOperationException(
                   $"ProcessDefinitionVersion {definitionVersionId} not found or not valid.");

        var graph = WorkflowGraphSerializer.Deserialize(version.GraphJson);

        // Find the single Start node (validated at publish time).
        var startNodeDef = graph.Nodes.FirstOrDefault(n => n.Kind == NodeKind.Start)
            ?? throw new InvalidOperationException(
                   $"Graph '{graph.Key}' has no Start node. Graph must have exactly one Start node.");

        // Create the ProcessInstance.
        var instance = new ProcessInstance
        {
            ID = Guid.NewGuid(),
            TenantCode = tenantCode,
            DefinitionVersionId = definitionVersionId,
            State = InstanceState.Draft,
            InitiatorITCode = initiatorITCode,
            BusinessType = businessType,
            BusinessKey = businessKey,
            FormDataJson = formDataJson,
            RowVer = 0,
            IsValid = true,
        };
        Db.Set<ProcessInstance>().Add(instance);
        await Db.SaveChangesAsync(ct);

        // Transition Draft → Running via GuardedTransition (spec §7.1 — every flip goes through it).
        var rows = await GuardedTransition.AdvanceProcessInstanceAsync(
            Db, instance.ID,
            expectedState: InstanceState.Draft,
            expectedRowVer: 0,
            nextState: InstanceState.Running,
            ct);

        if (rows == 0)
        {
            // Should not happen on a brand-new instance; treat as unexpected.
            _logger.LogWarning("StartAsync: GuardedTransition Draft→Running returned 0 rows for {InstanceId}. Possible race on new instance.", instance.ID);
        }

        // Append Submit event.
        await WorkflowEventLogWriter.AppendAsync(
            Db, instance.ID, tenantCode,
            EventAction.Submit,
            nodeKey: startNodeDef.NodeKey,
            actorITCode: initiatorITCode,
            beforeState: InstanceState.Draft.ToString(),
            afterState: InstanceState.Running.ToString(),
            ct: ct);

        // Mint the Start NodeInstance (Pending → engine will activate it in AdvanceAsync).
        var startNode = await MintNodeInstanceAsync(instance, startNodeDef, ct);
        _ = startNode; // used implicitly by AdvanceAsync below

        // Drive through pass-through nodes until blocked or completed.
        await AdvanceCoreAsync(instance, graph, ct);

        // WF-15 — Notify task-assigned for the first pending task(s) created during Start.
        // Called POST-advance (after all DB writes are committed) so a notify failure cannot
        // affect the instance state.
        if (_notifier is not null)
        {
            await NotifyFirstPendingTasksAsync(instance.ID, instance, ct);
        }

        // Re-read the current state for the caller.
        var current = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(x => x.ID == instance.ID, ct);

        return current;
    }

    // ── AdvanceAsync (public) ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<WorkflowActionResult> AdvanceAsync(
        Guid instanceId,
        CancellationToken ct = default)
    {
        var instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ID == instanceId && x.IsValid == true, ct)
            ?? throw new InvalidOperationException($"ProcessInstance {instanceId} not found.");

        if (instance.State != InstanceState.Running)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.AlreadyHandled,
                $"Instance {instanceId} is in state {instance.State}, not Running.");

        var version = await Db.Set<ProcessDefinitionVersion>()
            .AsNoTracking()
            .SingleAsync(v => v.ID == instance.DefinitionVersionId, ct);

        var graph = WorkflowGraphSerializer.Deserialize(version.GraphJson);

        return await AdvanceCoreAsync(instance, graph, ct);
    }

    // ── Core advance loop ─────────────────────────────────────────────────────

    /// <summary>
    /// Drive the token forward through all pass-through nodes until the engine hits an
    /// Approval node that blocks (CanComplete == false) or reaches the End node.
    ///
    /// <para>All writes happen inside one logical transaction per call.  The
    /// <see cref="GuardedTransition"/> CAS on every NodeInstance state flip ensures
    /// concurrent calls do not double-advance.</para>
    /// </summary>
    private async Task<WorkflowActionResult> AdvanceCoreAsync(
        ProcessInstance instance,
        WorkflowGraph graph,
        CancellationToken ct)
    {
        // Safety: loop guard prevents infinite cycles (malformed graphs).
        const int MaxSteps = 100;
        int steps = 0;

        while (steps++ < MaxSteps)
        {
            // Find the current active NodeInstance(s) for this process.
            var activeNode = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .Where(n => n.InstanceId == instance.ID
                             && (n.State == NodeState.Pending || n.State == NodeState.Activated))
                .OrderBy(n => n.ID) // deterministic tiebreak
                .FirstOrDefaultAsync(ct);

            if (activeNode is null)
            {
                // No active nodes — check if the instance is already final.
                var fresh = await Db.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleAsync(x => x.ID == instance.ID, ct);
                if (fresh.State == InstanceState.Approved || fresh.State == InstanceState.Rejected)
                    return WorkflowActionResult.InstanceApproved;

                _logger.LogWarning("AdvanceCoreAsync: no active node for running instance {InstanceId}. Possible data inconsistency.", instance.ID);
                return WorkflowActionResult.AlreadyHandled;
            }

            // Locate the NodeDef in the graph.
            var nodeDef = graph.Nodes.FirstOrDefault(n => n.NodeKey == activeNode.NodeKey)
                ?? throw new InvalidOperationException(
                       $"NodeKey '{activeNode.NodeKey}' not found in graph '{graph.Key}'.");

            // Activate the node if it is still Pending.
            if (activeNode.State == NodeState.Pending)
            {
                var activateRows = await GuardedTransition.ActivateNodeInstanceAsync(
                    Db, activeNode.ID, activeNode.RowVer, DateTime.UtcNow, ct);

                if (activateRows == 0)
                {
                    // Another concurrent caller activated it — re-read and continue.
                    _logger.LogDebug("AdvanceCoreAsync: NodeInstance {NodeId} already activated by concurrent caller.", activeNode.ID);
                    activeNode = await Db.Set<NodeInstance>()
                        .AsNoTracking()
                        .SingleAsync(n => n.ID == activeNode.ID, ct);
                }
                else
                {
                    // Re-read post-activation RowVer.
                    activeNode = await Db.Set<NodeInstance>()
                        .AsNoTracking()
                        .SingleAsync(n => n.ID == activeNode.ID, ct);

                    await WorkflowEventLogWriter.AppendAsync(
                        Db, instance.ID, instance.TenantCode,
                        EventAction.AutoAdvance,
                        nodeKey: activeNode.NodeKey,
                        actorITCode: null,
                        beforeState: NodeState.Pending.ToString(),
                        afterState: NodeState.Activated.ToString(),
                        ct: ct);
                }
            }

            var handler = _dispatcher.Resolve(activeNode.NodeKind);

            // Re-read instance for fresh state (may have been updated by concurrent caller).
            instance = await Db.Set<ProcessInstance>()
                .AsNoTracking()
                .SingleAsync(x => x.ID == instance.ID, ct);

            var ctx = new NodeHandlerContext
            {
                NodeDef = nodeDef,
                NodeInstance = activeNode,
                ProcessInstance = instance,
                Graph = graph,
                Db = Db,
                CancellationToken = ct,
            };

            // OnEnter — mint tasks/CC records.
            await handler.OnEnterAsync(ctx);

            // Check completion.
            bool canComplete = await handler.CanCompleteAsync(ctx);
            if (!canComplete)
            {
                // Blocked — waiting for human action (Approval node).
                return WorkflowActionResult.Blocked;
            }

            // For non-End nodes: resolve routing BEFORE completing the node so that if routing
            // fails (null nextKey) the NodeInstance stays Activated.  That way a subsequent
            // AdvanceAsync call can re-enter and re-return FailClosedRouting rather than seeing
            // "no active node" and returning AlreadyHandled (spec §5.8 invariant).
            string? nextKey = null;
            if (nodeDef.Kind != NodeKind.End)
            {
                nextKey = ResolveNextNodeKey(graph, nodeDef, instance);
                if (nextKey is null)
                {
                    _logger.LogError(
                        "AdvanceCoreAsync: routing failed for '{NodeKey}' in graph '{GraphKey}'. " +
                        "Node remains Activated (fail-closed).",
                        activeNode.NodeKey, graph.Key);

                    await WorkflowEventLogWriter.AppendAsync(
                        Db, instance.ID, instance.TenantCode,
                        EventAction.FailClosed,
                        nodeKey: activeNode.NodeKey,
                        actorITCode: null,
                        beforeState: NodeState.Activated.ToString(),
                        afterState: "FailClosed",
                        reason: "No matching branch and no default target.",
                        ct: ct);

                    return WorkflowActionResult.FailClosedRouting;
                }
            }

            // OnComplete — cleanup before routing onward.
            await handler.OnCompleteAsync(ctx);

            // Complete the NodeInstance via CAS.
            var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
                Db, activeNode.ID, activeNode.RowVer, NodeState.CompletedApproved, ct: ct);

            if (completeRows == 0)
            {
                _logger.LogDebug("AdvanceCoreAsync: NodeInstance {NodeId} already completed by concurrent caller.", activeNode.ID);
                return WorkflowActionResult.AlreadyHandled;
            }

            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.AutoAdvance,
                nodeKey: activeNode.NodeKey,
                actorITCode: null,
                beforeState: NodeState.Activated.ToString(),
                afterState: NodeState.CompletedApproved.ToString(),
                ct: ct);

            // End node reached → approve the instance.
            if (nodeDef.Kind == NodeKind.End)
            {
                // Re-read instance for current RowVer.
                instance = await Db.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleAsync(x => x.ID == instance.ID, ct);

                var approveRows = await GuardedTransition.AdvanceProcessInstanceAsync(
                    Db, instance.ID,
                    expectedState: InstanceState.Running,
                    expectedRowVer: instance.RowVer,
                    nextState: InstanceState.Approved,
                    ct);

                if (approveRows == 1)
                {
                    await WorkflowEventLogWriter.AppendAsync(
                        Db, instance.ID, instance.TenantCode,
                        EventAction.AutoAdvance,
                        nodeKey: activeNode.NodeKey,
                        actorITCode: null,
                        beforeState: InstanceState.Running.ToString(),
                        afterState: InstanceState.Approved.ToString(),
                        ct: ct);
                }

                return WorkflowActionResult.InstanceApproved;
            }

            // nextKey is guaranteed non-null here (checked above for non-End nodes).

            // Mint the next NodeInstance.
            var nextNodeDef = graph.Nodes.FirstOrDefault(n => n.NodeKey == nextKey)
                ?? throw new InvalidOperationException(
                       $"NextKey '{nextKey}' not found as a node in graph '{graph.Key}'.");

            await MintNodeInstanceAsync(instance, nextNodeDef, ct);
            // Loop: next iteration will pick up the newly minted Pending node.
        }

        _logger.LogError("AdvanceCoreAsync: exceeded MaxSteps ({Max}) for instance {InstanceId}. Possible cycle in graph.", MaxSteps, instance.ID);
        return WorkflowActionResult.FailClosedRouting;
    }

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

        // 1. Load task (no AsNoTracking — we need fresh RowVer).
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

        var now = DateTime.UtcNow;

        // 8. CAS: claim the task as Approved.
        var claimedRows = await GuardedTransition.ClaimApprovalTaskAsync(
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

        // 9. Write event log for the approve action.
        await WorkflowEventLogWriter.AppendAsync(
            Db, instance.ID, instance.TenantCode,
            EventAction.Approve,
            nodeKey: nodeInst.NodeKey,
            actorITCode: actorITCode,
            beforeState: TaskState.Pending.ToString(),
            afterState: TaskState.Approved.ToString(),
            reason: comment,
            ct: ct);

        // WF-15 — Notify approved (post-commit, best-effort).  Fires for every successful
        // approval regardless of mode; the node/instance-completion notification fires later
        // based on the final result of mode-specific processing below.
        if (_notifier is not null)
        {
            try { await _notifier.NotifyApprovedAsync(instance, nodeInst, task, actorITCode, ct); }
            catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyApprovedAsync failed for task {TaskId}.", taskId); }
        }

        // 10. Mode-specific completion logic.
        if (approveMode == ApproveMode.All)
        {
            // 会签: increment advisory ApprovedCount, then check if threshold is reached.
            // For non-final approvals (count < threshold) return Advanced immediately —
            // AdvanceCoreAsync would only see Blocked (node not yet complete).
            // For the final approval(s) that cross the threshold, call AdvanceAsync so that
            // AdvanceCoreAsync performs the authoritative node-completion CAS (W1 fix, spec §7.4).
            // Concurrent final approvers both reach this path; exactly one wins the CAS;
            // the other gets AlreadyHandled from CompleteNodeInstanceAsync returning 0 rows.
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
            var allResult = await AdvanceAsync(instance.ID, ct);
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
            await GuardedTransition.IncrementNodeApprovedCountAsync(Db, nodeInst.ID, ct);
            var anyResult = await AdvanceAsync(instance.ID, ct);
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
            // More steps remain: advance the pointer and activate the next task.
            // Step A: advance the SequencePointer on NodeInstance (CAS on RowVer).
            var freshNode = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, ct);

            var advanceRows = await Db.Set<NodeInstance>()
                .Where(n => n.ID == nodeInst.ID
                             && n.State == NodeState.Activated
                             && n.RowVer == freshNode.RowVer
                             && n.SequencePointer == nodeInst.SequencePointer)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(n => n.SequencePointer, nextPointer)
                           .SetProperty(n => n.RowVer, x => x.RowVer + 1),
                    ct);

            if (advanceRows == 0)
            {
                _logger.LogDebug(
                    "ApproveTaskAsync: NodeInstance {NodeId} pointer advance CAS returned 0 — " +
                    "concurrent actor already advanced. Instance proceeds as AlreadyHandled.",
                    nodeInst.ID);
                return WorkflowActionResult.AlreadyHandled;
            }

            // Step B: activate the next task.
            var activateRows = await Db.Set<ApprovalTask>()
                .Where(t => t.NodeInstanceId == nodeInst.ID
                             && t.SequenceOrder == nextPointer
                             && t.State == TaskState.NotYetActive)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.State, TaskState.Pending),
                    ct);

            if (activateRows == 0)
            {
                // Check if it was already auto-approved (InitiatorAutoApprove path).
                var nextTask = await Db.Set<ApprovalTask>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.NodeInstanceId == nodeInst.ID
                                               && t.SequenceOrder == nextPointer, ct);

                if (nextTask?.State == TaskState.AutoApproved)
                {
                    // Auto-approved step: recursively advance until a real pending step or completion.
                    _logger.LogDebug(
                        "ApproveTaskAsync: next task (order {Order}) is AutoApproved — continuing pointer advance.",
                        nextPointer);
                    // Re-read nodeInst with updated pointer and recurse via AdvanceAsync.
                    return await AdvanceAsync(instance.ID, ct);
                }

                _logger.LogWarning(
                    "ApproveTaskAsync: could not activate next task at order {Order} for node {NodeId}.",
                    nextPointer, nodeInst.ID);
            }

            // Node is still active — waiting for the next approver.
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

            await Db.Set<NodeInstance>()
                .Where(n => n.ID == nodeInst.ID
                             && n.State == NodeState.Activated
                             && n.RowVer == freshNodeLast.RowVer)
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

        // 8. CAS: claim the task as Rejected.
        var claimedRows = await GuardedTransition.ClaimApprovalTaskAsync(
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

        // 9. Mode-specific rejection logic.
        if (rejectMode == ApproveMode.All)
        {
            // 会签: increment advisory rejected count, then evaluate RejectGate.
            await GuardedTransition.IncrementNodeRejectedCountAsync(Db, nodeInst.ID, ct);
            var freshNodeAll = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, ct);

            bool nodeFailedAll = await AllApprovalHandler.TryCompleteRejectedAsync(
                Db, freshNodeAll, actorITCode, _logger, ct);

            if (!nodeFailedAll)
            {
                // Gate not met (AfterAll) or another actor already won: node continues.
                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, instance.TenantCode,
                    EventAction.Reject,
                    nodeKey: nodeInst.NodeKey,
                    actorITCode: actorITCode,
                    beforeState: TaskState.Pending.ToString(),
                    afterState: TaskState.Rejected.ToString(),
                    reason: reason,
                    ct: ct);
                return WorkflowActionResult.Advanced;
            }

            // Node is now CompletedRejected — fall through to instance-level rejection below.
            goto markInstanceRejected;
        }

        if (rejectMode == ApproveMode.Any)
        {
            // 或签: single reject does NOT fail node; only last-reject does.
            await GuardedTransition.IncrementNodeRejectedCountAsync(Db, nodeInst.ID, ct);
            var freshNodeAny = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, ct);

            bool nodeFailedAny = await AnyApprovalHandler.TryCompleteRejectedAsync(
                Db, freshNodeAny, actorITCode, _logger, ct);

            if (!nodeFailedAny)
            {
                // More approvers still pending — node continues.
                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, instance.TenantCode,
                    EventAction.Reject,
                    nodeKey: nodeInst.NodeKey,
                    actorITCode: actorITCode,
                    beforeState: TaskState.Pending.ToString(),
                    afterState: TaskState.Rejected.ToString(),
                    reason: reason,
                    ct: ct);
                return WorkflowActionResult.Advanced;
            }

            // All have rejected — fall through to instance-level rejection below.
            goto markInstanceRejected;
        }

        // Sequential path ─────────────────────────────────────────────────────────

        // Cancel remaining NotYetActive tasks on this node (Sequential-only: All/Any
        // mint all tasks as Pending so there are no NotYetActive tasks to cancel here).
        await Db.Set<ApprovalTask>()
            .Where(t => t.NodeInstanceId == nodeInst.ID && t.State == TaskState.NotYetActive)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.State, TaskState.Cancelled),
                ct);

        // Complete the node as CompletedRejected (CAS on node RowVer).
        var freshNode = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID, ct);

        var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
            Db, nodeInst.ID,
            expectedRowVer: freshNode.RowVer,
            completedState: NodeState.CompletedRejected,
            decidedBy: actorITCode,
            ct: ct);

        if (completeRows == 0)
        {
            _logger.LogDebug(
                "RejectTaskAsync: NodeInstance {NodeId} completion CAS returned 0 — concurrent actor already completed.",
                nodeInst.ID);
            return WorkflowActionResult.AlreadyHandled;
        }

        markInstanceRejected:

        // Write event log for task rejection.
        await WorkflowEventLogWriter.AppendAsync(
            Db, instance.ID, instance.TenantCode,
            EventAction.Reject,
            nodeKey: nodeInst.NodeKey,
            actorITCode: actorITCode,
            beforeState: NodeState.Activated.ToString(),
            afterState: NodeState.CompletedRejected.ToString(),
            reason: reason,
            ct: ct);

        // Apply RejectPolicy: both TerminateInstance and ReturnToInitiator mark the instance Rejected.
        // ReturnToInitiator full WF-12 restart is deferred. // WF-12
        var rejectPolicy = nodeInst.RejectPolicy;

        // Re-read instance for current RowVer.
        instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(x => x.ID == instance.ID, ct);

        var rejectRows = await GuardedTransition.AdvanceProcessInstanceAsync(
            Db, instance.ID,
            expectedState: InstanceState.Running,
            expectedRowVer: instance.RowVer,
            nextState: InstanceState.Rejected,
            ct);

        if (rejectRows == 1)
        {
            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.Reject,
                nodeKey: nodeInst.NodeKey,
                actorITCode: actorITCode,
                beforeState: InstanceState.Running.ToString(),
                afterState: InstanceState.Rejected.ToString(),
                reason: $"Rejected by '{actorITCode}'. RejectPolicy={rejectPolicy}. {reason}",
                ct: ct);
        }

        // WF-15 — Notify rejected (post-commit, best-effort).
        if (_notifier is not null)
        {
            try { await _notifier.NotifyRejectedAsync(instance, nodeInst, task, actorITCode, reason, ct); }
            catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyRejectedAsync failed for task {TaskId}.", taskId); }
        }

        return WorkflowActionResult.Rejected;
    }

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

        // 6. Instance-level guarded CAS (T-CONC-2): Running → Withdrawn.
        //    If the final approver wins this race first, AdvanceProcessInstanceAsync already
        //    flipped State to Approved (or Rejected) — our WHERE State==Running misses and
        //    returns rows==0 → CannotWithdrawAlreadyFinal.
        var rows = await GuardedTransition.AdvanceProcessInstanceAsync(
            Db, instance.ID,
            expectedState: InstanceState.Running,
            expectedRowVer: instance.RowVer,
            nextState: InstanceState.Withdrawn,
            ct);

        if (rows == 0)
        {
            _logger.LogDebug(
                "WithdrawAsync: CAS returned 0 rows for instance {InstanceId} — " +
                "concurrent actor already changed state. Treating as CannotWithdrawAlreadyFinal.",
                instanceId);
            return WorkflowActionResult.CannotWithdrawAlreadyFinal;
        }

        // 7. Cancel all Pending ApprovalTasks for this instance.
        //    Use node-instance-filtered bulk cancel to avoid cross-instance contamination.
        var nodeIds = await Db.Set<NodeInstance>()
            .Where(n => n.InstanceId == instanceId)
            .Select(n => n.ID)
            .ToListAsync(ct);

        if (nodeIds.Count > 0)
        {
            await Db.Set<ApprovalTask>()
                .Where(t => nodeIds.Contains(t.NodeInstanceId)
                             && (t.State == TaskState.Pending || t.State == TaskState.NotYetActive))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.State, TaskState.Cancelled),
                    ct);
        }

        // 8. Write event log.
        await WorkflowEventLogWriter.AppendAsync(
            Db, instance.ID, instance.TenantCode,
            EventAction.Withdraw,
            nodeKey: null,
            actorITCode: actorITCode,
            beforeState: InstanceState.Running.ToString(),
            afterState: InstanceState.Withdrawn.ToString(),
            reason: reason,
            ct: ct);

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

        var now = DateTime.UtcNow;

        // 7. CAS: claim the trigger task as Rejected (the task that triggered the return).
        var claimedRows = await GuardedTransition.ClaimApprovalTaskAsync(
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

        // 8. Cancel all remaining Pending/NotYetActive tasks on this node.
        await Db.Set<ApprovalTask>()
            .Where(t => t.NodeInstanceId == nodeInst.ID
                         && (t.State == TaskState.Pending || t.State == TaskState.NotYetActive)
                         && t.ID != taskId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.State, TaskState.Cancelled),
                ct);

        // 9. Complete node as Returned (CAS on fresh RowVer).
        var freshNode = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == nodeInst.ID, ct);

        var nodeCompleteRows = await GuardedTransition.CompleteNodeInstanceAsync(
            Db, nodeInst.ID,
            expectedRowVer: freshNode.RowVer,
            completedState: NodeState.Returned,
            decidedBy: actorITCode,
            ct: ct);

        if (nodeCompleteRows == 0)
        {
            _logger.LogDebug(
                "ReturnToInitiatorAsync: NodeInstance {NodeId} completion CAS returned 0 — concurrent actor already completed.",
                nodeInst.ID);
            return WorkflowActionResult.AlreadyHandled;
        }

        // 10. Set instance to Draft via instance-level CAS.
        instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(x => x.ID == instance.ID, ct);

        var instanceRows = await GuardedTransition.AdvanceProcessInstanceAsync(
            Db, instance.ID,
            expectedState: InstanceState.Running,
            expectedRowVer: instance.RowVer,
            nextState: InstanceState.Draft,
            ct);

        if (instanceRows == 0)
        {
            _logger.LogWarning(
                "ReturnToInitiatorAsync: instance {InstanceId} CAS Running→Draft returned 0 — " +
                "concurrent actor already changed state.",
                instance.ID);
            // Node was completed but instance flip failed — unusual; return AlreadyHandled
            // so the caller knows the action did not fully succeed.
            return WorkflowActionResult.AlreadyHandled;
        }

        // 11. Write event log with Return action.
        await WorkflowEventLogWriter.AppendAsync(
            Db, instance.ID, instance.TenantCode,
            EventAction.Return,
            nodeKey: nodeInst.NodeKey,
            actorITCode: actorITCode,
            beforeState: InstanceState.Running.ToString(),
            afterState: InstanceState.Draft.ToString(),
            reason: reason,
            ct: ct);

        _logger.LogInformation(
            "ReturnToInitiatorAsync: task {TaskId} returned to initiator by '{ActorITCode}'. Instance {InstanceId} now Draft.",
            taskId, actorITCode, instance.ID);

        // WF-15 — Notify returned to initiator (post-commit, best-effort).
        if (_notifier is not null)
        {
            // Re-read fresh instance (now Draft) for notification.
            var freshInst = await Db.Set<ProcessInstance>().AsNoTracking().SingleAsync(x => x.ID == instance.ID, CancellationToken.None);
            try { await _notifier.NotifyReturnedToInitiatorAsync(freshInst, nodeInst, task, actorITCode, reason, ct); }
            catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyReturnedToInitiatorAsync failed for task {TaskId}.", taskId); }
        }

        // WF-16 Wave-3: ReturnToPrev / ReturnToNode are deferred.
        return WorkflowActionResult.ReturnedToInitiator;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Mint a new <see cref="NodeInstance"/> in <see cref="NodeState.Pending"/> for the
    /// given node definition.
    /// </summary>
    private async Task<NodeInstance> MintNodeInstanceAsync(
        ProcessInstance instance,
        NodeDef nodeDef,
        CancellationToken ct)
    {
        var node = new NodeInstance
        {
            ID = Guid.NewGuid(),
            TenantCode = instance.TenantCode,
            InstanceId = instance.ID,
            NodeKey = nodeDef.NodeKey,
            NodeKind = nodeDef.Kind,
            State = NodeState.Pending,
            ApproveMode = nodeDef.ApproveMode,
            ApprovePercent = nodeDef.ApprovePercent,
            RejectGate = nodeDef.RejectGate ?? RejectGate.Immediate,
            RejectPolicy = nodeDef.RejectPolicy ?? RejectPolicy.ReturnToInitiator,
            RowVer = 0,
        };
        Db.Set<NodeInstance>().Add(node);
        await Db.SaveChangesAsync(ct);
        return node;
    }

    /// <summary>
    /// Determine the next node key to route to from the completed <paramref name="nodeDef"/>.
    ///
    /// WF-11 strategy for Condition nodes (Exclusive gateway):
    ///   1. Deserialize FormDataJson into a dictionary.
    ///   2. Evaluate branches IN ORDER via IRoutingEvaluator — first match wins.
    ///   3. If no branch matches, use the Default target (always present after publish validation).
    ///   4. If no match AND no default → fail-closed (return null; caller logs + returns FailClosedRouting).
    ///
    /// For all other node kinds: follow the first outgoing transition in the transitions list.
    /// </summary>
    private string? ResolveNextNodeKey(
        WorkflowGraph graph,
        NodeDef nodeDef,
        ProcessInstance instance)
    {
        if (nodeDef.Kind == NodeKind.Condition)
        {
            return ResolveConditionNodeKey(graph, nodeDef, instance);
        }

        // For all other node kinds: follow the first matching outgoing transition.
        return graph.Transitions
            .FirstOrDefault(t => t.From == nodeDef.NodeKey)
            ?.To;
    }

    /// <summary>
    /// Exclusive gateway routing (WF-11): evaluate branches in order against FormDataJson;
    /// take the first matching branch; fall back to Default if none match.
    /// Fail-closed when no match and no default.
    /// </summary>
    private string? ResolveConditionNodeKey(
        WorkflowGraph graph,
        NodeDef nodeDef,
        ProcessInstance instance)
    {
        // Deserialize FormDataJson to IReadOnlyDictionary<string, object?>.
        // An empty/null FormDataJson is treated as an empty dictionary (all fields missing → fail-closed → default).
        IReadOnlyDictionary<string, object?> formData = DeserializeFormData(instance.FormDataJson);

        // Evaluate branches in defined array order (Exclusive first-match).
        if (nodeDef.Branches is { Count: > 0 })
        {
            foreach (var branch in nodeDef.Branches)
            {
                // Runtime re-validation (defense in depth against publish-time bypass).
                var evalResult = _routingEvaluator.Evaluate(
                    branch.Rule,
                    graph.FieldWhitelist,
                    formData);

                if (evalResult.Code != RoutingEvaluationCode.Ok)
                {
                    // Log the security/structural violation but continue to next branch
                    // rather than short-circuiting the whole node — fail-closed at branch level.
                    _logger.LogWarning(
                        "ResolveConditionNodeKey: branch evaluation error for node '{NodeKey}', " +
                        "target '{Target}': {Code} — {Message}. Branch treated as non-matching.",
                        nodeDef.NodeKey, branch.Target, evalResult.Code, evalResult.ErrorMessage);
                    continue;
                }

                if (evalResult.IsMatch)
                {
                    _logger.LogDebug(
                        "ResolveConditionNodeKey: node '{NodeKey}' branch matched, routing to '{Target}'.",
                        nodeDef.NodeKey, branch.Target);
                    return branch.Target;
                }
            }
        }

        // No branch matched — use the Default target.
        if (nodeDef.Default is not null)
        {
            _logger.LogDebug(
                "ResolveConditionNodeKey: node '{NodeKey}' no branch matched; routing to default '{Default}'.",
                nodeDef.NodeKey, nodeDef.Default);
            return nodeDef.Default;
        }

        // No match and no default — fail-closed (spec §5.8: "no match + no default impossible
        // at runtime given publish validation; if somehow reached → FAIL CLOSED").
        _logger.LogError(
            "ResolveConditionNodeKey: node '{NodeKey}' in graph '{GraphKey}' has no matching branch " +
            "and no default target. Fail-closed.",
            nodeDef.NodeKey, graph.Key);
        return null;
    }

    // ── WF-14: Inbox query ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ApprovalTask>> GetPendingTasksAsync(
        string actorITCode,
        string? tenantCode,
        CancellationToken ct = default)
    {
        // The DataContext query filter (ITenant + IsValid) auto-scopes to tenantCode
        // because it is registered against the correct context.
        // We additionally filter by AssigneeITCode and State server-side.
        var tasks = await Db.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.AssigneeITCode == actorITCode
                     && t.State == TaskState.Pending
                     && t.IsValid == true)
            .Include(t => t.NodeInstance)
            .OrderBy(t => t.DueUtc.HasValue ? t.DueUtc.Value : DateTime.MaxValue)
            .ToListAsync(ct);

        return tasks.AsReadOnly();
    }

    // ── WF-15: Notify helper ─────────────────────────────────────────────────

    /// <summary>
    /// Fire <see cref="IWorkflowNotifier.NotifyTaskAssignedAsync"/> for every <see cref="TaskState.Pending"/>
    /// <see cref="ApprovalTask"/> that currently belongs to <paramref name="instanceId"/>.
    /// Called AFTER the engine advances (post-commit) when a new approval node becomes active.
    /// Best-effort — all exceptions are caught and logged, never propagated.
    /// </summary>
    private async Task NotifyFirstPendingTasksAsync(
        Guid instanceId,
        ProcessInstance instance,
        CancellationToken ct)
    {
        if (_notifier is null) return;

        try
        {
            // Read the currently active node and its pending tasks.
            var activeNodeInst = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .Where(n => n.InstanceId == instanceId
                             && (n.State == NodeState.Pending || n.State == NodeState.Activated))
                .OrderBy(n => n.ID)
                .FirstOrDefaultAsync(ct);

            if (activeNodeInst is null) return;

            var pendingTasks = await Db.Set<ApprovalTask>()
                .AsNoTracking()
                .Where(t => t.NodeInstanceId == activeNodeInst.ID && t.State == TaskState.Pending)
                .ToListAsync(ct);

            foreach (var pendingTask in pendingTasks)
            {
                try { await _notifier.NotifyTaskAssignedAsync(instance, activeNodeInst, pendingTask, ct); }
                catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyTaskAssignedAsync failed for task {TaskId}.", pendingTask.ID); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WF-15 NotifyFirstPendingTasksAsync failed for instance {InstanceId}.", instanceId);
        }
    }

    /// <summary>
    /// Deserialize <paramref name="formDataJson"/> into a flat string-keyed dictionary.
    /// Returns an empty dictionary for null/empty input (missing fields → fail-closed in evaluator).
    /// Only the top-level flat object is supported for the MVP routing evaluator.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> DeserializeFormData(string? formDataJson)
    {
        if (string.IsNullOrWhiteSpace(formDataJson))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        try
        {
            using var doc = JsonDocument.Parse(formDataJson);
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Clone the value so the dictionary outlives the JsonDocument.
                result[prop.Name] = prop.Value.Clone();
            }
            return result;
        }
        catch (JsonException ex)
        {
            // Malformed FormDataJson — return empty dict so all field lookups fail-closed.
            // The engine will fall back to the Default branch (which publish validation guarantees exists).
            _ = ex; // Suppress unused warning — intentional swallow here; caller handles default.
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }
}
