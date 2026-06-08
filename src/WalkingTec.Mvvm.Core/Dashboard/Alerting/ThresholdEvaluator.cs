#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace WalkingTec.Mvvm.Core.Dashboard.Alerting;

/// <summary>
/// Pure, static helper that extracts a named measure value from a <see cref="WidgetDataResult"/>
/// and evaluates whether a <see cref="WidgetThreshold"/> is breached.
/// </summary>
internal static class ThresholdEvaluator
{
    /// <summary>
    /// Attempts to resolve the measure named in <paramref name="threshold"/> from
    /// <paramref name="result"/> and compares it to the threshold value using the
    /// configured operator.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the measure is breached; <c>false</c> when not breached or when
    /// the measure cannot be resolved or parsed as a number.
    /// </returns>
    internal static bool IsBreached(WidgetDataResult result, WidgetThreshold threshold)
    {
        if (string.IsNullOrWhiteSpace(threshold.Measure))
            return false;

        var raw = ExtractRaw(result, threshold.Measure);
        if (raw == null)
            return false;

        if (!TryParseDouble(raw, out var actual))
            return false;

        return Compare(actual, threshold.Op, threshold.Value);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolution order:
    /// 1. <see cref="WidgetDataResult.Value"/> when it is a scalar or when the measure
    ///    name is "value" (case-insensitive).
    /// 2. <see cref="WidgetDataResult.Metadata"/> keyed by <paramref name="measure"/>.
    /// 3. First row of <see cref="WidgetDataResult.Rows"/> keyed by <paramref name="measure"/>.
    /// </summary>
    private static object? ExtractRaw(WidgetDataResult result, string measure)
    {
        // 1. Scalar value (or explicit "value" field name)
        if (result.Value != null &&
            (string.Equals(measure, "value", StringComparison.OrdinalIgnoreCase) ||
             result.Rows == null || result.Rows.Count == 0))
        {
            return result.Value;
        }

        // 2. Metadata dictionary
        if (result.Metadata != null &&
            result.Metadata.TryGetValue(measure, out var meta))
        {
            return meta;
        }

        // 3. First row
        if (result.Rows != null && result.Rows.Count > 0 &&
            result.Rows[0].TryGetValue(measure, out var rowVal))
        {
            return rowVal;
        }

        return null;
    }

    private static bool TryParseDouble(object? raw, out double value)
    {
        value = 0;
        if (raw == null) return false;

        // JsonElement (common from System.Text.Json deserialization)
        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number)
            {
                value = je.GetDouble();
                return true;
            }
            if (je.ValueKind == JsonValueKind.String)
                return double.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            return false;
        }

        // Numeric primitives
        if (raw is double d) { value = d; return true; }
        if (raw is float f) { value = f; return true; }
        if (raw is decimal dec) { value = (double)dec; return true; }
        if (raw is int i) { value = i; return true; }
        if (raw is long l) { value = l; return true; }
        if (raw is short s) { value = s; return true; }
        if (raw is byte b) { value = b; return true; }

        // Fallback: string parse
        return double.TryParse(raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static bool Compare(double actual, ThresholdComparisonOp op, double threshold) =>
        op switch
        {
            ThresholdComparisonOp.Gt => actual > threshold,
            ThresholdComparisonOp.Ge => actual >= threshold,
            ThresholdComparisonOp.Lt => actual < threshold,
            ThresholdComparisonOp.Le => actual <= threshold,
            ThresholdComparisonOp.Eq => Math.Abs(actual - threshold) < 1e-10,
            _ => false
        };
}
