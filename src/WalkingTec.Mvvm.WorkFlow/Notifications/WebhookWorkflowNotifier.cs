#nullable enable
// Security fix (#296): escape all graph/user-authored strings before they reach card bodies.
// EscapeMarkdown() neutralises [ ] ( ) * _ ~ ` # < > | and strips CR/LF → space so that
// a hostile nodeKey such as `[click](http://evil)` cannot render a phishing link in any
// supported webhook sink (DingTalk / WeCom / Feishu / Slack / Teams all parse Markdown).
// Framework-constant strings (Title, Chinese UI labels) are NOT escaped.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
///
/// <para><strong>Security:</strong> every graph/user-authored string interpolated into
/// card body text is passed through <see cref="EscapeMarkdown"/> before it reaches the
/// sink, preventing injection of Markdown links or formatting from hostile nodeKey values.
/// See issue #296.</para>
/// </summary>
public sealed class WebhookWorkflowNotifier : IWorkflowNotifier
{
    private readonly IWtmWebhookSink? _sink;
    private readonly ILogger<WebhookWorkflowNotifier> _logger;

    // Matches Markdown special characters that can be used to construct links or
    // formatting in DingTalk/WeCom/Feishu/Slack/Teams card bodies.
    // Angle brackets are matched individually (< and >) for HTML-tag neutralisation.
    private static readonly Regex MarkdownSpecialChars =
        new(@"[\[\]()\\*_~`#<>|]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // CR/LF stripper — collapse to a single space to prevent line-break injection.
    private static readonly Regex NewlineChars =
        new(@"[\r\n]+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

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

        var nodeKey  = EscapeMarkdown(nodeInstance.NodeKey);
        var assignee = EscapeMarkdown(task.AssigneeITCode);

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 審批任務待辦",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeKey}` 已指派審批任務給 `{assignee}`。",
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

        var nodeKey = EscapeMarkdown(nodeInstance.NodeKey);
        var actor   = EscapeMarkdown(actorITCode);

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 審批通過",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeKey}` 已由 `{actor}` 審批通過。",
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

        var nodeKey       = EscapeMarkdown(nodeInstance.NodeKey);
        var actor         = EscapeMarkdown(actorITCode);

        var fields = BuildInstanceFields(instance, nodeInstance, extraKey: "Actor", extraVal: actorITCode);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            // reason goes into Fields value (plain text, not Markdown body) — escape for safety.
            var list = new List<KeyValuePair<string, string>>(fields) { new("Reason", EscapeMarkdown(reason)) };
            fields = list;
        }

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 審批拒絕",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeKey}` 已由 `{actor}` 拒絕。",
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
                new("Initiator",     EscapeMarkdown(instance.InitiatorITCode)),
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

        var actor = EscapeMarkdown(actorITCode);

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 流程已撤回",
            Body  = $"流程 **{instance.ID}** 已由發起人 `{actor}` 撤回。",
            Level = WebhookLevel.Warning,
            Fields = new List<KeyValuePair<string, string>>
            {
                new("InstanceId",    instance.ID.ToString()),
                new("Initiator",     EscapeMarkdown(instance.InitiatorITCode)),
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

        var nodeKey = EscapeMarkdown(nodeInstance.NodeKey);
        var actor   = EscapeMarkdown(actorITCode);

        var fields = BuildInstanceFields(instance, nodeInstance, extraKey: "ReturnedBy", extraVal: actorITCode);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var list = new List<KeyValuePair<string, string>>(fields) { new("Reason", EscapeMarkdown(reason)) };
            fields = list;
        }

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 退回發起人",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeKey}` 由 `{actor}` 退回發起人，需補充資料後重新提交。",
            Level = WebhookLevel.Warning,
            Fields = fields,
        };

