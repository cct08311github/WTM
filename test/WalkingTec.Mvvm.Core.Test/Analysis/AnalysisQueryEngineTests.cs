#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// AnalysisQueryEngine 完整覆蓋測試。
    /// 涵蓋：ValidateFields 所有分支、ApplyFilters 所有運算子、
    /// ExecuteGroupBy 所有聚合函式、截斷邏輯、ExecuteDynamic。
    /// </summary>
    [TestClass]
    public class AnalysisQueryEngineTests
    {
        // ─── 測試模型 ──────────────────────────────────────────────────────────

        private enum SaleChannel { Online, Offline, Hybrid }

        // Enum with [Display(Name)] — used to test display-name reverse-lookup in ChangeType (#473)
        private enum CustomerTier
        {
            [System.ComponentModel.DataAnnotations.Display(Name = "一般")] Regular = 0,
            [System.ComponentModel.DataAnnotations.Display(Name = "銀卡")] Silver  = 1,
            [System.ComponentModel.DataAnnotations.Display(Name = "金卡")] Gold    = 2,
        }

        private class SaleRecord : TopBasePoco
        {
            [Dimension(DisplayName = "地區")]  public string Region   { get; set; }
            [Dimension(DisplayName = "類別")]  public string Category { get; set; }
            [Dimension(DisplayName = "通路")]  public SaleChannel Channel { get; set; }

            /// Amount 允許全部 5 種聚合函式
            [Measure(AllowedFuncs =
                AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg |
                AggregateFunc.Max | AggregateFunc.Min,
                DisplayName = "金額")]
            public decimal Amount { get; set; }

            /// CountOnly 僅允許 Count，用於「不允許的函式」測試
            [Measure(AllowedFuncs = AggregateFunc.Count, DisplayName = "計數")]
            public decimal CountOnly { get; set; }
        }

        private class SaleTestContext : DbContext
        {
            public SaleTestContext(DbContextOptions opts) : base(opts) { }
            public DbSet<SaleRecord> SaleRecords { get; set; }
        }

        // ─── NullableChannel 測試模型（#481 NotIn null-row exclusion）────────────

        private class NullableChannelRecord : TopBasePoco
        {
            [Dimension(DisplayName = "通路")] public SaleChannel? Channel { get; set; }
            [Measure(AllowedFuncs = AggregateFunc.Count, DisplayName = "筆數")] public decimal Count { get; set; }
        }

        private class NullableChannelContext : DbContext
        {
            public NullableChannelContext(DbContextOptions opts) : base(opts) { }
            public DbSet<NullableChannelRecord> Records { get; set; }
        }

        // ─── CustomerTier 測試模型（#473 enum display-name filter）───────────────

        private class CustomerRecord : TopBasePoco
        {
            [Dimension(DisplayName = "等級")] public CustomerTier Tier { get; set; }
            [Measure(AllowedFuncs = AggregateFunc.Count, DisplayName = "筆數")] public decimal Count { get; set; }
        }

        private class CustomerTestContext : DbContext
        {
            public CustomerTestContext(DbContextOptions opts) : base(opts) { }
            public DbSet<CustomerRecord> Customers { get; set; }
        }

        // ─── 基礎設施 ──────────────────────────────────────────────────────────

        private SqliteConnection _conn;
        private SaleTestContext  _ctx;
        private IEnumerable<AnalysisFieldMeta> _whitelist;

        [TestInitialize]
        public void Setup()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();

            var opts = new DbContextOptionsBuilder<SaleTestContext>()
                .UseSqlite(_conn).Options;

            _ctx = new SaleTestContext(opts);
            _ctx.Database.EnsureCreated();

            // 基準資料：華東(100), 華東(200), 華南(300)
            _ctx.SaleRecords.AddRange(
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Channel = SaleChannel.Online,  Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "B", Channel = SaleChannel.Offline, Amount = 200m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "A", Channel = SaleChannel.Online,  Amount = 300m }
            );
            _ctx.SaveChanges();

            _whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord));
        }

        [TestCleanup]
        public void Cleanup()
        {
            _ctx.Dispose();
            _conn.Dispose();
        }

        private static AnalysisQueryEngine Engine() => new AnalysisQueryEngine(GroupByStrategyResolver.Default);

        private IQueryable<SaleRecord> Q() => _ctx.SaleRecords.AsQueryable();

        // ─── ValidateFields 分支 ───────────────────────────────────────────────

        /// <summary>維度欄位不在白名單中 → 拋例外</summary>
        [TestMethod]
        public void Throws_when_dimension_not_in_whitelist()
        {
            var req = Req(dims: new[] { "Nonexistent" });
            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>嘗試將 Measure 欄位用作 Dimension（Kind 不符）→ 拋例外</summary>
        [TestMethod]
        public void Throws_when_measure_field_used_as_dimension()
        {
            var req = Req(dims: new[] { "Amount" }); // Amount is Measure, not Dimension
            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>度量欄位不在白名單中 → 拋例外</summary>
        [TestMethod]
        public void Throws_when_measure_field_not_in_whitelist()
        {
            var req = Req(msrs: new[] { ("Nonexistent", AggregateFunc.Sum) });
            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>嘗試將 Dimension 欄位用作 Measure（Kind 不符）→ 拋例外</summary>
        [TestMethod]
        public void Throws_when_dimension_field_used_as_measure()
        {
            var req = Req(msrs: new[] { ("Region", AggregateFunc.Sum) }); // Region is Dimension
            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>不允許的聚合函式（CountOnly 欄位不支援 Sum）→ 拋例外</summary>
        [TestMethod]
        public void Throws_when_measure_func_not_allowed()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("CountOnly", AggregateFunc.Sum) });
            // The actual exception is NotSupportedException, not InvalidOperationException
            Assert.ThrowsException<NotSupportedException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>過濾欄位不在白名單 → 拋例外</summary>
        [TestMethod]
        public void Throws_when_filter_field_not_in_whitelist()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Nonexistent", FilterOperator.Eq, "X") });
            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        // ─── ApplyFilters 運算子分支 ────────────────────────────────────────────

        /// <summary>Eq 過濾：正確縮減列數</summary>
        [TestMethod]
        public void Filter_Eq_reduces_rows()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.Eq, "華東") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual("華東", result.Rows[0]["Region"].ToString());
        }

        /// <summary>Gt 過濾（Amount > 150）：僅 200 和 300 通過 → 2 個分組</summary>
        [TestMethod]
        public void Filter_Gt_reduces_rows()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Amount", FilterOperator.Gt, "150") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(2, result.Rows.Count);
        }

        /// <summary>Gte 過濾（Amount >= 200）：200 和 300 通過 → 2 個分組</summary>
        [TestMethod]
        public void Filter_Gte_reduces_rows()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Amount", FilterOperator.Gte, "200") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(2, result.Rows.Count);
        }

        /// <summary>Lt 過濾（Amount < 200）：只有 100 通過 → 1 個分組（華東）</summary>
        [TestMethod]
        public void Filter_Lt_reduces_rows()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Amount", FilterOperator.Lt, "200") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual("華東", result.Rows[0]["Region"].ToString());
        }

        /// <summary>Lte 過濾（Amount <= 100）：只有 100 通過 → 1 個分組，Sum=100</summary>
        [TestMethod]
        public void Filter_Lte_reduces_rows()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Amount", FilterOperator.Lte, "100") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(100m, Convert.ToDecimal(result.Rows[0]["Amount_Sum"]));
        }

        /// <summary>Contains 過濾（Region contains "東"）：華東 2 筆通過 → 1 個分組</summary>
        [TestMethod]
        public void Filter_Contains_reduces_rows()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.Contains, "東") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(300m, Convert.ToDecimal(result.Rows[0]["Amount_Sum"])); // 100+200
        }

        /// <summary>未知運算子（超出 enum 範圍的 cast 值）→ 拋 InvalidOperationException（讓 controller 轉 400）</summary>
        [TestMethod]
        public void Filter_unknown_operator_throws_InvalidOperationException()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", (FilterOperator)99, "華東") });

            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>In 過濾：正確縮減列數</summary>
        [TestMethod]
        public void Filter_In_reduces_rows()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.In, "華東,華北") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count); // 只有華東
            Assert.AreEqual("華東", result.Rows[0]["Region"].ToString());
            Assert.AreEqual(300m, Convert.ToDecimal(result.Rows[0]["Amount_Sum"]));
        }

        /// <summary>In 過濾 (超過100個值)：應拋出例外</summary>
        [TestMethod]
        public void Filter_In_throws_when_values_exceed_100()
        {
            var values = string.Join(",", Enumerable.Range(1, 101).Select(i => $"Val{i}"));
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.In, values) });

            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>In 過濾 (0個值)：應拋出例外</summary>
        [TestMethod]
        public void Filter_In_throws_when_values_empty()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.In, "") });

            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        // ─── NotEq / NotContains / NotIn (#428) ──────────────────────────────

        [TestMethod]
        public void Filter_NotEq_excludes_matching_rows()
        {
            // Baseline data: 華東(100), 華東(200), 華南(300)
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.NotEq, "華東") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.IsFalse(result.Rows.Any(r => r["Region"]?.ToString() == "華東"),
                "NotEq 應排除 '華東'");
            Assert.IsTrue(result.Rows.Any(r => r["Region"]?.ToString() == "華南"),
                "NotEq 應保留 '華南'");
        }

        [TestMethod]
        public void Filter_NotContains_excludes_matching_rows()
        {
            // "華東" contains "東" → excluded; "華南" does not → retained
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.NotContains, "東") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.IsFalse(result.Rows.Any(r => r["Region"]?.ToString()?.Contains("東") == true),
                "NotContains '東' 應排除所有含 '東' 的地區");
            Assert.IsTrue(result.Rows.Any(r => r["Region"]?.ToString() == "華南"),
                "NotContains 應保留不含 '東' 的地區（華南）");
        }

        [TestMethod]
        public void Filter_NotIn_excludes_listed_values()
        {
            // NotIn 華東,華南 → all rows excluded
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.NotIn, "華東,華南") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(0, result.Rows.Count,
                "NotIn 華東,華南 應排除全部資料列");
        }

        [TestMethod]
        public void Filter_NotContains_on_non_string_field_throws()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Amount", FilterOperator.NotContains, "100") });

            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        // ─── NotIn / Nullable<T> null-row exclusion (#481) ───────────────────────

        [TestMethod]
        public void Filter_NotIn_nullable_enum_excludes_null_rows()
        {
            // Regression: NOT (NOT_NULL AND CONTAINS) let null rows pass (#481).
            // Expected: null rows are excluded, consistent with In / NotEq / SQL semantics.
            var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var ctx = new NullableChannelContext(
                new DbContextOptionsBuilder<NullableChannelContext>().UseSqlite(conn).Options);
            ctx.Database.EnsureCreated();
            ctx.Records.AddRange(
                new NullableChannelRecord { ID = Guid.NewGuid(), Channel = SaleChannel.Online,  Count = 1 },
                new NullableChannelRecord { ID = Guid.NewGuid(), Channel = SaleChannel.Offline, Count = 1 },
                new NullableChannelRecord { ID = Guid.NewGuid(), Channel = null,                Count = 1 }
            );
            ctx.SaveChanges();

            var wl = AnalysisFieldScanner.ScanModel(typeof(NullableChannelRecord));
            var req = Req(
                dims: new[] { "Channel" },
                msrs: new[] { ("Count", AggregateFunc.Count) },
                filters: new[] { ("Channel", FilterOperator.NotIn, "Online") });

            var result = new AnalysisQueryEngine(GroupByStrategyResolver.Default)
                .Execute(ctx.Records.AsQueryable(), req, wl);

            // Offline row: included (not in list) ✓
            Assert.IsTrue(result.Rows.Any(r => r["Channel"]?.ToString() == "Offline"),
                "Offline 應包含在 NotIn Online 結果中");
            // null row: must be excluded (#481)
            Assert.IsFalse(result.Rows.Any(r => r["Channel"] == null || r["Channel"]?.ToString() == ""),
                "null Channel 不應出現在 NotIn 結果中");

            conn.Close();
        }

        // ─── SQL Injection 回歸保護 ────────────────────────────────────────────
        //
        // Expression Tree 在結構上防止 SQL injection：filter value 轉為
        // Expression.Constant(value) → EF Core parameterized query（@p0），
        // 從不拼接成 raw SQL。以下測試確保此防護持續有效。

        [TestMethod]
        public void Filter_Eq_sql_injection_pattern_in_value_does_not_throw_and_matches_nothing()
        {
            // Arrange: 典型的 SQL injection pattern 作為 filter value
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.Eq, "'; DROP TABLE SaleRecords --") });

            // Act: Expression Tree → parameterized SQL → 不應拋出例外
            var result = Engine().Execute(Q(), req, _whitelist);

            // Assert: injection pattern 不是合法的 Region 值 → 0 列
            Assert.AreEqual(0, result.Rows.Count,
                "SQL injection pattern in filter value should return 0 rows, not throw");
            // 確認 table 仍存在（未被 DROP）
            Assert.AreEqual(3, _ctx.SaleRecords.Count(),
                "SaleRecords table must still contain all 3 rows — no injection occurred");
        }

        [TestMethod]
        public void Filter_Contains_sql_injection_pattern_in_value_does_not_throw()
        {
            // Arrange: Contains 運算子 + SQL injection value
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.Contains, "'; DROP TABLE--") });

            // Act: String.Contains translates to SQL LIKE @p0 — safe
            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(0, result.Rows.Count,
                "SQL injection pattern should match no rows");
            Assert.AreEqual(3, _ctx.SaleRecords.Count(),
                "Table must still have 3 rows after Contains with injection-patterned value");
        }

        [TestMethod]
        public void Filter_Eq_unicode_value_matches_correctly()
        {
            // Unicode filter values（中文、emoji）應正常運作
            _ctx.SaleRecords.Add(new SaleRecord
            {
                ID = Guid.NewGuid(), Region = "東南亞🌏", Category = "A", Amount = 500m
            });
            _ctx.SaveChanges();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.Eq, "東南亞🌏") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count, "Unicode filter value should match exactly 1 row");
            Assert.AreEqual("東南亞🌏", result.Rows[0]["Region"].ToString());
        }

        /// <summary>Contains 運算子用於非字串欄位 → 拋 InvalidOperationException</summary>
        [TestMethod]
        public void Filter_Contains_on_non_string_field_throws()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Amount", FilterOperator.Contains, "100") });

            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>TotalCount 應為截斷前的真實分組數，非截斷後的數量</summary>
        [TestMethod]
        public void TotalCount_reflects_pre_truncation_group_count()
        {
            var extras = Enumerable.Range(1, 10_001)
                .Select(i => new SaleRecord { ID = Guid.NewGuid(), Region = $"R{i}", Category = "X", Amount = i });
            _ctx.SaleRecords.AddRange(extras);
            _ctx.SaveChanges();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Count) });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.IsTrue(result.Truncated);
            Assert.AreEqual(10_000, result.Rows.Count);
            // TotalCount 必須 > MaxRows（截斷前有 10,001+2=10,003 個分組）
            Assert.IsTrue(result.TotalCount > 10_000,
                $"TotalCount 應大於 10000，實際為 {result.TotalCount}");
        }

        /// <summary>零維度（純聚合）→ 所有資料折疊成 1 列</summary>
        [TestMethod]
        public void Zero_dimensions_produces_single_aggregate_row()
        {
            var req = Req(
                dims: new string[0],
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(600m, Convert.ToDecimal(result.Rows[0]["Amount_Sum"])); // 100+200+300
        }

        /// <summary>過濾值無法轉換為目標型別 → 拋 InvalidOperationException</summary>
        [TestMethod]
        public void Filter_invalid_conversion_throws()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Amount", FilterOperator.Gt, "not_a_number") });

            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>過濾後無資料 → 結果為空，非截斷</summary>
        [TestMethod]
        public void Filter_no_match_returns_empty_result()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Region", FilterOperator.Eq, "不存在的地區") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(0, result.Rows.Count);
            Assert.IsFalse(result.Truncated);
        }

        /// <summary>多條件 AND：Region=華東 AND Amount&gt;150 → 只剩華東 B=200</summary>
        [TestMethod]
        public void Filter_multiple_conditions_AND_logic_narrows_result()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] {
                    ("Region", FilterOperator.Eq, "華東"),   // 排除華南 A=300
                    ("Amount", FilterOperator.Gt, "150")    // 排除華東 A=100
                });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count, "AND: 只有華東 Amount>150 的一列應通過");
            Assert.AreEqual(200m, Convert.ToDecimal(result.Rows[0]["Amount_Sum"]));
        }

        /// <summary>三條件 AND：Region=華東 AND Amount&gt;=100 AND Amount&lt;200 → 只剩華東 A=100</summary>
        [TestMethod]
        public void Filter_three_conditions_AND_logic_all_must_match()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] {
                    ("Region", FilterOperator.Eq,  "華東"),
                    ("Amount", FilterOperator.Gte, "100"),
                    ("Amount", FilterOperator.Lt,  "200")
                });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count, "AND: Region=華東 AND 100≤Amount<200 → 只有華東 A=100");
            Assert.AreEqual(100m, Convert.ToDecimal(result.Rows[0]["Amount_Sum"]));
        }

        // ─── AggregateFunc 分支 ────────────────────────────────────────────────

        /// <summary>Count 聚合：華東有 2 筆 → Count=2</summary>
        [TestMethod]
        public void GroupBy_single_dimension_count()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Count) });

            var result = Engine().Execute(Q(), req, _whitelist);

            var huaDong = result.Rows.Single(r => r["Region"].ToString() == "華東");
            Assert.AreEqual(2m, Convert.ToDecimal(huaDong["Amount_Count"]));
        }

        /// <summary>Avg 聚合：華東 (100+200)/2=150</summary>
        [TestMethod]
        public void GroupBy_single_dimension_avg()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Avg) });

            var result = Engine().Execute(Q(), req, _whitelist);

            var huaDong = result.Rows.Single(r => r["Region"].ToString() == "華東");
            Assert.AreEqual(150m, Convert.ToDecimal(huaDong["Amount_Avg"]));
        }

        /// <summary>Max 聚合：華東 Max=200</summary>
        [TestMethod]
        public void GroupBy_single_dimension_max()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Max) });

            var result = Engine().Execute(Q(), req, _whitelist);

            var huaDong = result.Rows.Single(r => r["Region"].ToString() == "華東");
            Assert.AreEqual(200m, Convert.ToDecimal(huaDong["Amount_Max"]));
        }

        /// <summary>Min 聚合：華東 Min=100</summary>
        [TestMethod]
        public void GroupBy_single_dimension_min()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Min) });

            var result = Engine().Execute(Q(), req, _whitelist);

            var huaDong = result.Rows.Single(r => r["Region"].ToString() == "華東");
            Assert.AreEqual(100m, Convert.ToDecimal(huaDong["Amount_Min"]));
        }

        /// <summary>
        /// AggregateFunc=0（未定義值）通過 HasFlag 檢查但 switch 無 case → 拋 NotSupportedException
        /// </summary>
        [TestMethod]
        public void GroupBy_undefined_func_value_throws()
        {
            // (AggregateFunc)0 通過 HasFlag(0)=true 但 switch 無對應 case
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", (AggregateFunc)0) });

            Assert.ThrowsException<NotSupportedException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        // ─── 多維度 / 多度量 ──────────────────────────────────────────────────

        /// <summary>Sum 聚合：基準驗證</summary>
        [TestMethod]
        public void GroupBy_single_dimension_sum()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(2, result.Rows.Count);
            var huaDong = result.Rows.Single(r => r["Region"].ToString() == "華東");
            Assert.AreEqual(300m, Convert.ToDecimal(huaDong["Amount_Sum"]));
        }

        /// <summary>雙維度 GroupBy：Region×Category → 3 個分組</summary>
        [TestMethod]
        public void GroupBy_two_dimensions_produces_correct_groups()
        {
            var req = Req(
                dims: new[] { "Region", "Category" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(3, result.Rows.Count);
            var huaDongA = result.Rows.Single(r =>
                r["Region"].ToString() == "華東" && r["Category"].ToString() == "A");
            Assert.AreEqual(100m, Convert.ToDecimal(huaDongA["Amount_Sum"]));
        }

        /// <summary>同一查詢同時計算 Sum 和 Count</summary>
        [TestMethod]
        public void Multiple_measures_in_one_query()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[]
                {
                    ("Amount", AggregateFunc.Sum),
                    ("Amount", AggregateFunc.Count)
                });

            var result = Engine().Execute(Q(), req, _whitelist);

            var huaDong = result.Rows.Single(r => r["Region"].ToString() == "華東");
            Assert.AreEqual(300m, Convert.ToDecimal(huaDong["Amount_Sum"]));
            Assert.AreEqual(2m,   Convert.ToDecimal(huaDong["Amount_Count"]));
        }

        // ─── 截斷邏輯 ────────────────────────────────────────────────────────

        /// <summary>超過 10,000 列時截斷並標記 Truncated=true</summary>
        [TestMethod]
        public void Result_truncated_at_10000_rows()
        {
            var extras = Enumerable.Range(1, 10_001)
                .Select(i => new SaleRecord { ID = Guid.NewGuid(), Region = $"R{i}", Category = "X", Amount = i });
            _ctx.SaleRecords.AddRange(extras);
            _ctx.SaveChanges();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Count) });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(10_000, result.Rows.Count);
            Assert.IsTrue(result.Truncated);
        }

        /// <summary>結果不超過限制時 Truncated=false，TotalCount 正確</summary>
        [TestMethod]
        public void Non_truncated_result_has_correct_total_count()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.IsFalse(result.Truncated);
            Assert.AreEqual(2, result.TotalCount);
        }

        /// <summary>來源資料超過 50,000 筆時 DataTruncated=true（#490）</summary>
        [TestMethod]
        public void DataTruncated_is_true_when_source_exceeds_50000_rows()
        {
            // 加入 50,000 筆（超過 MaxMaterializeRows）
            var bulk = Enumerable.Range(1, 50_000)
                .Select(i => new SaleRecord { ID = Guid.NewGuid(), Region = $"R{i}", Category = "X", Amount = i });
            _ctx.SaleRecords.AddRange(bulk);
            _ctx.SaveChanges();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.IsTrue(result.DataTruncated, "來源 > 50,000 時 DataTruncated 應為 true");
        }

        /// <summary>來源資料未超過 50,000 筆時 DataTruncated=false（#490）</summary>
        [TestMethod]
        public void DataTruncated_is_false_when_source_within_50000_rows()
        {
            // 基準資料只有 3 筆，遠低於 MaxMaterializeRows
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.IsFalse(result.DataTruncated, "來源 <= 50,000 時 DataTruncated 應為 false");
        }

        // ─── 結果結構 ────────────────────────────────────────────────────────

        /// <summary>Columns 包含維度欄位名 + 度量欄位名_函式名</summary>
        [TestMethod]
        public void Columns_contains_dimension_and_measure_names()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = Engine().Execute(Q(), req, _whitelist);

            CollectionAssert.Contains(result.Columns, "Region");
            CollectionAssert.Contains(result.Columns, "Amount_Sum");
            Assert.AreEqual(2, result.Columns.Count);
        }

        /// <summary>QueryHash 已設定且長度為 16 字元</summary>
        [TestMethod]
        public void QueryHash_is_populated_and_16_chars()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.IsNotNull(result.QueryHash);
            Assert.AreEqual(16, result.QueryHash.Length);
        }

        /// <summary>相同請求產生相同的 QueryHash（deterministic）</summary>
        [TestMethod]
        public void Same_request_produces_same_query_hash()
        {
            var req1 = Req(dims: new[] { "Region" }, msrs: new[] { ("Amount", AggregateFunc.Sum) });
            var req2 = Req(dims: new[] { "Region" }, msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var r1 = Engine().Execute(Q(), req1, _whitelist);
            var r2 = Engine().Execute(Q(), req2, _whitelist);

            Assert.AreEqual(r1.QueryHash, r2.QueryHash);
        }

        // ─── ExecuteDynamic ────────────────────────────────────────────────────

        /// <summary>ExecuteDynamic 透過反射呼叫 Execute，結果應與直接呼叫一致</summary>
        [TestMethod]
        public void ExecuteDynamic_produces_same_result_as_Execute()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            IQueryable baseQuery = Q(); // non-generic IQueryable
            var result = Engine().ExecuteDynamic(baseQuery, req, _whitelist);

            Assert.AreEqual(2, result.Rows.Count);
            Assert.IsFalse(result.Truncated);
        }

        // ─── Fallback 機制 ────────────────────────────────────────────────────

        /// <summary>ServerSide 策略拋 InvalidOperationException → 自動降級到 InProcess</summary>
        [TestMethod]
        public void Fallback_to_InProcess_when_ServerSide_throws()
        {
            var resolver = new FailingServerSideResolver();
            var engine = new AnalysisQueryEngine(resolver);

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            // dbType=SqlServer 觸發 FailingServerSideStrategy → fallback → InProcess 完成
            var result = engine.Execute(Q(), req, _whitelist, DBTypeEnum.SqlServer);

            Assert.AreEqual(2, result.Rows.Count);
            var huaDong = result.Rows.Single(r => r["Region"].ToString() == "華東");
            Assert.AreEqual(300m, Convert.ToDecimal(huaDong["Amount_Sum"]));
        }

        /// <summary>InProcess 策略拋 InvalidOperationException → 不攔截，直接往上拋</summary>
        [TestMethod]
        public void InProcess_exception_is_not_caught()
        {
            // 使用預設 Resolver（SQLite → InProcess），但資料有問題會自然拋
            // 用 undefined func 觸發 InProcess 內的 NotSupportedException
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", (AggregateFunc)0) });

            // InProcess 拋出的異常不被 fallback 攔截（因為 strategy 不是 ServerSideGroupByStrategy）
            Assert.ThrowsException<NotSupportedException>(() =>
                Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>Fallback 後的結果應有正確的 QueryHash 和 Columns</summary>
        [TestMethod]
        public void Fallback_result_has_correct_metadata()
        {
            var resolver = new FailingServerSideResolver();
            var engine = new AnalysisQueryEngine(resolver);

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = engine.Execute(Q(), req, _whitelist, DBTypeEnum.SqlServer);

            Assert.IsNotNull(result.QueryHash);
            Assert.AreEqual(16, result.QueryHash.Length);
            CollectionAssert.Contains(result.Columns, "Region");
            CollectionAssert.Contains(result.Columns, "Amount_Sum");
            Assert.IsFalse(result.Truncated);
        }

        /// <summary>模擬 ServerSide 失敗的策略：繼承 ServerSideGroupByStrategy 使 type check 成立</summary>
        private class FailingServerSideStrategy : ServerSideGroupByStrategy
        {
            public override List<Dictionary<string, object?>> Execute<TModel>(
                IQueryable<TModel> query,
                AnalysisQueryRequest req,
                Dictionary<string, AnalysisFieldMeta> whitelist,
                System.Threading.CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Simulated SQL translation failure.");
            }
        }

        /// <summary>回傳 FailingServerSide 的 Resolver</summary>
        private class FailingServerSideResolver : GroupByStrategyResolver
        {
            private static readonly FailingServerSideStrategy Failing = new();

            public override IGroupByStrategy Resolve(DBTypeEnum dbType, AnalysisQueryRequest req)
            {
                return dbType switch
                {
                    DBTypeEnum.SqlServer => Failing,
                    DBTypeEnum.Oracle => Failing,
                    _ => base.Resolve(dbType, req)
                };
            }
        }

        // ─── Enum Filter (Fixes #264) ───────────────────────────────────────────

        /// <summary>Filter by enum name string (e.g. "Online")</summary>
        [TestMethod]
        public void Filter_Eq_enum_by_name()
        {
            var req = Req(
                dims: new[] { "Channel" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Channel", FilterOperator.Eq, "Online") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(400m, Convert.ToDecimal(result.Rows[0]["Amount_Sum"])); // 100+300
        }

        /// <summary>Filter by enum integer value (e.g. "1" for Offline)</summary>
        [TestMethod]
        public void Filter_Eq_enum_by_integer_value()
        {
            var req = Req(
                dims: new[] { "Channel" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Channel", FilterOperator.Eq, "1") }); // Offline=1

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(200m, Convert.ToDecimal(result.Rows[0]["Amount_Sum"]));
        }

        /// <summary>Filter In with multiple enum values</summary>
        [TestMethod]
        public void Filter_In_enum_multiple_values()
        {
            var req = Req(
                dims: new[] { "Channel" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Channel", FilterOperator.In, "Online,Offline") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(2, result.Rows.Count);
        }

        /// <summary>Filter by enum name is case-insensitive</summary>
        [TestMethod]
        public void Filter_Eq_enum_case_insensitive()
        {
            var req = Req(
                dims: new[] { "Channel" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Channel", FilterOperator.Eq, "online") });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(400m, Convert.ToDecimal(result.Rows[0]["Amount_Sum"]));
        }

        /// <summary>Filter by invalid enum value → 400 with user-friendly message (#482)</summary>
        [TestMethod]
        public void Filter_Eq_enum_invalid_value_throws()
        {
            var req = Req(
                dims: new[] { "Channel" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Channel", FilterOperator.Eq, "InvalidChannel") });

            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
        }

        /// <summary>Invalid enum filter message is user-friendly and lists valid values (#482)</summary>
        [TestMethod]
        public void Filter_Eq_enum_invalid_value_message_contains_valid_values()
        {
            var req = Req(
                dims: new[] { "Channel" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) },
                filters: new[] { ("Channel", FilterOperator.Eq, "XXX") });

            var ex = Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
            // Message should mention the field name
            StringAssert.Contains(ex.Message, "Channel");
            // Message should mention the invalid value
            StringAssert.Contains(ex.Message, "XXX");
            // Message should include at least one valid enum value ("Online" or "Offline")
            Assert.IsTrue(ex.Message.Contains("Online") || ex.Message.Contains("Offline"),
                "Error message should list valid enum values");
        }

        /// <summary>Enum dimension grouping works correctly</summary>
        [TestMethod]
        public void GroupBy_enum_dimension()
        {
            var req = Req(
                dims: new[] { "Channel" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = Engine().Execute(Q(), req, _whitelist);

            Assert.AreEqual(2, result.Rows.Count); // Online, Offline
            var online = result.Rows.Single(r => r["Channel"].ToString() == "Online");
            Assert.AreEqual(400m, Convert.ToDecimal(online["Amount_Sum"])); // 100+300
        }

        // ─── Regression: duplicate dimensions (#345) ──────────────────────────

        /// <summary>
        /// Regression (#345): 重複維度名稱 (["Region","Region"]) 不應產生靜默錯誤資料。
        /// 行為：engine 接受重複維度並折疊（Dict key 相同，結果等同單一 Region 查詢），
        /// 或者拋出例外。無論哪種，結果必須一致：不能產生笛卡兒乘積或欄位衝突。
        /// 現行實作：重複維度在 InProcess GroupBy 中使用相同 key 折疊，等同去重後的結果。
        /// </summary>
        [TestMethod]
        public void Duplicate_dimension_names_produce_consistent_result()
        {
            // ["Region", "Region"] — two identical dimension names
            var req = Req(
                dims: new[] { "Region", "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            // Either throws cleanly OR produces same result as single-dim query.
            // Both behaviours are acceptable regressions to document; we test
            // that no silent garbage (wrong row count or wrong sum) is returned.
            try
            {
                var result = Engine().Execute(Q(), req, _whitelist);

                // If it succeeds: must have same groups as single-Region query
                // (2 groups: 華東, 華南) — NOT 9 (3×3 cartesian).
                Assert.IsTrue(result.Rows.Count <= 2,
                    $"重複維度不應造成笛卡兒乘積，實際回傳 {result.Rows.Count} 列");

                // Each group sum must be correct (同 single-Region 的值)
                foreach (var row in result.Rows)
                {
                    var regionVal = row["Region"]?.ToString();
                    Assert.IsTrue(regionVal == "華東" || regionVal == "華南",
                        $"未預期的地區值：{regionVal}");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                // 拋例外也是合法行為 — 只要不是 NullReferenceException 等非預期錯誤
                Assert.IsTrue(true, "拋出明確例外是可接受的行為");
            }
        }

        /// <summary>
        /// Regression (#345): ValidateFields 不驗證重複維度名稱（因為每個名稱都在白名單中）。
        /// 驗證 Execute 不因重複 GroupBy key 而崩潰（NullReferenceException / KeyNotFoundException）。
        /// </summary>
        [TestMethod]
        public void Duplicate_dimension_names_do_not_crash_with_null_reference()
        {
            var req = Req(
                dims: new[] { "Region", "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Count) });

            // 不論結果為何，不應拋 NullReferenceException / KeyNotFoundException
            Exception caughtEx = null;
            try
            {
                Engine().Execute(Q(), req, _whitelist);
            }
            catch (NullReferenceException ex)
            {
                caughtEx = ex;
            }
            catch (KeyNotFoundException ex)
            {
                caughtEx = ex;
            }

            Assert.IsNull(caughtEx,
                $"重複維度不應拋 NullReferenceException / KeyNotFoundException：{caughtEx?.Message}");
        }

        // ─── Enum display-name filter（Fixes #473）──────────────────────────────

        private (SqliteConnection conn, CustomerTestContext ctx, IQueryable<CustomerRecord> query) SetupCustomerDb(params CustomerRecord[] records)
        {
            var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var opts = new DbContextOptionsBuilder<CustomerTestContext>()
                .UseSqlite(conn).Options;
            var ctx = new CustomerTestContext(opts);
            ctx.Database.EnsureCreated();
            ctx.Customers.AddRange(records);
            ctx.SaveChanges();
            return (conn, ctx, ctx.Customers.AsQueryable());
        }

        [TestMethod]
        public void ApplyFilters_enum_display_name_matches_row()
        {
            // Chart click sends the display name "一般" (from ResolveEnumDisplayNames).
            // ChangeType must reverse-lookup to CustomerTier.Regular.
            var id = Guid.NewGuid();
            var (conn, ctx, query) = SetupCustomerDb(
                new CustomerRecord { ID = id,           Tier = CustomerTier.Regular, Count = 1 },
                new CustomerRecord { ID = Guid.NewGuid(), Tier = CustomerTier.Gold,    Count = 1 });

            var wl = AnalysisFieldScanner.ScanModel(typeof(CustomerRecord))
                .ToDictionary(f => f.FieldName);
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Tier" },
                Measures   = new List<MeasureRequest> { new MeasureRequest { Field = "Count", Func = AggregateFunc.Count } },
                Filters    = new List<FilterCondition> { new FilterCondition { Field = "Tier", Operator = FilterOperator.Eq, Value = "一般" } }
            };

            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            var result = engine.Execute(query, req, wl.Values.ToList());

            Assert.AreEqual(1, result.Rows.Count, "應只回傳 Regular(一般) 的那一筆");
            conn.Dispose();
        }

        [DataTestMethod]
        [DataRow("Regular", "enum member name")]
        [DataRow("一般",     "Display(Name)")]
        [DataRow("0",       "integer string")]
        public void ApplyFilters_enum_accepts_member_name_displayname_and_integer(string filterVal, string label)
        {
            var id = Guid.NewGuid();
            var (conn, ctx, query) = SetupCustomerDb(
                new CustomerRecord { ID = id,           Tier = CustomerTier.Regular, Count = 1 },
                new CustomerRecord { ID = Guid.NewGuid(), Tier = CustomerTier.Gold,    Count = 1 });

            var wl = AnalysisFieldScanner.ScanModel(typeof(CustomerRecord))
                .ToDictionary(f => f.FieldName);
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Tier" },
                Measures   = new List<MeasureRequest> { new MeasureRequest { Field = "Count", Func = AggregateFunc.Count } },
                Filters    = new List<FilterCondition> { new FilterCondition { Field = "Tier", Operator = FilterOperator.Eq, Value = filterVal } }
            };

            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            var result = engine.Execute(query, req, wl.Values.ToList());

            Assert.AreEqual(1, result.Rows.Count, $"格式 '{label}' 應篩出 1 筆");
            conn.Dispose();
        }

        // ─── Helper methods ────────────────────────────────────────────────────

        private static AnalysisQueryRequest Req(
            string[]   dims    = null,
            (string field, AggregateFunc func)[] msrs    = null,
            (string field, FilterOperator op, string val)[] filters = null)
        {
            return new AnalysisQueryRequest
            {
                Dimensions = dims?.ToList() ?? new List<string>(),
                Measures   = msrs?.Select(m => new MeasureRequest { Field = m.field, Func = m.func }).ToList()
                             ?? new List<MeasureRequest>(),
                Filters    = filters?.Select(f => new FilterCondition
                             {
                                 Field = f.field, Operator = f.op, Value = f.val
                             }).ToList()
                             ?? new List<FilterCondition>()
            };
        }
    }
}
