#nullable enable
namespace WalkingTec.Mvvm.Core.Dashboard.Alerting;

/// <summary>
/// Comparison operator used in a <see cref="WidgetThreshold"/> rule.
/// </summary>
public enum ThresholdComparisonOp
{
    /// <summary>Greater than (&gt;).</summary>
    Gt,
    /// <summary>Greater than or equal (≥).</summary>
    Ge,
    /// <summary>Less than (&lt;).</summary>
    Lt,
    /// <summary>Less than or equal (≤).</summary>
    Le,
    /// <summary>Equal (==).</summary>
    Eq
}
