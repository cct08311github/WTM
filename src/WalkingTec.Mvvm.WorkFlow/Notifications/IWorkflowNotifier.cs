#nullable enable
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Notifications;

/// <summary>
/// Opt-in notification seam for workflow lifecycle events.
/// Registered by <see cref="ServiceCollectionExtensions.AddWtmWorkFlowNotifications"/>.
///
/// <para>All methods are fire-and-forget best-effort: implementations MUST NOT throw in a way
/// that propagates to the caller.  Notification failures are logged but never roll back
/// the originating engine transaction.</para>
///
/// <para>Card content is restricted to process/instance/task identifiers, actor code, and
/// decision outcome.  Sensitive form data and PII are never included.</para>
/// </summary>
public interface IWorkflowNotifier
{
    /// <summary>
    /// Called after a task is assigned to an approver (node activation).
    /// </summary>
    Task NotifyTaskAssignedAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        ApprovalTask task,
        CancellationToken ct = default);

    /// <summary>
    /// Called after an approver approves a task.
    /// </summary>
    Task NotifyApprovedAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        ApprovalTask task,
        string actorITCode,
        CancellationToken ct = default);

    /// <summary>
    /// Called after an approver rejects a task.
    /// </summary>
    Task NotifyRejectedAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        ApprovalTask task,
        string actorITCode,
        string? reason,
        CancellationToken ct = default);

    /// <summary>
    /// Called after an instance reaches a terminal approved state.
    /// </summary>
    Task NotifyInstanceCompletedAsync(
        ProcessInstance instance,
        CancellationToken ct = default);

    /// <summary>
    /// Called after an instance is withdrawn by the initiator.
    /// </summary>
    Task NotifyWithdrawnAsync(
        ProcessInstance instance,
        string actorITCode,
        CancellationToken ct = default);

    /// <summary>
    /// Called after an approver returns a task to the initiator (回退).
    /// </summary>
    Task NotifyReturnedToInitiatorAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        ApprovalTask task,
        string actorITCode,
        string? reason,
        CancellationToken ct = default);

    // ── WF-20.3: Timeout-wave notifier DIMs (default interface methods) ──────

    /// <summary>
    /// Called post-commit when a 催办 (Remind) timer fires and notifies current
    /// Pending assignees.  Re-gated: only called when the node is still Activated
    /// with the same generation.
    ///
    /// <para>Card content: process/instance/node identifiers and RemindCount only.
    /// NEVER form data or PII.</para>
    ///
    /// <para>Default implementation: no-op (<see cref="Task.CompletedTask"/>).
    /// Override in a concrete notifier to deliver actual notifications.</para>
    /// </summary>
    virtual Task NotifyTimeoutRemindAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        int remindCount,
        CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Called post-commit when a timeout Escalate timer fires and reassigns a task
    /// to the escalation target.
    ///
    /// <para>Card content: process/instance/node identifiers, old and new assignee
    /// ITCodes.  NEVER form data or PII.</para>
    ///
    /// <para>Default implementation: no-op (<see cref="Task.CompletedTask"/>).
    /// Override in a concrete notifier to deliver actual notifications.
    /// Call site implemented in WF-20.5.</para>
    /// </summary>
    virtual Task NotifyTimeoutEscalatedAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        string oldAssigneeITCode,
        string newAssigneeITCode,
        CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Called post-commit when a timeout AutoApprove or AutoReject fires and
    /// acts on a task on behalf of an assignee.
    ///
    /// <para>Card content: process/instance/task identifiers, outcome (AutoApproved
    /// or AutoRejected), and assignee ITCode.  NEVER form data or PII.</para>
    ///
    /// <para>Default implementation: no-op (<see cref="Task.CompletedTask"/>).
    /// Override in a concrete notifier to deliver actual notifications.
    /// Call site implemented in WF-20.4.</para>
    /// </summary>
    virtual Task NotifyTimeoutAutoActionedAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        ApprovalTask task,
        string outcome,
        CancellationToken ct = default) => Task.CompletedTask;
}
