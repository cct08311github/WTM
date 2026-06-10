#nullable enable
namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>Overall state of a <see cref="ProcessInstance"/>.</summary>
public enum InstanceState
{
    Draft,
    Running,
    Approved,
    Rejected,
    Withdrawn,
    Terminated,

    /// <summary>
    /// Transient mutex sub-state entered at the STEP-1 linearization point of a 回退-to-node
    /// operation.  The return is in progress; the instance holds a <c>ReturningLeaseUtc</c>
    /// that a Wave-5 reaper can reclaim if the engine crashes mid-operation (Race D).
    /// </summary>
    Returning,
}

/// <summary>State of a <see cref="NodeInstance"/> within a running process.</summary>
public enum NodeState
{
    Pending,
    Activated,
    CompletedApproved,
    CompletedRejected,
    Skipped,
    Returned,

    /// <summary>
    /// Terminal: this node was part of a span discarded during a 回退-to-node operation.
    /// The row is NEVER deleted — a late approver's CAS will always find it and can resolve
    /// to AlreadyHandled instead of an FK abort.  State is terminal so the advisory
    /// <c>IncrementNodeApprovedCountAsync</c> (WHERE State==Activated) auto-no-ops.
    /// (Wave-3 design §1.1, §3 Race A, §4.1 token model.)
    /// </summary>
    Superseded,
}

/// <summary>State of an <see cref="ApprovalTask"/> assigned to an approver.</summary>
public enum TaskState
{
    NotYetActive,
    Pending,
    Suspended,
    Approved,
    Rejected,
    Transferred,
    Delegated,
    AddedPending,
    Expired,
    AutoApproved,
    AutoRejected,
    Cancelled,
}

// ── WF-17: AckMode (blocking-acknowledge completion mode) ──────────────────

/// <summary>
/// Completion mode for a <c>NodeKind.Ack</c> (blocking-acknowledge) node.
/// Mirrors <see cref="ApproveMode"/> semantics: determines how many of the
/// assigned acknowledgers must act before the token is released.
/// </summary>
public enum AckMode
{
    /// <summary>All assigned acknowledgers must acknowledge before the token passes.</summary>
    All,

    /// <summary>Any single acknowledger's action releases the token.</summary>
    Any,

    /// <summary>A configured quorum (proportion) must acknowledge.</summary>
    Quorum,
}

/// <summary>Approval completion mode for an Approval node.</summary>
public enum ApproveMode
{
    /// <summary>串签 — approvers act in sequence; one active task at a time.</summary>
    Sequential,

    /// <summary>会签 — all (or a configured percent) must approve.</summary>
    All,

    /// <summary>或签 — any single approver's action decides.</summary>
    Any,
}

/// <summary>When a rejection closes the node in 会签 mode.</summary>
public enum RejectGate
{
    /// <summary>First reject immediately closes the node (default).</summary>
    Immediate,

    /// <summary>Wait for all approvers to act; fail if any reject.</summary>
    AfterAll,
}

/// <summary>What happens to the instance when a node is rejected.</summary>
public enum RejectPolicy
{
    TerminateInstance,
    ReturnToPrev,
    ReturnToNode,
    ReturnToInitiator,
}

/// <summary>Type of a node embedded in the process-definition graph JSON.</summary>
public enum NodeKind
{
    Start,
    Approval,
    Condition,
    Cc,
    Ack,
    Join,
    End,

    // ── WF-17: Parallel/Inclusive gateways (multi-token marking, Wave-3 §4) ──

    /// <summary>
    /// AND-fork gateway: activates ALL outgoing branch tokens simultaneously.
    /// Paired with a downstream <see cref="Join"/> node whose
    /// <c>JoinExpectedArrivals</c> equals the number of branches minted.
    /// </summary>
    ParallelGateway,

    /// <summary>
    /// OR-fork gateway: activates only the outgoing branches whose
    /// <c>TransitionDef.Condition</c> evaluates true via
    /// <c>WhitelistRoutingEvaluator</c>.  <c>JoinExpectedArrivals</c> on the
    /// paired Join is pinned at fork time to the number of branches actually activated.
    /// Fail-closed if zero branches match (same as exclusive Condition node).
    /// </summary>
    InclusiveGateway,
}

/// <summary>Actions recorded in <see cref="WorkflowEventLog"/>.</summary>
public enum EventAction
{
    Submit,
    Approve,
    Reject,
    Withdraw,
    Return,
    AddApprover,
    Transfer,
    Delegate,
    AutoAdvance,
    Skip,
    Notify,
    TimeoutFire,
    FailClosed,
}

/// <summary>Status of a durable <see cref="WorkflowTimer"/>.</summary>
public enum TimerStatus
{
    Armed,
    Fired,
    Cancelled,
}

/// <summary>Action executed when a <see cref="WorkflowTimer"/> fires.</summary>
public enum TimerAction
{
    Remind,
    AutoApprove,
    AutoReject,
    Escalate,
}

/// <summary>Trigger for a CC record to be emitted.</summary>
public enum CcTrigger
{
    OnSubmit,
    OnNode,
    OnComplete,
}
