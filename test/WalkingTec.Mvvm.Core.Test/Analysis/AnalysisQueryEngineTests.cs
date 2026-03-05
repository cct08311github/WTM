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

        private class SaleRecord : TopBasePoco
        {
            [Dimension(DisplayName = "地區")]  public string Region   { get; set; }
            [Dimension(DisplayName = "類別")]  public string Category { get; set; }

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
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "B", Amount = 200m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "A", Amount = 300m }
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

        private static AnalysisQueryEngine Engine() => new AnalysisQueryEngine();

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
            Assert.ThrowsException<InvalidOperationException>(() => Engine().Execute(Q(), req, _whitelist));
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
