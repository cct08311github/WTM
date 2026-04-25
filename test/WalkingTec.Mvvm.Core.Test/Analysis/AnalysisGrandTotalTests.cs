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
    /// Tests for AnalysisQueryRequest.IncludeGrandTotal /
    /// AnalysisQueryResponse.GrandTotalRow. Covers per-aggregate
    /// rules (Sum/Count/Max/Min compute, Avg/DistinctCount null),
    /// dimension cells null, post-HAVING / pre-TopN scope, and
    /// default-disabled behaviour.
    /// </summary>
    [TestClass]
    public class AnalysisGrandTotalTests
    {
        private class Record : TopBasePoco
        {
            [Dimension(DisplayName = "地區")] public string Region { get; set; }

            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count
                                  | AggregateFunc.Max | AggregateFunc.Min
                                  | AggregateFunc.Avg | AggregateFunc.DistinctCount,
                     DisplayName = "金額")]
            public decimal Amount { get; set; }
        }

        private class TestCtx : DbContext
        {
            public TestCtx(DbContextOptions opts) : base(opts) { }
            public DbSet<Record> Records { get; set; }
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

            _ctx.Records.AddRange(
                new Record { ID = Guid.NewGuid(), Region = "北", Amount = 100m },
                new Record { ID = Guid.NewGuid(), Region = "北", Amount = 200m },
                new Record { ID = Guid.NewGuid(), Region = "南", Amount = 300m },
                new Record { ID = Guid.NewGuid(), Region = "東", Amount = 400m }
            );
            _ctx.SaveChanges();
            _whitelist = AnalysisFieldScanner.ScanModel(typeof(Record));
        }

        [TestCleanup]
        public void Cleanup()
        {
            _ctx?.Dispose();
            _conn?.Dispose();
        }

        private AnalysisQueryEngine Engine() => new(GroupByStrategyResolver.Default);

        // ── Default off ─────────────────────────────────────────────────

        [TestMethod]
        public void IncludeGrandTotal_default_false_leaves_GrandTotalRow_null()
        {
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
            }, _whitelist);

            Assert.IsNull(resp.GrandTotalRow,
                "Default request must leave GrandTotalRow null — backwards compatible.");
        }

        // ── Sum / Count totals ──────────────────────────────────────────

        [TestMethod]
        public void Sum_grand_total_is_sum_of_group_sums()
        {
            // Group sums: 北 300, 南 300, 東 400. Grand = 1000.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeGrandTotal = true,
            }, _whitelist);

            Assert.IsNotNull(resp.GrandTotalRow);
            Assert.AreEqual(1000m, (decimal?)resp.GrandTotalRow!["Amount_Sum"]);
            Assert.IsNull(resp.GrandTotalRow["Region"], "Dimension cell must be null in the grand total row.");
        }

        [TestMethod]
        public void Count_grand_total_is_sum_of_group_counts()
        {
            // Group counts: 北=2, 南=1, 東=1. Grand = 4.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Count } },
                IncludeGrandTotal = true,
            }, _whitelist);

            Assert.AreEqual(4m, (decimal?)resp.GrandTotalRow!["Amount_Count"]);
        }

        // ── Max / Min totals ────────────────────────────────────────────

        [TestMethod]
        public void Max_grand_total_is_max_across_groups()
        {
            // Group maxes: 北 200, 南 300, 東 400. Grand max = 400.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Max } },
                IncludeGrandTotal = true,
            }, _whitelist);

            Assert.AreEqual(400m, (decimal?)resp.GrandTotalRow!["Amount_Max"]);
        }

        [TestMethod]
        public void Min_grand_total_is_min_across_groups()
        {
            // Group mins: 北 100, 南 300, 東 400. Grand min = 100.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Min } },
                IncludeGrandTotal = true,
            }, _whitelist);

            Assert.AreEqual(100m, (decimal?)resp.GrandTotalRow!["Amount_Min"]);
        }

        // ── Avg / DistinctCount intentionally null ──────────────────────

        [TestMethod]
        public void Avg_grand_total_is_null_documented_limitation()
        {
            // A meaningful weighted Avg requires per-group sample counts
            // which the GroupBy result doesn't carry; emit null rather
            // than silently lie with "average of group averages".
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Avg } },
                IncludeGrandTotal = true,
            }, _whitelist);

            Assert.IsTrue(resp.GrandTotalRow!.ContainsKey("Amount_Avg"));
            Assert.IsNull(resp.GrandTotalRow["Amount_Avg"]);
        }

        [TestMethod]
        public void DistinctCount_grand_total_is_null_documented_limitation()
        {
            // Sum of per-group distincts is wrong when the same value
            // appears across groups — would need a re-query of raw data.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.DistinctCount } },
                IncludeGrandTotal = true,
            }, _whitelist);

            Assert.IsTrue(resp.GrandTotalRow!.ContainsKey("Amount_DistinctCount"));
            Assert.IsNull(resp.GrandTotalRow["Amount_DistinctCount"]);
        }

        // ── Multiple measures ───────────────────────────────────────────

        [TestMethod]
        public void Multiple_measures_each_get_their_own_total_cell()
        {
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new()
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Max },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Min },
                },
                IncludeGrandTotal = true,
            }, _whitelist);

            Assert.AreEqual(1000m, (decimal?)resp.GrandTotalRow!["Amount_Sum"]);
            Assert.AreEqual(400m,  (decimal?)resp.GrandTotalRow!["Amount_Max"]);
            Assert.AreEqual(100m,  (decimal?)resp.GrandTotalRow!["Amount_Min"]);
        }

        // ── Pipeline integration ────────────────────────────────────────

        [TestMethod]
        public void Grand_total_covers_post_HAVING_universe_not_pre_HAVING()
        {
            // Group sums: 北 300, 南 300, 東 400.
            // HAVING Amount_Sum > 300 keeps only 東 → grand = 400, NOT 1000.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Gt, Value = "300" },
                },
                IncludeGrandTotal = true,
            }, _whitelist);

            Assert.AreEqual(400m, (decimal?)resp.GrandTotalRow!["Amount_Sum"],
                "Grand total must reflect post-HAVING groups (just 東 = 400), not pre-HAVING (1000).");
            Assert.AreEqual(1, resp.TotalCount,
                "TotalCount and grand-total scope must be the same universe.");
        }

        [TestMethod]
        public void TopN_does_not_shrink_grand_total_scope()
        {
            // 3 groups (北=300, 南=300, 東=400); TopN=1 keeps just 東 400 in Rows.
            // But grand total covers all 3 groups → 1000.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                Sort = new()
                {
                    new SortSpec { Field = "Amount_Sum", Descending = true },
                },
                TopN = 1,
                IncludeGrandTotal = true,
            }, _whitelist);

            Assert.AreEqual(1, resp.Rows.Count);
            Assert.AreEqual(3, resp.TotalCount);
            Assert.AreEqual(1000m, (decimal?)resp.GrandTotalRow!["Amount_Sum"],
                "TopN trims visible rows, but grand total covers the full pre-TopN universe.");
        }

        // ── Edge cases ──────────────────────────────────────────────────

        [TestMethod]
        public void Empty_result_emits_grand_total_with_null_measure_cells()
        {
            // Filter such that no group passes HAVING → empty Rows.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Gt, Value = "9999999" },
                },
                IncludeGrandTotal = true,
            }, _whitelist);

            Assert.AreEqual(0, resp.Rows.Count);
            Assert.IsNotNull(resp.GrandTotalRow,
                "Grand total row should still be present so the client can render an empty totals footer.");
            Assert.IsNull(resp.GrandTotalRow!["Amount_Sum"],
                "Empty universe → null Sum (rather than 0, which would be misleading).");
        }
    }
}
