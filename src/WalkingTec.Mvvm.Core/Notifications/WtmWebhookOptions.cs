#nullable enable
using System;
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.Notifications;

/// <summary>
/// Configuration for a single webhook sink.
/// Bind via <c>services.Configure&lt;WtmWebhookOptions&gt;(...)</c> or pass an
/// <see cref="Action{WtmWebhookOptions}"/> to
/// <see cref="WebhookServiceCollectionExtensions.AddWtmWebhookSink"/>.
/// </summary>
public sealed class WtmWebhookOptions
{
    /// <summary>
    /// Provider kind — determines which JSON card format is used.
    /// </summary>
    public WebhookProviderKind Provider { get; set; } = WebhookProviderKind.DingTalk;

    /// <summary>
    /// The full webhook URL supplied by the provider console.
    /// Must use <c>https://</c> (enforced at registration time).
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret used for signed webhooks.
    /// Required by DingTalk (HMAC-SHA256) and Feishu (optional but recommended).
    /// WeCom, Slack, and Teams do not support server-side signing at the URL level
    /// and leave this empty.
    /// </summary>
    /// <remarks>
    /// This value is never logged.
    /// </remarks>
    public string? Secret { get; set; }

    /// <summary>
    /// HTTP request timeout in seconds.  Clamped to [1, 30].  Default: 10 s.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of retry attempts on transient failures (5xx / network errors).
    /// Clamped to [0, 3].  Default: 2.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Optional list of hostname suffixes that are allowed as the webhook target.
    /// When non-empty, the webhook URL host must end with one of the listed suffixes.
    /// Example: <c>["oapi.dingtalk.com", "qyapi.weixin.qq.com"]</c>.
    /// When empty (default) every <c>https://</c> host is accepted.
    /// </summary>
    public IReadOnlyList<string> AllowedHostSuffixes { get; set; }
        = Array.Empty<string>();
}
