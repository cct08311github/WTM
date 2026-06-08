#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Core.Dashboard.Alerting;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    [TestClass]
    public class ThresholdEvaluatorTests
    {
        // ── Scalar Value resolution ────────────────────────────────────────────

        [TestMethod]
        public void Scalar_Value_GreaterThan_breaches()
        {
            var result = new WidgetDataResult { Value = 150.0 };
            var rule = MakeRule("value", ThresholdComparisonOp.Gt, 100, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeTrue();
        }

        [TestMethod]
        public void Scalar_Value_Equal_to_threshold_does_not_breach_Gt()
        {
            var result = new WidgetDataResult { Value = 100.0 };
            var rule = MakeRule("value", ThresholdComparisonOp.Gt, 100, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeFalse();
        }

        [TestMethod]
        public void Scalar_Value_Ge_equal_breaches()
        {
            var result = new WidgetDataResult { Value = 100.0 };
            var rule = MakeRule("value", ThresholdComparisonOp.Ge, 100, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeTrue();
        }

        [TestMethod]
        public void Scalar_Value_Lt_breaches_when_below()
        {
            var result = new WidgetDataResult { Value = 5.0 };
            var rule = MakeRule("value", ThresholdComparisonOp.Lt, 10, ThresholdAlertLevel.Error);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeTrue();
        }

        [TestMethod]
        public void Scalar_Value_Le_equal_breaches()
        {
            var result = new WidgetDataResult { Value = 10.0 };
            var rule = MakeRule("value", ThresholdComparisonOp.Le, 10, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeTrue();
        }

        [TestMethod]
        public void Scalar_Value_Eq_exact_match_breaches()
        {
            var result = new WidgetDataResult { Value = 42.0 };
            var rule = MakeRule("value", ThresholdComparisonOp.Eq, 42, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeTrue();
        }

        [TestMethod]
        public void Scalar_Value_Eq_no_match_does_not_breach()
        {
            var result = new WidgetDataResult { Value = 43.0 };
            var rule = MakeRule("value", ThresholdComparisonOp.Eq, 42, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeFalse();
        }

        // ── Metadata resolution ───────────────────────────────────────────────

        [TestMethod]
        public void Metadata_measure_resolution_breaches()
        {
            var result = new WidgetDataResult
            {
                Metadata = new Dictionary<string, object?> { ["errorRate"] = 0.15 }
            };
            var rule = MakeRule("errorRate", ThresholdComparisonOp.Gt, 0.1, ThresholdAlertLevel.Error);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeTrue();
        }

        [TestMethod]
        public void Metadata_measure_below_threshold_does_not_breach()
        {
            var result = new WidgetDataResult
            {
                Metadata = new Dictionary<string, object?> { ["errorRate"] = 0.05 }
            };
            var rule = MakeRule("errorRate", ThresholdComparisonOp.Gt, 0.1, ThresholdAlertLevel.Error);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeFalse();
        }

        // ── Row-based resolution ──────────────────────────────────────────────

        [TestMethod]
        public void Row_first_row_value_breaches()
        {
            var result = new WidgetDataResult
            {
                Rows = new List<Dictionary<string, object?>>
                {
                    new Dictionary<string, object?> { ["sales"] = 9500.0, ["region"] = "North" }
                }
            };
            var rule = MakeRule("sales", ThresholdComparisonOp.Gt, 9000, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeTrue();
        }

        [TestMethod]
        public void Row_missing_measure_column_does_not_breach()
        {
            var result = new WidgetDataResult
            {
                Rows = new List<Dictionary<string, object?>>
                {
                    new Dictionary<string, object?> { ["otherField"] = 99.0 }
                }
            };
            var rule = MakeRule("sales", ThresholdComparisonOp.Gt, 0, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeFalse();
        }

        // ── JsonElement values (as produced by System.Text.Json) ─────────────

        [TestMethod]
        public void JsonElement_numeric_value_breaches()
        {
            var je = JsonDocument.Parse("999").RootElement;
            var result = new WidgetDataResult
            {
                Metadata = new Dictionary<string, object?> { ["count"] = je }
            };
            var rule = MakeRule("count", ThresholdComparisonOp.Gt, 100, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeTrue();
        }

        [TestMethod]
        public void JsonElement_string_number_value_breaches()
        {
            var je = JsonDocument.Parse("\"42.5\"").RootElement;
            var result = new WidgetDataResult
            {
                Metadata = new Dictionary<string, object?> { ["ratio"] = je }
            };
            var rule = MakeRule("ratio", ThresholdComparisonOp.Gt, 40.0, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeTrue();
        }

        // ── Edge cases ────────────────────────────────────────────────────────

        [TestMethod]
        public void Null_Value_and_empty_rows_does_not_breach()
        {
            var result = new WidgetDataResult { Value = null, Rows = null, Metadata = null };
            var rule = MakeRule("value", ThresholdComparisonOp.Gt, 0, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeFalse();
        }

        [TestMethod]
        public void Empty_measure_name_does_not_breach()
        {
            var result = new WidgetDataResult { Value = 999.0 };
            var rule = new WidgetThreshold
            {
                RuleId = "r1",
                Measure = "",   // empty
                Op = ThresholdComparisonOp.Gt,
                Value = 0
            };
            ThresholdEvaluator.IsBreached(result, rule).Should().BeFalse();
        }

        [TestMethod]
        public void Integer_value_is_treated_as_numeric()
        {
            var result = new WidgetDataResult
            {
                Metadata = new Dictionary<string, object?> { ["count"] = 200 }
            };
            var rule = MakeRule("count", ThresholdComparisonOp.Gt, 100, ThresholdAlertLevel.Warning);
            ThresholdEvaluator.IsBreached(result, rule).Should().BeTrue();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static WidgetThreshold MakeRule(
            string measure,
            ThresholdComparisonOp op,
            double value,
            ThresholdAlertLevel level) =>
            new WidgetThreshold
            {
                RuleId = "test-rule",
                Measure = measure,
                Op = op,
                Value = value,
                Level = level
            };
    }

    // Make internal member accessible to test project.
    // ThresholdEvaluator.IsBreached is internal — expose via the InternalsVisibleTo already set on Core.
}
