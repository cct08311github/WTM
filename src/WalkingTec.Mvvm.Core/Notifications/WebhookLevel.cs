#nullable enable
namespace WalkingTec.Mvvm.Core.Notifications;

/// <summary>
/// Severity/level of a <see cref="WebhookMessage"/>.
/// Maps to colour-coded card elements in provider-specific payloads
/// (e.g. DingTalk/WeCom danger colour, Slack colour attachment, Teams card colour).
/// </summary>
public enum WebhookLevel
{
    /// <summary>Informational notice — green / blue card accent.</summary>
    Info,

    /// <summary>Action recommended — yellow / orange card accent.</summary>
    Warning,

    /// <summary>Failure or anomaly requiring attention — red card accent.</summary>
    Error
}
