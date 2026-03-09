#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    [TestClass]
    public class AnalysisPivotEngineTests
    {
        // ─── 基本 Pivot ───────────────────────────────────────────────────────

        [TestMethod]
        public void Pivot_two_dims_one_measure()
        {
            // Region × Category → Pivot on Category
            var groupBy = MakeGroupByResult(
                new[] { "Region", "Category", "Amount_Sum" },
                new object?[][]
                {
                    new object?[] { "華東", "家電", 100m },
                    new object?[] { "華東", "3C", 200m },
                    new object?[] { "華南", "家電", 300m },
                });

            var result = AnalysisPivotEngine.Pivot(
                groupBy,
                pivotDimension: "Category",
                allDimensions: new List<string> { "Region", "Category" },
                measureNames: new List<string> { "Amount_Sum" });

            Assert.AreEqual(1, result.RowDimensions.Count);
            Assert.AreEqual("Region", result.RowDimensions[0]);
            CollectionAssert.AreEqual(new[] { "3C", "家電" }, result.PivotValues); // sorted
            Assert.AreEqual(2, result.Rows.Count);

            var huaDong = result.Rows.Single(r => r["Region"]?.ToString() == "華東");
            Assert.AreEqual(200m, huaDong["3C_Amount_Sum"]);
            Assert.AreEqual(100m, huaDong["家電_Amount_Sum"]);

            var huaNan = result.Rows.Single(r => r["Region"]?.ToString() == "華南");
            Assert.AreEqual(0m, huaNan["3C_Amount_Sum"]); // missing → 0
            Assert.AreEqual(300m, huaNan["家電_Amount_Sum"]);
        }

        [TestMethod]
        public void Pivot_multiple_measures()
        {
            var groupBy = MakeGroupByResult(
                new[] { "Region", "Category", "Amount_Sum", "Amount_Count" },
                new object?[][]
                {
                    new object?[] { "華東", "家電", 100m, 5m },
                    new object?[] { "華東", "3C", 200m, 3m },
                });

            var result = AnalysisPivotEngine.Pivot(
                groupBy,
                pivotDimension: "Category",
                allDimensions: new List<string> { "Region", "Category" },
                measureNames: new List<string> { "Amount_Sum", "Amount_Count" });

            var row = result.Rows.Single();
            Assert.AreEqual(100m, row["家電_Amount_Sum"]);
            Assert.AreEqual(5m, row["家電_Amount_Count"]);
            Assert.AreEqual(200m, row["3C_Amount_Sum"]);
            Assert.AreEqual(3m, row["3C_Amount_Count"]);
            // Columns: Region, 3C_Amount_Sum, 3C_Amount_Count, 家電_Amount_Sum, 家電_Amount_Count
            Assert.AreEqual(5, result.Columns.Count);
        }

        // ─── 驗證 ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Pivot_empty_pivotDimension_throws()
        {
            var groupBy = MakeGroupByResult(new[] { "Region" }, Array.Empty<object?[]>());
            Assert.ThrowsException<InvalidOperationException>(() =>
                AnalysisPivotEngine.Pivot(groupBy, "", new List<string> { "Region" }, new List<string> { "Amount_Sum" }));
        }

        [TestMethod]
        public void Pivot_dimension_not_in_list_throws()
        {
            var groupBy = MakeGroupByResult(new[] { "Region" }, Array.Empty<object?[]>());
            Assert.ThrowsException<InvalidOperationException>(() =>
                AnalysisPivotEngine.Pivot(groupBy, "NotADim", new List<string> { "Region" }, new List<string> { "Amount_Sum" }));
        }

        [TestMethod]
        public void Pivot_exceeds_max_values_throws()
        {
            // Create 51 unique pivot values → should throw
            var rows = Enumerable.Range(1, 51).Select(i => new object?[] { "R1", $"Cat{i}", (decimal)i }).ToArray();
            var groupBy = MakeGroupByResult(new[] { "Region", "Category", "Amount_Sum" }, rows);

            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                AnalysisPivotEngine.Pivot(groupBy, "Category",
                    new List<string> { "Region", "Category" },
                    new List<string> { "Amount_Sum" }));
            Assert.IsTrue(ex.Message.Contains("51"));
        }

        // ─── 邊界情況 ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Pivot_empty_rows_returns_empty()
        {
            var groupBy = MakeGroupByResult(
                new[] { "Region", "Category", "Amount_Sum" },
                Array.Empty<object?[]>());

            var result = AnalysisPivotEngine.Pivot(groupBy, "Category",
                new List<string> { "Region", "Category" },
                new List<string> { "Amount_Sum" });

            Assert.AreEqual(0, result.Rows.Count);
            Assert.AreEqual(0, result.PivotValues.Count);
        }

        [TestMethod]
        public void Pivot_single_dimension_as_pivot()
        {
            // Only 1 dimension, pivot on it → rowDims is empty, all data in columns
            var groupBy = MakeGroupByResult(
                new[] { "Category", "Amount_Sum" },
                new object?[][]
                {
                    new object?[] { "家電", 100m },
                    new object?[] { "3C", 200m },
                });

            var result = AnalysisPivotEngine.Pivot(groupBy, "Category",
                new List<string> { "Category" },
                new List<string> { "Amount_Sum" });

            Assert.AreEqual(0, result.RowDimensions.Count);
            Assert.AreEqual(1, result.Rows.Count); // single row with all pivoted values
            Assert.AreEqual(100m, result.Rows[0]["家電_Amount_Sum"]);
            Assert.AreEqual(200m, result.Rows[0]["3C_Amount_Sum"]);
        }

        [TestMethod]
        public void Pivot_null_values_treated_as_empty_string()
        {
            var groupBy = new AnalysisQueryResponse
            {
                Columns = new List<string> { "Region", "Category", "Amount_Sum" },
                Rows = new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "華東", ["Category"] = null, ["Amount_Sum"] = 100m },
                    new() { ["Region"] = "華東", ["Category"] = "3C", ["Amount_Sum"] = 200m },
                }
            };

            var result = AnalysisPivotEngine.Pivot(groupBy, "Category",
                new List<string> { "Region", "Category" },
                new List<string> { "Amount_Sum" });

            Assert.IsTrue(result.PivotValues.Contains(""));
            Assert.IsTrue(result.PivotValues.Contains("3C"));
        }

        [TestMethod]
        public void Pivot_columns_are_ordered_correctly()
        {
            var groupBy = MakeGroupByResult(
                new[] { "Region", "Category", "Amount_Sum" },
                new object?[][]
                {
                    new object?[] { "華東", "B", 100m },
                    new object?[] { "華東", "A", 200m },
                });

            var result = AnalysisPivotEngine.Pivot(groupBy, "Category",
                new List<string> { "Region", "Category" },
                new List<string> { "Amount_Sum" });

            // Columns: Region (row dim), then A_Amount_Sum, B_Amount_Sum (pivot values sorted)
            Assert.AreEqual("Region", result.Columns[0]);
            Assert.AreEqual("A_Amount_Sum", result.Columns[1]);
            Assert.AreEqual("B_Amount_Sum", result.Columns[2]);
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static AnalysisQueryResponse MakeGroupByResult(string[] columns, object?[][] rows)
        {
            var result = new AnalysisQueryResponse
            {
                Columns = columns.ToList(),
                Rows = new List<Dictionary<string, object?>>()
            };

            foreach (var row in rows)
            {
                var dict = new Dictionary<string, object?>();
                for (int i = 0; i < columns.Length; i++)
                {
                    dict[columns[i]] = row[i];
                }
                result.Rows.Add(dict);
            }

            return result;
        }
    }
}
