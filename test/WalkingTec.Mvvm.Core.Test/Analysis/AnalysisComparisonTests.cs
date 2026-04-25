#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// Period-over-period comparison mode — the killer dashboard
    /// feature where every measure column is shadowed by Compare /
    /// Delta / ChangePct columns derived from a second filter set.
    /// Covers: derived-column emission, dimension-tuple join, "compare-
    /// only" rows surfaced, divide-by-zero guard, Sort by derived
    /// column, async parity, ColumnDisplayNames, GrandTotal coexistence.
    /// </summary>
    [TestClass]
    public class AnalysisComparisonTests
    {
        private class Sale : TopBasePoco
        {
            [Dimension(DisplayName = "地區")] public string Region { get; set; }
            [Dimension(DisplayName = "期間")] public string Period { get; set; }
            [Measure(AllowedFuncs = AggregateFunc.Sum, DisplayName = "金額")]
            public decimal Amount { get; set; }
        }

        private class TestCtx : DbContext
        {
            public TestCtx(DbContextOptions opts) : base(opts) { }
            public DbSet<Sale> Sales { get; set; }
        }

        private SqliteConnection _conn;
        private TestCtx _ctx;
        private IEnumerable<AnalysisFieldMeta> _whitelist;

        [TestInitialize]
        public void Setup()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            var opts = new DbContextOptionsBuilder<TestCtx>().UseSqlite(_conn).Options;
            _ctx = new TestCtx(opts);
            _ctx.Database.EnsureCreated();

            // Period 'this': 北 500, 南 300, 東 200
            // Period 'last': 北 400, 南 350,        西 100  (no 東; new 西)
            _ctx.Sales.AddRange(
                new Sale { ID = Guid.NewGuid(), Region = "北", Period = "this", Amount = 500m },
                new Sale { ID = Guid.NewGuid(), Region = "南", Period = "this", Amount = 300m },
                new Sale { ID = Guid.NewGuid(), Region = "東", Period = "this", Amount = 200m },
                new Sale { ID = Guid.NewGuid(), Region = "北", Period = "last", Amount = 400m },
                new Sale { ID = Guid.NewGuid(), Region = "南", Period = "last", Amount = 350m },
                new Sale { ID = Guid.NewGuid(), Region = "西", Period = "last", Amount = 100m }
            );
            _ctx.SaveChanges();

            _whitelist = AnalysisFieldScanner.ScanModel(typeof(Sale));
        }

        [TestCleanup]
        public void Cleanup()
        {
            _ctx?.Dispose();
            _conn?.Dispose();
        }

        private AnalysisQueryEngine Engine() => new(GroupByStrategyResolver.Default);

        private static AnalysisQueryRequest BaseReq()
            => new()
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                Filters = new() { new FilterCondition { Field = "Period", Operator = FilterOperator.Eq, Value = "this" } },
                CompareWith = new ComparisonRequest
                {
                    Filters = new() { new FilterCondition { Field = "Period", Operator = FilterOperator.Eq, Value = "last" } },
                    Label = "上期",
                },
            };

        // ── Derived columns appear ──────────────────────────────────────

        [TestMethod]
        public void Comparison_emits_three_derived_columns_per_measure()
        {
            var resp = Engine().Execute(_ctx.Sales, BaseReq(), _whitelist);

            CollectionAssert.Contains(resp.Columns, "Amount_Sum");
            CollectionAssert.Contains(resp.Columns, "Amount_Sum_Compare");
            CollectionAssert.Contains(resp.Columns, "Amount_Sum_Delta");
            CollectionAssert.Contains(resp.Columns, "Amount_Sum_ChangePct");
        }

        // ── Join semantics ──────────────────────────────────────────────

        [TestMethod]
        public void Matched_dimensions_carry_correct_delta_and_changePct()
        {
            var resp = Engine().Execute(_ctx.Sales, BaseReq(), _whitelist);
            var north = resp.Rows.Single(r => (string)r["Region"] == "北");

            Assert.AreEqual(500m, (decimal?)north["Amount_Sum"]);
            Assert.AreEqual(400m, (decimal?)north["Amount_Sum_Compare"]);
            Assert.AreEqual(100m, (decimal?)north["Amount_Sum_Delta"]);
            Assert.AreEqual(0.25m, (decimal?)north["Amount_Sum_ChangePct"]); // (500-400)/400
        }

        [TestMethod]
        public void Compare_only_rows_appear_with_null_primary_values()
        {
            // 西 exists in last but not this → must appear with Amount_Sum=null,
            // _Compare=100. Otherwise users miss "regions that lost all sales".
            var resp = Engine().Execute(_ctx.Sales, BaseReq(), _whitelist);
            var west = resp.Rows.SingleOrDefault(r => (string)r["Region"] == "西");

            Assert.IsNotNull(west, "Compare-only group must surface in result.");
            Assert.IsNull(west["Amount_Sum"]);
            Assert.AreEqual(100m, (decimal?)west["Amount_Sum_Compare"]);
        }

        [TestMethod]
        public void Primary_only_rows_have_null_compare_and_delta()
        {
            // 東 exists in this but not last → _Compare = null, Delta + Pct = null.
            var resp = Engine().Execute(_ctx.Sales, BaseReq(), _whitelist);
            var east = resp.Rows.Single(r => (string)r["Region"] == "東");

            Assert.AreEqual(200m, (decimal?)east["Amount_Sum"]);
            Assert.IsNull(east["Amount_Sum_Compare"]);
            Assert.IsNull(east["Amount_Sum_Delta"]);
            Assert.IsNull(east["Amount_Sum_ChangePct"]);
        }

        // ── Divide-by-zero guard ────────────────────────────────────────

        [TestMethod]
        public void Compare_value_zero_yields_null_changePct_not_infinity()
        {
            // Inject a zero-compare scenario via a fresh fixture row.
            _ctx.Sales.Add(new Sale { ID = Guid.NewGuid(), Region = "新區", Period = "this", Amount = 50m });
            _ctx.Sales.Add(new Sale { ID = Guid.NewGuid(), Region = "新區", Period = "last", Amount = 0m });
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, BaseReq(), _whitelist);
            var newRegion = resp.Rows.Single(r => (string)r["Region"] == "新區");

            Assert.AreEqual(50m, (decimal?)newRegion["Amount_Sum_Delta"]);
            Assert.IsNull(newRegion["Amount_Sum_ChangePct"],
                "ChangePct must be null when Compare = 0 (avoid serialising Infinity into JSON).");
        }

        // ── Sort by derived column ──────────────────────────────────────

        [TestMethod]
        public void Sort_by_ChangePct_DESC_is_accepted_and_orders_correctly()
        {
            var req = BaseReq();
            req.Sort = new() { new SortSpec { Field = "Amount_Sum_ChangePct", Descending = true } };

            var resp = Engine().Execute(_ctx.Sales, req, _whitelist);

            // ChangePct values:
            //   北: +25%, 南: ~-14.3%, 東: null (primary-only), 西: null (compare-only)
            // DESC with nulls last → 北 first.
            Assert.AreEqual("北", resp.Rows[0]["Region"]);
        }

        [TestMethod]
        public void Sort_by_compare_or_delta_field_is_accepted()
        {
            // Both _Compare and _Delta must be on the sort whitelist.
            // No throw on validate.
            AnalysisQueryEngine.ValidateSortAndTopN(new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                CompareWith = new ComparisonRequest { Filters = new() },
                Sort = new()
                {
                    new SortSpec { Field = "Amount_Sum_Compare" },
                    new SortSpec { Field = "Amount_Sum_Delta" },
                },
            });
        }

        [TestMethod]
        public void Sort_by_derived_field_without_CompareWith_is_rejected()
        {
            // Without CompareWith, _Compare etc. don't exist — validator
            // must reject so the user gets a clear error instead of a
            // silently empty sort.
            var ex = Assert.ThrowsException<AnalysisException>(() =>
                AnalysisQueryEngine.ValidateSortAndTopN(new AnalysisQueryRequest
                {
                    Dimensions = new() { "Region" },
                    Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                    Sort = new() { new SortSpec { Field = "Amount_Sum_Delta" } },
                }));
            StringAssert.Contains(ex.Message, "Amount_Sum_Delta");
        }

        // ── Display names ───────────────────────────────────────────────

        [TestMethod]
        public void Comparison_columns_get_human_readable_display_names()
        {
            var resp = Engine().Execute(_ctx.Sales, BaseReq(), _whitelist);

            // baseLabel = "金額 合計"; compareLabel = "上期"
            Assert.AreEqual("金額 合計 (上期)", resp.ColumnDisplayNames["Amount_Sum_Compare"]);
            Assert.AreEqual("金額 合計 差值",   resp.ColumnDisplayNames["Amount_Sum_Delta"]);
            Assert.AreEqual("金額 合計 變化%",  resp.ColumnDisplayNames["Amount_Sum_ChangePct"]);
        }

        // ── Async parity ────────────────────────────────────────────────

        [TestMethod]
        public async Task Async_path_emits_same_derived_columns()
        {
            var resp = await Engine().ExecuteAsync(_ctx.Sales, BaseReq(), _whitelist);
            var north = resp.Rows.Single(r => (string)r["Region"] == "北");
            Assert.AreEqual(100m, (decimal?)north["Amount_Sum_Delta"]);
        }

        // ── Default (no comparison) unchanged ───────────────────────────

        [TestMethod]
        public void Without_CompareWith_response_columns_unchanged()
        {
            var req = BaseReq();
            req.CompareWith = null;

            var resp = Engine().Execute(_ctx.Sales, req, _whitelist);

            Assert.IsFalse(resp.Columns.Any(c => c.EndsWith("_Compare")
                                              || c.EndsWith("_Delta")
                                              || c.EndsWith("_ChangePct")),
                "No comparison → no derived columns. Backwards-compat.");
        }

        // ── BuildComparisonSubRequest sanity ────────────────────────────

        [TestMethod]
        public void Comparison_sub_request_drops_recursive_options()
        {
            var primary = new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                Filters = new() { new FilterCondition { Field = "x", Operator = FilterOperator.Eq, Value = "a" } },
                Sort = new() { new SortSpec { Field = "Region" } },
                TopN = 5,
                IncludeGrandTotal = true,
                HavingFilters = new() { new HavingFilter { Field = "Amount_Sum" } },
                CompareWith = new ComparisonRequest
                {
                    Filters = new() { new FilterCondition { Field = "x", Operator = FilterOperator.Eq, Value = "b" } },
                },
            };

            var sub = AnalysisQueryEngine.BuildComparisonSubRequest(primary);

            // Filters replaced with comparison filters.
            Assert.AreEqual(1, sub.Filters.Count);
            Assert.AreEqual("b", sub.Filters[0].Value);

            // Recursive / mismatched-shape options must be cleared.
            Assert.IsNull(sub.Sort);
            Assert.IsNull(sub.TopN);
            Assert.IsFalse(sub.IncludeGrandTotal);
            Assert.IsNull(sub.HavingFilters);
            Assert.IsNull(sub.CompareWith,
                "Sub request must NOT carry CompareWith — would recurse into infinite re-execution.");

            // Dimensions / Measures inherited from primary.
            CollectionAssert.AreEqual(primary.Dimensions, sub.Dimensions.ToList());
        }
    }
}
