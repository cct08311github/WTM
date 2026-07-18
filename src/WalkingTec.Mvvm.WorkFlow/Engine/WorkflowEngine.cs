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
using Microsoft.EntityFrameworkCore.Storage;
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
internal sealed partial class WorkflowEngine : IWorkflowEngine, IDisposable
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
    // #727: true when ServiceCollectionExtensions.AddWtmWorkFlow's factory created _dc via
    // IWtmDataContextFactory.CreateDC() (the DI-tracked NullContext fallback and the internal
    // DbContext-only test constructor both leave this false — the caller owns that lifetime).
    private readonly bool _ownsDc;
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
    // #666: deserialized-graph cache. Defaults to a private (non-shared) instance when not
    // supplied — production DI always supplies the process-wide singleton (see
    // ServiceCollectionExtensions.AddWtmWorkFlow); the private-instance fallback only matters
    // for the internal test constructors below, which still get correct (just non-shared)
    // caching behavior without needing to be updated for this parameter.
    private readonly IWorkflowGraphProvider _graphProvider;
    // #676: clock seam. Defaults to TimeProvider.System when DI has no TimeProvider registered
    // (or when a test constructs the engine directly) — identical behavior to raw DateTime.UtcNow.
    // Production DI resolves whatever TimeProvider the host registered (e.g. Mvc's
    // `services.TryAddSingleton(TimeProvider.System)`), so a single fake TimeProvider registered
    // by a test host flows through automatically without any extra wiring.
    private readonly TimeProvider _timeProvider;

    // Convenience alias — keeps all the engine body code readable.
    private DbContext Db => _db;

    /// <summary>Production constructor. Called by a factory lambda in
    /// <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/> — NOT by ASP.NET Core's plain
    /// constructor-injection — so <paramref name="dc"/> arrives pre-resolved via
    /// <see cref="ServiceCollectionExtensions.ResolveDataContext"/> and is always a
    /// <see cref="DbContext"/> subclass at runtime (#727: a bare
    /// <c>services.AddScoped&lt;IWorkflowEngine, WorkflowEngine&gt;()</c> registration would let
    /// DI inject the raw <c>IDataContext</c> placeholder — <see cref="NullContext"/> in every
    /// real deployment — and this cast would throw <see cref="InvalidCastException"/> the first
    /// time the engine was constructed). The cast is validated at construction so any
    /// mis-registration still fails loudly, on first use.
    /// <para><see cref="IWorkflowNotifier"/> is optional — injected when
    /// <see cref="ServiceCollectionExtensions.AddWtmWorkFlowNotifications"/> was called; null otherwise.</para>
    /// <para>#676: <paramref name="timeProvider"/> is optional — trailing-optional-parameter pattern
    /// (mirrors <paramref name="graphProvider"/> from #666). Defaults to <see cref="TimeProvider.System"/>
    /// so every existing call site / test compiles and behaves unchanged.</para>
    /// <para>#727: <paramref name="ownsDc"/> — true when the caller created <paramref name="dc"/>
    /// specifically for this engine instance (via <c>IWtmDataContextFactory.CreateDC()</c>) and
    /// this engine should dispose it; false when the caller (DI fallback, or an ad-hoc caller)
    /// owns that lifetime instead. See <see cref="Dispose"/>.</para>
    /// <para>#727-followup: the production DI factory (<c>AddWtmWorkFlow</c>) always passes
    /// <c>ownsDc: false</c> now — <paramref name="dc"/> is resolved via the scoped
    /// <c>ScopedWorkflowDataContextHolder</c>, which is the sole owner/disposer, so that
    /// <see cref="WorkflowEngine"/> and <c>WorkflowTimerExecutor</c> resolved from the same DI
    /// scope share one DbContext/DB connection instead of each minting their own.</para>
    /// </summary>
    public WorkflowEngine(
        IDataContext dc,
        INodeKindDispatcher dispatcher,
        IRoutingEvaluator routingEvaluator,
        IOptions<WorkFlowOptions> options,
        ILogger<WorkflowEngine> logger,
        IWorkflowNotifier? notifier = null,
        IBusinessCalendar? businessCalendar = null,
        IWorkflowGraphProvider? graphProvider = null,
        TimeProvider? timeProvider = null,
        bool ownsDc = false)
    {
        if (dc is null) throw new ArgumentNullException(nameof(dc));
        _dc = dc;
        _db = (DbContext)dc;
        _ownsDc = ownsDc;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _routingEvaluator = routingEvaluator ?? throw new ArgumentNullException(nameof(routingEvaluator));
        _options = options?.Value ?? new WorkFlowOptions();
        _logger = (ILogger)(logger ?? throw new ArgumentNullException(nameof(logger)));
        _notifier = notifier;
        _businessCalendar = businessCalendar; // null → timer arming skipped (opt-in via AddWtmWorkFlowTimers)
        // #666: DI always supplies the process-wide singleton; null only in ad-hoc construction
        // (defensive fallback — never expected on the production DI path).
        _graphProvider = graphProvider ?? new WorkflowGraphProvider();
        // #676: null only when DI has no TimeProvider registered and no ad-hoc caller supplied one.
        _timeProvider = timeProvider ?? TimeProvider.System;

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
        IWorkflowNotifier? notifier = null,
        IWorkflowGraphProvider? graphProvider = null,
        TimeProvider? timeProvider = null)
        : this(db, dispatcher, routingEvaluator, new WorkFlowOptions(), logger, notifier,
               graphProvider: graphProvider, timeProvider: timeProvider)
    { }

    /// <summary>Full internal constructor used by tests that need to override WorkFlowOptions.</summary>
    internal WorkflowEngine(
        DbContext db,
        INodeKindDispatcher dispatcher,
        IRoutingEvaluator routingEvaluator,
        WorkFlowOptions options,
        ILogger logger,
        IWorkflowNotifier? notifier = null,
        IBusinessCalendar? businessCalendar = null,
        IWorkflowGraphProvider? graphProvider = null,
        TimeProvider? timeProvider = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _dc = null; // No IDataContext in the direct-DbContext test path — ValidateDbTypeOnFirstUse skipped.
        _ownsDc = false; // Test caller owns and disposes `db` itself.
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _routingEvaluator = routingEvaluator ?? throw new ArgumentNullException(nameof(routingEvaluator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notifier = notifier; // null is valid — skip all notifications
        _businessCalendar = businessCalendar; // null → timer arming skipped in arm helpers
        // #666: tests that don't care about cache sharing get a private per-instance cache;
        // production DI always supplies the process-wide singleton via the public constructor above.
        _graphProvider = graphProvider ?? new WorkflowGraphProvider();
        // #676: same default-to-System fallback as the production constructor.
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// #727: disposes the DataContext this engine created via
    /// <see cref="ServiceCollectionExtensions.ResolveDataContext"/> (i.e. through
    /// <c>IWtmDataContextFactory.CreateDC()</c>). No-op for the DI-fallback and
    /// direct-<see cref="DbContext"/> test constructor paths, whose caller owns that lifetime.
    /// Safe for ASP.NET Core's scoped-service auto-dispose: <see cref="IWorkflowEngine"/> does
    /// not itself declare <see cref="IDisposable"/>, but the DI container disposes any resolved
    /// instance that implements it, regardless of which service type it was requested as.
    /// </summary>
    public void Dispose()
    {
        if (_ownsDc)
        {
            _dc?.Dispose();
        }
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

        var graph = _graphProvider.GetGraph(version);

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

        // #357: Atomic start handoff — mint Start NodeInstance + flip Draft→Running in ONE transaction.
        // Lock order: NodeInstance (mint, Step 1) → ProcessInstance (flip, Step 2 — LAST) — canonical.
        // Crash invariant: if the process dies before commit the instance stays Draft with no node
        // (valid, non-stranded resting state — a Draft with no node can be safely re-driven or GC'd).
        // AppendAsync (Step 3) runs inside the same tx AFTER the state flip because it only bumps
        // ProcessInstance.NextSeq (not State); this does not conflict with the flip CAS.
        // #667: routed through ExecuteInTransactionAsync — was a raw, unretried BeginTransactionAsync;
        // a deadlock/transient victim here previously threw a raw provider exception to the caller.
        if (Db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "StartAsync: unexpected ambient transaction at start-handoff (#357).");

        var startTxResult = await ExecuteInTransactionAsync(async innerCt =>
        {
            await using var txStart = await Db.Database.BeginTransactionAsync(innerCt);
            try
            {
                // Step 1 (NodeInstance — FIRST): mint Start node inside tx.
                // WF-19: stamp DefinitionCode from graph.Key for delegation scope filtering.
                await MintNodeInstanceAsync(instance, startNodeDef, innerCt, definitionCode: graph.Key);

                // Step 2 (ProcessInstance — LAST): flip Draft → Running.
                var startRows = await GuardedTransition.AdvanceProcessInstanceAsync(
                    Db, instance.ID,
                    expectedState: InstanceState.Draft,
                    expectedRowVer: 0,
                    nextState: InstanceState.Running,
                    innerCt);

                if (startRows == 0)
                {
                    // Should not happen on a brand-new single-caller instance;
                    // CAS returned 0 — concurrent race (treat as AlreadyHandled).
                    await txStart.RollbackAsync(CancellationToken.None);
                    _logger.LogWarning(
                        "StartAsync: GuardedTransition Draft→Running returned 0 rows for {InstanceId}. Possible race on new instance.",
                        instance.ID);
                    return WorkflowActionResult.AlreadyHandled;
                }

                // Step 3 (Audit): Submit event append AFTER both NodeInstance + ProcessInstance writes.
                // AppendAsync only bumps ProcessInstance.NextSeq, not State — safe inside the same tx.
                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, tenantCode,
                    EventAction.Submit,
                    nodeKey: startNodeDef.NodeKey,
                    actorITCode: initiatorITCode,
                    beforeState: InstanceState.Draft.ToString(),
                    afterState: InstanceState.Running.ToString(),
                    timeProvider: _timeProvider,
                    ct: innerCt);

                await txStart.CommitAsync(innerCt);
                return WorkflowActionResult.NodeCompleted;
            }
            catch
            {
                await txStart.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, ct);

        bool startTxWon = startTxResult.Code == WorkflowActionCode.NodeCompleted;

        // Re-read instance post-commit for AdvanceCoreAsync (fresh RowVer).
        instance = await Db.Set<ProcessInstance>()
            .AsNoTracking()
            .SingleAsync(x => x.ID == instance.ID, ct);

        if (!startTxWon)
        {
            // The start-tx CAS was a concurrent loser — only valid on new instances if something
            // is very wrong. Return the current instance state without driving further.
            return instance;
        }

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

        var graph = _graphProvider.GetGraph(version);

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

        var graph = _graphProvider.GetGraph(version);

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
                // #401: reaching this branch means THIS call performed no completing transition;
                // all tokens were already drained and the instance was flipped to its final state
                // by a concurrent winner (or this is an idempotent replay). The genuine completer
                // returns InstanceApproved via the token-processing path earlier in this loop.
                // Returning InstanceApproved here produced a FALSE second winner in Any-mode
                // races (#401) and was also semantically wrong for Rejected instances.
                var fresh = await Db.Set<ProcessInstance>()
                    .AsNoTracking()
                    .SingleAsync(x => x.ID == instance.ID, ct);
                if (fresh.State == InstanceState.Approved || fresh.State == InstanceState.Rejected)
                    return WorkflowActionResult.AlreadyHandled;

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
                Db, activeNode.ID, activeNode.RowVer, _timeProvider.GetUtcNow().UtcDateTime,
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
                    timeProvider: _timeProvider,
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
            var now20 = _timeProvider.GetUtcNow().UtcDateTime;
            var approveMode = nodeDef.ApproveMode ?? ApproveMode.Sequential;
            if (approveMode == ApproveMode.Sequential)
            {
                // Sequential: arm task-scoped timer for the first active step.
                // #529: re-read the node's SequencePointer instead of assuming SequenceOrder==0 —
                // OnEnterAsync may have advanced it past a leading run of InitiatorAutoApprove
                // steps, so the first Pending task can live at any SequenceOrder.
                var freshNodeForTimer = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleAsync(n => n.ID == activeNode.ID, ct);
                var step0Task = await Db.Set<ApprovalTask>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        t => t.NodeInstanceId == activeNode.ID
                             && t.SequenceOrder == freshNodeForTimer.SequencePointer
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
                    timeProvider: _timeProvider,
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
                    timeProvider: _timeProvider,
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
                // #667: routed through ExecuteInTransactionAsync — was a raw, unretried
                // BeginTransactionAsync; a deadlock/transient victim here previously threw a raw
                // provider exception to the caller. NodeCompleted is used purely as an internal
                // "tx succeeded, proceed to post-commit work" sentinel — the method's real
                // success return value (InstanceApproved) is unchanged below.
                var advanceApproveEndResult = await ExecuteInTransactionAsync(async innerCt =>
                {
                    await using var txAdvanceApproveEnd = await Db.Database.BeginTransactionAsync(innerCt);
                    try
                    {
                        // Step 1: complete the End NodeInstance (CAS guard closes W2 window a).
                        var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
                            Db, activeNode.ID, activeNode.RowVer, NodeState.CompletedApproved,
                            decidedBy: decidedByForApproval,
                            generation: instance.Generation, ct: innerCt);

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
                            .SingleAsync(x => x.ID == instance.ID, innerCt);

                        // Step 3: advance instance Running → Approved (CAS guard closes W2 window b).
                        var approveRows = await GuardedTransition.AdvanceProcessInstanceAsync(
                            Db, instance.ID,
                            expectedState: InstanceState.Running,
                            expectedRowVer: instance.RowVer,
                            nextState: InstanceState.Approved,
                            innerCt);

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
                            timeProvider: _timeProvider,
                            ct: innerCt);

                        // Instance-level event: Running → Approved.
                        await WorkflowEventLogWriter.AppendAsync(
                            Db, instance.ID, instance.TenantCode,
                            EventAction.AutoAdvance,
                            nodeKey: activeNode.NodeKey,
                            actorITCode: null,
                            beforeState: InstanceState.Running.ToString(),
                            afterState: InstanceState.Approved.ToString(),
                            timeProvider: _timeProvider,
                            ct: innerCt);

                        await txAdvanceApproveEnd.CommitAsync(innerCt);
                        return WorkflowActionResult.NodeCompleted;
                    }
                    catch
                    {
                        await txAdvanceApproveEnd.RollbackAsync(CancellationToken.None);
                        throw;
                    }
                }, ct);

                if (advanceApproveEndResult.Code != WorkflowActionCode.NodeCompleted)
                    return advanceApproveEndResult;

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
            // #667: routed through ExecuteInTransactionAsync — was a raw, unretried
            // BeginTransactionAsync; a deadlock/transient victim here previously threw a raw
            // provider exception to the caller.
            var advanceCompleteResult = await ExecuteInTransactionAsync(async innerCt =>
            {
                await using var txAdvanceComplete = await Db.Database.BeginTransactionAsync(innerCt);
                try
                {
                    var completeRows = await GuardedTransition.CompleteNodeInstanceAsync(
                        Db, activeNode.ID, activeNode.RowVer, NodeState.CompletedApproved,
                        decidedBy: decidedByForApproval,
                        generation: instance.Generation, ct: innerCt);

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
                        await MintNodeInstanceAsync(instance, nextNodeDef, innerCt, definitionCode: graph.Key);
                    }

                    await WorkflowEventLogWriter.AppendAsync(
                        Db, instance.ID, instance.TenantCode,
                        EventAction.AutoAdvance,
                        nodeKey: activeNode.NodeKey,
                        actorITCode: null,
                        beforeState: NodeState.Activated.ToString(),
                        afterState: NodeState.CompletedApproved.ToString(),
                        timeProvider: _timeProvider,
                        ct: innerCt);

                    await txAdvanceComplete.CommitAsync(innerCt);
                    return WorkflowActionResult.NodeCompleted;
                }
                catch
                {
                    await txAdvanceComplete.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }, ct);

            if (advanceCompleteResult.Code != WorkflowActionCode.NodeCompleted)
                return advanceCompleteResult;

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
                timeProvider: _timeProvider,
                ct: ct);

            // Branches are now Pending — the outer drain loop will pick them up.
            return WorkflowActionResult.NodeCompleted;
        }
    }

    /// <summary>
    /// #667: unified transaction execution helper — supersedes the #290-era
    /// <c>RunWithDeadlockRetryAsync</c>, which only wrapped 6 of the engine's ~14
    /// transactional units.  EVERY transactional unit in the engine now routes through
    /// this method with NO exceptions for the execution-strategy legality wrap (concern A —
    /// see <see cref="WorkflowTransactionExecutor"/>'s file-level comment for the full (A)/(B)
    /// split).  The Return txReturn/txA/txB split and any other lease-reaper-covered unit are
    /// the sole, deliberate exceptions to concern (B) — the classifier-driven deadlock-RETRY
    /// loop — via <c>retryOnDeadlock: false</c>; they are still strategy-wrapped and therefore
    /// still legal under a host-configured retrying execution strategy.  See the comments at
    /// those call sites.
    ///
    /// <para><strong><c>retryOnDeadlock</c> (default <c>true</c>):</strong> when <c>false</c>,
    /// <c>maxAttempts</c> collapses to 1 — the execution-strategy wrap (concern A) still runs
    /// unconditionally, but a deadlock-classified failure on the single attempt returns
    /// <see cref="WorkflowActionResult.DeadlockRetryExhausted"/> immediately instead of looping.
    /// Reserved for units with an independent self-healing backstop (the Wave-5 Returning-lease
    /// reaper) where an inline retry would duplicate work the reaper already does safely on its
    /// own schedule. Every pre-#667-completion call site omits this argument and therefore keeps
    /// today's retryOnDeadlock:true behaviour unchanged (no silent default-behaviour change).</para>
    ///
    /// <para><strong>Why <c>Db.Database.CreateExecutionStrategy()</c>:</strong>
    /// EF Core throws <c>InvalidOperationException</c> ("does not support user-initiated
    /// transactions") if <c>Database.BeginTransactionAsync()</c> is called directly while
    /// the DbContext's configured execution strategy has <c>RetriesOnFailure == true</c>
    /// (i.e. the host application called <c>options.EnableRetryOnFailure()</c> for
    /// SqlServer/Npgsql/MySql — a commonly recommended setting for cloud databases).  Before
    /// #667, every one of the engine's raw <c>BeginTransactionAsync</c> calls broke on any
    /// host that had enabled that setting.  Routing the transaction body through
    /// <c>strategy.ExecuteAsync</c> makes <paramref name="body"/>'s own
    /// <c>BeginTransactionAsync</c> call always sanctioned, whether the host configured a
    /// retrying strategy or not (the default is <c>NonRetryingExecutionStrategy</c>, which
    /// is a transparent single-pass wrapper).</para>
    ///
    /// <para><strong>Why <see cref="WorkflowDeadlockClassifier"/> is retained (not subsumed):</strong>
    /// the EF-Core-level retry described above only fires when the HOST has opted into
    /// <c>EnableRetryOnFailure</c> — the default is no retry at all.  SQLite's own provider
    /// does not even offer an <c>EnableRetryOnFailure</c> option.  If this method relied
    /// solely on the host-configured strategy, the six paths that retry deadlocks
    /// unconditionally TODAY would silently stop retrying for any host that never opted in
    /// — a silent default-behaviour regression (project red line).  The outer loop below,
    /// driven by <see cref="WorkflowDeadlockClassifier.IsDeadlockVictim"/>, is therefore kept
    /// as the unconditional, provider-agnostic retry mechanism; <c>CreateExecutionStrategy</c>
    /// is used purely as the transaction-boundary sanctioning mechanism described above.  When
    /// the host DOES configure a retrying strategy, its own transient-error retries run INSIDE
    /// this method's outer loop (harmless extra layering, bounded on both sides).</para>
    ///
    /// <para><strong>Own-or-enlist:</strong> <paramref name="body"/> is responsible for
    /// checking <c>Db.Database.CurrentTransaction</c> and beginning its own transaction only
    /// when null (own), else running against the ambient transaction (enlist) — this
    /// preserves each call site's existing commit/rollback-per-branch contract, which a
    /// single generic "always commit unless throw" wrapper cannot express without changing
    /// the control flow of every call site.  See individual call sites for their own-or-enlist
    /// guards.</para>
    ///
    /// <para><strong>Idempotency (#290 FIX-1, unchanged):</strong> <c>Db.ChangeTracker.Clear()</c>
    /// runs immediately before every invocation of <paramref name="body"/> — both on this
    /// method's own outer-loop retries AND on any inner retry performed by a host-configured
    /// EF execution strategy — so that a prior attempt's Added-but-rolled-back entities (e.g.
    /// new ApprovalTask rows) can never be re-inserted alongside a retry's fresh entities.
    /// DB-side rollback does NOT revert in-memory Added entities; this Clear() is the only
    /// mechanism that makes retry-idempotency hold end-to-end (duplicate-insert protection for
    /// <c>IX_Wf_ApprovalTask_Node_Assignee_Gen</c>).</para>
    ///
    /// <para>On SQLite (the unit-test substrate) the classifier fires only for genuine
    /// SQLITE_BUSY/SQLITE_LOCKED transient contention (#667 extends coverage here — see
    /// <see cref="WorkflowDeadlockClassifier"/>), NOT for the distinct #629
    /// "cannot start a transaction within a transaction" (SQLITE_ERROR) wrapper-state-desync
    /// signature, which is a different failure class already mitigated at the connection layer
    /// (busy_timeout widening in the test fixtures) rather than by retrying the whole unit —
    /// see the #629 analysis in the Issue #667 PR description for the full reasoning.</para>
    /// </summary>
    // internal (not private) so that WorkFlow.Test can drive it directly
    // via InternalsVisibleTo without reflection.  This is a test seam only —
    // production callers always go through the engine's public API methods.
    //
    // #667 completion: the retry-envelope loop itself now lives in the shared
    // WorkflowTransactionExecutor static helper (so WorkflowTimerExecutor — a different
    // class — can reuse the identical legality-wrap + idempotency logic without duplicating
    // it). This method is a thin forwarding wrapper that supplies the engine's Db/_options/
    // _logger. retryOnDeadlock defaults to true — EVERY pre-existing call site that omits the
    // argument keeps today's behaviour byte-for-byte (no silent default-behaviour change).
    internal Task<WorkflowActionResult> ExecuteInTransactionAsync(
        Func<CancellationToken, Task<WorkflowActionResult>> body,
        CancellationToken ct,
        bool retryOnDeadlock = true)
        => WorkflowTransactionExecutor.ExecuteInTransactionAsync(
            Db, _options, _logger, body, ct, retryOnDeadlock);

    /// <summary>
    /// Generic overload of <see cref="ExecuteInTransactionAsync"/> that allows the body
    /// to return an arbitrary result alongside the <see cref="WorkflowActionResult"/>.
    /// Used by the Sequential atomic helpers (WF-373) to carry post-commit signals
    /// (NextIsAutoApproved, IsLastStep) back to the caller without a post-commit re-read,
    /// and (as of #667 completion) by the Return-path txA helper to carry gNew out of the
    /// strategy-wrapped body. See the scalar overload for the retryOnDeadlock contract.
    /// </summary>
    internal Task<(WorkflowActionResult result, T extra)> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<(WorkflowActionResult result, T extra)>> body,
        T defaultExtra,
        CancellationToken ct,
        bool retryOnDeadlock = true)
        => WorkflowTransactionExecutor.ExecuteInTransactionAsync(
            Db, _options, _logger, body, defaultExtra, ct, retryOnDeadlock);
}
