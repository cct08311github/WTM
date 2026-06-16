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
    // WF-20.2: business-calendar seam.  Null when AddWtmWorkFlowTimers() was not called.
    // When null the arm helpers skip all timer arming (no timer rows) — fully opt-in.
    private readonly IBusinessCalendar? _businessCalendar;

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
        IWorkflowNotifier? notifier = null,
        IBusinessCalendar? businessCalendar = null)
    {
        if (dc is null) throw new ArgumentNullException(nameof(dc));
        _dc = dc;
        _db = (DbContext)dc;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _routingEvaluator = routingEvaluator ?? throw new ArgumentNullException(nameof(routingEvaluator));
        _options = options?.Value ?? new WorkFlowOptions();
        _logger = (ILogger)(logger ?? throw new ArgumentNullException(nameof(logger)));
        _notifier = notifier;
        _businessCalendar = businessCalendar; // null → timer arming skipped (opt-in via AddWtmWorkFlowTimers)

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
        // to be paired with the Wave-5 timeout reaper (shipped in WF-20.5).
        // Log once per engine instance (scoped → once per request) at Warning so it
        // appears in application logs during first use.  NO BuildServiceProvider() here.
        if (_options.DelegationWindowMode == DelegationWindowMode.AtAction)
        {
            _logger.LogWarning(
                "WorkflowEngine: DelegationWindowMode is set to AtAction (opt-in, non-default). " +
                "AtAction re-checks the delegation window at claim time. " +
                "To automatically revert expired delegations, call AddWtmWorkFlowTimers() and set " +
                "DelegationExpiredSweep=RevertToPrincipal (default) — the reaper phase-3 sweep handles " +
                "expired AtAction tasks each tick. " +
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
        IWorkflowNotifier? notifier = null,
        IBusinessCalendar? businessCalendar = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _dc = null; // No IDataContext in the direct-DbContext test path — ValidateDbTypeOnFirstUse skipped.
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _routingEvaluator = routingEvaluator ?? throw new ArgumentNullException(nameof(routingEvaluator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notifier = notifier; // null is valid — skip all notifications
        _businessCalendar = businessCalendar; // null → timer arming skipped in arm helpers
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

    // ── AdvanceWithActorAsync (private, C10 fix) ─────────────────────────────

    /// <summary>
    /// Internal variant of <see cref="AdvanceAsync"/> that threads the approving actor's
    /// ITCode into <see cref="AdvanceCoreAsync"/> so <see cref="AdvanceTokenAsync"/> can
    /// stamp <c>DecidedBy</c> on the NodeInstance when it completes as CompletedApproved.
    ///
    /// <para>Only called from <see cref="ExecuteApproveCompletionAsync"/> for All/Any modes
    /// (C10 fix). The Sequential path already sets <c>DecidedBy</c> directly via
    /// <see cref="ExecuteRejectCompletionAsync"/>-equivalent CAS.</para>
    /// </summary>
    private async Task<WorkflowActionResult> AdvanceWithActorAsync(
        ProcessInstance instanceSnapshot,
        CancellationToken ct,
        string? actingApproverITCode)
    {
        // Re-read the instance from DB (identical to the check in AdvanceAsync) so that
        // concurrent callers that race here after the IncrementNodeApprovedCountAsync CAS
        // see the fresh state — e.g. the concurrent loser sees Approved and returns
        // AlreadyHandled instead of double-advancing.  Using the stale snapshot would allow
        // both concurrent winners through the state guard before either commits (C10 fix §concurrency).
        var instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ID == instanceSnapshot.ID && x.IsValid == true, ct)
            ?? throw new InvalidOperationException($"ProcessInstance {instanceSnapshot.ID} not found.");

        if (instance.State != InstanceState.Running)
            return WorkflowActionResult.WithDetail(WorkflowActionCode.AlreadyHandled,
                $"Instance {instance.ID} is in state {instance.State}, not Running.");

        var version = await Db.Set<ProcessDefinitionVersion>()
            .AsNoTracking()
            .SingleAsync(v => v.ID == instance.DefinitionVersionId, ct);

        var graph = WorkflowGraphSerializer.Deserialize(version.GraphJson);

        return await AdvanceCoreAsync(instance, graph, ct, actingApproverITCode);
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
        CancellationToken ct,
        string? actingApproverITCode = null)
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
                var tokenResult = await AdvanceTokenAsync(activeNode, instance, graph, ct, actingApproverITCode);

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
        CancellationToken ct,
        string? actingApproverITCode = null)
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

        // WF-20.2: arm timeout timer(s) for Approval nodes after tasks are minted.
        if (activeNode.NodeKind == NodeKind.Approval && nodeDef.Timeout is not null)
        {
            var now20 = DateTime.UtcNow;
            var approveMode = nodeDef.ApproveMode ?? ApproveMode.Sequential;
            if (approveMode == ApproveMode.Sequential)
            {
                // Sequential: arm task-scoped timer for the first active step (SequenceOrder==0, Pending).
                var step0Task = await Db.Set<ApprovalTask>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        t => t.NodeInstanceId == activeNode.ID
                             && t.SequenceOrder == 0
                             && t.State == TaskState.Pending,
                        ct);
                if (step0Task is not null)
                    await ArmTaskTimerIfConfiguredAsync(step0Task, activeNode, nodeDef, now20, ct);
            }
            else
            {
                // All/Any: arm one node-scoped timer (covers all tasks in this node-generation).
                await ArmNodeTimerIfConfiguredAsync(activeNode, nodeDef, now20, ct);
            }
        }

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
            // C10 fix: for Approval nodes completing as CompletedApproved, stamp the actor
            // who triggered the advance (threaded from ApproveTaskAsync via ExecuteApproveCompletionAsync
            // → AdvanceWithActorAsync → AdvanceCoreAsync). Gateway forks and non-Approval nodes
            // pass null (actingApproverITCode is null for auto-advance paths).
            var decidedByForApproval = nodeDef.Kind == NodeKind.Approval ? actingApproverITCode : null;

            // End node reached → approve the instance (T2 txAdvanceApproveEnd, #320 W2).
            if (nodeDef.Kind == NodeKind.End)
            {
                // #320 PR A — T2 txAdvanceApproveEnd: widen txC3Approve upward to include
                // CompleteNode(End) so that a crash between CompleteNode and
                // AdvanceProcessInstance cannot strand a CompletedApproved End node on
                // a still-Running instance.
                //
                // Lock-order: NodeInstance (write) → ProcessInstance (write) — canonical.
                // AppendAsync calls come AFTER the AdvanceProcessInstance CAS (not before)
                // because AllocateSeqAsync bumps ProcessInstance.RowVer; any Append before
                // the CAS would make the captured RowVer stale → CAS returns 0 → happy-path
                // strand (stale-RowVer regression — do NOT reintroduce).
                //
                // Timer cancels are best-effort post-commit — Armed timers whose node is in
                // a terminal generation will fire-and-no-op (gen-gated CAS) without harming
                // correctness.
                await using (var txAdvanceApproveEnd = await Db.Database.BeginTransactionAsync(ct))
                {
                    try
                    {
                        // Step 1: complete the End NodeInstance (CAS guard closes W2 window a).
                        var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
                            Db, activeNode.ID, activeNode.RowVer, NodeState.CompletedApproved,
                            decidedBy: decidedByForApproval,
                            generation: instance.Generation, ct: ct);

                        if (completeRows == 0)
                        {
                            await txAdvanceApproveEnd.RollbackAsync(CancellationToken.None);
                            _logger.LogDebug(
                                "AdvanceTokenAsync: NodeInstance {NodeId} (End) already completed by concurrent caller.",
                                activeNode.ID);
                            return WorkflowActionResult.AlreadyHandled;
                        }

                        // Step 2: re-read instance for fresh RowVer BEFORE any AppendAsync.
                        // AppendAsync calls AllocateSeqAsync which bumps RowVer; reading AFTER
                        // would yield a stale value → AdvanceProcessInstanceAsync CAS returns 0.
                        instance = await Db.Set<ProcessInstance>()
                            .AsNoTracking()
                            .SingleAsync(x => x.ID == instance.ID, ct);

                        // Step 3: advance instance Running → Approved (CAS guard closes W2 window b).
                        var approveRows = await GuardedTransition.AdvanceProcessInstanceAsync(
                            Db, instance.ID,
                            expectedState: InstanceState.Running,
                            expectedRowVer: instance.RowVer,
                            nextState: InstanceState.Approved,
                            ct);

                        if (approveRows != 1)
                        {
                            await txAdvanceApproveEnd.RollbackAsync(CancellationToken.None);
                            _logger.LogDebug(
                                "AdvanceTokenAsync: ProcessInstance {InstanceId} state-flip lost to concurrent caller.",
                                instance.ID);
                            return WorkflowActionResult.AlreadyHandled;
                        }

                        // Steps 4 & 5: audit appends — enlisting in ambient txAdvanceApproveEnd.
                        // Node-level event: End node Activated → CompletedApproved.
                        await WorkflowEventLogWriter.AppendAsync(
                            Db, instance.ID, instance.TenantCode,
                            EventAction.AutoAdvance,
                            nodeKey: activeNode.NodeKey,
                            actorITCode: null,
                            beforeState: NodeState.Activated.ToString(),
                            afterState: NodeState.CompletedApproved.ToString(),
                            ct: ct);

                        // Instance-level event: Running → Approved.
                        await WorkflowEventLogWriter.AppendAsync(
                            Db, instance.ID, instance.TenantCode,
                            EventAction.AutoAdvance,
                            nodeKey: activeNode.NodeKey,
                            actorITCode: null,
                            beforeState: InstanceState.Running.ToString(),
                            afterState: InstanceState.Approved.ToString(),
                            ct: ct);

                        await txAdvanceApproveEnd.CommitAsync(ct);
                    }
                    catch
                    {
                        await txAdvanceApproveEnd.RollbackAsync(CancellationToken.None);
                        throw;
                    }
                }

                // WF-20.2: instance-wide timer cancel — best-effort post-commit.
                var allNodeIds = await Db.Set<NodeInstance>()
                    .Where(n => n.InstanceId == instance.ID)
                    .Select(n => n.ID)
                    .ToListAsync(ct);
                foreach (var nid in allNodeIds)
                    await GuardedTransition.CancelTimersForNodeAsync(Db, nid, ct);

                return WorkflowActionResult.InstanceApproved;
            }

            // Non-End node: T1 txAdvanceComplete (#320 W1).
            // Wraps CompleteNode(source) + MintNode(successor) + Append in one atomic transaction
            // so a crash between CompleteNode and MintNode cannot strand a CompletedApproved source
            // node with no successor minted.
            //
            // Lock-order: NodeInstance (write) only — ProcessInstance is untouched here
            // (Seq bump via AllocateSeqAsync is safe; it bumps RowVer on the same row the
            // AdvanceProcessInstance CAS in T2 will later read with a fresh re-read).
            await using (var txAdvanceComplete = await Db.Database.BeginTransactionAsync(ct))
            {
                try
                {
                    var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
                        Db, activeNode.ID, activeNode.RowVer, NodeState.CompletedApproved,
                        decidedBy: decidedByForApproval,
                        generation: instance.Generation, ct: ct);

                    if (completeRows == 0)
                    {
                        await txAdvanceComplete.RollbackAsync(CancellationToken.None);
                        _logger.LogDebug(
                            "AdvanceTokenAsync: NodeInstance {NodeId} already completed by concurrent caller.",
                            activeNode.ID);
                        return WorkflowActionResult.AlreadyHandled;
                    }

                    // Mint the successor node inside the same transaction (closes W1 crash window).
                    if (nextKey is not null)
                    {
                        var nextNodeDef = graph.Nodes.FirstOrDefault(
                            n => string.Equals(n.NodeKey, nextKey, StringComparison.Ordinal))
                            ?? throw new InvalidOperationException(
                                   $"NextKey '{nextKey}' not found as a node in graph '{graph.Key}'.");

                        // WF-19: pass graph.Key so delegation scope filtering works on the minted node.
                        await MintNodeInstanceAsync(instance, nextNodeDef, ct, definitionCode: graph.Key);
                    }

                    await WorkflowEventLogWriter.AppendAsync(
                        Db, instance.ID, instance.TenantCode,
                        EventAction.AutoAdvance,
                        nodeKey: activeNode.NodeKey,
                        actorITCode: null,
                        beforeState: NodeState.Activated.ToString(),
                        afterState: NodeState.CompletedApproved.ToString(),
                        ct: ct);

                    await txAdvanceComplete.CommitAsync(ct);
                }
                catch
                {
                    await txAdvanceComplete.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }

            // WF-20.2: cancel any Armed timers on this node — best-effort, post-commit.
            await GuardedTransition.CancelTimersForNodeAsync(Db, activeNode.ID, ct);

            return WorkflowActionResult.NodeCompleted;
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

        // 10. Mode-specific completion logic — shared with SystemContinueTaskAsync (WF-20.4).
        // C10 fix: pass actorITCode so All/Any completion can stamp DecidedBy on NodeInstance.
        return await ExecuteApproveCompletionAsync(task.ID, nodeInst, instance, approveMode, ct, actorITCode);
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
        string? actorITCode = null)
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
                    "ExecuteApproveCompletionAsync: NodeInstance {NodeId} pointer advance CAS returned 0 — " +
                    "concurrent actor already advanced. Instance proceeds as AlreadyHandled.",
                    nodeInst.ID);
                return WorkflowActionResult.AlreadyHandled;
            }

            // Step B: activate the next task.
            // FIX-B5a: also match AddedPending (injected steps from WF-18 AddApproverAsync).
            // Injected steps are inserted with State=AddedPending so they are not skipped by
            // the normal NotYetActive→Pending pointer advance (design §2). When the preceding
            // step completes and the pointer reaches their slot, they must be activated here.
            var activateRows = await Db.Set<ApprovalTask>()
                .Where(t => t.NodeInstanceId == nodeInst.ID
                             && t.SequenceOrder == nextPointer
                             && (t.State == TaskState.NotYetActive || t.State == TaskState.AddedPending))
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
                            nextPendingForArm, freshNodeForArm, stepBNodeDef, DateTime.UtcNow, ct);
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

        // 9. Mode-specific rejection logic — shared with SystemContinueTaskAsync (WF-20.4).
        return await ExecuteRejectCompletionAsync(task.ID, nodeInst, instance, task, actorITCode, reason, rejectMode, writeNodeCompletionEvent: true, ct);
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
            await using (var txRejectAll = await Db.Database.BeginTransactionAsync(ct))
            {
                try
                {
                    var nodeFailed = await AllApprovalHandler.TryCompleteRejectedAsync(
                        Db, freshNodeAll, actorITCode ?? string.Empty, _logger, ct);

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
                                ct: ct);
                        }
                        return WorkflowActionResult.Advanced;
                    }

                    // Node is now CompletedRejected inside this tx — continue to instance flip.
                    return await CompleteInstanceRejectionInTxAsync(
                        txRejectAll, nodeInst, instance, task, actorITCode, reason, writeNodeCompletionEvent, ct);
                }
                catch
                {
                    await txRejectAll.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
        }
        else if (rejectMode == ApproveMode.Any)
        {
            // 或签: single reject does NOT fail node; only last-reject does.
            await GuardedTransition.IncrementNodeRejectedCountAsync(Db, nodeInst.ID, ct);
            var freshNodeAny = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == nodeInst.ID, ct);

            await using (var txRejectAny = await Db.Database.BeginTransactionAsync(ct))
            {
                try
                {
                    var nodeFailed = await AnyApprovalHandler.TryCompleteRejectedAsync(
                        Db, freshNodeAny, actorITCode ?? string.Empty, _logger, ct);

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
                                ct: ct);
                        }
                        return WorkflowActionResult.Advanced;
                    }

                    // All have rejected — continue in same tx to instance flip.
                    return await CompleteInstanceRejectionInTxAsync(
                        txRejectAny, nodeInst, instance, task, actorITCode, reason, writeNodeCompletionEvent, ct);
                }
                catch
                {
                    await txRejectAny.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
        }
        else
        {
            // Sequential path ─────────────────────────────────────────────────────
            // Canonical order inside the tx: Task cancels → Node CAS → Instance flip.

            await using (var txRejectSeq = await Db.Database.BeginTransactionAsync(ct))
            {
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
                            ct);

                    // Complete the node as CompletedRejected (Node write SECOND, CAS on RowVer).
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
                            "ExecuteRejectCompletionAsync: NodeInstance {NodeId} completion CAS returned 0 — concurrent actor already completed.",
                            nodeInst.ID);
                        await txRejectSeq.RollbackAsync(CancellationToken.None);
                        return WorkflowActionResult.AlreadyHandled;
                    }

                    // Node is CompletedRejected — continue in same tx to instance flip.
                    return await CompleteInstanceRejectionInTxAsync(
                        txRejectSeq, nodeInst, instance, task, actorITCode, reason, writeNodeCompletionEvent, ct);
                }
                catch
                {
                    await txRejectSeq.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
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
        var seqResult = await AllocateSeqWithRetryAsync(Db, instance.ID, instance.RowVer, ct);
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
    /// can be re-driven by a future reaper phase).</para>
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

    // ── WF-20.4: AllocateSeqWithRetryAsync (engine-side claim helper) ───────────

    /// <summary>
    /// Retry-wrapped <see cref="GuardedTransition.AllocateSeqAsync"/> for use inside the
    /// engine transaction (mirrors the executor helper — both share the same retry pattern).
    /// </summary>
    private static async Task<(int rows, int seq)> AllocateSeqWithRetryAsync(
        DbContext db,
        Guid instanceId,
        uint instanceRowVer,
        CancellationToken ct,
        int maxRetries = 5)
    {
        var rowVer = instanceRowVer;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var result = await GuardedTransition.AllocateSeqAsync(db, instanceId, rowVer, ct);
            if (result.rows == 1)
                return result;

            var fresh = await db.Set<ProcessInstance>()
                .AsNoTracking()
                .Where(i => i.ID == instanceId)
                .Select(i => new { i.RowVer })
                .FirstOrDefaultAsync(ct);

            if (fresh == null)
                return (0, 0);

            rowVer = fresh.RowVer;
        }
        return (0, 0);
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

            // WF-20.2: instance-wide timer cancel on Withdrawn (§6 R4 spec §5.6 gap close).
            foreach (var nid in nodeIds)
                await GuardedTransition.CancelTimersForNodeAsync(Db, nid, ct);
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

        // WF-20.2: cancel all Armed timers for this node (ReturnToInitiator closes the node).
        await GuardedTransition.CancelTimersForNodeAsync(Db, nodeInst.ID, ct);

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
        var leaseExpiry = DateTime.UtcNow.Add(_options.ReturningLeaseTtl);

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
        uint gNew;
        await using (var txA = await Db.Database.BeginTransactionAsync(ct))
        {
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
                    await txA.RollbackAsync(CancellationToken.None);
                    return WorkflowActionResult.WithDetail(WorkflowActionCode.AlreadyHandled,
                        $"Instance {instance.ID} is in state {instance.State}, not Running. Concurrent actor won.");
                }

                // Check MaxReturnLoops before the CAS (early-exit; CAS also enforces it atomically).
                if ((int)instance.ReturnLoops >= maxReturnLoops)
                {
                    await txA.RollbackAsync(CancellationToken.None);
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
                    await txA.RollbackAsync(CancellationToken.None);
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

                gNew = instance.Generation;

                // ── txA COMMIT ── State=Returning + gNew + lease are now durable.
                // ProcessInstance write-lock released here — txB acquires it LAST (canonical order).
                await txA.CommitAsync(ct);
            }
            catch
            {
                await txA.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        // ── txB — STEP-2 through STEP-6 (canonical lock order: Timer → Task → Node → Instance) ──
        // txB re-acquires ProcessInstance only at STEP-6 + AllocateSeq (instance LAST).
        // A failed txB leaves the instance in Returning with the lease stamped by txA.
        // The widened catch below attempts a prompt compensating Returning→Running flip;
        // if that also fails, the Wave-5 lease reaper recovers it at lease expiry.
        await using var txB = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            // ── STEP-2: Cancel timers for all span NodeInstances ──────────────
            // First lock acquired in txB: WorkflowTimer (canonical order position 1).
            if (spanNodeIds.Count > 0)
            {
                await GuardedTransition.CancelTimersForReturnAsync(Db, spanNodeIds, ct);
            }

            // ── STEP-3: Discard tasks on span nodes (current generation) ──────
            // Second lock order: ApprovalTask (canonical order position 2).
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
            // (BeginReturnAsync already won the linearization point in txA), but we log the anomaly and
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
                // Either way, BeginReturnAsync already won the instance transition (txA committed) —
                // the return proceeds.
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
            // Fourth (last) lock order: ProcessInstance (canonical order position 4).
            // This is the canonical "instance LAST" acquisition — matching Delegate/AddApprover/reaper.
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
                ct: ct);

            await txB.CommitAsync(ct);

            _logger.LogInformation(
                "ExecuteReturnToNodeAsync: instance {InstanceId} returned to '{TargetNodeKey}' " +
                "by '{ActorITCode}'. Generation={Gen}, ReturnLoops={Loops}.",
                instance.ID, targetNodeKey, actorITCode, gNew, instance.ReturnLoops);
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
        // #310 (WF-290.2): AddApproverAsync now acquires ApprovalTask (shift+INSERT) BEFORE
        // NodeInstance (AddApproversToNodeAsync), matching DelegateTaskAsync's Task→Node order.
        // All human multi-row txns now share one total lock order: ApprovalTask → NodeInstance
        // → ProcessInstance(Seq). The C-backstop (RunWithDeadlockRetryAsync) is retained as
        // pure defense-in-depth; the (Node,Task) ABBA cycle is now structurally eliminated.
        // On SQLite (unit tests) the classifier never fires — transparent pass-through.
        var addResult = await RunWithDeadlockRetryAsync(async innerCt =>
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

                var now = DateTime.UtcNow;
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
                delta, string.Join(",", toInject), nodeInst.NodeKey, nodeInst.ID, position, newDepth);
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
        // C-backstop (defense-in-depth): DelegateTaskAsync is wrapped in RunWithDeadlockRetryAsync.
        // #310 (WF-290.2) eliminated the Delegate-vs-AddApprover (Node,Task) ABBA cycle by
        // unifying AddApprover to Task-before-Node lock order. The retry envelope is retained
        // as belt-and-suspenders. On SQLite (unit tests) the classifier never fires.
        // Capture locals for the lambda (task/nodeInst are already captured by ref in the lambda).
        var delegateResult = await RunWithDeadlockRetryAsync(async innerCt =>
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
                    taskId, delegateeITCode, task.NodeInstanceId, task.Generation);
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
                taskId, actorITCode, delegateeITCode, nodeInst.NodeKey, nodeInst.ID);
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

    // ── WF-20.2: Timer arm helpers ─────────────────────────────────────────────

    /// <summary>
    /// Arm a node-scoped timer for an All/Any Approval node immediately after the
    /// <c>ActivateNodeInstanceAsync</c> winner site.
    ///
    /// <para>Skips silently when:
    /// <list type="bullet">
    ///   <item><see cref="_businessCalendar"/> is null (AddWtmWorkFlowTimers not called).</item>
    ///   <item><see cref="Definition.TimeoutDef"/> is absent on the node.</item>
    ///   <item>BusinessCalendar:true + pass-through + dangerous auto-action (§0 S2 fail-closed).</item>
    /// </list>
    /// </para>
    ///
    /// <para>IdempotencyKey: <c>tmo:n:{NodeInstanceId:N}:{Generation}:0</c>.
    /// Unique-key violations are caught and swallowed (idempotent — concurrent double-activation loses gracefully).</para>
    /// </summary>
    private async Task ArmNodeTimerIfConfiguredAsync(
        NodeInstance nodeInst,
        Definition.NodeDef nodeDef,
        DateTime now,
        CancellationToken ct)
    {
        if (_businessCalendar is null) return;
        var td = nodeDef.Timeout;
        if (td is null) return;

        bool isAutoAction = td.Action is TimerAction.AutoApprove or TimerAction.AutoReject or TimerAction.Escalate;

        // §0 S2 arm-time severity: pass-through calendar + auto-action → skip + warn.
        if (td.BusinessCalendar && _businessCalendar.IsPassThrough && isAutoAction)
        {
            _logger.LogWarning(
                "ArmNodeTimer: node '{NodeKey}' timeout action={Action} + businessCalendar:true " +
                "but only PassThroughBusinessCalendar is registered. Skipping arm (fail-closed on weekends). " +
                "Register a real IBusinessCalendar to enable this action.",
                nodeDef.NodeKey, td.Action);
            return;
        }

        var fireAt = _businessCalendar.AddBusinessTime(now, td.Duration, _options.BusinessCalendarId);
        // Deterministic idempotency key: tmo:n:{nodeId:N}:{generation}:0
        var key = $"tmo:n:{nodeInst.ID:N}:{nodeInst.Generation}:0";

        // FIX-B5d: stamp DueUtc on all Pending tasks covered by this node-scoped timer.
        // Design §0 table: "set ApprovalTask.DueUtc = activation + duration at the SAME site
        // that arms the covering timer" (task-scoped ArmTaskTimerIfConfiguredAsync already does
        // this; node-scoped was missing the stamp until this fix).
        await Db.Set<ApprovalTask>()
            .Where(t => t.NodeInstanceId == nodeInst.ID
                         && t.State == TaskState.Pending
                         && t.Generation == nodeInst.Generation)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DueUtc, fireAt), ct);

        var timer = new WorkflowTimer
        {
            ID              = Guid.NewGuid(),
            TenantCode      = nodeInst.TenantCode,
            NodeInstanceId  = nodeInst.ID,
            ApprovalTaskId  = null, // node-scoped: no task FK
            FireAtUtc       = fireAt,
            Action          = td.Action,
            IdempotencyKey  = key,
            Status          = TimerStatus.Armed,
            RemindCount     = 0,
            Generation      = nodeInst.Generation,
            RowVer          = 0,
        };

        try
        {
            Db.Set<WorkflowTimer>().Add(timer);
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx))
        {
            // Idempotent: concurrent activation already inserted this key → detach and continue.
            Db.Entry(timer).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            _logger.LogDebug(
                "ArmNodeTimer: unique-key collision for key '{Key}' (concurrent activation) — no-op.",
                key);
        }
    }

    /// <summary>
    /// Arm a task-scoped timer for a Sequential step that just became Pending.
    ///
    /// <para>Stamps <see cref="ApprovalTask.DueUtc"/> on the task at the same site (derived output).</para>
    ///
    /// <para>IdempotencyKey: <c>tmo:t:{TaskId:N}:0</c>.
    /// Unique-key violations are caught and swallowed (idempotent).</para>
    /// </summary>
    private async Task ArmTaskTimerIfConfiguredAsync(
        ApprovalTask task,
        NodeInstance nodeInst,
        Definition.NodeDef nodeDef,
        DateTime now,
        CancellationToken ct)
    {
        if (_businessCalendar is null) return;
        var td = nodeDef.Timeout;
        if (td is null) return;

        bool isAutoAction = td.Action is TimerAction.AutoApprove or TimerAction.AutoReject or TimerAction.Escalate;

        // §0 S2 arm-time severity: pass-through calendar + auto-action → skip + warn.
        if (td.BusinessCalendar && _businessCalendar.IsPassThrough && isAutoAction)
        {
            _logger.LogWarning(
                "ArmTaskTimer: node '{NodeKey}' task {TaskId} timeout action={Action} + businessCalendar:true " +
                "but only PassThroughBusinessCalendar is registered. Skipping arm (fail-closed on weekends).",
                nodeDef.NodeKey, task.ID, td.Action);
            return;
        }

        var fireAt = _businessCalendar.AddBusinessTime(now, td.Duration, _options.BusinessCalendarId);
        // Deterministic idempotency key: tmo:t:{taskId:N}:0
        var key = $"tmo:t:{task.ID:N}:0";

        // Stamp DueUtc on the task (derived output, not a timer predicate).
        await Db.Set<ApprovalTask>()
            .Where(t => t.ID == task.ID)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DueUtc, fireAt), ct);

        var timer = new WorkflowTimer
        {
            ID              = Guid.NewGuid(),
            TenantCode      = nodeInst.TenantCode,
            NodeInstanceId  = nodeInst.ID,
            ApprovalTaskId  = task.ID,
            FireAtUtc       = fireAt,
            Action          = td.Action,
            IdempotencyKey  = key,
            Status          = TimerStatus.Armed,
            RemindCount     = 0,
            Generation      = nodeInst.Generation,
            RowVer          = 0,
        };

        try
        {
            Db.Set<WorkflowTimer>().Add(timer);
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx))
        {
            Db.Entry(timer).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            _logger.LogDebug(
                "ArmTaskTimer: unique-key collision for key '{Key}' (concurrent step activation) — no-op.",
                key);
        }
    }

    /// <summary>
    /// Heuristic: detect unique-constraint violations from <see cref="DbUpdateException"/>.
    /// Used to implement the idempotent arm pattern (duplicate key = safe no-op).
    ///
    /// <para><strong>PostgreSQL note:</strong> on PostgreSQL, a unique-constraint violation
    /// (error code 23505) ABORTS the enclosing transaction — the catch path here rolls back
    /// the whole fire transaction and the timer retries on the next tick.  That is the correct
    /// behavior: the unique index on IdempotencyKey guarantees exactly-once arm;
    /// the retry sees <see cref="TimerStatus.Fired"/> from the winning host and exits cleanly
    /// (GATE-0 generation-mismatch or LostRace CAS).</para>
    ///
    /// <para>FIX-B5f: narrowed heuristic — require constraint-name markers (IX_, UniqueConstraint)
    /// or well-known duplicate-key phrases rather than matching the bare word "UNIQUE" which can
    /// appear in unrelated error messages (e.g. "unique" in a column description).</para>
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // EF Core wraps the provider-specific exception — check both levels.
        var inner = ex.InnerException?.Message ?? ex.Message;

        // FIX-B5f: require constraint-name marker (IX_ prefix from ApplyWorkFlowModels conventions)
        // or well-known duplicate-key phrases to avoid false positives on generic messages
        // that happen to contain the word "unique".
        return inner.Contains("IX_", StringComparison.OrdinalIgnoreCase)         // index name prefix (all providers)
            || inner.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) // SQLite / PostgreSQL
            || inner.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) // SQLite
            || inner.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)    // SQL Server / PostgreSQL
            || inner.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)  // MySQL / MariaDB
            || inner.Contains("unique index", StringComparison.OrdinalIgnoreCase)     // Oracle / DaMeng
            || inner.Contains("23505", StringComparison.Ordinal)                      // PostgreSQL SQLSTATE
            || inner.Contains("ORA-00001", StringComparison.OrdinalIgnoreCase);       // Oracle unique violation
    }

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

        var graph = WorkflowGraphSerializer.Deserialize(version.GraphJson);
        return graph.Nodes.FirstOrDefault(n =>
            string.Equals(n.NodeKey, nodeKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// #290 backstop (C): run <paramref name="body"/> inside a bounded jittered-backoff retry
    /// envelope scoped ONLY to <see cref="DelegateTaskAsync"/> and <see cref="AddApproverAsync"/>.
    ///
    /// <para>When <paramref name="body"/> throws an exception that
    /// <see cref="WorkflowDeadlockClassifier.IsDeadlockVictim"/> classifies as a provider
    /// deadlock victim, the whole body (which is a complete <c>BeginTransaction..Commit</c>
    /// unit) is retried up to <see cref="WorkFlowOptions.DeadlockRetryAttempts"/> times with
    /// jittered exponential backoff.  Because the body is an entire atomic transaction, a
    /// victim-abort guarantees rollback; NextSeq/Generation/lease are all undone before replay,
    /// making each retry provably idempotent.</para>
    ///
    /// <para>On SQLite (the unit-test substrate) the classifier never fires — SQLite has no
    /// multi-writer deadlock — so this method is a transparent single-pass pass-through in tests.
    /// All existing T-DEL-* / T-ADD-* / T-MIX-* suites remain byte-identical.</para>
    ///
    /// <para>NOT applied to the return transaction (fixed structurally by the STEP-1 split) and
    /// NOT applied to the reaper (it already self-heals via Armed re-fire).</para>
    /// </summary>
    // internal (not private) so that WorkFlow.Test can drive it directly
    // via InternalsVisibleTo without reflection.  This is a test seam only —
    // production callers always go through DelegateTaskAsync/AddApproverAsync.
    internal async Task<WorkflowActionResult> RunWithDeadlockRetryAsync(
        Func<CancellationToken, Task<WorkflowActionResult>> body,
        CancellationToken ct)
    {
        int maxAttempts = Math.Max(1, _options.DeadlockRetryAttempts);
        var baseDelay = _options.DeadlockRetryBaseDelay;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // #290 FIX-1: Clear the EF change-tracker before each attempt.
            // EF moves Added→Unchanged only on a SUCCESSFUL SaveChanges.  When a deadlock
            // victim throws DURING SaveChanges, the prior attempt's Added entities (k new
            // ApprovalTask rows + WorkflowEventLog row) stay in Added state.  Without this
            // Clear(), the retry body creates fresh entities AND re-inserts the stale Added
            // ones → duplicate rows, colliding on IX_Wf_ApprovalTask_Node_Assignee_Gen,
            // double TotalRequired bump, corrupted 会签/串签 quorum.
            // DB-side rollback does NOT revert in-memory Added entities — this Clear() is
            // the only mechanism that makes retry-idempotency hold end-to-end.
            // The body re-reads/re-builds everything each attempt (AsNoTracking reads inside
            // the txn body), so clearing here discards only stale tracked state.
            Db.ChangeTracker.Clear();
            try
            {
                return await body(ct);
            }
            catch (Exception ex) when (WorkflowDeadlockClassifier.IsDeadlockVictim(ex)
                                        && attempt < maxAttempts)
            {
                // Deadlock victim on a known provider — back off and retry the whole txn body.
                // The exception guarantees the txn was rolled back; replay is idempotent.
                var delay = TimeSpan.FromMilliseconds(
                    baseDelay.TotalMilliseconds * attempt
                    + Random.Shared.NextDouble() * baseDelay.TotalMilliseconds);

                _logger.LogWarning(
                    "RunWithDeadlockRetryAsync: deadlock victim on attempt {Attempt}/{Max}. " +
                    "Retrying after {DelayMs:F0} ms. Exception: {ExMessage}",
                    attempt, maxAttempts, delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (WorkflowDeadlockClassifier.IsDeadlockVictim(ex)
                                        && attempt >= maxAttempts)
            {
                // Exhausted all retry attempts — return a closed result code instead of
                // propagating the raw provider exception.
                _logger.LogError(ex,
                    "RunWithDeadlockRetryAsync: deadlock retry exhausted after {Max} attempts. " +
                    "Returning DeadlockRetryExhausted.",
                    maxAttempts);
                return WorkflowActionResult.DeadlockRetryExhausted;
            }
        }

        // Unreachable — the loop always returns or throws.
        throw new InvalidOperationException("RunWithDeadlockRetryAsync: unexpected fall-through.");
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
