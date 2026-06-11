#nullable enable
// WF-7: Closed discriminated outcome type for all workflow engine operations.
//
// Design rationale:
//   • Closed enum prevents callers from matching on free-form strings.
//   • Value-object record — immutable, equality by code, no side effects.
//   • Every state-changing operation in the engine returns a WorkflowActionResult
//     (spec §9 / §7.1 invariant #1).

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Closed discriminated outcome for all workflow engine state transitions.
/// Maps directly to spec §9 result codes.  Callers switch on <see cref="Code"/>
/// to decide the HTTP response or business action — never on free-form strings.
/// </summary>
public sealed record WorkflowActionResult
{
    // ── Static factory instances (common outcomes) ──────────────────────────

    /// <summary>The node advanced and is still in progress (e.g. more approvers needed).</summary>
    public static readonly WorkflowActionResult Advanced = new(WorkflowActionCode.Advanced);

    /// <summary>The current node completed (approval/cc/condition done); next node was minted.</summary>
    public static readonly WorkflowActionResult NodeCompleted = new(WorkflowActionCode.NodeCompleted);

    /// <summary>The entire process instance reached the End node and is now Approved.</summary>
    public static readonly WorkflowActionResult InstanceApproved = new(WorkflowActionCode.InstanceApproved);

    /// <summary>The instance was rejected and the rejection policy has been applied.</summary>
    public static readonly WorkflowActionResult Rejected = new(WorkflowActionCode.Rejected);

    /// <summary>The operation was a no-op because another concurrent actor already completed it.
    /// Rows-affected == 0 on the guarded CAS.  Not an error — idempotent loser.</summary>
    public static readonly WorkflowActionResult AlreadyHandled = new(WorkflowActionCode.AlreadyHandled);

    /// <summary>The node is blocked waiting for human action (e.g. Approval node pending tasks).
    /// Stub return for the Approval handler until WF-8/9/10 land.</summary>
    public static readonly WorkflowActionResult Blocked = new(WorkflowActionCode.Blocked);

    /// <summary>The instance was withdrawn by its initiator.</summary>
    public static readonly WorkflowActionResult Withdrawn = new(WorkflowActionCode.Withdrawn);

    /// <summary>The task was not active when the actor tried to act (sequential pointer mismatch).</summary>
    public static readonly WorkflowActionResult TaskNotActive = new(WorkflowActionCode.TaskNotActive);

    /// <summary>The node was already closed when the actor tried to act.</summary>
    public static readonly WorkflowActionResult NodeClosed = new(WorkflowActionCode.NodeClosed);

    /// <summary>Withdrawal was attempted but the instance is already in a final state.</summary>
    public static readonly WorkflowActionResult CannotWithdrawAlreadyFinal = new(WorkflowActionCode.CannotWithdrawAlreadyFinal);

    /// <summary>The actor is not the initiator of this instance (withdraw guard).</summary>
    public static readonly WorkflowActionResult NotInitiator = new(WorkflowActionCode.NotInitiator);

    /// <summary>The actor is not authorized to perform this action (RBAC guard).</summary>
    public static readonly WorkflowActionResult NotAuthorized = new(WorkflowActionCode.NotAuthorized);

    /// <summary>Condition node had no matching branch and no default — FAIL CLOSED (spec §5.8).</summary>
    public static readonly WorkflowActionResult FailClosedRouting = new(WorkflowActionCode.FailClosedRouting);

    /// <summary>No approver could be resolved; escalated to the admin fallback.</summary>
    public static readonly WorkflowActionResult AdminFallback = new(WorkflowActionCode.AdminFallback);

    /// <summary>The task was returned to the initiator; instance is now in Draft state for resubmission.</summary>
    public static readonly WorkflowActionResult ReturnedToInitiator = new(WorkflowActionCode.ReturnedToInitiator);

    /// <summary>
    /// 回退-to-node succeeded: span superseded and instance re-materialized at the target node.
    /// </summary>
    public static readonly WorkflowActionResult Returned = new(WorkflowActionCode.Returned);

    /// <summary>
    /// Instance terminated fail-closed because <c>ReturnLoops</c> reached <c>MaxReturnLoops</c>.
    /// </summary>
    public static readonly WorkflowActionResult MaxReturnLoopsExceeded = new(WorkflowActionCode.MaxReturnLoopsExceeded);

    /// <summary>
    /// The requested return target is not a dominator of the trigger node — invalid return path.
    /// Only dominators (every path from Start passes through the target) are valid return targets.
    /// </summary>
    public static readonly WorkflowActionResult NoDominatorTarget = new(WorkflowActionCode.NoDominatorTarget);

