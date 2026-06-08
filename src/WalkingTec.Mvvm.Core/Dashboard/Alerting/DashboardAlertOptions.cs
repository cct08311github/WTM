#nullable enable
namespace WalkingTec.Mvvm.Core.Dashboard.Alerting;

/// <summary>
/// Configuration for the KPI threshold alerting subsystem.
/// All settings are opt-in; the default state leaves the hosted service dormant.
/// </summary>
public sealed class DashboardAlertOptions
{
    /// <summary>
    /// How often (in seconds) the hosted service evaluates thresholds across all dashboards.
    /// Set to <c>0</c> (default) to disable threshold evaluation entirely.
    /// </summary>
    public int EvaluationIntervalSeconds { get; set; } = 0;

    /// <summary>
    /// Minimum number of seconds that must elapse before the same rule breach fires again.
    /// This is a fallback cooldown on top of the primary alert-on-transition de-duplication.
    /// Set to <c>0</c> to rely solely on alert-on-transition (fire once per breach episode).
    /// Default: 3600 (1 hour).
    /// </summary>
    public int AlertCooldownSeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum number of seconds each widget data fetch may run during threshold evaluation.
    /// Defaults to the same value as <see cref="DashboardOptions.WidgetDataTimeoutSeconds"/> (30 s)
    /// when left at zero. Set explicitly to override.
    /// </summary>
    public int EvaluationWidgetTimeoutSeconds { get; set; } = 30;
}
