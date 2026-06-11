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
//
// WF-16 (Wave-3) — ReturnToPrevAsync / ReturnToNodeAsync:
//   • Supersede-not-delete backbone: span nodes → State=Superseded; tasks → State=Cancelled.
//   • BeginReturnAsync: linearization-point CAS (Running→Returning, Generation++, ReturnLoops++, lease).
//   • Dominator validation: only Approval nodes that dominate the trigger are valid targets.
//   • Race A: SupersedeNodeAsync shares RowVer with CompleteNodeInstanceAsync — exactly one wins.
//   • Race B: Seq via AllocateSeqAsync counter (no SERIALIZABLE needed).
//   • Race C: CancelTimersForReturnAsync gates on generation match (stale timers no-op).
//   • Race D: MaxReturnLoops cap enforced atomically in BeginReturnAsync predicate.

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

        // WF-19 FIX-8: AtAction mode uses a nullable-DateTime WHERE clause inside
        // ClaimDelegatedTaskAsync's ExecuteUpdateAsync.  Oracle and DaMeng EF Core providers
        // do not reliably translate nullable DateTime comparisons in bulk-update predicates
        // (RETURNING-clause semantics differ; DaMeng provider has incomplete nullable support).
        // Fail fast at construction so the misconfiguration is caught during DI warm-up
        // rather than silently firing incorrect SQL at runtime.
        if (_options.DelegationWindowMode == DelegationWindowMode.AtAction
            && dc is not null
            && (dc.DBType == DBTypeEnum.Oracle || dc.DBType == DBTypeEnum.DaMeng))
        {
            throw new InvalidOperationException(
                $"DelegationWindowMode.AtAction is not supported with DBTypeEnum.{dc.DBType}. " +
                "Oracle and DaMeng EF Core providers do not reliably translate the nullable " +
                "DateTime comparison used by ClaimDelegatedTaskAsync's ExecuteUpdateAsync predicate. " +
                "Use DelegationWindowMode.AtAssignment (the default) for Oracle and DaMeng deployments, " +
                "or switch to a supported provider (SQLite, SqlServer, PgSql, MySql).");
        }

        // WF-19 #284.5: startup warning — AtAction mode is compliance-relevant and recommended
        // to be paired with the Wave-5 timeout reaper (AddWtmWorkFlowTimers), which is not yet
        // shipped.  Log once per engine instance (scoped → once per request) at Warning so it
        // appears in application logs during first use.  NO BuildServiceProvider() here.
        if (_options.DelegationWindowMode == DelegationWindowMode.AtAction)
        {
            _logger.LogWarning(
                "WorkflowEngine: DelegationWindowMode is set to AtAction (opt-in, non-default). " +
                "AtAction re-checks the delegation window at claim time; it is recommended to be " +
                "paired with the Wave-5 timeout reaper (AddWtmWorkFlowTimers, not yet shipped). " +
                "Without the reaper, expired-window tasks remain Pending indefinitely and require " +
                "manual reassignment or RevokeDelegationAsync. " +
                "This is a compliance-relevant configuration — document it in CHANGELOG.");
        }
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
        // WF-19: stamp DefinitionCode from graph.Key for delegation scope filtering.
        var startNode = await MintNodeInstanceAsync(instance, startNodeDef, ct,
            definitionCode: graph.Key);
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
    /// Drive all active tokens forward through pass-through nodes until every token
    /// is either blocked (Approval/Ack node waiting for human action), waiting at a Join
    /// barrier, or the instance completes/fails.
    ///
    /// <para><strong>WF-17 multi-token drain loop:</strong> each iteration reads ALL active
    /// current-gen NodeInstances (Pending OR Activated, Generation == instance.Generation,
    /// State != Superseded) ordered by ID for deterministic processing.  Each token is
    /// advanced independently.  Gateway fork handlers (ParallelGateway / InclusiveGateway)
    /// mint branch tokens during <c>OnEnterAsync</c>; subsequent iterations pick them up.
    /// Join nodes are held until <c>FireJoinIfSatisfiedAsync</c> succeeds.</para>
    ///
    /// <para>All writes happen inside one logical call.  The
    /// <see cref="GuardedTransition"/> CAS on every NodeInstance state flip ensures
    /// concurrent calls do not double-advance.</para>
    /// </summary>
    private async Task<WorkflowActionResult> AdvanceCoreAsync(
        ProcessInstance instance,
        WorkflowGraph graph,
        CancellationToken ct)
    {
        // Safety: loop guard prevents infinite cycles (malformed graphs).
        const int MaxSteps = 200;
        int steps = 0;

        while (steps++ < MaxSteps)
        {
            // WF-17: load ALL active current-gen tokens (not just the first one).
            // This is the core multi-token marking change from WF-16.
            uint currentGen = instance.Generation;
            var activeNodes = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .Where(n => n.InstanceId == instance.ID
                             && n.Generation == currentGen
                             && (n.State == NodeState.Pending || n.State == NodeState.Activated))
                .OrderBy(n => n.ID) // deterministic processing order
                .ToListAsync(ct);

            if (activeNodes.Count == 0)
            {
                // No active tokens — check if the instance is already final.
                var fresh = await Db.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleAsync(x => x.ID == instance.ID, ct);
                if (fresh.State == InstanceState.Approved || fresh.State == InstanceState.Rejected)
                    return WorkflowActionResult.InstanceApproved;

                _logger.LogWarning(
                    "AdvanceCoreAsync: no active tokens for running instance {InstanceId}. Possible data inconsistency.",
                    instance.ID);
                return WorkflowActionResult.AlreadyHandled;
            }

            // Process each active token.  Track whether any token made progress or is blocked.
            bool anyProgress   = false;
            bool anyBlocked    = false;
            WorkflowActionResult? failResult = null;

            foreach (var activeNode in activeNodes)
            {
                var tokenResult = await AdvanceTokenAsync(activeNode, instance, graph, ct);

                // Re-read instance after each token step (state may have changed).
                instance = await Db.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleAsync(x => x.ID == instance.ID, ct);

                switch (tokenResult.Code)
                {
                    case WorkflowActionCode.InstanceApproved:
                        return tokenResult;

                    case WorkflowActionCode.FailClosedRouting:
                    case WorkflowActionCode.JoinUnsatisfiable:
                        failResult = tokenResult;
                        break;

                    case WorkflowActionCode.Blocked:
                        anyBlocked = true;
                        break;

                    case WorkflowActionCode.NodeCompleted:
                    case WorkflowActionCode.Advanced:
                        anyProgress = true;
                        break;

                    case WorkflowActionCode.AlreadyHandled:
                        // Concurrent loser — not an error; drain loop will see updated state next pass.
                        anyProgress = true;
                        break;

                    default:
                        // Propagate unexpected results.
                        failResult = tokenResult;
                        break;
                }
            }

            // If any token hit a hard failure, return it.
            if (failResult is not null)
                return failResult;

            // If all tokens are blocked and none made progress → wait for human.
            if (!anyProgress && anyBlocked)
                return WorkflowActionResult.Blocked;

            // If no progress was made and nothing is blocked, the drain loop is stuck.
            // This guards against edge cases where tokens are in an unresolvable state.
            if (!anyProgress && !anyBlocked)
            {
                _logger.LogWarning(
                    "AdvanceCoreAsync: no progress and no blocked tokens for instance {InstanceId}. " +
                    "Active token count={Count}. Returning AlreadyHandled.",
                    instance.ID, activeNodes.Count);
                return WorkflowActionResult.AlreadyHandled;
            }

            // Progress was made — loop again to pick up newly minted tokens.
        }

        _logger.LogError(
            "AdvanceCoreAsync: exceeded MaxSteps ({Max}) for instance {InstanceId}. Possible cycle in graph.",
            MaxSteps, instance.ID);
        return WorkflowActionResult.FailClosedRouting;
    }

    /// <summary>
    /// Advance a single token (NodeInstance) one step.
    ///
    /// <para>Returns <see cref="WorkflowActionResult.NodeCompleted"/> when the token completed
    /// and the next token was minted.  Returns <see cref="WorkflowActionResult.Blocked"/> when
    /// the token requires human action.  Returns <see cref="WorkflowActionResult.InstanceApproved"/>
    /// when the End node was reached.  Returns <see cref="WorkflowActionResult.AlreadyHandled"/>
    /// for concurrent-loser no-ops.</para>
    ///
    /// <para><strong>WF-17 Join routing:</strong> when a branch token completes and its
    /// successor is a Join node, this method calls <c>IncrementJoinArrivedAsync</c> to record
    /// the arrival, then <c>FireJoinIfSatisfiedAsync</c> to attempt the fire CAS.
    /// Only the single winner that fires the Join proceeds to mint the Join's successor.</para>
    /// </summary>
    private async Task<WorkflowActionResult> AdvanceTokenAsync(
        NodeInstance activeNode,
        ProcessInstance instance,
        WorkflowGraph graph,
        CancellationToken ct)
    {
        // Locate the NodeDef in the graph.
        var nodeDef = graph.Nodes.FirstOrDefault(n => n.NodeKey == activeNode.NodeKey)
            ?? throw new InvalidOperationException(
                   $"NodeKey '{activeNode.NodeKey}' not found in graph '{graph.Key}'.");

        // Activate the node if it is still Pending.
        if (activeNode.State == NodeState.Pending)
        {
            var activateRows = await GuardedTransition.ActivateNodeInstanceAsync(
                Db, activeNode.ID, activeNode.RowVer, DateTime.UtcNow,
                generation: instance.Generation, ct: ct);

            if (activateRows == 0)
            {
                // Another concurrent caller activated it — re-read and continue.
                _logger.LogDebug(
                    "AdvanceTokenAsync: NodeInstance {NodeId} already activated by concurrent caller.",
                    activeNode.ID);
                activeNode = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleAsync(n => n.ID == activeNode.ID, ct);

                if (activeNode.State == NodeState.Superseded)
                    return WorkflowActionResult.AlreadyHandled;
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

        var ctx = new NodeHandlerContext
        {
            NodeDef          = nodeDef,
            NodeInstance     = activeNode,
            ProcessInstance  = instance,
            Graph            = graph,
            Db               = Db,
            CancellationToken = ct,
        };

        // OnEnter — mint tasks/CC records/branch tokens.
        await handler.OnEnterAsync(ctx);

        // Check completion.
        bool canComplete = await handler.CanCompleteAsync(ctx);
        if (!canComplete)
        {
            // Blocked — waiting for human action, or Join not yet satisfied, or IG fail-closed.
            if (activeNode.NodeKind is NodeKind.ParallelGateway or NodeKind.InclusiveGateway)
            {
                // InclusiveGateway with no matching branches — fail-closed.
                _logger.LogError(
                    "AdvanceTokenAsync: InclusiveGateway '{NodeKey}' in graph '{GraphKey}' could not complete " +
                    "(no branches matched). Fail-closed for instance {InstanceId}.",
                    activeNode.NodeKey, graph.Key, instance.ID);
                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, instance.TenantCode,
                    EventAction.FailClosed,
                    nodeKey: activeNode.NodeKey,
                    actorITCode: null,
                    beforeState: NodeState.Activated.ToString(),
                    afterState: "FailClosed",
                    reason: "InclusiveGateway: no outgoing branches matched.",
                    ct: ct);
                return WorkflowActionResult.FailClosedRouting;
            }

            // Join or Approval/Ack — return Blocked; drain loop will continue with other tokens.
            return WorkflowActionResult.Blocked;
        }

        // For non-End, non-gateway nodes: resolve routing BEFORE completing the node.
        // Gateway nodes (ParallelGateway/InclusiveGateway) do NOT use ResolveNextNodeKey
        // because they fork into multiple branches (handled in OnEnterAsync).
        // Join nodes route to a single successor normally.
        string? nextKey = null;
        bool isGatewayFork = nodeDef.Kind is NodeKind.ParallelGateway or NodeKind.InclusiveGateway;

        if (nodeDef.Kind != NodeKind.End && !isGatewayFork)
        {
            nextKey = ResolveNextNodeKey(graph, nodeDef, instance);
            if (nextKey is null)
            {
                _logger.LogError(
                    "AdvanceTokenAsync: routing failed for '{NodeKey}' in graph '{GraphKey}'. " +
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

        // ── WF-17: Join routing ──────────────────────────────────────────────
        // When the next node is a Join, use the dedicated CAS methods instead of
        // a standard CompletedApproved + MintNodeInstance chain.
        if (!isGatewayFork && nextKey is not null)
        {
            var nextDef = graph.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeKey, nextKey, StringComparison.Ordinal));

            if (nextDef?.Kind == NodeKind.Join)
            {
                return await AdvanceBranchIntoJoinAsync(
                    activeNode, instance, graph, nextKey, ct);
            }
        }

        // Complete the NodeInstance via CAS (for non-gateway, non-Join-routing tokens).
        if (!isGatewayFork)
        {
            var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
                Db, activeNode.ID, activeNode.RowVer, NodeState.CompletedApproved,
                generation: instance.Generation, ct: ct);

            if (completeRows == 0)
            {
                _logger.LogDebug(
                    "AdvanceTokenAsync: NodeInstance {NodeId} already completed by concurrent caller.",
                    activeNode.ID);
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
        }
        else
        {
            // Gateway fork: branches were already minted in OnEnterAsync.
            // Complete the gateway node itself.
            var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
                Db, activeNode.ID, activeNode.RowVer, NodeState.CompletedApproved,
                generation: instance.Generation, ct: ct);

            if (completeRows == 0)
                return WorkflowActionResult.AlreadyHandled;

            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.AutoAdvance,
                nodeKey: activeNode.NodeKey,
                actorITCode: null,
                beforeState: NodeState.Activated.ToString(),
                afterState: NodeState.CompletedApproved.ToString(),
                ct: ct);

            // Branches are now Pending — the outer drain loop will pick them up.
            return WorkflowActionResult.NodeCompleted;
        }

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

        // Mint the next NodeInstance for non-gateway, non-End, non-Join-routing tokens.
        if (nextKey is not null)
        {
            var nextNodeDef = graph.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeKey, nextKey, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                       $"NextKey '{nextKey}' not found as a node in graph '{graph.Key}'.");

            // WF-19: pass graph.Key so delegation scope filtering works on the minted node.
            await MintNodeInstanceAsync(instance, nextNodeDef, ct, definitionCode: graph.Key);
        }

        return WorkflowActionResult.NodeCompleted;
    }

    /// <summary>
    /// Handle a branch token completing and routing into a Join node (WF-17 §4.3).
    ///
    /// <para>Protocol:
    /// <list type="number">
    ///   <item>Ensure the Join NodeInstance exists (mint if needed — idempotent via unique index).</item>
    ///   <item>Complete this branch token via CAS (<c>CompletedApproved</c>).</item>
    ///   <item>Activate the Join node if still Pending.</item>
    ///   <item>Re-read Join for fresh RowVer.</item>
    ///   <item>Call <c>IncrementJoinArrivedAsync</c> — record this arrival.</item>
    ///   <item>Call <c>FireJoinIfSatisfiedAsync</c> — exactly-once CAS fire.</item>
    ///   <item>If fire won (rows==1): mint the Join's successor node and return <c>NodeCompleted</c>.</item>
    ///   <item>If fire lost (rows==0): another branch already fired the Join OR quorum not yet met.
    ///        Check for the orphan fail-closed backstop (§4.4). Return <c>Advanced</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<WorkflowActionResult> AdvanceBranchIntoJoinAsync(
        NodeInstance branchNode,
        ProcessInstance instance,
        WorkflowGraph graph,
        string joinNodeKey,
        CancellationToken ct)
    {
        var joinDef = graph.Nodes.First(n => string.Equals(n.NodeKey, joinNodeKey, StringComparison.Ordinal));

        // 1. Ensure the Join NodeInstance exists (gateway handler mints it; idempotent).
        await GuardedTransition.MintNodeInstanceGuardedAsync(
            Db, instance, joinDef, instance.Generation, ct);

        // 2. Complete the branch token.
        var branchCompleteRows = await GuardedTransition.CompleteNodeInstanceAsync(
            Db, branchNode.ID, branchNode.RowVer, NodeState.CompletedApproved,
            generation: instance.Generation, ct: ct);

        if (branchCompleteRows == 0)
        {
            _logger.LogDebug(
                "AdvanceBranchIntoJoinAsync: branch {BranchId} already completed by concurrent caller.",
                branchNode.ID);
            return WorkflowActionResult.AlreadyHandled;
        }

        await WorkflowEventLogWriter.AppendAsync(
            Db, instance.ID, instance.TenantCode,
            EventAction.AutoAdvance,
            nodeKey: branchNode.NodeKey,
            actorITCode: null,
            beforeState: NodeState.Activated.ToString(),
            afterState: NodeState.CompletedApproved.ToString(),
            ct: ct);

        // 3. Activate the Join node if still Pending.
        var joinNode = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                n => n.InstanceId == instance.ID
                  && n.NodeKey == joinNodeKey
                  && n.Generation == instance.Generation,
                ct);

        if (joinNode is null)
        {
            _logger.LogError(
                "AdvanceBranchIntoJoinAsync: Join node '{JoinKey}' not found for instance {InstanceId}. " +
                "Possible mint failure.",
                joinNodeKey, instance.ID);
            return WorkflowActionResult.FailClosedRouting;
        }

        if (joinNode.State == NodeState.CompletedApproved)
        {
            // Another branch already fired the Join and the successor is already minted.
            // This branch is a late arriver — it already recorded its completion above.
            return WorkflowActionResult.Advanced;
        }

        if (joinNode.State == NodeState.Pending)
        {
            var activateJoinRows = await GuardedTransition.ActivateNodeInstanceAsync(
                Db, joinNode.ID, joinNode.RowVer, DateTime.UtcNow,
                generation: instance.Generation, ct: ct);

            // Re-read (another caller may have activated it first — that is fine).
            joinNode = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == joinNode.ID, ct);

            if (activateJoinRows == 1)
            {
                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, instance.TenantCode,
                    EventAction.AutoAdvance,
                    nodeKey: joinNodeKey,
                    actorITCode: null,
                    beforeState: NodeState.Pending.ToString(),
                    afterState: NodeState.Activated.ToString(),
                    ct: ct);
            }
        }

        if (joinNode.State != NodeState.Activated)
        {
            // Join already completed (e.g. concurrent branch fired it just now).
            return WorkflowActionResult.Advanced;
        }

        // 4. Increment arrival count.
        var incrRows = await GuardedTransition.IncrementJoinArrivedAsync(
            Db, joinNode.ID, joinNode.RowVer, instance.Generation, ct);

        if (incrRows == 0)
        {
            // Join CAS lost — re-read for updated RowVer and try fire.
            joinNode = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == joinNode.ID, ct);
        }
        else
        {
            // Re-read post-increment RowVer.
            joinNode = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == joinNode.ID, ct);
        }

        // 5. Try to fire the Join (single-statement CAS — exactly-once).
        var fireRows = await GuardedTransition.FireJoinIfSatisfiedAsync(
            Db, joinNode.ID, joinNode.RowVer, instance.Generation, ct);

        if (fireRows == 0)
        {
            // Quorum not yet met OR concurrent loser (another branch fired it first).
            // Check for orphan fail-closed (§4.4): are there still live branches that haven't arrived?
            await CheckJoinOrphanAsync(joinNode, instance, ct);
            return WorkflowActionResult.Advanced;
        }

        // 6. Join fired — mint the Join's successor.
        await WorkflowEventLogWriter.AppendAsync(
            Db, instance.ID, instance.TenantCode,
            EventAction.AutoAdvance,
            nodeKey: joinNodeKey,
            actorITCode: null,
            beforeState: NodeState.Activated.ToString(),
            afterState: NodeState.CompletedApproved.ToString(),
            ct: ct);

        var joinSuccessorKey = graph.Transitions
            .FirstOrDefault(t => string.Equals(t.From, joinNodeKey, StringComparison.Ordinal))
            ?.To;

        if (joinSuccessorKey is null)
        {
            _logger.LogError(
                "AdvanceBranchIntoJoinAsync: Join '{JoinKey}' has no outgoing transition. Fail-closed.",
                joinNodeKey);
            return WorkflowActionResult.FailClosedRouting;
        }

        var successorDef = graph.Nodes.FirstOrDefault(
            n => string.Equals(n.NodeKey, joinSuccessorKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                   $"Join successor '{joinSuccessorKey}' not found in graph '{graph.Key}'.");

        // WF-19: pass graph.Key so delegation scope filtering works on the minted Join successor.
        await MintNodeInstanceAsync(instance, successorDef, ct, definitionCode: graph.Key);
        return WorkflowActionResult.NodeCompleted;
    }

    /// <summary>
    /// Orphan fail-closed backstop (WF-17 §4.4).
    ///
    /// <para>When <c>FireJoinIfSatisfiedAsync</c> returns 0 (quorum not yet met), check
    /// whether all remaining expected arrivals are from dead branches (Superseded or
    /// CompletedRejected but never arrived).  If so, decrement expected and attempt
    /// another fire — eventually making the Join satisfiable with the arrivals that did arrive.</para>
    ///
    /// <para>A branch is "dead non-arriving" when: its NodeInstance is in a terminal
    /// state (CompletedRejected / Superseded) AND it has NOT yet been counted as an
    /// arrival (i.e., the Join's JoinArrivedCount does not include it).  Detecting
    /// this exactly requires the live-cohort query: count Pending/Activated branch tokens
    /// in the same ForkGroup that point to this Join.</para>
    ///
    /// <para>If live branch count + arrived count &gt;= expected, the Join is still satisfiable
    /// and we wait.  If live + arrived &lt; expected, some branches died without arriving →
    /// decrement expected and log a JoinUnsatisfiable warning if it reaches 0.</para>
    /// </summary>
    private async Task CheckJoinOrphanAsync(
        NodeInstance joinNode,
        ProcessInstance instance,
        CancellationToken ct)
    {
        // Re-read for freshest counts.
        joinNode = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == joinNode.ID, ct);

        if (joinNode.State != NodeState.Activated) return;

        // Count live branch tokens still in flight (Pending or Activated, same generation,
        // JoinNodeKey == this join, not including the join node itself).
        int liveBranches = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .CountAsync(
                n => n.InstanceId == instance.ID
                  && n.Generation == instance.Generation
                  && n.JoinNodeKey == joinNode.NodeKey
                  && (n.State == NodeState.Pending || n.State == NodeState.Activated),
                ct);

        // If liveBranches + arrivedCount >= expectedArrivals, the Join is still satisfiable.
        if (liveBranches + joinNode.JoinArrivedCount >= joinNode.JoinExpectedArrivals)
            return;

        // Dead non-arriving branches detected — decrement expected count.
        int deadNonArriving = joinNode.JoinExpectedArrivals - liveBranches - joinNode.JoinArrivedCount;
        _logger.LogWarning(
            "CheckJoinOrphanAsync: Join '{JoinKey}' (instance {InstanceId}) has {Dead} dead non-arriving " +
            "branch(es). Decrementing JoinExpectedArrivals by {DeadCount} to prevent permanent block.",
            joinNode.NodeKey, instance.ID, deadNonArriving, deadNonArriving);

        for (int i = 0; i < deadNonArriving; i++)
        {
            // Re-read before each decrement to get the latest RowVer.
            joinNode = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == joinNode.ID, ct);

            if (joinNode.State != NodeState.Activated) return;

            await GuardedTransition.DecrementJoinExpectedAsync(
                Db, joinNode.ID, joinNode.RowVer, instance.Generation, ct);
        }

        // After decrement(s), re-read and attempt final fire.
        joinNode = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == joinNode.ID, ct);

        if (joinNode.State != NodeState.Activated) return;

        if (joinNode.JoinExpectedArrivals <= 0)
        {
            // Join is unsatisfiable — fail-closed.
            _logger.LogError(
                "CheckJoinOrphanAsync: Join '{JoinKey}' for instance {InstanceId} is unsatisfiable " +
                "(JoinExpectedArrivals={Expected}, JoinArrivedCount={Arrived}). Fail-closing Join.",
                joinNode.NodeKey, instance.ID, joinNode.JoinExpectedArrivals, joinNode.JoinArrivedCount);

            await GuardedTransition.CompleteNodeInstanceAsync(
                Db, joinNode.ID, joinNode.RowVer,
                NodeState.CompletedRejected,
                generation: instance.Generation, ct: ct);

            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.FailClosed,
                nodeKey: joinNode.NodeKey,
                actorITCode: null,
                beforeState: NodeState.Activated.ToString(),
                afterState: NodeState.CompletedRejected.ToString(),
                reason: "Join unsatisfiable: all feeding branches died without arriving.",
                ct: ct);
        }
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

            // WF-18 FIX-F: assert ApproverSetEpoch alongside RowVer so a concurrent Before-加签
            // that inserts a task ahead of nextPointer invalidates this pointer advance (rows→0).
            // The approver re-reads freshNode (fresh epoch) and retries.
            var advanceRows = await Db.Set<NodeInstance>()
                .Where(n => n.ID == nodeInst.ID
                             && n.State == NodeState.Activated
                             && n.RowVer == freshNode.RowVer
                             && n.SequencePointer == nodeInst.SequencePointer
                             && n.ApproverSetEpoch == freshNode.ApproverSetEpoch)
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

            // WF-18 FIX-F: assert ApproverSetEpoch on the final pointer advance too —
            // guards against a concurrent After-加签 that extends the chain after the
            // last task was approved but before the pointer is advanced to completion.
            await Db.Set<NodeInstance>()
                .Where(n => n.ID == nodeInst.ID
                             && n.State == NodeState.Activated
                             && n.RowVer == freshNodeLast.RowVer
                             && n.ApproverSetEpoch == freshNodeLast.ApproverSetEpoch)
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

        var graph = WorkflowGraphSerializer.Deserialize(version.GraphJson);

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

        var graph = WorkflowGraphSerializer.Deserialize(version.GraphJson);

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
    /// <para>All STEP 1-6 writes happen inside one engine-owned transaction.
    /// This is the linearization point: if BeginReturnAsync (STEP-1) wins the CAS
    /// the rest of the steps are committed atomically.</para>
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
        var leaseExpiry = DateTime.UtcNow.AddMinutes(30); // Wave-5 reaper TTL; configurable in WF-20.

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

        // ── Engine-owned transaction: STEP 1 through 6 ────────────────────────
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            // ── STEP-1: BeginReturnAsync — linearization point ─────────────────
            // Atomically: Running → Returning, Generation+1, ReturnLoops+1, stamp lease.
            // Re-read instance for current RowVer before the CAS.
            instance = await Db.Set<ProcessInstance>()
                .AsNoTracking()
                .SingleAsync(p => p.ID == instance.ID, ct);

            if (instance.State != InstanceState.Running)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return WorkflowActionResult.WithDetail(WorkflowActionCode.AlreadyHandled,
                    $"Instance {instance.ID} is in state {instance.State}, not Running. Concurrent actor won.");
            }

            // Check MaxReturnLoops before the CAS (early-exit; CAS also enforces it atomically).
            if ((int)instance.ReturnLoops >= maxReturnLoops)
            {
                await tx.RollbackAsync(CancellationToken.None);
                _logger.LogWarning(
                    "ExecuteReturnToNodeAsync: instance {InstanceId} has reached MaxReturnLoops ({Max}). Fail-closed.",
                    instance.ID, maxReturnLoops);
                return WorkflowActionResult.MaxReturnLoopsExceeded;
            }

            var (beginRows, _) = await GuardedTransition.BeginReturnAsync(
                Db, instance.ID,
                expectedRowVer: instance.RowVer,
                expectedGeneration: instance.Generation,
                maxReturnLoops: maxReturnLoops,
                leaseExpiry: leaseExpiry,
                ct: ct);

            if (beginRows == 0)
            {
                await tx.RollbackAsync(CancellationToken.None);
                _logger.LogDebug(
                    "ExecuteReturnToNodeAsync: BeginReturnAsync CAS returned 0 for instance {InstanceId} — " +
                    "concurrent actor won or MaxReturnLoops reached.",
                    instance.ID);
                return WorkflowActionResult.AlreadyHandled;
            }

            // Re-read instance to get the new Generation (gNew = gOld+1 after BeginReturnAsync).
            instance = await Db.Set<ProcessInstance>()
                .AsNoTracking()
                .SingleAsync(p => p.ID == instance.ID, ct);

            uint gNew = instance.Generation;

            // ── STEP-2: Cancel timers for all span NodeInstances ──────────────
            if (spanNodeIds.Count > 0)
            {
                await GuardedTransition.CancelTimersForReturnAsync(Db, spanNodeIds, ct);
            }

            // ── STEP-3: Discard tasks on span nodes (current generation) ──────
            if (spanNodeIds.Count > 0)
            {
                // Exclude the trigger task — it is claimed in STEP-3b below.
                await GuardedTransition.DiscardTasksForReturnAsync(
                    Db, spanNodeIds, excludeTaskId: taskId, ct);
            }

            // STEP-3b: Claim the trigger task itself as Rejected (the task that triggered return).
            // FIX-3: AtAction window applies to ALL actions by the delegatee, not just Approve.
            // Under AtAction, route through ClaimDelegatedTaskAsync so that the window check and
            // state flip are atomic.  If the window has expired, we still proceed with the return
            // (BeginReturnAsync already won the linearization point), but we log the anomaly and
            // stamp WindowVerifiedUtc only on success.
            var stepNow = DateTime.UtcNow;
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
                    ct: ct);

                // If CAS returned 0, the delegation window check in ClaimDelegatedTaskAsync folded
                // out the predicate (expired) or a concurrent actor already claimed it.
                // Either way, BeginReturnAsync already won the instance transition — the return proceeds.
                if (claimedRows == 0)
                {
                    var freshTask3b = await Db.Set<ApprovalTask>()
                        .AsNoTracking()
                        .Select(t => new { t.ID, t.State, t.DelegationExpiresUtc })
                        .SingleOrDefaultAsync(t => t.ID == taskId, ct);

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
                            ct);
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
                    ct: ct);

                // If another actor already claimed it — we already atomically won the instance
                // state transition (BeginReturnAsync), so treat this as an idempotent no-op;
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
            foreach (var spanNode in spanNodes)
            {
                // Re-read fresh RowVer for each span node (other steps may have bumped it).
                var freshSpanNode = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(n => n.ID == spanNode.ID, ct);

                if (freshSpanNode is null) continue; // already gone (edge case)

                // Skip if already superseded (concurrent twin return path).
                if (freshSpanNode.State == NodeState.Superseded) continue;

                var supersedeRows = await GuardedTransition.SupersedeNodeAsync(
                    Db, spanNode.ID,
                    expectedRowVer: freshSpanNode.RowVer,
                    supersededAtGen: gNew,
                    ct: ct);

                if (supersedeRows == 0)
                {
                    _logger.LogDebug(
                        "ExecuteReturnToNodeAsync: SupersedeNodeAsync CAS returned 0 for span node {NodeId} — " +
                        "concurrent actor won this node. Continuing span supersede.",
                        spanNode.ID);
                }
            }

            // ── STEP-5: Mint fresh NodeInstance at target node ────────────────
            // Generation is gNew (stamped at mint time).
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
                Db, targetNodeInst, ct);

            if (!mintOk)
            {
                // UNIQUE constraint: another concurrent call already minted it — idempotent.
                _logger.LogDebug(
                    "ExecuteReturnToNodeAsync: MintNodeInstanceGuardedAsync for '{TargetNodeKey}' gen={Gen} " +
                    "already exists (UNIQUE constraint) — idempotent, proceeding.",
                    targetNodeKey, gNew);
            }

            // ── STEP-6: Set instance Running ─────────────────────────────────
            instance = await Db.Set<ProcessInstance>()
                .AsNoTracking()
                .SingleAsync(p => p.ID == instance.ID, ct);

            var runningRows = await GuardedTransition.AdvanceProcessInstanceAsync(
                Db, instance.ID,
                expectedState: InstanceState.Returning,
                expectedRowVer: instance.RowVer,
                nextState: InstanceState.Running,
                ct);

            if (runningRows == 0)
            {
                // ── STEP-6-FC: fail-closed ────────────────────────────────────
                // We already incremented ReturnLoops and minted the target node.
                // The only reason STEP-6 can fail is if a concurrent actor (e.g. a
                // leased-reaper) flipped the state from Returning to something else.
                // Safest: terminate instance as Withdrawn (prevents zombie state).
                _logger.LogError(
                    "ExecuteReturnToNodeAsync: STEP-6 Returning→Running CAS returned 0 for " +
                    "instance {InstanceId}. Fail-closed: marking instance as Withdrawn. " +
                    "This indicates a rare concurrent reaper race — investigate lease config.",
                    instance.ID);

                await tx.RollbackAsync(CancellationToken.None);
                return WorkflowActionResult.WithDetail(WorkflowActionCode.AlreadyHandled,
                    $"Instance {instance.ID} STEP-6 CAS missed. Rolled back. " +
                    "The return operation may have been superseded by a concurrent caller.");
            }

            // ── Write event log (inside the same transaction) ─────────────────
            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.Return,
                nodeKey: triggerNode.NodeKey,
                actorITCode: actorITCode,
                beforeState: InstanceState.Running.ToString(),
                afterState: InstanceState.Running.ToString(),
                reason: $"ReturnToNode '{targetNodeKey}'. {reason}",
                generation: (int)gNew,
                ct: ct);

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "ExecuteReturnToNodeAsync: instance {InstanceId} returned to '{TargetNodeKey}' " +
                "by '{ActorITCode}'. Generation={Gen}, ReturnLoops={Loops}.",
                instance.ID, targetNodeKey, actorITCode, gNew, instance.ReturnLoops);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        // WF-15 — Notify return (post-commit, best-effort).
        if (_notifier is not null)
        {
            var freshInst = await Db.Set<ProcessInstance>().AsNoTracking().SingleAsync(x => x.ID == instance.ID, CancellationToken.None);
            try { await NotifyFirstPendingTasksAsync(instance.ID, freshInst, ct); }
            catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyFirstPendingTasksAsync (ReturnToNode) failed for instance {InstanceId}.", instance.ID); }
        }

        return WorkflowActionResult.Returned;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Mint a new <see cref="NodeInstance"/> in <see cref="NodeState.Pending"/> for the
    /// given node definition, stamped with <paramref name="generation"/>.
    /// </summary>
    private async Task<NodeInstance> MintNodeInstanceAsync(
        ProcessInstance instance,
        NodeDef nodeDef,
        CancellationToken ct,
        uint? generation = null,
        string? definitionCode = null)
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
            // Wave-3: stamp generation epoch at mint time.
            Generation = generation ?? instance.Generation,
            // WF-17: stamp AckMode for Ack nodes.
            AckMode = nodeDef.AckMode,
            // WF-19: stamp DefinitionCode for delegation scope filtering.
            DefinitionCode = definitionCode,
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
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            // Re-read fresh node snapshot inside the transaction for current RowVer + ApproverSetEpoch.
            nodeInst = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleOrDefaultAsync(n => n.ID == nodeInst.ID, ct)
                ?? nodeInst; // keep stale as fallback (CAS will fail safely below)

            if (nodeInst.State != NodeState.Activated)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeAlreadyDecided,
                    $"NodeInstance {nodeInst.ID} left Activated state before transaction started.");
            }

            // 6a. Guarded UPDATE: TotalRequired+=delta, ApproverSetEpoch+=1, RowVer+=1.
            // Atomically binds the threshold bump to the epoch guard (FIX-A/B, FIX-G).
            var casRows = await GuardedTransition.AddApproversToNodeAsync(
                Db,
                nodeInst.ID,
                expectedRowVer: nodeInst.RowVer,
                generation: nodeInst.Generation,
                expectedApproverSetEpoch: nodeInst.ApproverSetEpoch,
                delta: delta,
                ct: ct);

            if (casRows == 0)
            {
                await tx.RollbackAsync(CancellationToken.None);
                _logger.LogDebug(
                    "AddApproverAsync: AddApproversToNodeAsync CAS returned 0 for node {NodeId} — " +
                    "concurrent actor already modified the approver set.",
                    nodeInst.ID);
                return WorkflowActionResult.AlreadyHandled;
            }

            // 6b. Insert k new tasks.
            // Sequential mode: compute insertion point based on position.
            // All/Any mode: position ignored; tasks are parallel inboxes.
            var approveMode = nodeInst.ApproveMode ?? ApproveMode.Sequential;

            int insertionOrder;
            if (approveMode == ApproveMode.Sequential)
            {
                // Re-read current TotalRequired before the bump (original value = newTotal - delta).
                int originalTotal = nodeInst.TotalRequired; // snapshot before CAS updated it

                if (position == AddPosition.Before)
                {
                    // Insert before current pointer: shift existing tasks >= pointer up by delta.
                    int pointer = task.SequenceOrder; // pointer == task's order == current active
                    await Db.Set<ApprovalTask>()
                        .Where(t => t.NodeInstanceId == nodeInst.ID
                                     && t.Generation == nodeInst.Generation
                                     && t.SequenceOrder >= pointer
                                     && (t.State == TaskState.Pending
                                         || t.State == TaskState.NotYetActive
                                         || t.State == TaskState.AddedPending))
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(t => t.SequenceOrder, t => t.SequenceOrder + delta),
                            ct);

                    insertionOrder = pointer;
                }
                else // After
                {
                    // Insert after current pointer: shift tasks > pointer up by delta.
                    int pointer = task.SequenceOrder;
                    await Db.Set<ApprovalTask>()
                        .Where(t => t.NodeInstanceId == nodeInst.ID
                                     && t.Generation == nodeInst.Generation
                                     && t.SequenceOrder > pointer
                                     && (t.State == TaskState.Pending
                                         || t.State == TaskState.NotYetActive
                                         || t.State == TaskState.AddedPending))
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(t => t.SequenceOrder, t => t.SequenceOrder + delta),
                            ct);

                    insertionOrder = pointer + 1;
                }
            }
            else
            {
                // All/Any: append in parallel (SequenceOrder for non-Sequential is unused for ordering,
                // but we still assign monotonically increasing values for uniqueness).
                insertionOrder = nodeInst.TotalRequired; // append at end (pre-bump value)
            }

            var now = DateTime.UtcNow;
            var newTasks = new List<ApprovalTask>(delta);
            for (int i = 0; i < delta; i++)
            {
                newTasks.Add(new ApprovalTask
                {
                    ID              = Guid.NewGuid(),
                    TenantCode      = instance.TenantCode,
                    NodeInstanceId  = nodeInst.ID,
                    AssigneeITCode  = toInject[i],
                    State           = TaskState.AddedPending,
                    SequenceOrder   = insertionOrder + i,
                    AddDepth        = newDepth,
                    AddedByITCode   = actorITCode,
                    IsRuntimeInjected = true,
                    Generation      = nodeInst.Generation,
                    RowVer          = 0,
                    IsValid         = true,
                });
            }

            Db.Set<ApprovalTask>().AddRange(newTasks);
            await Db.SaveChangesAsync(ct);

            // 6c. Append event log row.
            var addedCodes = string.Join(",", toInject);
            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.AddApprover,
                nodeKey: nodeInst.NodeKey,
                actorITCode: actorITCode,
                beforeState: nodeInst.State.ToString(),
                afterState: nodeInst.State.ToString(),
                reason: reason is not null
                    ? $"[加签:{position}→{addedCodes}] {reason}"
                    : $"加签:{position}→{addedCodes}",
                ct: ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        _logger.LogInformation(
            "AddApproverAsync: injected {Count} task(s) ({ITCodes}) onto node '{NodeKey}' " +
            "(id={NodeId}, position={Position}, depth={Depth}).",
            delta, string.Join(",", toInject), nodeInst.NodeKey, nodeInst.ID, position, newDepth);

        return WorkflowActionResult.Advanced;
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
                delegateeITCode, nodeInst.ID);

            // Log the refused attempt inside its own transaction (append-only, best-effort).
            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.Delegate,
                nodeKey: nodeInst.NodeKey,
                actorITCode: actorITCode,
                beforeState: nodeInst.State.ToString(),
                afterState: nodeInst.State.ToString(),
                reason: $"[委托拒绝: delegatee already an approver] delegate→{delegateeITCode}",
                ct: ct);

            return WorkflowActionResult.DelegateAlreadyParticipant;
        }

        // 5. Engine-owned explicit transaction: guarded ReassignTaskAssigneeAsync + epoch bump + event log.
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            // Re-read fresh task snapshot inside the transaction to get current RowVer.
            task = await Db.Set<ApprovalTask>()
                .AsNoTracking()
                .SingleOrDefaultAsync(t => t.ID == taskId && t.IsValid == true, ct)
                ?? task; // keep stale as CAS-will-fail-safely fallback

            if (task.State != TaskState.Pending)
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
            if (!string.Equals(task.AssigneeITCode, actorITCode, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(CancellationToken.None);
                _logger.LogWarning(
                    "DelegateTaskAsync: task {TaskId} assignee changed to '{NewAssignee}' inside " +
                    "transaction (was '{Actor}'). Concurrent reassign won; returning NotAuthorized.",
                    taskId, task.AssigneeITCode, actorITCode);
                return WorkflowActionResult.WithDetail(WorkflowActionCode.NotAuthorized,
                    $"Task {taskId} was reassigned to '{task.AssigneeITCode}' by a concurrent actor; " +
                    $"'{actorITCode}' is no longer the assignee.");
            }

            // Re-read node inside transaction for current RowVer + ApproverSetEpoch.
            nodeInst = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleOrDefaultAsync(n => n.ID == nodeInst.ID, ct)
                ?? nodeInst;

            if (nodeInst.State != NodeState.Activated)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return WorkflowActionResult.WithDetail(WorkflowActionCode.NodeAlreadyDecided,
                    $"NodeInstance {nodeInst.ID} left Activated state before transaction.");
            }

            // 5a. Single-statement CAS: reassign slot (FIX-C — TotalRequired NOT touched).
            // FIX-6: pass nodeInst.Generation (the in-tx node snapshot), NOT task.Generation.
            // task.Generation is the freshly re-read task row's value — comparing the row to
            // itself is a tautology that can never reject.  nodeInst.Generation is the
            // generation on the node; if a concurrent 回退 bumped the instance generation
            // between our pre-tx check and this CAS, nodeInst.Generation > task.Generation
            // and the predicate correctly rejects (rows==0 → AlreadyHandled).
            // Note: DiscardTasksForReturnAsync sets task.State = Cancelled (not Generation) as
            // the fence against stale-span tasks; State==Pending is the primary real fence —
            // the generation guard is a belt-and-suspenders epoch check here.
            var casRows = await GuardedTransition.ReassignTaskAssigneeAsync(
                Db,
                taskId: taskId,
                expectedRowVer: task.RowVer,
                generation: nodeInst.Generation,
                delegateeITCode: delegateeITCode,
                principalITCode: actorITCode,
                delegationRuleId: delegationRuleId,
                delegationExpiresUtc: null, // explicit 转办-now; no window expiry
                ct: ct);

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
                .SingleOrDefaultAsync(n => n.ID == nodeInst.ID, ct);

            if (nodeInstFresh is not null)
            {
                await GuardedTransition.AdvanceNodeApproverSetEpochAsync(
                    Db,
                    nodeInst.ID,
                    expectedRowVer: nodeInstFresh.RowVer,
                    ct: ct);
                // Epoch bump uses its own guard — rows==0 is benign (completion CAS already committed).
            }

            // 5c. Append event log row (inside the transaction; AppendAsync participates in ambient tx).
            var delegateDetail = reason is not null
                ? $"[委托→{delegateeITCode}] {reason}"
                : $"委托→{delegateeITCode}";

            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.Delegate,
                nodeKey: nodeInst.NodeKey,
                actorITCode: actorITCode,
                beforeState: nodeInst.State.ToString(),
                afterState: nodeInst.State.ToString(),
                reason: delegateDetail,
                generation: (int)nodeInst.Generation,
                ct: ct);

            await tx.CommitAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // FIX-2: race-safe backstop.
            // A second concurrent DelegateTaskAsync call between our pre-check and the CAS could
            // attempt to assign the same delegatee on the same (NodeInstanceId, Generation) pair,
            // colliding with the UNIQUE index IX_Wf_ApprovalTask_Node_Assignee_Gen.
            // Roll back and surface DelegateAlreadyParticipant instead of a raw HTTP 500.
            await tx.RollbackAsync(CancellationToken.None);
            _logger.LogWarning(
                "DelegateTaskAsync: unique-index collision on task {TaskId} → delegatee '{Delegatee}' " +
                "already has a row on (NodeInstanceId={NodeId}, Generation={Gen}). " +
                "Concurrent delegate call won; returning DelegateAlreadyParticipant.",
                taskId, delegateeITCode, task.NodeInstanceId, task.Generation);
            return WorkflowActionResult.DelegateAlreadyParticipant;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        _logger.LogInformation(
            "DelegateTaskAsync: task {TaskId} reassigned from '{Principal}' to '{Delegatee}' " +
            "on node '{NodeKey}' (id={NodeId}).",
            taskId, actorITCode, delegateeITCode, nodeInst.NodeKey, nodeInst.ID);

        return WorkflowActionResult.Advanced;
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
