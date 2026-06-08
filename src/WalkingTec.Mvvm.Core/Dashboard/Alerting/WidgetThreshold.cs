#nullable enable
namespace WalkingTec.Mvvm.Core.Dashboard.Alerting;

/// <summary>
/// Defines a single KPI threshold rule attached to a widget.
/// When the widget's data is fetched and the named <see cref="Measure"/> breaches the
/// <see cref="Op"/>/<see cref="Value"/> boundary, an alert is sent via the configured sink.
/// </summary>
/// <remarks>
/// Threshold rules are stored in <see cref="WidgetDefinition.Thresholds"/> and evaluated
/// at the interval configured in <see cref="DashboardAlertOptions.EvaluationIntervalSeconds"/>
/// by <see cref="DashboardAlertHostedService"/>.
/// </remarks>
public sealed class WidgetThreshold
{
    /// <summary>
    /// A short, unique identifier for this rule within the owning widget.
    /// Used for de-duplication (alert-on-transition): the service tracks which rule IDs are
    /// currently breached so repeated evaluations do not re-fire the same alert.
    /// Required; must not be empty.
    /// </summary>
    public string RuleId { get; set; } = "";

    /// <summary>
    /// The measure or field name to inspect from the widget data result.
    /// For scalar KPI widgets this matches the key in <see cref="WidgetDataResult.Metadata"/>
    /// or the property name of a single-row <see cref="WidgetDataResult.Value"/>.
    /// For row-based widgets it matches a column name in <see cref="WidgetDataResult.Rows"/>,
    /// and the threshold evaluates against the value of the <em>first</em> row.
    /// </summary>
    public string Measure { get; set; } = "";

    /// <summary>Comparison operator applied between the actual metric value and <see cref="Value"/>.</summary>
    public ThresholdComparisonOp Op { get; set; } = ThresholdComparisonOp.Gt;

    /// <summary>
    /// Threshold boundary value. The actual metric value is compared to this using <see cref="Op"/>.
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// Alert severity level. Determines the <see cref="Notifications.WebhookLevel"/> and
    /// message formatting sent to the sink.
    /// </summary>
    public ThresholdAlertLevel Level { get; set; } = ThresholdAlertLevel.Warning;

    /// <summary>
    /// Optional human-readable message included in the alert body.
    /// When null/empty, a default message is generated from the rule fields.
    /// </summary>
    public string? Message { get; set; }
}
