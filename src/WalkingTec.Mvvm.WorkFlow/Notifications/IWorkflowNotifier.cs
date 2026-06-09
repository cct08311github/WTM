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
}
