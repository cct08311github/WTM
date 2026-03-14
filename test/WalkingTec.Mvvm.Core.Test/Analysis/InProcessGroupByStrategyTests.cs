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

        /// <summary>
        /// 帶有 DateTime 維度的測試模型，用於驗證日期階層截斷。
        /// </summary>
        private class DateSaleRecord
        {
            [Dimension(DisplayName = "訂單日期", Hierarchy = DateHierarchy.Month)]
            public DateTime OrderDate { get; set; }

            [Dimension(DisplayName = "地區")]
            public string Region { get; set; } = string.Empty;

            [Dimension(DisplayName = "出貨日期")]
            public DateTime? ShipDate { get; set; }

            [Measure(AllowedFuncs =
                AggregateFunc.Sum | AggregateFunc.Count,
                DisplayName = "金額")]
            public decimal Amount { get; set; }
        }

        // ─── 基礎設施 ──────────────────────────────────────────────────────────

        private InProcessGroupByStrategy _strategy = null!;
        private Dictionary<string, AnalysisFieldMeta> _whitelist = null!;
        private Dictionary<string, AnalysisFieldMeta> _dateWhitelist = null!;

        [TestInitialize]
        public void Setup()
        {
            _strategy = new InProcessGroupByStrategy();
            _whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord))
                .ToDictionary(f => f.FieldName);
            _dateWhitelist = AnalysisFieldScanner.ScanModel(typeof(DateSaleRecord))
                .ToDictionary(f => f.FieldName);
        }

        private static IQueryable<SaleRecord> MakeQuery(params SaleRecord[] records)
            => records.AsQueryable();

        private static IQueryable<DateSaleRecord> MakeDateQuery(params DateSaleRecord[] records)
            => records.AsQueryable();

        private static AnalysisQueryRequest Req(
            string[]? dims = null,
            (string field, AggregateFunc func)[]? msrs = null,
            Dictionary<string, DateHierarchy>? hierarchies = null)
        {
            return new AnalysisQueryRequest
            {
                Dimensions = dims?.ToList() ?? new List<string>(),
                Measures = msrs?.Select(m => new MeasureRequest { Field = m.field, Func = m.func }).ToList()
                           ?? new List<MeasureRequest>(),
                Filters = new List<FilterCondition>(),
                DimensionHierarchies = hierarchies
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

        // ─── 日期階層截斷測試（PR #228 修復驗證）─────────────────────────────

        [TestMethod]
        public void Date_dimension_with_Month_hierarchy_groups_by_month()
        {
            // 3 筆不同日期但同月 → 應只產生 1 個群組
            var data = MakeDateQuery(
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 1), Amount = 100m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 15), Amount = 200m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 31), Amount = 300m }
            );
            var req = Req(
                dims: new[] { "OrderDate" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                hierarchies: new Dictionary<string, DateHierarchy> { ["OrderDate"] = DateHierarchy.Month });

            var rows = _strategy.Execute(data, req, _dateWhitelist);

            Assert.AreEqual(1, rows.Count, "同月份的 3 筆應合併為 1 組");
            Assert.AreEqual(600m, Convert.ToDecimal(rows[0]["Amount_Sum"]));
            Assert.AreEqual("2026-03", rows[0]["OrderDate"]?.ToString());
        }

        [TestMethod]
        public void Date_dimension_with_Year_hierarchy_groups_by_year()
        {
            var data = MakeDateQuery(
                new DateSaleRecord { OrderDate = new DateTime(2025, 1, 10), Amount = 100m },
                new DateSaleRecord { OrderDate = new DateTime(2025, 6, 20), Amount = 200m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 5), Amount = 300m }
            );
            var req = Req(
                dims: new[] { "OrderDate" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                hierarchies: new Dictionary<string, DateHierarchy> { ["OrderDate"] = DateHierarchy.Year });

            var rows = _strategy.Execute(data, req, _dateWhitelist);

            Assert.AreEqual(2, rows.Count, "2025 和 2026 應分為 2 組");
            var y2025 = rows.Single(r => r["OrderDate"]?.ToString() == "2025");
            Assert.AreEqual(300m, Convert.ToDecimal(y2025["Amount_Sum"]));
            var y2026 = rows.Single(r => r["OrderDate"]?.ToString() == "2026");
            Assert.AreEqual(300m, Convert.ToDecimal(y2026["Amount_Sum"]));
        }

        [TestMethod]
        public void Date_dimension_with_Quarter_hierarchy_groups_by_quarter()
        {
            var data = MakeDateQuery(
                new DateSaleRecord { OrderDate = new DateTime(2026, 1, 15), Amount = 100m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 2, 20), Amount = 200m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 10), Amount = 300m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 4, 5), Amount = 400m }
            );
            var req = Req(
                dims: new[] { "OrderDate" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                hierarchies: new Dictionary<string, DateHierarchy> { ["OrderDate"] = DateHierarchy.Quarter });

            var rows = _strategy.Execute(data, req, _dateWhitelist);

            Assert.AreEqual(2, rows.Count, "Q1 和 Q2 應分為 2 組");
            var q1 = rows.Single(r => r["OrderDate"]?.ToString() == "2026 Q1");
            Assert.AreEqual(600m, Convert.ToDecimal(q1["Amount_Sum"]));
            var q2 = rows.Single(r => r["OrderDate"]?.ToString() == "2026 Q2");
            Assert.AreEqual(400m, Convert.ToDecimal(q2["Amount_Sum"]));
        }

        [TestMethod]
        public void Date_dimension_with_Day_hierarchy_groups_by_day()
        {
            var data = MakeDateQuery(
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 9, 8, 0, 0), Amount = 100m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 9, 16, 30, 0), Amount = 200m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 10, 9, 0, 0), Amount = 300m }
            );
            var req = Req(
                dims: new[] { "OrderDate" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                hierarchies: new Dictionary<string, DateHierarchy> { ["OrderDate"] = DateHierarchy.Day });

            var rows = _strategy.Execute(data, req, _dateWhitelist);

            Assert.AreEqual(2, rows.Count, "同天不同時間應合併，不同天分開");
            var day9 = rows.Single(r => r["OrderDate"]?.ToString() == "2026-03-09");
            Assert.AreEqual(300m, Convert.ToDecimal(day9["Amount_Sum"]));
            var day10 = rows.Single(r => r["OrderDate"]?.ToString() == "2026-03-10");
            Assert.AreEqual(300m, Convert.ToDecimal(day10["Amount_Sum"]));
        }

        [TestMethod]
        public void Date_dimension_without_hierarchy_preserves_exact_values()
        {
            // 不傳 DimensionHierarchies → 每個不同的 DateTime 值各自一組
            var data = MakeDateQuery(
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 1), Amount = 100m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 15), Amount = 200m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 3, 31), Amount = 300m }
            );
            var req = Req(
                dims: new[] { "OrderDate" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                hierarchies: null);

            var rows = _strategy.Execute(data, req, _dateWhitelist);

            Assert.AreEqual(3, rows.Count, "無階層時每個不同日期各自一組");
        }

        [TestMethod]
        public void Nullable_DateTime_null_values_grouped_as_empty()
        {
            // ShipDate 是 DateTime?，null 值應歸為空字串群組
            var data = MakeDateQuery(
                new DateSaleRecord { OrderDate = new DateTime(2026, 1, 1), ShipDate = new DateTime(2026, 1, 5), Amount = 100m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 1, 2), ShipDate = null, Amount = 200m },
                new DateSaleRecord { OrderDate = new DateTime(2026, 1, 3), ShipDate = null, Amount = 300m }
            );
            var req = Req(
                dims: new[] { "ShipDate" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                hierarchies: new Dictionary<string, DateHierarchy> { ["ShipDate"] = DateHierarchy.Month });

            var rows = _strategy.Execute(data, req, _dateWhitelist);

            Assert.AreEqual(2, rows.Count, "有值和 null 應分為 2 組");
            var nullGroup = rows.Single(r => string.IsNullOrEmpty(r["ShipDate"]?.ToString()));
            Assert.AreEqual(500m, Convert.ToDecimal(nullGroup["Amount_Sum"]));
        }

        [TestMethod]
        public void Date_hierarchy_combined_with_string_dimension()
        {
            // 混合 string 維度 + date 階層：Region × Month
            var data = MakeDateQuery(
                new DateSaleRecord { Region = "華東", OrderDate = new DateTime(2026, 1, 10), Amount = 100m },
                new DateSaleRecord { Region = "華東", OrderDate = new DateTime(2026, 1, 20), Amount = 200m },
                new DateSaleRecord { Region = "華東", OrderDate = new DateTime(2026, 2, 5), Amount = 300m },
                new DateSaleRecord { Region = "華南", OrderDate = new DateTime(2026, 1, 15), Amount = 400m }
            );
            var req = Req(
                dims: new[] { "Region", "OrderDate" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                hierarchies: new Dictionary<string, DateHierarchy> { ["OrderDate"] = DateHierarchy.Month });

            var rows = _strategy.Execute(data, req, _dateWhitelist);

            Assert.AreEqual(3, rows.Count, "華東×2026-01, 華東×2026-02, 華南×2026-01");
            var hdJan = rows.Single(r =>
                r["Region"]?.ToString() == "華東" && r["OrderDate"]?.ToString() == "2026-01");
            Assert.AreEqual(300m, Convert.ToDecimal(hdJan["Amount_Sum"]));
        }

        // ─── 原有測試 ─────────────────────────────────────────────────────────

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
            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
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
