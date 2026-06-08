#nullable enable
namespace WalkingTec.Mvvm.Core.Notifications;

/// <summary>
/// Identifies the webhook provider whose JSON card format is used by a sink.
/// </summary>
public enum WebhookProviderKind
{
    /// <summary>钉钉 (DingTalk) — markdown msgtype with optional HMAC-SHA256 signing.</summary>
    DingTalk,

    /// <summary>企业微信 (WeCom / WeChat Work) — markdown msgtype.</summary>
    WeCom,

    /// <summary>飞书 (Feishu / Lark) — interactive card (post) or text.</summary>
    Feishu,

    /// <summary>Slack — Block Kit layout (section + context blocks, colour attachment).</summary>
    Slack,

    /// <summary>Microsoft Teams — Adaptive Card (via Incoming Webhook).</summary>
    MicrosoftTeams
}
