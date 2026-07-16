#nullable enable
// WF-20.1: WorkflowTimerExecutor skeleton — GATE-0 orphan retirement + fire txn + Remind action.
// WF-20.3: Remind chain next-link INSERT (cap composition) + IWorkflowNotifier DIM wiring.
// WF-20.4: AutoApprove / AutoReject — double-gate + in-txn bounded DRAIN + NotifyTimeoutAutoActionedAsync.
// WF-20.5: Escalate + AtAction expired-delegation sweep (phase-3 reaper).
//
// Sub-scope WF-20.1 delivers:
//   • Candidate SELECT: TimerBatchSize, (Status,FireAtUtc) index shape, IgnoreQueryFilters + justification.
//   • GATE-0: orphan retirement for non-Running/generation-mismatch/non-Activated timers.
//   • Per-timer txn: FireTimerAsync + Remind action (next-link INSERT + TimeoutRemind event).
//
// Sub-scope WF-20.3 delivers:
//   • Remind chain next-link INSERT with min(MaxReminders ?? MaxRemindersDefault, MaxRemindersHardCap) cap.
//   • RemindEveryHours==null → one-shot (no next-link).
//   • Chain links inherit Generation.
//   • Post-commit: NotifyTimeoutRemindAsync called with re-read current Pending assignees,
//     re-gated on node Activated + generation match; null-check + try/catch + LogError.
//   • NotifyTimeoutEscalatedAsync / NotifyTimeoutAutoActionedAsync call-site markers exist
//     in the executor dispatch (WF-20.4/20.5 stub comments).
//
// Sub-scope WF-20.4 delivers:
//   • Double-gate (AllowTimerAutoAction + concrete WorkflowEngine type-test).
//   • In-txn bounded DRAIN (pendingTaskCount+8) per design §5.
//   • Post-commit: NotifyTimeoutAutoActionedAsync for each successfully acted task.
//   • AutoReject routes through handlers — Any-unanimity and All-RejectGate respected.
//
// Escalate is gated to WF-20.5.
// Arm sites (§2) are gated to WF-20.2.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Scoped service — one per poller tick scope.  Drives the per-timer fire pipeline:
/// candidate SELECT → GATE-0 orphan check → per-timer transactional fire + action.
///
/// <para>Only <see cref="TimerAction.Remind"/> is fully implemented here (WF-20.1/20.3).
/// Other actions are stubbed with <see cref="TimerFireOutcome.FailClosed"/> and a
/// clearly-marked seam comment — they are filled in WF-20.4 (auto-actions) and
/// WF-20.5 (escalation).</para>
///
/// <para><see cref="IWorkflowNotifier"/> DIMs are wired in WF-20.3.  The notifier is
/// optional — null or a non-registered notifier is a silent no-op.  All notification
/// calls are post-commit and surrounded by null-check + try/catch + LogError.</para>
/// </summary>
internal sealed partial class WorkflowTimerExecutor
{
    private readonly IDataContext? _dc;
    // Test-path: direct DbContext (WfTestContext is DbContext but not IDataContext).
    private readonly DbContext? _dbDirect;
    private readonly WorkFlowOptions _options;
    private readonly ILogger<WorkflowTimerExecutor> _logger;
    private readonly IWorkflowNotifier? _notifier;
    // WF-20.4: typed to IWorkflowEngine for type-test; system auto-actions require the concrete
    // WorkflowEngine.SystemClaimTaskAsync / SystemContinueTaskAsync path.  Custom IWorkflowEngine registrations → downgrade.
    private readonly IWorkflowEngine? _engine;
    // #666: deserialized-graph cache. Defaults to a private (non-shared) instance when not
    // supplied — production DI always supplies the process-wide singleton (see
    // ServiceCollectionExtensions.AddWtmWorkFlow).
    private readonly IWorkflowGraphProvider _graphProvider;

    public WorkflowTimerExecutor(
        IDataContext dc,
        IOptions<WorkFlowOptions> options,
        ILogger<WorkflowTimerExecutor> logger,
        IWorkflowEngine? engine = null,
        IWorkflowNotifier? notifier = null,
        IWorkflowGraphProvider? graphProvider = null)
    {
        _dc = dc;
        _dbDirect = null;
        _options = options.Value;
        _logger = logger;
        _engine = engine;
        _notifier = notifier;
        _graphProvider = graphProvider ?? new WorkflowGraphProvider();
    }

    /// <summary>Test / direct-DbContext constructor (mirrors WorkflowEngine's test path).
    /// <para><c>_dc</c> is null in this path — <c>DBType</c> guards are skipped; tests always
    /// supply a real SQLite DbContext, never EF InMemory.</para></summary>
    internal WorkflowTimerExecutor(
        DbContext db,
        IOptions<WorkFlowOptions> options,
        ILogger<WorkflowTimerExecutor> logger,
        IWorkflowEngine? engine = null,
        IWorkflowNotifier? notifier = null,
        IWorkflowGraphProvider? graphProvider = null)
    {
        _dc = null;
        _dbDirect = db ?? throw new ArgumentNullException(nameof(db));
        _options = options.Value;
        _logger = logger;
        _engine = engine;
        _notifier = notifier;
        _graphProvider = graphProvider ?? new WorkflowGraphProvider();
    }

    // Returns the underlying DbContext from either the prod or test constructor path.
    private DbContext GetDb() =>
        _dbDirect
        ?? (_dc as DbContext)
        ?? throw new InvalidOperationException(
            "WorkflowTimerExecutor requires IDataContext to be a DbContext subclass.");

    /// <summary>
    /// Run one complete tick: fetch due timers, gate-check, fire each in its own transaction.
    /// </summary>
    /// <param name="now">UTC instant bound ONCE per tick by the hosted service (never SQL CURRENT_TIMESTAMP).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunTickAsync(DateTime now, CancellationToken ct)
    {
        // Phase-1: fire due timers.
        await FireDueTimersAsync(now, ct);

        // Phase-2: Returning-lease reclaim (crash recovery for 回退 operations).
        await ReclaimExpiredLeasesAsync(now, ct);

        // Phase-3: AtAction expired-delegation sweep (WF-20.5).
        // Gated: DelegationWindowMode==AtAction AND DelegationExpiredSweep==RevertToPrincipal.
        await SweepExpiredAtActionDelegationsAsync(now, ct);

        // Phase-4: Strand-reaper — re-drive Sequential nodes where the SequencePointer advance was
        // lost (crash between system auto-approve claim commit and the post-commit continuation).
        // Re-drive entry: WorkflowEngine.SystemContinueTaskAsync (RowVer CAS-guarded, idempotent).
        // Default-ON (batch size 50); disabled by setting WorkFlowOptions.StrandReaperBatchSize = 0.
        await ReDriveStrandedSequentialNodesAsync(now, ct);
    }

    // #668: AllocateSeqWithRetryAsync consolidated onto GuardedTransition.AllocateSeqWithRetryAsync
    // (was verbatim-duplicated here and in WorkflowEngine) — see GuardedTransition.cs.
}