    /// <summary>
    /// A Join node became unsatisfiable (all feeding branches died without arriving) and
    /// was fail-closed (Wave-3 §4.4 orphan fail-closed backstop).
    /// </summary>
    public static readonly WorkflowActionResult JoinUnsatisfiable = new(WorkflowActionCode.JoinUnsatisfiable);

    // ── WF-18 Wave-4: 加签 ──────────────────────────────────────────────────

    /// <summary>
    /// 加签 rejected: chain depth has reached <see cref="WorkFlowOptions.MaxAddDepth"/>.
    /// </summary>
    public static readonly WorkflowActionResult MaxAddDepthExceeded = new(WorkflowActionCode.MaxAddDepthExceeded);

    /// <summary>
    /// 加签 rejected: the node has already left <see cref="NodeState.Activated"/>
    /// (completed, rejected, or superseded).  No task was inserted.
    /// </summary>
    public static readonly WorkflowActionResult NodeAlreadyDecided = new(WorkflowActionCode.NodeAlreadyDecided);

    // ── WF-19 Wave-4: 委托 (delegation) ────────────────────────────────────────

    /// <summary>
    /// The delegation window has expired at action time
    /// (<see cref="DelegationWindowMode.AtAction"/> mode only).
    /// The claim was rejected; the task stays Pending for manual reassignment.
    /// Distinct from <see cref="WorkflowActionCode.AlreadyHandled"/> so that UI/audit can show the real reason.
    /// </summary>
    public static readonly WorkflowActionResult DelegationExpired = new(WorkflowActionCode.DelegationExpired);

    /// <summary>
    /// The transitive delegation chain exceeded <see cref="WorkFlowOptions.MaxDelegationHops"/>.
    /// Resolution stopped at the last successfully resolved principal.  Logged at Warning.
    /// </summary>
    public static readonly WorkflowActionResult DelegationHopsExceeded = new(WorkflowActionCode.DelegationHopsExceeded);

    /// <summary>
    /// A delegation cycle was detected (A→B→A or A→A self-delegation).
    /// Resolution routes to <see cref="WorkFlowOptions.AdminFallbackITCode"/> when set;
    /// otherwise the node blocks via the FailClose policy.
    /// </summary>
    public static readonly WorkflowActionResult DelegationCycle = new(WorkflowActionCode.DelegationCycle);

    /// <summary>
    /// Mid-flight reassignment refused: the delegatee already holds an active task on this
    /// node.  <c>TotalRequired</c> is invariant — mid-flight merge is never permitted.
    /// </summary>
    public static readonly WorkflowActionResult DelegateAlreadyParticipant = new(WorkflowActionCode.DelegateAlreadyParticipant);

    // ── Instance ──────────────────────────────────────────────────────────────

    /// <summary>The outcome code for this result.</summary>
    public WorkflowActionCode Code { get; init; }

    /// <summary>Optional human-readable detail (for logs, not for control-flow branching).</summary>
    public string? Detail { get; init; }

    private WorkflowActionResult(WorkflowActionCode code, string? detail = null)
    {
        Code = code;
        Detail = detail;
    }

    /// <summary>
    /// Create a result with an additional detail message.
    /// Used for logging / error-context enrichment only — callers branch on <see cref="Code"/>.
    /// </summary>
    public static WorkflowActionResult WithDetail(WorkflowActionCode code, string detail)
        => new(code, detail);

    /// <summary>True when the result represents a successful forward advance.</summary>
    public bool IsSuccess => Code is WorkflowActionCode.Advanced
                                     or WorkflowActionCode.NodeCompleted
                                     or WorkflowActionCode.InstanceApproved
                                     or WorkflowActionCode.Withdrawn;

    /// <summary>True when the result is the idempotent loser in a concurrent race (rows == 0).</summary>
    public bool IsAlreadyHandled => Code == WorkflowActionCode.AlreadyHandled;

    public override string ToString() =>
        Detail is null ? Code.ToString() : $"{Code}: {Detail}";
}

/// <summary>
/// Closed enum of possible workflow action outcomes.
/// Never use free-form strings for engine-level branching (spec §9).
/// </summary>
public enum WorkflowActionCode
{
    /// <summary>The node advanced; more work may remain (e.g. sequential pointer moved).</summary>
    Advanced,

    /// <summary>The current node completed; the next node was minted.</summary>
    NodeCompleted,

    /// <summary>The entire process reached End and is Approved.</summary>
    InstanceApproved,

