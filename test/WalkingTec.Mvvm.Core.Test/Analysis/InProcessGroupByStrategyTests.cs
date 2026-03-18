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
        /// 帶有可空字串維度的測試模型，用於驗證 null 字串分組行為。
        /// </summary>
        private class NullableStringRecord
        {
            [Dimension(DisplayName = "地區")]
            public string? Region { get; set; }

            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count,
                DisplayName = "金額")]
            public decimal Amount { get; set; }
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
        private Dictionary<string, AnalysisFieldMeta> _nullableStringWhitelist = null!;

        [TestInitialize]
        public void Setup()
        {
            _strategy = new InProcessGroupByStrategy();
            _whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord))
                .ToDictionary(f => f.FieldName);
            _dateWhitelist = AnalysisFieldScanner.ScanModel(typeof(DateSaleRecord))
                .ToDictionary(f => f.FieldName);
            _nullableStringWhitelist = AnalysisFieldScanner.ScanModel(typeof(NullableStringRecord))
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

        // ─── MaxMaterializeRows 截斷測試 ──────────────────────────────────────
        //
        // 策略：使用「哨兵記錄」（sentinel）放在位置 50,001。
        // 若 Take(50_000) 正常運作，哨兵不會出現在結果中。
        // 這比僅驗證 Count 更精確，因為它直接確認「哪些資料被截斷」。

        [TestMethod]
        public void Source_exceeding_50000_rows_drops_records_beyond_limit()
        {
            // 前 50,000 筆：Region = "Common"，Amount = 1
            // 第 50,001 筆（哨兵）：Region = "SENTINEL"，Amount = 99999
            var records = Enumerable.Range(1, 50_000)
                .Select(_ => new SaleRecord { Region = "Common", Amount = 1m })
                .Append(new SaleRecord { Region = "SENTINEL", Amount = 99_999m })
                .ToList();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(records.AsQueryable(), req, _whitelist);

            var hasSentinel = rows.Any(r => r.TryGetValue("Region", out var v) && (string?)v == "SENTINEL");
            Assert.IsFalse(hasSentinel,
                "第 50,001 筆（哨兵）超過 MaxMaterializeRows(50,000)，應被 Take() 截斷而不出現在結果中");
        }

        [TestMethod]
        public void Source_with_exactly_50000_rows_is_not_truncated()
        {
            // 恰好 50,000 筆唯一 Region → 所有資料都在 Take(50_000) 上限內
            var records = Enumerable.Range(1, 50_000)
                .Select(i => new SaleRecord { Region = $"R{i:D6}", Amount = 1m })
                .ToList();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(records.AsQueryable(), req, _whitelist);

            // GroupBy 後 50,000 個分組，但 Take(MaxRows+1)=10,001 會截斷輸出
            // 重要的是：所有資料都被 Take(50,000) 收到（未被上層截斷）
            Assert.IsTrue(rows.Count <= InProcessGroupByStrategy.MaxMaterializeRows,
                "恰好 50,000 筆時不應觸發 MaxMaterializeRows 截斷");
            // 且輸出行數符合 MaxRows+1 的限制
            Assert.IsTrue(rows.Count <= 10_001,
                "GroupBy 結果應受 MaxRows+1 限制");
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

        // ─── Regression: all-null measure group (#345) ────────────────────────

        /// <summary>
        /// Regression (#345): 當某分組的所有 measure 值都是 null 時，
        /// Max/Min 應回傳 null（非 decimal 的 default(0)），Sum 應回傳 0，Count 應回傳 0。
        /// 這驗證 InProcessGroupByStrategy 對 nullable decimal 的聚合不會靜默錯誤。
        /// </summary>
        private class NullableSaleRecord
        {
            [Dimension(DisplayName = "地區")]
            public string Region { get; set; } = string.Empty;

            [Measure(AllowedFuncs =
                AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg |
                AggregateFunc.Max | AggregateFunc.Min,
                DisplayName = "金額")]
            public decimal? NullableAmount { get; set; }
        }

        [TestMethod]
        public void All_null_measure_group_max_returns_null_or_zero()
        {
            // 建立一個分組，所有 NullableAmount = null
            var wl = AnalysisFieldScanner.ScanModel(typeof(NullableSaleRecord))
                .ToDictionary(f => f.FieldName);

            var data = new List<NullableSaleRecord>
            {
                new NullableSaleRecord { Region = "華東", NullableAmount = null },
                new NullableSaleRecord { Region = "華東", NullableAmount = null },
            }.AsQueryable();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("NullableAmount", AggregateFunc.Max) });

            var rows = _strategy.Execute(data, req, wl);

            Assert.AreEqual(1, rows.Count, "全 null 值的分組仍應產生 1 列");
            // Max of all nulls: 可以是 null 或 0，但不應拋例外，
            // 且不應是一個不正確的非零數字
            var maxVal = rows[0]["NullableAmount_Max"];
            Assert.IsTrue(maxVal == null || Convert.ToDecimal(maxVal) == 0m,
                $"全 null 的 Max 應回傳 null 或 0，實際為：{maxVal}");
        }

        [TestMethod]
        public void All_null_measure_group_min_returns_null_or_zero()
        {
            var wl = AnalysisFieldScanner.ScanModel(typeof(NullableSaleRecord))
                .ToDictionary(f => f.FieldName);

            var data = new List<NullableSaleRecord>
            {
                new NullableSaleRecord { Region = "華南", NullableAmount = null },
            }.AsQueryable();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("NullableAmount", AggregateFunc.Min) });

            var rows = _strategy.Execute(data, req, wl);

            Assert.AreEqual(1, rows.Count);
            var minVal = rows[0]["NullableAmount_Min"];
            Assert.IsTrue(minVal == null || Convert.ToDecimal(minVal) == 0m,
                $"全 null 的 Min 應回傳 null 或 0，實際為：{minVal}");
        }

        [TestMethod]
        public void All_null_measure_group_sum_returns_zero()
        {
            var wl = AnalysisFieldScanner.ScanModel(typeof(NullableSaleRecord))
                .ToDictionary(f => f.FieldName);

            var data = new List<NullableSaleRecord>
            {
                new NullableSaleRecord { Region = "華東", NullableAmount = null },
                new NullableSaleRecord { Region = "華東", NullableAmount = null },
            }.AsQueryable();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("NullableAmount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(data, req, wl);

            Assert.AreEqual(1, rows.Count);
            // Sum of nulls = 0 (LINQ DefaultIfEmpty behaviour)
            var sumVal = rows[0]["NullableAmount_Sum"];
            Assert.AreEqual(0m, Convert.ToDecimal(sumVal ?? 0m),
                "全 null 的 Sum 應回傳 0");
        }

        [TestMethod]
        public void Mixed_null_and_non_null_measure_max_ignores_nulls()
        {
            var wl = AnalysisFieldScanner.ScanModel(typeof(NullableSaleRecord))
                .ToDictionary(f => f.FieldName);

            var data = new List<NullableSaleRecord>
            {
                new NullableSaleRecord { Region = "華東", NullableAmount = null  },
                new NullableSaleRecord { Region = "華東", NullableAmount = 500m  },
                new NullableSaleRecord { Region = "華東", NullableAmount = null  },
            }.AsQueryable();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("NullableAmount", AggregateFunc.Max) });

            var rows = _strategy.Execute(data, req, wl);

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(500m, Convert.ToDecimal(rows[0]["NullableAmount_Max"]),
                "混合 null 與非 null 的 Max 應忽略 null，回傳 500");
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

        // ─── Null-byte separator regression tests (#372) ──────────────────────

        [TestMethod]
        public void EncodeKeyPart_plain_string_returns_same_instance()
        {
            var s = "NorthEast";
            Assert.AreSame(s, InProcessGroupByStrategy.EncodeKeyPart(s),
                "無特殊字元時應回傳原始字串實例（無額外分配）");
        }

        [TestMethod]
        public void EncodeKeyPart_escapes_null_byte()
        {
            Assert.AreEqual("A%00B", InProcessGroupByStrategy.EncodeKeyPart("A\0B"));
        }

        [TestMethod]
        public void EncodeKeyPart_escapes_percent_sign()
        {
            Assert.AreEqual("100%25", InProcessGroupByStrategy.EncodeKeyPart("100%"));
        }

        [TestMethod]
        public void EncodeKeyPart_escapes_both_percent_and_null_byte()
        {
            // "A%\0B" → escape % first → "A%25\0B" → escape \0 → "A%25%00B"
            Assert.AreEqual("A%25%00B", InProcessGroupByStrategy.EncodeKeyPart("A%\0B"));
        }

        [TestMethod]
        public void DecodeKeyPart_plain_string_returns_same_instance()
        {
            var s = "NorthEast";
            Assert.AreSame(s, InProcessGroupByStrategy.DecodeKeyPart(s));
        }

        [TestMethod]
        public void DecodeKeyPart_restores_null_byte()
        {
            Assert.AreEqual("A\0B", InProcessGroupByStrategy.DecodeKeyPart("A%00B"));
        }

        [TestMethod]
        public void DecodeKeyPart_restores_percent_sign()
        {
            Assert.AreEqual("100%", InProcessGroupByStrategy.DecodeKeyPart("100%25"));
        }

        [TestMethod]
        public void EncodeDecodeKeyPart_roundtrip_with_literal_percent00_string()
        {
            // 原始值字面上含 "%00"（不是 null byte），不應被錯誤解讀
            var original = "CODE%00XY";
            var encoded = InProcessGroupByStrategy.EncodeKeyPart(original);
            var decoded = InProcessGroupByStrategy.DecodeKeyPart(encoded);
            Assert.AreEqual(original, decoded, "含 %00 字面字串應正確往返編解碼");
        }

        [TestMethod]
        public void Multi_dimension_with_null_byte_in_first_dimension_does_not_corrupt()
        {
            // 金融情境：商品代碼含 ETL 載入殘留的 \0（Oracle CHAR 填充等）
            var data = new List<SaleRecord>
            {
                new SaleRecord { Region = "A\0B", Category = "North", Amount = 100m },
                new SaleRecord { Region = "CD",   Category = "South", Amount = 200m }
            };

            var req = Req(
                dims: new[] { "Region", "Category" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(data.AsQueryable(), req, _whitelist);

            Assert.AreEqual(2, rows.Count, "應有 2 個分組");
            var nullByteRow = rows.Single(r => r["Region"]?.ToString() == "A\0B");
            Assert.AreEqual("North", nullByteRow["Category"]?.ToString(),
                "含 \\0 的維度值不應導致 Category 欄位錯位");
            Assert.AreEqual(100m, Convert.ToDecimal(nullByteRow["Amount_Sum"]));
        }

        [TestMethod]
        public void Multi_dimension_with_null_byte_in_second_dimension_does_not_corrupt()
        {
            var data = new List<SaleRecord>
            {
                new SaleRecord { Region = "East", Category = "Cat\0A", Amount = 50m },
                new SaleRecord { Region = "West", Category = "CatB",   Amount = 75m }
            };

            var req = Req(
                dims: new[] { "Region", "Category" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(data.AsQueryable(), req, _whitelist);

            Assert.AreEqual(2, rows.Count);
            var nullByteRow = rows.Single(r => r["Category"]?.ToString() == "Cat\0A");
            Assert.AreEqual("East", nullByteRow["Region"]?.ToString(),
                "第二維度含 \\0 不應導致 Region 欄位錯位");
        }

        [TestMethod]
        public void Single_dimension_with_null_byte_groups_correctly()
        {
            var data = new List<SaleRecord>
            {
                new SaleRecord { Region = "A\0B", Amount = 100m },
                new SaleRecord { Region = "A\0B", Amount = 200m },
                new SaleRecord { Region = "CD",   Amount = 300m }
            };

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(data.AsQueryable(), req, _whitelist);

            Assert.AreEqual(2, rows.Count, "A\\0B 應歸為同一分組");
            var nullByteRow = rows.Single(r => r["Region"]?.ToString() == "A\0B");
            Assert.AreEqual(300m, Convert.ToDecimal(nullByteRow["Amount_Sum"]),
                "含 \\0 的單維度應正確聚合 100+200=300");
        }

        // ─── nullable string 維度分組 (#376) ──────────────────────────────────

        [TestMethod]
        public void Null_string_dimension_grouped_as_empty_string()
        {
            // null 維度值應以空字串作為 group key，落入 "" 分組
            var data = new List<NullableStringRecord>
            {
                new NullableStringRecord { Region = null,    Amount = 100m },
                new NullableStringRecord { Region = null,    Amount = 200m },
                new NullableStringRecord { Region = "North", Amount = 300m },
            };
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(data.AsQueryable(), req, _nullableStringWhitelist);

            Assert.AreEqual(2, rows.Count, "null 和非 null 應分成 2 個分組");
            var nullGroup = rows.Single(r => r["Region"]?.ToString() == "");
            Assert.AreEqual(300m, Convert.ToDecimal(nullGroup["Amount_Sum"]),
                "null Region 的兩筆 100+200 應聚合為 300");
        }

        [TestMethod]
        public void Empty_string_dimension_grouped_as_empty_string()
        {
            // 空字串與 null 都映射到相同 group key（空字串）
            var data = new List<NullableStringRecord>
            {
                new NullableStringRecord { Region = "",      Amount = 50m },
                new NullableStringRecord { Region = "",      Amount = 75m },
                new NullableStringRecord { Region = "South", Amount = 200m },
            };
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(data.AsQueryable(), req, _nullableStringWhitelist);

            Assert.AreEqual(2, rows.Count, "空字串和非空字串應分成 2 個分組");
            var emptyGroup = rows.Single(r => r["Region"]?.ToString() == "");
            Assert.AreEqual(125m, Convert.ToDecimal(emptyGroup["Amount_Sum"]),
                "空字串 Region 的兩筆 50+75 應聚合為 125");
        }

        [TestMethod]
        public void Null_and_empty_string_dimension_merged_into_same_group()
        {
            // null 和 "" 的 group key 相同，應合併到同一分組
            var data = new List<NullableStringRecord>
            {
                new NullableStringRecord { Region = null, Amount = 100m },
                new NullableStringRecord { Region = "",   Amount = 200m },
                new NullableStringRecord { Region = "X",  Amount = 999m },
            };
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(data.AsQueryable(), req, _nullableStringWhitelist);

            Assert.AreEqual(2, rows.Count, "null 與 \"\" 應合併為同一分組，共 2 個分組");
            var mergedGroup = rows.Single(r => r["Region"]?.ToString() == "");
            Assert.AreEqual(300m, Convert.ToDecimal(mergedGroup["Amount_Sum"]),
                "null(100) + \"\"(200) 應合併聚合為 300");
        }

        // ─── #558: FormatException protection for non-numeric Measure fields ──

        /// <summary>
        /// Model that deliberately tags a string property as [Measure] — simulates the
        /// developer mistake of marking a non-numeric field as a Measure.
        /// </summary>
        private class BadMeasureRecord
        {
            [Dimension(DisplayName = "地區")]
            public string Region { get; set; } = string.Empty;

            [Measure(AllowedFuncs = AggregateFunc.Sum, DisplayName = "狀態（錯誤用作 Measure）")]
            public string Status { get; set; } = string.Empty;
        }

        [TestMethod]
        public void Non_numeric_measure_throws_InvalidOperationException_not_FormatException()
        {
            // Arrange: string property marked as [Measure] — the scanner accepts it,
            // but conversion at aggregation time must throw a descriptive error, not a raw FormatException.
            var wl = AnalysisFieldScanner.ScanModel(typeof(BadMeasureRecord))
                .ToDictionary(f => f.FieldName);

            var data = new List<BadMeasureRecord>
            {
                new BadMeasureRecord { Region = "北區", Status = "Active" },
                new BadMeasureRecord { Region = "北區", Status = "Closed" },
            }.AsQueryable();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Status", AggregateFunc.Sum) });

            // Act & Assert: should throw InvalidOperationException (not raw FormatException)
            // with a message that mentions the field name.
            var ex = Assert.ThrowsException<InvalidOperationException>(
                () => _strategy.Execute(data, req, wl));

            StringAssert.Contains(ex.Message, "Status",
                "錯誤訊息應包含欄位名稱，方便開發者定位問題");
        }

        [TestMethod]
        public void Non_numeric_measure_error_message_contains_value_type()
        {
            var wl = AnalysisFieldScanner.ScanModel(typeof(BadMeasureRecord))
                .ToDictionary(f => f.FieldName);

            var data = new List<BadMeasureRecord>
            {
                new BadMeasureRecord { Region = "南區", Status = "Pending" },
            }.AsQueryable();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Status", AggregateFunc.Sum) });

            var ex = Assert.ThrowsException<InvalidOperationException>(
                () => _strategy.Execute(data, req, wl));

            // The inner exception should be a FormatException (or similar) — not swallowed
            Assert.IsNotNull(ex.InnerException,
                "原始例外應保留在 InnerException，供偵錯使用");
        }
    }
}
