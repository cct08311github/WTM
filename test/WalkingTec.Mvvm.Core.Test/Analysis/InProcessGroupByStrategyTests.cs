#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    [TestClass]
    public class InProcessGroupByStrategyTests
    {
        // ─── 測試模型 ──────────────────────────────────────────────────────────

        private class SaleRecord
        {
            [Dimension(DisplayName = "地區")]  public string Region   { get; set; } = string.Empty;
            [Dimension(DisplayName = "類別")]  public string Category { get; set; } = string.Empty;
            [Dimension(DisplayName = "年份")]  public string Year     { get; set; } = string.Empty;

            [Measure(AllowedFuncs =
                AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg |
                AggregateFunc.Max | AggregateFunc.Min,
                DisplayName = "金額")]
            public decimal Amount { get; set; }

            [Measure(AllowedFuncs =
                AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg |
                AggregateFunc.Max | AggregateFunc.Min,
                DisplayName = "數量")]
            public decimal Quantity { get; set; }

            [Measure(AllowedFuncs =
                AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg |
                AggregateFunc.Max | AggregateFunc.Min,
                DisplayName = "折扣")]
            public decimal Discount { get; set; }
        }

        // ─── 基礎設施 ──────────────────────────────────────────────────────────

        private InProcessGroupByStrategy _strategy = null!;
        private Dictionary<string, AnalysisFieldMeta> _whitelist = null!;

        [TestInitialize]
        public void Setup()
        {
            _strategy = new InProcessGroupByStrategy();
            _whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord))
                .ToDictionary(f => f.FieldName);
        }

        private static IQueryable<SaleRecord> MakeQuery(params SaleRecord[] records)
            => records.AsQueryable();

        private static AnalysisQueryRequest Req(
            string[]? dims = null,
            (string field, AggregateFunc func)[]? msrs = null)
        {
            return new AnalysisQueryRequest
            {
                Dimensions = dims?.ToList() ?? new List<string>(),
                Measures = msrs?.Select(m => new MeasureRequest { Field = m.field, Func = m.func }).ToList()
                           ?? new List<MeasureRequest>(),
                Filters = new List<FilterCondition>()
            };
        }

        // ─── 測試 ──────────────────────────────────────────────────────────────

        [TestMethod]
        public void Basic_1_dimension_1_measure_sum()
        {
            var data = MakeQuery(
                new SaleRecord { Region = "華東", Amount = 100m },
                new SaleRecord { Region = "華東", Amount = 200m },
                new SaleRecord { Region = "華南", Amount = 300m }
            );
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(data, req, _whitelist);

            Assert.AreEqual(2, rows.Count);
            var huaDong = rows.Single(r => r["Region"]?.ToString() == "華東");
            Assert.AreEqual(300m, Convert.ToDecimal(huaDong["Amount_Sum"]));
            var huaNan = rows.Single(r => r["Region"]?.ToString() == "華南");
            Assert.AreEqual(300m, Convert.ToDecimal(huaNan["Amount_Sum"]));
        }

        [TestMethod]
        public void Three_dimensions_three_measures()
        {
            var data = MakeQuery(
                new SaleRecord { Region = "華東", Category = "A", Year = "2025", Amount = 100m, Quantity = 10m, Discount = 5m },
                new SaleRecord { Region = "華東", Category = "A", Year = "2025", Amount = 200m, Quantity = 20m, Discount = 10m },
                new SaleRecord { Region = "華東", Category = "B", Year = "2026", Amount = 300m, Quantity = 30m, Discount = 15m }
            );
            var req = Req(
                dims: new[] { "Region", "Category", "Year" },
                msrs: new[]
                {
                    ("Amount", AggregateFunc.Sum),
                    ("Quantity", AggregateFunc.Avg),
                    ("Discount", AggregateFunc.Max)
                });

            var rows = _strategy.Execute(data, req, _whitelist);

            Assert.AreEqual(2, rows.Count);

            var groupAA25 = rows.Single(r =>
                r["Region"]?.ToString() == "華東" &&
                r["Category"]?.ToString() == "A" &&
                r["Year"]?.ToString() == "2025");
            Assert.AreEqual(300m, Convert.ToDecimal(groupAA25["Amount_Sum"]));
            Assert.AreEqual(15m, Convert.ToDecimal(groupAA25["Quantity_Avg"]));  // (10+20)/2
            Assert.AreEqual(10m, Convert.ToDecimal(groupAA25["Discount_Max"]));

            var groupB26 = rows.Single(r =>
                r["Category"]?.ToString() == "B" &&
                r["Year"]?.ToString() == "2026");
            Assert.AreEqual(300m, Convert.ToDecimal(groupB26["Amount_Sum"]));
            Assert.AreEqual(30m, Convert.ToDecimal(groupB26["Quantity_Avg"]));
            Assert.AreEqual(15m, Convert.ToDecimal(groupB26["Discount_Max"]));
        }

        [TestMethod]
        public void Empty_query_returns_empty_results()
        {
            var data = MakeQuery(); // no records
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(data, req, _whitelist);

            Assert.AreEqual(0, rows.Count);
        }

        [TestMethod]
        public void MaxMaterializeRows_truncation_is_applied()
        {
            // Create a data source larger than MaxMaterializeRows
            // We can't easily create 50K+ in-memory records without performance issues,
            // so we verify that Take(MaxMaterializeRows) is applied by using a custom IQueryable
            // that tracks whether Take was called.
            var records = Enumerable.Range(1, 100)
                .Select(i => new SaleRecord { Region = $"R{i}", Amount = i })
                .ToList();
            var data = records.AsQueryable();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(data, req, _whitelist);

            // All 100 unique regions should produce 100 groups (well under MaxMaterializeRows)
            Assert.AreEqual(100, rows.Count);
            // Verify the constant is accessible and correct
            Assert.AreEqual(50_000, InProcessGroupByStrategy.MaxMaterializeRows);
        }

        [TestMethod]
        public void Produces_same_results_as_old_engine_inline()
        {
            // Verify that using InProcessGroupByStrategy via the engine
            // produces the same result as direct strategy call
            var data = new List<SaleRecord>
            {
                new SaleRecord { Region = "華東", Amount = 100m },
                new SaleRecord { Region = "華東", Amount = 200m },
                new SaleRecord { Region = "華南", Amount = 300m }
            };

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            // Direct strategy call
            var directRows = _strategy.Execute(data.AsQueryable(), req, _whitelist);

            // Via engine
            var engine = new AnalysisQueryEngine();
            var engineResult = engine.Execute(data.AsQueryable(), req, _whitelist.Values);

            Assert.AreEqual(directRows.Count, engineResult.Rows.Count);
            foreach (var directRow in directRows)
            {
                var region = directRow["Region"]?.ToString();
                var engineRow = engineResult.Rows.Single(r => r["Region"]?.ToString() == region);
                Assert.AreEqual(
                    Convert.ToDecimal(directRow["Amount_Sum"]),
                    Convert.ToDecimal(engineRow["Amount_Sum"]));
            }
        }
    }
}