        return SendSafeAsync(message, "NotifyReturnedToInitiator", instance.ID, ct);
    }

    // ── WF-20.3: Timeout-wave notifier overrides ─────────────────────────────

    /// <inheritdoc/>
    public Task NotifyTimeoutRemindAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        int remindCount,
        CancellationToken ct = default)
    {
        if (_sink is null) return Task.CompletedTask;

        var nodeKey = EscapeMarkdown(nodeInstance.NodeKey);

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 超時催辦提醒",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeKey}` 催辦提醒（第 {remindCount + 1} 次）。",
            Level = WebhookLevel.Warning,
            Fields = new List<KeyValuePair<string, string>>
            {
                new("InstanceId",    instance.ID.ToString()),
                new("Initiator",     EscapeMarkdown(instance.InitiatorITCode)),
                new("NodeKey",       nodeInstance.NodeKey),
                new("ApproveMode",   (nodeInstance.ApproveMode ?? ApproveMode.Sequential).ToString()),
                new("BusinessType",  instance.BusinessType ?? "—"),
                new("BusinessKey",   instance.BusinessKey  ?? "—"),
                new("RemindCount",   (remindCount + 1).ToString()),
            },
        };

        return SendSafeAsync(message, "NotifyTimeoutRemind", instance.ID, ct);
    }

    /// <inheritdoc/>
    public Task NotifyTimeoutEscalatedAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        string oldAssigneeITCode,
        string newAssigneeITCode,
        CancellationToken ct = default)
    {
        if (_sink is null) return Task.CompletedTask;

        var nodeKey      = EscapeMarkdown(nodeInstance.NodeKey);
        var oldAssignee  = EscapeMarkdown(oldAssigneeITCode);
        var newAssignee  = EscapeMarkdown(newAssigneeITCode);

        var message = new WebhookMessage
        {
            Title = "[WorkFlow] 超時升級 — 任務已轉移",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeKey}` 因超時已將任務從 `{oldAssignee}` 升級轉移至 `{newAssignee}`。",
            Level = WebhookLevel.Warning,
            Fields = new List<KeyValuePair<string, string>>
            {
                new("InstanceId",       instance.ID.ToString()),
                new("Initiator",        EscapeMarkdown(instance.InitiatorITCode)),
                new("NodeKey",          nodeInstance.NodeKey),
                new("ApproveMode",      (nodeInstance.ApproveMode ?? ApproveMode.Sequential).ToString()),
                new("BusinessType",     instance.BusinessType ?? "—"),
                new("BusinessKey",      instance.BusinessKey  ?? "—"),
                new("OldAssignee",      oldAssigneeITCode),
                new("NewAssignee",      newAssigneeITCode),
            },
        };

        return SendSafeAsync(message, "NotifyTimeoutEscalated", instance.ID, ct);
    }

    /// <inheritdoc/>
    public Task NotifyTimeoutAutoActionedAsync(
        ProcessInstance instance,
        NodeInstance nodeInstance,
        ApprovalTask task,
        string outcome,
        CancellationToken ct = default)
    {
        if (_sink is null) return Task.CompletedTask;

        var nodeKey  = EscapeMarkdown(nodeInstance.NodeKey);
        var assignee = EscapeMarkdown(task.AssigneeITCode);
        // outcome is a framework-controlled enum string (AutoApproved/AutoRejected) — not graph-authored.
        var outcomeLabel = outcome == "AutoApproved" ? "通過" : "拒絕";

        var message = new WebhookMessage
        {
            Title = $"[WorkFlow] 超時自動{outcomeLabel}",
            Body  = $"流程 **{instance.ID}** 節點 `{nodeKey}` 因超時已代 `{assignee}` 執行自動{outcomeLabel}。",
            Level = WebhookLevel.Warning,
            Fields = new List<KeyValuePair<string, string>>
            {
                new("InstanceId",    instance.ID.ToString()),
                new("Initiator",     EscapeMarkdown(instance.InitiatorITCode)),
                new("NodeKey",       nodeInstance.NodeKey),
                new("ApproveMode",   (nodeInstance.ApproveMode ?? ApproveMode.Sequential).ToString()),
                new("BusinessType",  instance.BusinessType ?? "—"),
                new("BusinessKey",   instance.BusinessKey  ?? "—"),
                new("Assignee",      task.AssigneeITCode),
                new("Outcome",       outcome),
            },
        };

        return SendSafeAsync(message, "NotifyTimeoutAutoActioned", instance.ID, ct);
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
            new("Initiator",     EscapeMarkdown(instance.InitiatorITCode)),
            new("NodeKey",       nodeInstance.NodeKey),
            new("ApproveMode",   (nodeInstance.ApproveMode ?? ApproveMode.Sequential).ToString()),
            new("BusinessType",  instance.BusinessType ?? "—"),
            new("BusinessKey",   instance.BusinessKey  ?? "—"),
        };

        if (extraKey is not null && extraVal is not null)
            fields.Add(new(extraKey, extraVal));

        return fields;
    }

    /// <summary>
    /// Neutralise Markdown special characters in a graph/user-authored string so that
    /// the value cannot render as a link, bold, italic, code-span, heading, or HTML tag
    /// in any supported webhook sink card renderer.
    ///
    /// <para>Escaped characters: <c>[ ] ( ) \ * _ ~ ` # &lt; &gt; |</c>
    /// CR/LF sequences are collapsed to a single space to prevent line-break injection.</para>
    ///
    /// <para>CJK characters, digits, and Latin letters are left intact so that CJK
    /// node keys (e.g. <c>審批節點一</c>) remain readable.</para>
    ///
    /// <para>Call this helper on EVERY graph/user-authored string before it appears
    /// in a card body interpolation.  Framework-constant strings (Title field,
    /// Chinese UI labels baked into source code) do NOT need escaping.</para>
    /// </summary>
    internal static string EscapeMarkdown(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        // Collapse CR/LF to space first.
        var s = NewlineChars.Replace(value, " ");
        // Backslash-escape each Markdown special character.
        return MarkdownSpecialChars.Replace(s, m => @"\" + m.Value);
    }
}
