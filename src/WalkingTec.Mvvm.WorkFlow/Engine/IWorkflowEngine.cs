#nullable enable
// WF-6: IWorkflowEngine public contract.
//
// All methods operate inside a single DbContext transaction per call (spec §7.3).
// Callers (WorkflowActionVM) interact only through this interface — never touching
// DataContext directly (WTM red line: no controller-level DC access).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// The workflow engine public contract.
/// Registered as scoped by <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>.
///
/// <para><strong>Transaction contract:</strong> every method wraps all DB writes in a
/// single transaction.  On <c>DbUpdateConcurrencyException</c> the engine retries up to
/// 3 times with a fresh read (spec §7.3).</para>
///
/// <para><strong>Audit:</strong> every method appends at least one
/// <see cref="WorkflowEventLog"/> row inside its transaction (spec §8.4).</para>
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>
    /// Start a new process instance for the published definition identified by
    /// <paramref name="definitionVersionId"/>.
    ///
    /// <list type="number">
    ///   <item>Creates a <see cref="ProcessInstance"/> in <see cref="InstanceState.Running"/>.</item>
    ///   <item>Deserializes the pinned <see cref="ProcessDefinitionVersion.GraphJson"/>.</item>
    ///   <item>Mints the initial token at the Start node and calls <c>AdvanceAsync</c>
    ///         to drive through all pass-through nodes until the process either completes
    ///         or reaches an Approval node that blocks.</item>
    /// </list>
    /// </summary>
    /// <param name="definitionVersionId">FK to the immutable <see cref="ProcessDefinitionVersion"/>.</param>
    /// <param name="formDataJson">JSON-serialized form data captured at submission time.
    /// Used by the routing evaluator (WF-11).</param>
    /// <param name="initiatorITCode">ITCode of the submitter.</param>
    /// <param name="tenantCode">Tenant isolation code.</param>
    /// <param name="businessType">Optional discriminator for the business object type.</param>
    /// <param name="businessKey">Optional PK of the associated business object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created <see cref="ProcessInstance"/> (ID + State after driving).</returns>
    Task<ProcessInstance> StartAsync(
        Guid definitionVersionId,
        string? formDataJson,
        string initiatorITCode,
        string? tenantCode,
        string? businessType = null,
        string? businessKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Advance the process instance identified by <paramref name="instanceId"/>.
    ///
    /// <para>This is the single internal advancement entry:
    /// claim current active node → run handler → on completion route via outgoing
    /// transitions → mint next token(s) → write event log — all in one transaction.</para>
    ///
    /// <para>Called automatically by <see cref="StartAsync"/> and after human actions
    /// (Approve/Reject/etc.) once the node's completion condition is satisfied.</para>
    /// </summary>
    Task<WorkflowActionResult> AdvanceAsync(
        Guid instanceId,
        CancellationToken ct = default);

    /// <summary>
    /// Actor approves the <see cref="ApprovalTask"/> identified by <paramref name="taskId"/>.
    ///
    /// <para>Full Sequential-mode flow per spec §5.1:
    /// <list type="number">
    ///   <item>Load task + node + instance; verify tenant isolation.</item>
    ///   <item>Verify <paramref name="actorITCode"/> matches <c>AssigneeITCode</c>
    ///         (early-act guard: returns <see cref="WorkflowActionCode.TaskNotActive"/> if mismatch).</item>
    ///   <item>Guarded CAS via <c>GuardedTransition.ClaimApprovalTaskAsync</c>:
    ///         <c>WHERE State==Pending AND RowVer==expected</c>.
    ///         rows==0 → <see cref="WorkflowActionCode.AlreadyHandled"/>.</item>
    ///   <item>If more steps remain: advance <c>NodeInstance.SequencePointer</c> + activate next task.</item>
    ///   <item>If last step: <see cref="AdvanceAsync"/> to route onward (or reach End → Approved).</item>
    ///   <item>Write <see cref="WorkflowEventLog"/> row for the approve action.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="taskId">PK of the <see cref="ApprovalTask"/> to act on.</param>
    /// <param name="actorITCode">ITCode of the acting approver (RBAC: must match AssigneeITCode).</param>
    /// <param name="comment">Optional approver comment.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WorkflowActionResult> ApproveTaskAsync(
        Guid taskId,
        string actorITCode,
        string? comment = null,
        CancellationToken ct = default);

    /// <summary>
    /// Actor rejects the <see cref="ApprovalTask"/> identified by <paramref name="taskId"/>.
    ///
    /// <para>MVP reject behavior (spec §5.1):
    /// <list type="number">
    ///   <item>Load task + node + instance; verify tenant isolation.</item>
    ///   <item>Verify <paramref name="actorITCode"/> matches <c>AssigneeITCode</c>.</item>
    ///   <item>Guarded CAS: claim task as Rejected.</item>
    ///   <item>Cancel any remaining <see cref="TaskState.NotYetActive"/> tasks on this node.</item>
    ///   <item>Complete node as <see cref="NodeState.CompletedRejected"/>.</item>
    ///   <item>Apply <c>NodeInstance.RejectPolicy</c>:
    ///         <see cref="RejectPolicy.TerminateInstance"/> → instance Rejected (terminal);
    ///         <see cref="RejectPolicy.ReturnToInitiator"/> → returns instance to draft/initiator state
    ///         so the initiator can resubmit (WF-12 ReturnToInitiator).</item>
    ///   <item>Write <see cref="WorkflowEventLog"/> row.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="taskId">PK of the <see cref="ApprovalTask"/> to act on.</param>
    /// <param name="actorITCode">ITCode of the acting approver.</param>
    /// <param name="reason">Rejection reason (required for reject actions).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WorkflowActionResult> RejectTaskAsync(
        Guid taskId,
        string actorITCode,
        string? reason = null,
        CancellationToken ct = default);

    // ── WF-12: 撤回 (Withdraw) ────────────────────────────────────────────────

    /// <summary>
    /// Initiator (or admin) withdraws the process instance identified by
    /// <paramref name="instanceId"/>.
    ///
    /// <para>Only the original initiator (<see cref="Models.ProcessInstance.InitiatorITCode"/>)
    /// or an admin may call this.  If <paramref name="isAdmin"/> is <c>true</c> the
    /// initiator check is bypassed.</para>
    ///
    /// <para>Honors <see cref="WorkFlowOptions.WithdrawPolicy"/>:
    /// <list type="bullet">
    ///   <item><see cref="WithdrawPolicy.BeforeAnyAction"/> (L0) — withdrawal only allowed while
    ///         no approver has acted yet (all tasks still Pending / NotYetActive).</item>
    ///   <item><see cref="WithdrawPolicy.BeforeFinalApproval"/> (L1, default) — withdrawal allowed
    ///         as long as the instance has not yet reached a terminal / approved state.</item>
    ///   <item><see cref="WithdrawPolicy.Disabled"/> (L2) — withdrawal is never allowed.</item>
    /// </list>
    /// </para>
    ///
    /// <para><strong>Instance-level guarded CAS:</strong>
    /// <c>WHERE ProcessInstance.State == Running AND RowVer == expected</c>.
    /// If another concurrent actor (e.g. the final approver) wins the race first,
    /// this method returns <see cref="WorkflowActionCode.CannotWithdrawAlreadyFinal"/>
    /// rather than throwing.  This is the T-CONC-2 race (spec §7.1.c).</para>
    ///
    /// <para>On success: all Pending <see cref="Models.ApprovalTask"/>s for the instance are
    /// cancelled; a <see cref="Models.WorkflowEventLog"/> row is appended.</para>
    /// </summary>
    /// <param name="instanceId">PK of the <see cref="Models.ProcessInstance"/> to withdraw.</param>
    /// <param name="actorITCode">ITCode of the actor requesting withdrawal.</param>
    /// <param name="reason">Optional withdrawal reason for the audit log.</param>
    /// <param name="isAdmin">When <c>true</c>, bypass the initiator check (admin override).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="WorkflowActionCode.Withdrawn"/> on success;
    /// <see cref="WorkflowActionCode.NotInitiator"/> when actor is not the initiator and not admin;
    /// <see cref="WorkflowActionCode.CannotWithdrawAlreadyFinal"/> when the instance has already
    ///   reached a terminal state (race lost or already final);
    /// <see cref="WorkflowActionCode.NotAuthorized"/> when <see cref="WithdrawPolicy.Disabled"/>.
    /// </returns>
    Task<WorkflowActionResult> WithdrawAsync(
        Guid instanceId,
        string actorITCode,
        string? reason = null,
        bool isAdmin = false,
        CancellationToken ct = default);

    // ── WF-14: Inbox query ────────────────────────────────────────────────────

    /// <summary>
    /// Returns all <see cref="Models.ApprovalTask"/>s currently in
    /// <see cref="Models.TaskState.Pending"/> state for the given actor,
    /// scoped to the actor's tenant.
    ///
    /// <para>Used by the controller inbox endpoint to build the approver's to-do list
    /// without the controller touching <c>DataContext</c> directly (WTM red line).</para>
    /// </summary>
    /// <param name="actorITCode">Server-side ITCode from <c>Wtm.LoginUserInfo.ITCode</c>.</param>
    /// <param name="tenantCode">Server-side tenant from <c>Wtm.LoginUserInfo.TenantCode</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of pending tasks ordered by <c>DueUtc</c> ascending (nulls last).</returns>
    Task<IReadOnlyList<Models.ApprovalTask>> GetPendingTasksAsync(
        string actorITCode,
        string? tenantCode,
        CancellationToken ct = default);

    // ── WF-12: 回退发起人 (ReturnToInitiator) ────────────────────────────────

    /// <summary>
    /// Approver returns the task to the initiator so the initiator may revise and resubmit.
    ///
    /// <para>MVP "ReturnToInitiator" — restart semantics (spec §5.7):
    /// <list type="number">
    ///   <item>Load task + node + instance.</item>
    ///   <item>Verify <paramref name="actorITCode"/> matches <c>AssigneeITCode</c>.</item>
    ///   <item>Guarded CAS: claim task as Rejected (the task that triggered the return).</item>
    ///   <item>Cancel all remaining Pending/NotYetActive tasks on the current node.</item>
    ///   <item>Complete the node as <see cref="Models.NodeState.Returned"/>.</item>
    ///   <item>Set instance to <see cref="Models.InstanceState.Draft"/> (re-editable state)
    ///         via guarded CAS.</item>
    ///   <item>Write <see cref="Models.WorkflowEventLog"/> with <see cref="Models.EventAction.Return"/>.</item>
    /// </list>
    /// </para>
    ///
    /// <para><strong>Resubmit:</strong> the initiator calls <see cref="StartAsync"/> with the
    /// same <c>definitionVersionId</c> + updated <c>formDataJson</c> (a new <see cref="Models.ProcessInstance"/>
    /// is created). The returned instance remains in Draft state and is not reused.</para>
    /// </summary>
    /// <param name="taskId">PK of the <see cref="Models.ApprovalTask"/> being returned.</param>
    /// <param name="actorITCode">ITCode of the approver initiating the return.</param>
    /// <param name="reason">Reason for the return (surfaced in the event log).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WorkflowActionResult> ReturnToInitiatorAsync(
        Guid taskId,
        string actorITCode,
        string? reason = null,
        CancellationToken ct = default);

    // ── WF-16: 回退-to-node (Wave-3) ─────────────────────────────────────────

    /// <summary>
    /// Approver returns the flow to the immediately-preceding Approval node
    /// (<c>ReturnToPrev</c> convenience wrapper over <see cref="ReturnToNodeAsync"/>).
    ///
    /// <para>The preceding node is the last Approval node in the transition path
    /// that dominated the current trigger node.  If no preceding Approval node
    /// exists the method returns <see cref="WorkflowActionCode.NoDominatorTarget"/>.</para>
    ///
    /// <para><strong>Wave-3 semantics (supersede-not-delete backbone):</strong>
    /// <list type="number">
    ///   <item>STEP-0: Load and validate trigger task + node + instance.</item>
    ///   <item>STEP-1 (<em>linearization point</em>): <c>BeginReturnAsync</c> — instance-level
    ///         CAS atomically sets <c>State=Returning</c>, increments <c>Generation</c>,
    ///         increments <c>ReturnLoops</c>, stamps <c>ReturningLeaseUtc</c>.</item>
    ///   <item>STEP-2: Cancel armed timers for every span node (Race C guard).</item>
    ///   <item>STEP-3: Discard active tasks on span nodes (sets State=Cancelled, scoped by
    ///         current generation).</item>
    ///   <item>STEP-4: Supersede all span NodeInstances (CAS sets <c>State=Superseded</c> +
    ///         <c>SupersededAtGen</c>) — Race A guard via shared RowVer.</item>
    ///   <item>STEP-5: Mint fresh <see cref="Models.NodeInstance"/> at the target node
    ///         (idempotent via UNIQUE constraint on TenantCode+InstanceId+NodeKey+Generation).</item>
    ///   <item>STEP-6: Set instance <c>State=Running</c> via guarded CAS.</item>
    ///   <item>Write <see cref="Models.WorkflowEventLog"/> with <see cref="Models.EventAction.Return"/>.</item>
    /// </list>
    /// </para>
    ///
    /// <para><strong>Race A (span-discard vs in-flight approve):</strong> supersede CAS shares
    /// the same RowVer as the approver's CompleteNodeInstanceAsync — exactly one wins.</para>
    ///
    /// <para><strong>Race D (concurrent returns + MaxReturnLoops):</strong> capped by
    /// <see cref="WorkFlowOptions.MaxReturnLoops"/>; <c>BeginReturnAsync</c> predicate
    /// atomically enforces the cap.</para>
    /// </summary>
    /// <param name="taskId">PK of the trigger <see cref="Models.ApprovalTask"/>.</param>
    /// <param name="actorITCode">ITCode of the approver initiating the return.</param>
    /// <param name="reason">Reason for the return (surfaced in the event log).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="WorkflowActionCode.Returned"/> on success;
    /// <see cref="WorkflowActionCode.NoDominatorTarget"/> when no preceding Approval node exists;
    /// <see cref="WorkflowActionCode.MaxReturnLoopsExceeded"/> when <see cref="WorkFlowOptions.MaxReturnLoops"/> is reached;
    /// <see cref="WorkflowActionCode.AlreadyHandled"/> when the task/node was already acted on by a concurrent caller.
    /// </returns>
    Task<WorkflowActionResult> ReturnToPrevAsync(
        Guid taskId,
        string actorITCode,
        string? reason = null,
        CancellationToken ct = default);

    // ── WF-18: 加签 (add-approver, pre/post) ─────────────────────────────────

    /// <summary>
    /// Current approver injects one or more additional approvers into the active node
    /// (加签 — add-approver, Wave-4 WF-18).
    ///
    /// <para><strong>Supported modes:</strong>
    /// <list type="bullet">
    ///   <item><see cref="Models.ApproveMode.Sequential"/> (<em>串签</em>) —
    ///     new tasks are inserted Before or After the current active pointer position
    ///     (see <paramref name="position"/>).  The existing tasks are re-sequenced so
    ///     there are no gaps or duplicates in <c>SequenceOrder</c>.</item>
    ///   <item><see cref="Models.ApproveMode.All"/> (<em>会签</em>) —
    ///     new tasks are added as parallel inboxes; <c>TotalRequired</c> is incremented
    ///     and the node threshold re-evaluated atomically via the
    ///     <c>ApproverSetEpoch</c> CAS guard (FIX-A/B).</item>
    ///   <item><see cref="Models.ApproveMode.Any"/> (<em>或签</em>) —
    ///     <c>position</c> is ignored; new tasks are added in parallel;
    ///     the first-to-approve rule is unchanged.</item>
    /// </list>
    /// </para>
    ///
    /// <para><strong>Guards:</strong>
    /// <list type="bullet">
    ///   <item>RBAC: <paramref name="actorITCode"/> must own an active
    ///     <see cref="Models.TaskState.Pending"/> task on the target node.</item>
    ///   <item>Depth cap: injected tasks get <c>AddDepth = sourceTask.AddDepth + 1</c>.
    ///     If that would exceed <see cref="WorkFlowOptions.MaxAddDepth"/> the method
    ///     returns <see cref="WorkflowActionCode.MaxAddDepthExceeded"/>.</item>
    ///   <item>Node state: if the node has already left
    ///     <see cref="Models.NodeState.Activated"/> the method returns
    ///     <see cref="WorkflowActionCode.NodeAlreadyDecided"/>.</item>
    ///   <item>CAS: <c>AddApproversToNodeAsync</c> asserts
    ///     <c>ApproverSetEpoch</c> + <c>RowVer</c> in one atomic UPDATE so that
    ///     concurrent 加签 requests cannot double-inflate <c>TotalRequired</c>.</item>
    /// </list>
    /// </para>
    ///
    /// <para><strong>Transaction boundary:</strong> the guarded UPDATE on NodeInstance
    /// and all k task INSERTs happen inside one explicit transaction; a crash between
    /// them is impossible.</para>
    ///
    /// <para><strong>Event log:</strong> one <see cref="Models.WorkflowEventLog"/> row
    /// with <see cref="Models.EventAction.AddApprover"/> is appended on success.</para>
    /// </summary>
    /// <param name="taskId">PK of the requesting actor's active <see cref="Models.ApprovalTask"/>.</param>
    /// <param name="actorITCode">Server-side ITCode of the requesting actor (RBAC guard).</param>
    /// <param name="newApproverITCodes">ITCodes of the approvers to inject (deduped; must be non-empty).</param>
    /// <param name="position">
    ///   Before or After the current pointer (Sequential mode only; ignored for All/Any).
    /// </param>
    /// <param name="reason">Optional reason for the 加签 action (surfaced in the event log).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="WorkflowActionCode.Advanced"/> on success (tasks injected, node still active);
    /// <see cref="WorkflowActionCode.MaxAddDepthExceeded"/> when the depth cap would be exceeded;
    /// <see cref="WorkflowActionCode.NodeAlreadyDecided"/> when the node is no longer Activated;
    /// <see cref="WorkflowActionCode.NotAuthorized"/> when <paramref name="actorITCode"/> has no active task on the node;
    /// <see cref="WorkflowActionCode.AlreadyHandled"/> when a concurrent actor won the epoch CAS.
    /// </returns>
    Task<WorkflowActionResult> AddApproverAsync(
        Guid taskId,
        string actorITCode,
        IReadOnlyList<string> newApproverITCodes,
        Models.AddPosition position = Models.AddPosition.After,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Mid-flight delegation (转办/委托-now): atomically reassigns an existing Pending
    /// <see cref="Models.ApprovalTask"/> from its current holder to a new delegatee via a
    /// single-statement CAS guard (design §3 path B, §4 FIX-C, WF-19).
    ///
    /// <para><strong>1-for-1 slot transfer:</strong> <c>TotalRequired</c> on the node is
    /// NEVER modified.  The delegatee steps into exactly the principal's slot; vote count
    /// is structurally unchanged.</para>
    ///
    /// <para><strong>Collision guard:</strong> if the delegatee already holds an active
    /// (<see cref="Models.TaskState.Pending"/>, <see cref="Models.TaskState.AddedPending"/>,
    /// or <see cref="Models.TaskState.NotYetActive"/>) task on the same node and generation,
    /// the reassignment is refused with
    /// <see cref="WorkflowActionCode.DelegateAlreadyParticipant"/> — mid-flight merge of
    /// two active slots would break the vote count invariant.</para>
    ///
    /// <para><strong>Generation guard:</strong> if the task's
    /// <see cref="Models.ApprovalTask.Generation"/> has been superseded (e.g. after a
    /// 回退 span-discard), the CAS returns rows==0 and the method returns
    /// <see cref="WorkflowActionCode.AlreadyHandled"/> — no zombie reassignment.</para>
    ///
    /// <para><strong>Standing rules:</strong> this entry is explicit 转办/委托-now only.
    /// Standing DelegationRules affect the next activation path; no retro-mint is performed
    /// here.</para>
    ///
    /// <para><strong>Event log:</strong> one <see cref="Models.WorkflowEventLog"/> row with
    /// <see cref="Models.EventAction.Delegate"/> is appended on every attempt that reaches
    /// the CAS (success or collision).</para>
    /// </summary>
    /// <param name="taskId">PK of the actor's active <see cref="Models.ApprovalTask"/> to reassign.</param>
    /// <param name="actorITCode">Server-side ITCode of the principal (RBAC guard: must equal task.AssigneeITCode).</param>
    /// <param name="delegateeITCode">ITCode of the new assignee after reassignment.</param>
    /// <param name="delegationRuleId">Optional FK-by-value to a DelegationRule for provenance tracking.</param>
    /// <param name="reason">Optional reason surfaced in the event log.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="WorkflowActionCode.Advanced"/> on success (slot reassigned, node epoch bumped);
    /// <see cref="WorkflowActionCode.DelegateAlreadyParticipant"/> when delegatee already has an active slot;
    /// <see cref="WorkflowActionCode.TaskNotActive"/> when the task is not found or not Pending;
    /// <see cref="WorkflowActionCode.NotAuthorized"/> when <paramref name="actorITCode"/> is not the assignee;
    /// <see cref="WorkflowActionCode.NodeClosed"/> when the node or process instance is not found;
    /// <see cref="WorkflowActionCode.NodeAlreadyDecided"/> when the node left Activated state;
    /// <see cref="WorkflowActionCode.AlreadyHandled"/> when a concurrent CAS beat this call (rows==0).
    /// </returns>
    Task<WorkflowActionResult> DelegateTaskAsync(
        Guid taskId,
        string actorITCode,
        string delegateeITCode,
        Guid? delegationRuleId = null,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Approver returns the flow to an arbitrary upstream Approval node that dominates
    /// the current trigger node (ReturnToNode — spec §5.7 Wave-3).
    ///
    /// <para>The <paramref name="targetNodeKey"/> must be an Approval node whose key
    /// appears in the dominator set of the trigger node (every path from Start to the
    /// trigger node passes through it).  Attempting to return to a non-dominating node
    /// returns <see cref="WorkflowActionCode.NoDominatorTarget"/>.</para>
    ///
    /// <para>See <see cref="ReturnToPrevAsync"/> for the full Wave-3 STEP 0-6 description.</para>
    /// </summary>
    /// <param name="taskId">PK of the trigger <see cref="Models.ApprovalTask"/>.</param>
    /// <param name="targetNodeKey">NodeKey of the Approval node to return to.</param>
    /// <param name="actorITCode">ITCode of the approver initiating the return.</param>
    /// <param name="reason">Reason for the return (surfaced in the event log).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="WorkflowActionCode.Returned"/> on success;
    /// <see cref="WorkflowActionCode.NoDominatorTarget"/> when target is not a dominator;
    /// <see cref="WorkflowActionCode.MaxReturnLoopsExceeded"/> when the cap is reached;
    /// <see cref="WorkflowActionCode.AlreadyHandled"/> when the task/node was already handled.
    /// </returns>
    Task<WorkflowActionResult> ReturnToNodeAsync(
        Guid taskId,
        string targetNodeKey,
        string actorITCode,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Admin revocation: reverts all open Pending <see cref="Models.ApprovalTask"/> rows
    /// produced by <paramref name="delegationRuleId"/> back to their original principals
    /// (1-for-1 slot reassignment — <c>TotalRequired</c> NEVER changes).
    ///
    /// <para><strong>Relationship to <see cref="Models.DelegationRule.IsValid"/>:</strong>
    /// Flipping <c>DelegationRule.IsValid = false</c> alone affects future activations only
    /// (standing delegation affects the next node activation, not nodes already activated).
    /// This method is the in-flight revocation path.</para>
    ///
    /// <para><strong>Idempotent:</strong> tasks that were already claimed, cancelled, or
    /// superseded since the snapshot are reported as <see cref="GuardedTransition.RevokeSingleTaskResult.NotPending"/>
    /// — not an error.  Partial success (some tasks reverted, some already handled) is valid.</para>
    ///
    /// <para><strong>RBAC:</strong> caller must hold the admin role before invoking this method.</para>
    ///
    /// <para><strong>Epoch bump:</strong> each successfully reverted task causes its owning node's
    /// <c>ApproverSetEpoch</c> to be bumped so any in-flight completion re-reads the updated
    /// eligible-actor set.</para>
    ///
    /// <para><strong>Event log:</strong> one <see cref="Models.WorkflowEventLog"/> row per
    /// successfully revoked task (action = <see cref="Models.EventAction.Delegate"/>).</para>
    /// </summary>
    /// <param name="delegationRuleId">PK of the <see cref="Models.DelegationRule"/> to revoke.</param>
    /// <param name="actorITCode">ITCode of the admin performing the revocation (RBAC + audit).</param>
    /// <param name="reason">Optional reason surfaced in the event log.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Count of tasks successfully reverted (rows==1 CAS); 0 if no tasks were affected.
    /// Partial success (some tasks already handled) does NOT produce an error result.
    /// </returns>
    Task<int> RevokeDelegationAsync(
        Guid delegationRuleId,
        string actorITCode,
        string? reason = null,
        CancellationToken ct = default);
}
