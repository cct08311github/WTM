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
    /// <summary>
    /// Auto-approve the node and log a warning.
    /// <strong>OPT-IN ONLY — not the default.</strong>  Silently bypasses the approval step,
    /// which constitutes a compliance bypass in regulated environments.
    /// Must be documented in CHANGELOG when enabled.
    /// </summary>
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

// ── WF-20: Timeout-wave option enums ──────────────────────────────────────────

/// <summary>
/// Controls how expired AtAction-mode delegation tasks are handled by the reaper sweep.
/// Only reachable when both <c>AddWtmWorkFlowTimers()</c> is called and
/// <c>DelegationWindowMode == AtAction</c> (doubly opt-in).
/// </summary>
public enum DelegationExpiredSweep
{
    /// <summary>Sweep disabled — expired AtAction tasks are left as-is.</summary>
    Off,

    /// <summary>
    /// Revert expired delegated tasks back to the original principal (default).
    /// This narrows authority back to the accountable approver — fail-safe behaviour.
    /// </summary>
    RevertToPrincipal,
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
    /// What happens when no approver can be resolved for a node (e.g. empty role, unresolvable
    /// ManagerChain, or unsupported rule type).
    ///
    /// <strong>DEFAULT = <see cref="AutoApproveOnMissingHandlerPolicy.FailClose"/> (safe, opt-in only to change).</strong>
    ///
    /// <para>With <c>FailClose</c> (default), a node whose approver cannot be resolved is
    /// blocked and requires manual admin intervention — the instance will NOT silently advance.
    /// This is the correct behaviour for any compliance-sensitive approval engine.</para>
    ///
    /// <para>Setting this to <c>AutoApprove</c> causes the node to be silently approved and
    /// the instance to advance as if the step had been properly approved.  This is a
    /// <strong>compliance bypass</strong> and must be documented in CHANGELOG as an explicit
    /// opt-in decision.  Do not enable in regulated environments.</para>
    ///
    /// <para><c>EscalateToAdmin</c> reassigns the task to <see cref="AdminFallbackITCode"/>
    /// when set; if <see cref="AdminFallbackITCode"/> is empty the engine <strong>fails
    /// closed</strong> (never auto-approves).</para>
    ///
    /// Red-line change (WF-8): prior default was <c>AutoApprove</c> — changed to <c>FailClose</c>
    /// to eliminate the silent-approval compliance bypass when no approver is resolvable.
    /// </summary>
    public AutoApproveOnMissingHandlerPolicy AutoApproveOnMissingHandler { get; set; }
        = AutoApproveOnMissingHandlerPolicy.FailClose;

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

    // ── WF-20: Timeout-wave options (all additive, all opt-in defaults) ──────────

    /// <summary>
    /// When <c>true</c>, the timer reaper is permitted to auto-approve or auto-reject
    /// tasks when the timer fires with <c>Action == AutoApprove|AutoReject</c>.
    ///
    /// <strong>DEFAULT = FALSE (fail-closed, opt-in only).</strong>
    /// Auto-actions bypass human approval — a compliance risk.  Must be explicitly opted
    /// into and documented in CHANGELOG.
    ///
    /// Fire-time authoritative: even if a graph is published with an auto-action node,
    /// the reaper downgrades to Remind + FailClosed event when this gate is off.
    /// </summary>
    public bool AllowTimerAutoAction { get; set; } = false;

    /// <summary>
    /// TTL for the <c>Returning</c> sub-state lease used by the reaper to reclaim
    /// crash-abandoned return-to-node operations (WF-16 Race D crash addendum).
    ///
    /// <para>When a <c>BeginReturnAsync</c> operation crashes before completing STEP-6
    /// (clearing the lease), the reaper phase-2 reclaims the instance via
    /// <c>ReclaimReturningLeaseByRowVerAsync</c> after this TTL elapses.</para>
    ///
    /// Default: 30 minutes (replaces the formerly hardcoded constant — zero behaviour change).
    /// </summary>
    public TimeSpan ReturningLeaseTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum number of timers processed in a single reaper poll tick.
    /// Limits batch size to prevent long-running ticks.
    /// Default: 100.
    /// </summary>
    public int TimerBatchSize { get; set; } = 100;

    /// <summary>
    /// Default number of 催办 reminders sent per timeout chain when
    /// <c>TimeoutDef.MaxReminders</c> is not set in the graph.
    /// Default: 3.
    /// </summary>
    public int MaxRemindersDefault { get; set; } = 3;

    /// <summary>
    /// Hard cap on the number of reminders per timeout chain.
    /// <c>min(MaxReminders ?? MaxRemindersDefault, MaxRemindersHardCap)</c> is applied
    /// at arm time.  Cannot be exceeded even if MaxReminders is set higher in the graph.
    /// Default: 10.
    /// </summary>
    public int MaxRemindersHardCap { get; set; } = 10;

    /// <summary>
    /// Controls how expired AtAction-mode delegation tasks are handled by the reaper sweep.
    /// Default: <see cref="DelegationExpiredSweep.RevertToPrincipal"/> (fail-safe — reverts
    /// authority back to the accountable approver).
    ///
    /// Only reachable in the doubly-opt-in intersection:
    /// <c>AddWtmWorkFlowTimers()</c> called AND <c>DelegationWindowMode == AtAction</c>.
    /// AtAction is ctor-blocked on Oracle/DaMeng — those providers are structurally unreachable.
    /// </summary>
    public DelegationExpiredSweep DelegationExpiredSweep { get; set; }
        = DelegationExpiredSweep.RevertToPrincipal;

    /// <summary>
    /// When <c>true</c>, the DBTypeEnum.Memory guard fires lazily on first
    /// <see cref="WalkingTec.Mvvm.Core.IDataContext"/> resolution rather than at
    /// <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/> call time.
    /// Use this flag only when the DataContext is registered after <c>AddWtmWorkFlow</c>
    /// (unusual; the eager check is preferred).
    /// Default: false (eager check at DI registration time).
    /// </summary>
    public bool ValidateDbTypeOnFirstUse { get; set; } = false;

    // ── WF-21.3: Low-code designer sub-options ────────────────────────────────

    /// <summary>
    /// Sub-options for the low-code workflow designer (WF-21).
    /// Only relevant when <see cref="ServiceCollectionExtensions.AddWtmWorkFlowDesigner"/> is called.
    /// </summary>
    public DesignerOptions Designer { get; set; } = new();
}

/// <summary>
/// Options for the low-code workflow designer surface (WF-21.3).
///
/// <para>Registered as a nested class under <see cref="WorkFlowOptions"/> so it follows
/// the same <c>IOptions&lt;WorkFlowOptions&gt;</c> binding chain — no extra Options
/// registration call is needed.</para>
/// </summary>
public sealed class DesignerOptions
{
    /// <summary>
    /// Maximum allowed graph document size in bytes for the raw PUT draft and
    /// POST publish endpoints.
    ///
    /// <para>Bodies larger than this value are rejected with 413 Request Entity Too Large
    /// before any deserialization occurs.</para>
    ///
    /// <para>Default: 1,048,576 bytes (1 MiB).
    /// Adjust downward in regulated environments where graph documents must remain small.</para>
    /// </summary>
    public int MaxGraphBytes { get; set; } = 1_048_576; // 1 MiB
}
