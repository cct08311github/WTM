#nullable enable
// WF-6: IWorkflowEngine public contract.
//
// All methods operate inside a single DbContext transaction per call (spec §7.3).
// Callers (WorkflowActionVM) interact only through this interface — never touching
// DataContext directly (WTM red line: no controller-level DC access).

using System;
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
    ///         <see cref="RejectPolicy.ReturnToInitiator"/> → basic reject-terminates for MVP
    ///         (full WF-12 ReturnToInitiator restart is deferred).</item>
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
}
