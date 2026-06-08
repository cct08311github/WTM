#nullable enable
namespace WalkingTec.Mvvm.Core.Dashboard.Alerting;

/// <summary>
/// Severity of a KPI threshold breach, forwarded to the webhook sink level.
/// </summary>
public enum ThresholdAlertLevel
{
    /// <summary>Advisory — maps to <see cref="Notifications.WebhookLevel.Warning"/>.</summary>
    Warning,
    /// <summary>Critical — maps to <see cref="Notifications.WebhookLevel.Error"/>.</summary>
    Error
}
