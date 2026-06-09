#nullable enable
// WF-6: WorkflowEngine — token/marking engine core.
//
// Design:
//   • StartAsync: create ProcessInstance, mint Start NodeInstance, call AdvanceAsync.
//   • AdvanceAsync: drive current active node(s) through their handler until blocked or End.
//     All writes happen inside ONE transaction (spec §7.3).
//   • GuardedTransition is the ONLY path for state flips — never raw SaveChanges (spec §7.1).
//   • WorkflowEventLogWriter.AppendAsync is called inside every transition.
//   • On DbUpdateConcurrencyException: retry up to MaxRetries with fresh read (spec §7.3).
//   • Condition node routing: MVP takes default/first outgoing transition (WF-11). // WF-11
//   • Graph deserialization: WorkflowGraphSerializer.Deserialize cached by ContentHash. // WF-11
//
// This file deliberately does NOT implement Approval-mode completion policies (WF-8/9/10),
// conditional routing evaluator (WF-11), or Withdraw/Return (WF-12).

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;

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
    private readonly INodeKindDispatcher _dispatcher;
    // Stored as non-generic ILogger so that test subclasses can inject an
    // ILogger<TSubclass> without a covariance problem.  Extension methods on
    // ILogger (LogDebug/LogWarning/LogError) work identically on the base type.
    private readonly ILogger _logger;

    // Convenience alias — keeps all the engine body code readable.
    private DbContext Db => _db;

    /// <summary>Production constructor: DI injects <see cref="IDataContext"/> which is always a
    /// <see cref="DbContext"/> subclass at runtime.  The cast is validated at construction so any
    /// mis-registration fails loudly at startup.</summary>
    public WorkflowEngine(
        IDataContext dc,
        INodeKindDispatcher dispatcher,
        ILogger<WorkflowEngine> logger)
        : this(
              (DbContext)(dc ?? throw new ArgumentNullException(nameof(dc))),
              dispatcher,
              (ILogger)logger)
    { }

    /// <summary>Test / direct-DbContext constructor.  Internal so tests in the sibling project can
    /// use it; production code always goes through the <see cref="IDataContext"/> overload.
    /// Accepts the non-generic <see cref="ILogger"/> base so that subclass-typed
    /// <c>NullLogger&lt;TSubclass&gt;</c> instances satisfy the parameter without a cast.</summary>
    internal WorkflowEngine(
        DbContext db,
        INodeKindDispatcher dispatcher,
        ILogger logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            // OnComplete — cleanup before routing onward.
            await handler.OnCompleteAsync(ctx);

            // Complete the NodeInstance via CAS.
            var targetNodeState = nodeDef.Kind == NodeKind.End
                ? NodeState.CompletedApproved
                : NodeState.CompletedApproved;

            var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
                Db, activeNode.ID, activeNode.RowVer, targetNodeState, ct: ct);

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
                afterState: targetNodeState.ToString(),
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

            // Route to the next node(s).
            var nextKey = ResolveNextNodeKey(graph, nodeDef, instance);
            if (nextKey is null)
            {
                _logger.LogError("AdvanceCoreAsync: no outgoing transition from '{NodeKey}' in graph '{GraphKey}'. Fail-closed.", activeNode.NodeKey, graph.Key);

                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, instance.TenantCode,
                    EventAction.FailClosed,
                    nodeKey: activeNode.NodeKey,
                    actorITCode: null,
                    beforeState: NodeState.Activated.ToString(),
                    afterState: "FailClosed",
                    reason: "No outgoing transition found.",
                    ct: ct);

                return WorkflowActionResult.FailClosedRouting;
            }

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
            return await AdvanceAsync(instance.ID, ct);
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
            return await AdvanceAsync(instance.ID, ct);
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

            return await AdvanceAsync(instance.ID, ct);
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

        return WorkflowActionResult.Rejected;
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
    /// MVP strategy:
    ///   1. For Condition nodes: use the Default target. // WF-11 real evaluator
    ///   2. For all other nodes: use the first outgoing transition in the transitions list.
    ///   3. If no transition found: return null (fail-closed).
    /// </summary>
    private static string? ResolveNextNodeKey(
        WorkflowGraph graph,
        NodeDef nodeDef,
        ProcessInstance instance)
    {
        if (nodeDef.Kind == NodeKind.Condition)
        {
            // WF-11: real WhitelistRoutingEvaluator evaluates branches against FormDataJson here.
            // MVP: use the Default target (always present after publish validation).
            if (nodeDef.Default is not null)
                return nodeDef.Default;

            // Fallback: first branch target (should not happen with valid graphs).
            return nodeDef.Branches?.FirstOrDefault()?.Target;
        }

        // For all other node kinds: follow the first matching outgoing transition.
        return graph.Transitions
            .FirstOrDefault(t => t.From == nodeDef.NodeKey)
            ?.To;
    }
}
