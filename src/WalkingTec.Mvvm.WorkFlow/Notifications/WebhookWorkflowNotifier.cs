#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Notifications;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Notifications;

/// <summary>
/// Default <see cref="IWorkflowNotifier"/> implementation that dispatches structured
/// <see cref="WebhookMessage"/> cards through the registered <see cref="IWtmWebhookSink"/>
/// (DingTalk / WeCom / Feishu / Slack / Teams).
///
/// <para><strong>Opt-in:</strong> registered only when
/// <see cref="ServiceCollectionExtensions.AddWtmWorkFlowNotifications"/> is called.
/// The <see cref="IWtmWebhookSink"/> constructor parameter is optional — when no sink is
/// registered in DI, this notifier is a silent no-op on every event.  This mirrors the
/// <c>EtlAlertService</c> pattern exactly.</para>
///
/// <para><strong>Best-effort / non-blocking:</strong> every <c>SendAsync</c> call is
/// wrapped in a try/catch.  A webhook delivery failure is logged at <c>Error</c> level
/// but never re-thrown.  Notifications are always called AFTER the engine transaction
/// commits — a delivery failure can never roll back an approval decision.</para>
///
/// <para><strong>No PII / form data in cards:</strong> cards carry only process/instance/
/// task identifiers, node key, actor ITCode, and decision outcome — never form field values.</para>
/// </summary>
public sealed class WebhookWorkflowNotifier : IWorkflowNotifier
{
    private readonly IWtmWebhookSink? _sink;
    private readonly ILogger<WebhookWorkflowNotifier> _logger;

    /// <summary>
    /// Production constructor.  <paramref name="sink"/> is optional — DI resolves it as
    /// <c>null</c> when no <see cref="IWtmWebhookSink"/> has been registered.
    /// </summary>
    public WebhookWorkflowNotifier(
        ILogger<WebhookWorkflowNotifier> logger,
        IWtmWebhookSink? sink = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sink = sink;
    }

    /// <inheritdoc/>
    public Task NotifyTaskAssignedAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        ApprovalTask task,
        CancellationToken ct = default)
    {
        if (_sink is null) return Task.CompletedTask;

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 審批任務待辦",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeInstance.NodeKey}` 已指派審批任務給 `{task.AssigneeITCode}`。",
            Level = WebhookLevel.Info,
            Fields = BuildInstanceFields(instance, nodeInstance, extraKey: "Assignee", extraVal: task.AssigneeITCode),
        };

        return SendSafeAsync(message, "NotifyTaskAssigned", instance.ID, ct);
    }

    /// <inheritdoc/>
    public Task NotifyApprovedAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        ApprovalTask task,
        string actorITCode,
        CancellationToken ct = default)
    {
        if (_sink is null) return Task.CompletedTask;

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 審批通過",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeInstance.NodeKey}` 已由 `{actorITCode}` 審批通過。",
            Level = WebhookLevel.Info,
            Fields = BuildInstanceFields(instance, nodeInstance, extraKey: "Actor", extraVal: actorITCode),
        };

        return SendSafeAsync(message, "NotifyApproved", instance.ID, ct);
    }

    /// <inheritdoc/>
    public Task NotifyRejectedAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        ApprovalTask task,
        string actorITCode,
        string? reason,
        CancellationToken ct = default)
    {
        if (_sink is null) return Task.CompletedTask;

        var fields = BuildInstanceFields(instance, nodeInstance, extraKey: "Actor", extraVal: actorITCode);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var list = new List<KeyValuePair<string, string>>(fields) { new("Reason", reason) };
            fields = list;
        }

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 審批拒絕",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeInstance.NodeKey}` 已由 `{actorITCode}` 拒絕。",
            Level = WebhookLevel.Warning,
            Fields = fields,
        };

        return SendSafeAsync(message, "NotifyRejected", instance.ID, ct);
    }

    /// <inheritdoc/>
    public Task NotifyInstanceCompletedAsync(
        ProcessInstance instance,
        CancellationToken ct = default)
    {
        if (_sink is null) return Task.CompletedTask;

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 流程完成",
            Body  = $"流程 **{instance.ID}** 已全部審批通過，進入完成狀態。",
            Level = WebhookLevel.Info,
            Fields = new List<KeyValuePair<string, string>>
            {
                new("InstanceId",    instance.ID.ToString()),
                new("Initiator",     instance.InitiatorITCode),
                new("BusinessType",  instance.BusinessType ?? "—"),
                new("BusinessKey",   instance.BusinessKey  ?? "—"),
                new("FinalState",    instance.State.ToString()),
            },
        };

        return SendSafeAsync(message, "NotifyInstanceCompleted", instance.ID, ct);
    }

    /// <inheritdoc/>
    public Task NotifyWithdrawnAsync(
        ProcessInstance instance,
        string actorITCode,
        CancellationToken ct = default)
    {
        if (_sink is null) return Task.CompletedTask;

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 流程已撤回",
            Body  = $"流程 **{instance.ID}** 已由發起人 `{actorITCode}` 撤回。",
            Level = WebhookLevel.Warning,
            Fields = new List<KeyValuePair<string, string>>
            {
                new("InstanceId",    instance.ID.ToString()),
                new("Initiator",     instance.InitiatorITCode),
                new("WithdrawnBy",   actorITCode),
                new("BusinessType",  instance.BusinessType ?? "—"),
                new("BusinessKey",   instance.BusinessKey  ?? "—"),
            },
        };

        return SendSafeAsync(message, "NotifyWithdrawn", instance.ID, ct);
    }

    /// <inheritdoc/>
    public Task NotifyReturnedToInitiatorAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        ApprovalTask task,
        string actorITCode,
        string? reason,
        CancellationToken ct = default)
    {
        if (_sink is null) return Task.CompletedTask;

        var fields = BuildInstanceFields(instance, nodeInstance, extraKey: "ReturnedBy", extraVal: actorITCode);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var list = new List<KeyValuePair<string, string>>(fields) { new("Reason", reason) };
            fields = list;
        }

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 退回發起人",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeInstance.NodeKey}` 由 `{actorITCode}` 退回發起人，需補充資料後重新提交。",
            Level = WebhookLevel.Warning,
            Fields = fields,
        };

        return SendSafeAsync(message, "NotifyReturnedToInitiator", instance.ID, ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Send <paramref name="message"/> via the sink; swallow any exception and log at Error.
    /// This guarantees a webhook failure can never propagate to the engine caller.
    /// </summary>
    private async Task SendSafeAsync(
        WebhookMessage message,
        string operationName,
        Guid instanceId,
        CancellationToken ct)
    {
        // _sink guaranteed non-null by callers, but satisfy the compiler.
        if (_sink is null) return;

        try
        {
            await _sink.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "WebhookWorkflowNotifier.{Operation}: failed to deliver webhook card for instance {InstanceId}.",
                operationName, instanceId);
        }
    }

    /// <summary>
    /// Build a standard set of card fields from the instance and node context,
    /// optionally appending one extra key/value pair.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> BuildInstanceFields(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        string? extraKey = null,
        string? extraVal = null)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("InstanceId",    instance.ID.ToString()),
            new("Initiator",     instance.InitiatorITCode),
            new("NodeKey",       nodeInstance.NodeKey),
            new("ApproveMode",   (nodeInstance.ApproveMode ?? ApproveMode.Sequential).ToString()),
            new("BusinessType",  instance.BusinessType ?? "—"),
            new("BusinessKey",   instance.BusinessKey  ?? "—"),
        };

        if (extraKey is not null && extraVal is not null)
            fields.Add(new(extraKey, extraVal));

        return fields;
    }
}
