#nullable enable
// WF-20: Internal closed union for per-timer fire pipeline outcomes.
// ITimeoutActionRegistry design-doc sketch was DROPPED (Wave-5 §0 verdict A).
// All branching is done via a closed switch on TimerAction; outcomes are this union.

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Closed union of possible outcomes for a single timer's fire pipeline.
/// Internal — not part of the public engine surface.
/// Used by <see cref="WorkflowTimerExecutor"/> to drive per-timer metrics and logging.
/// </summary>
internal enum TimerFireOutcome
{
    /// <summary>Timer successfully fired and the action was executed (Remind sent, auto-action applied, etc.).</summary>
    Fired,

    /// <summary>Another host won the <see cref="GuardedTransition.FireTimerAsync"/> CAS (rows==0). No side-effects.</summary>
    LostRace,

    /// <summary>
    /// Timer was orphaned (instance not Running, generation mismatch, or node not Activated)
    /// before the fire CAS was reached.  Timer was retired (Fired) with zero action side-effects.
    /// </summary>
    OrphanRetired,

    /// <summary>
    /// A human actor already acted on the task before the timer fired.
    /// Fire CAS succeeded but the action-decision CAS found no Pending tasks.
    /// </summary>
    HumanActedNoOp,

    /// <summary>
    /// Timer generation does not match the current instance/node generation (superseded span).
    /// Retired via GATE-0 with zero side-effects.
    /// </summary>
    SupersededNoOp,

    /// <summary>
    /// AutoApprove/AutoReject action was downgraded to Remind because
    /// <c>AllowTimerAutoAction == false</c>. A FailClosed event was emitted.
    /// </summary>
    DowngradedToRemind,

    /// <summary>
    /// The engine returned a fail-closed result (missing handler, Memory guard, custom
    /// <c>IWorkflowEngine</c> without system-act support, or empty escalation targets).
    /// A FailClosed event was emitted.
    /// </summary>
    FailClosed,

    /// <summary>
    /// Escalation collision: the target already has a task on the same node+generation.
    /// Downgraded to notify-only; no task reassignment occurred.
    /// </summary>
    EscalateCollisionNotifyOnly,
}
