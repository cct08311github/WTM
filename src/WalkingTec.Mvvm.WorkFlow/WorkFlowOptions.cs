#nullable enable
using System;

namespace WalkingTec.Mvvm.WorkFlow;

// ── Supporting option enums ────────────────────────────────────────────────

/// <summary>
/// Controls when a process instance may be withdrawn by its initiator.
/// </summary>
public enum WithdrawPolicy
{
    /// <summary>Withdrawal is allowed only before any approver has acted (L0).</summary>
    BeforeAnyAction,

    /// <summary>Withdrawal is allowed until the final approval is cast (L1 — default).</summary>
    BeforeFinalApproval,

    /// <summary>Withdrawal is disabled entirely (L2).</summary>
    Disabled,
}

/// <summary>
/// Policy when an approver cannot be resolved for a node (e.g. role has no members).
/// </summary>
public enum AutoApproveOnMissingHandlerPolicy
{
    /// <summary>Auto-approve the node and log a warning (default).</summary>
    AutoApprove,

    /// <summary>Escalate to the admin fallback ITCode defined in <see cref="WorkFlowOptions.AdminFallbackITCode"/>.</summary>
    EscalateToAdmin,

    /// <summary>Fail-close the instance with <c>FailClosedRouting</c> result.</summary>
    FailClose,
}

/// <summary>
/// When a standing DelegationRule window is evaluated.
/// W11 fix — explicit, loud enum; never inferred.  MUST be documented in CHANGELOG
/// when changed from the default.
/// </summary>
public enum DelegationWindowMode
{
    /// <summary>
    /// Window is checked at task-assignment time (default).
    /// Task is created for the delegatee if the rule is active when the task is minted.
    /// </summary>
    AtAssignment,

    /// <summary>
    /// Window is re-checked when the delegatee acts.
    /// If the window has expired, the action is rejected.
    /// </summary>
    AtAction,
}

/// <summary>
/// What happens to accumulated approvals on a node when it is re-entered after 回退.
/// </summary>
public enum ReturnResetMode
{
    /// <summary>
    /// All prior approvals on the re-entered span are discarded; approvers must re-act (default).
    /// </summary>
    Reset,

    /// <summary>
    /// Prior approvals are preserved; only missing approvals need to be re-cast.
    /// </summary>
    Resume,
}

// ── Main options class ─────────────────────────────────────────────────────

/// <summary>
/// Configuration options for the WTM WorkFlow engine.
/// Pass to <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/> to customise defaults.
///
/// All properties keep safe, conservative defaults that match the spec §9 invariants.
/// Any change to <see cref="InitiatorAutoApprove"/> or <see cref="DelegationWindowMode"/>
/// MUST be documented in CHANGELOG as an explicit opt-in behavior change.
/// </summary>
public sealed class WorkFlowOptions
{
    /// <summary>
    /// When <c>true</c>, the process initiator's own approval step is automatically
    /// approved on submission (common Chinese-corporate convenience).
    ///
    /// <strong>DEFAULT = FALSE (conservative, opt-in only).</strong>
    /// Changing this to <c>true</c> silently bypasses an approval step, which may
    /// constitute a control bypass in compliance-sensitive environments.
    /// MUST be documented in CHANGELOG and requires explicit opt-in.
    ///
    /// v2 spec: changed from <c>true</c> → <c>false</c> (red-line fix, spec §9).
    /// </summary>
    public bool InitiatorAutoApprove { get; set; } = false;

    /// <summary>
    /// Controls when a process instance may be withdrawn by its initiator.
    /// Default: <see cref="WithdrawPolicy.BeforeFinalApproval"/> (L1).
    /// </summary>
    public WithdrawPolicy WithdrawPolicy { get; set; } = WithdrawPolicy.BeforeFinalApproval;

    /// <summary>
    /// What happens when no approver can be resolved for a node.
    /// Default: auto-approve and log a warning (avoids deadlock; conservative for multi-tenant).
    /// </summary>
    public AutoApproveOnMissingHandlerPolicy AutoApproveOnMissingHandler { get; set; }
        = AutoApproveOnMissingHandlerPolicy.AutoApprove;

    /// <summary>
    /// Admin fallback ITCode used when <see cref="AutoApproveOnMissingHandler"/> is
    /// <see cref="AutoApproveOnMissingHandlerPolicy.EscalateToAdmin"/>.
    /// </summary>
    public string? AdminFallbackITCode { get; set; }

    /// <summary>
    /// Controls when a standing DelegationRule window is evaluated.
    /// W11 fix — explicit enum, never inferred.  Document in CHANGELOG if changed.
    /// Default: <see cref="DelegationWindowMode.AtAssignment"/>.
    /// </summary>
    public DelegationWindowMode DelegationWindowMode { get; set; } = DelegationWindowMode.AtAssignment;

    /// <summary>
    /// What happens to accumulated approvals when a node is re-entered after 回退.
    /// Default: <see cref="ReturnResetMode.Reset"/> (discard all prior approvals — safest).
    /// </summary>
    public ReturnResetMode ReturnResetMode { get; set; } = ReturnResetMode.Reset;

    /// <summary>
    /// Maximum ManagerChain traversal depth for approver resolution.
    /// Default: 5.
    /// </summary>
    public int MaxLevel { get; set; } = 5;

    /// <summary>
    /// Maximum depth for 加签 chains.
    /// Default: 3.
    /// </summary>
    public int MaxAddDepth { get; set; } = 3;

    /// <summary>
    /// Maximum transitive 委托 hops (P→D→E …) before cycle detection fires.
    /// Default: 3.
    /// </summary>
    public int MaxDelegationHops { get; set; } = 3;

    /// <summary>
    /// Maximum number of A↔B 回退 ping-pong loops before the engine terminates the instance.
    /// Default: 3.
    /// </summary>
    public int MaxReturnLoops { get; set; } = 3;

    /// <summary>
    /// Optional business-calendar ID used by the timeout wave to exclude weekends/holidays
    /// when computing due dates.  Null disables business-calendar adjustment.
    /// </summary>
    public string? BusinessCalendarId { get; set; }

    /// <summary>
    /// Poll interval for the <c>WorkflowTimerHostedService</c> (timeout wave).
    /// Default: 1 minute.
    /// </summary>
    public TimeSpan TimerPollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// When <c>true</c>, the DBTypeEnum.Memory guard fires lazily on first
    /// <see cref="WalkingTec.Mvvm.Core.IDataContext"/> resolution rather than at
    /// <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/> call time.
    /// Use this flag only when the DataContext is registered after <c>AddWtmWorkFlow</c>
    /// (unusual; the eager check is preferred).
    /// Default: false (eager check at DI registration time).
    /// </summary>
    public bool ValidateDbTypeOnFirstUse { get; set; } = false;
}
