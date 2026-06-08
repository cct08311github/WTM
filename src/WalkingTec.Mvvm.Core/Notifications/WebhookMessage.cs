#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.Notifications;

/// <summary>
/// A structured notification message sent through <see cref="IWtmWebhookSink"/>.
/// Carries a title, a markdown/plain body, a severity level, and an optional set of
/// key/value fields rendered as a card table by supported providers.
/// </summary>
public sealed class WebhookMessage
{
    /// <summary>
    /// Short, one-line title shown in the card heading.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Body text. Markdown is supported; providers that don't render Markdown
    /// will fall back to plain text automatically.
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Severity level. Controls card accent colour in provider payloads.
    /// Default: <see cref="WebhookLevel.Info"/>.
    /// </summary>
    public WebhookLevel Level { get; set; } = WebhookLevel.Info;

    /// <summary>
    /// Optional key/value pairs rendered as a table/card section (e.g. "Affected rows: 5000").
    /// Providers that don't support structured fields append them as extra Markdown lines.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; set; }
        = System.Array.Empty<KeyValuePair<string, string>>();
}