    /// <summary>The instance was rejected.</summary>
    Rejected,

    /// <summary>Concurrent loser: another actor won the CAS first (rows == 0). Not an error.</summary>
    AlreadyHandled,

    /// <summary>Node is waiting for human action (Approval stub returns this until WF-8/9/10).</summary>
    Blocked,

    /// <summary>Withdrawal succeeded.</summary>
    Withdrawn,

    /// <summary>Task was not active (sequential pointer mismatch or wrong state).</summary>
    TaskNotActive,

    /// <summary>Node was already in a terminal state when the action was attempted.</summary>
    NodeClosed,

    /// <summary>Cannot withdraw because instance is already in a final state.</summary>
    CannotWithdrawAlreadyFinal,

    /// <summary>Actor is not the process initiator (withdrawal guard).</summary>
    NotInitiator,

    /// <summary>Actor is not authorized (RBAC gate).</summary>
    NotAuthorized,

    /// <summary>Condition routing failed closed (no match + no default).</summary>
    FailClosedRouting,

    /// <summary>Admin fallback was triggered (no approver could be resolved).</summary>
    AdminFallback,

    /// <summary>Task was returned to the initiator; instance is now Draft for resubmission.</summary>
    ReturnedToInitiator,

    /// <summary>
    /// Span superseded and instance re-materialized at target node (回退-to-node success).
    /// </summary>
    Returned,

    /// <summary>
    /// Return was attempted but <c>ProcessInstance.ReturnLoops</c> has reached
    /// <c>WorkFlowOptions.MaxReturnLoops</c>.  The engine terminates the instance
    /// fail-closed rather than allowing infinite ping-pong (Race D cap, §2 STEP-6-FC).
    /// </summary>
    MaxReturnLoopsExceeded,

    /// <summary>
    /// The requested return target nodeKey is not in the dominator set of the trigger node.
    /// Only Approval nodes that dominate the trigger (every forward path from Start passes
    /// through the target) are valid return targets.  The caller must choose a different target.
    /// </summary>
    NoDominatorTarget,

    // ── WF-17 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Join node became unsatisfiable (all feeding branches died without arriving) and
    /// was fail-closed.  The engine recorded a <c>FailClosed</c> event and the Join's
    /// token was terminated.  (Wave-3 §4.4 orphan fail-closed backstop.)
    /// </summary>
    JoinUnsatisfiable,

    // ── WF-18 Wave-4: 加签 (add-approver) ─────────────────────────────────────

    /// <summary>
    /// 加签 was rejected because the 加签 chain depth has reached
    /// <see cref="WorkFlowOptions.MaxAddDepth"/>.  No CAS was attempted; no task was inserted.
    /// </summary>
    MaxAddDepthExceeded,

    /// <summary>
    /// 加签 was rejected because the node has already left <see cref="NodeState.Activated"/>
    /// (completed, rejected, or superseded).  Tasks are never inserted into a decided node.
    /// </summary>
    NodeAlreadyDecided,

    // ── WF-19 Wave-4: 委托 (delegation) ──────────────────────────────────────

    /// <summary>
    /// The delegation window has expired at claim time
    /// (<see cref="DelegationWindowMode.AtAction"/> mode only).
    /// The claim CAS returned zero rows because <c>@now &gt; DelegationExpiresUtc</c>.
    /// The task stays Pending; the node is NOT auto-approved.
    /// Callers should show a user-visible "delegation window expired" message and
    /// trigger manual reassignment.
    /// </summary>
    DelegationExpired,

    /// <summary>
    /// Transitive delegation chain exceeded <see cref="WorkFlowOptions.MaxDelegationHops"/>.
    /// Resolution stopped at the last successfully resolved delegatee; the hop cap prevents
    /// infinite chains.  Logged at Warning level.
    /// </summary>
    DelegationHopsExceeded,

    /// <summary>
    /// A delegation cycle was detected during transitive resolution (A→B→A, or A→A
    /// self-delegation).  Resolution routes to the admin fallback when configured; otherwise
    /// the node blocks via the FailClose policy.  Fail-closed — never fail-open.
    /// </summary>
    DelegationCycle,

    /// <summary>
    /// Mid-flight task reassignment refused because the intended delegatee already holds an
    /// active (Pending) task on the same node.  <c>TotalRequired</c> is invariant; merging
    /// two slots mid-flight is never permitted.  The original task remains Pending and
    /// the original holder may still act.
    /// </summary>
    DelegateAlreadyParticipant,
}
