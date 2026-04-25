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
    /// Auto-insight narrative generator — turns aggregated data into
    /// 2-5 short Chinese sentences. Per-heuristic coverage: top
    /// performer, bottom performer, period-over-period leaders,
    /// Pareto concentration, z-score outliers; safe degradation on
    /// empty / single-row / null measures.
    /// </summary>
    [TestClass]
    public class AnalysisInsightsTests
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
            _whitelist = AnalysisFieldScanner.ScanModel(typeof(Sale));
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
        public void Default_request_leaves_Insights_null()
        {
            _ctx.Sales.Add(new Sale { ID = Guid.NewGuid(), Region = "北", Period = "x", Amount = 100m });
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
            }, _whitelist);

            Assert.IsNull(resp.Insights, "Backwards-compat: default request must leave Insights null.");
        }

        // ── Top performer + average ratio ───────────────────────────────

        [TestMethod]
        public void Top_performer_line_emitted_with_ratio_to_average()
        {
            // Group sums: 北 1000, 南 200, 東 100. avg = 433.33; 北 ratio = 2.31x
            _ctx.Sales.AddRange(
                new Sale { ID = Guid.NewGuid(), Region = "北", Period = "x", Amount = 1000m },
                new Sale { ID = Guid.NewGuid(), Region = "南", Period = "x", Amount = 200m },
                new Sale { ID = Guid.NewGuid(), Region = "東", Period = "x", Amount = 100m }
            );
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeInsights = true,
            }, _whitelist);

            Assert.IsNotNull(resp.Insights);
            var topLine = resp.Insights.FirstOrDefault(s => s.StartsWith("本期最高"));
            Assert.IsNotNull(topLine);
            StringAssert.Contains(topLine, "北");
            StringAssert.Contains(topLine, "1,000");
            StringAssert.Contains(topLine, "倍");
        }

        // ── Bottom performer ────────────────────────────────────────────

        [TestMethod]
        public void Bottom_performer_line_emitted_when_at_least_two_groups()
        {
            _ctx.Sales.AddRange(
                new Sale { ID = Guid.NewGuid(), Region = "北", Period = "x", Amount = 500m },
                new Sale { ID = Guid.NewGuid(), Region = "南", Period = "x", Amount = 100m }
            );
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeInsights = true,
            }, _whitelist);

            var bottomLine = resp.Insights.FirstOrDefault(s => s.StartsWith("本期最低"));
            Assert.IsNotNull(bottomLine);
            StringAssert.Contains(bottomLine, "南");
            StringAssert.Contains(bottomLine, "100");
        }

        [TestMethod]
        public void Single_group_skips_bottom_line_to_avoid_duplicating_top()
        {
            _ctx.Sales.Add(new Sale { ID = Guid.NewGuid(), Region = "北", Period = "x", Amount = 100m });
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeInsights = true,
            }, _whitelist);

            Assert.IsFalse(resp.Insights.Any(s => s.StartsWith("本期最低")),
                "With a single group, top and bottom would be identical — skip bottom.");
        }

        // ── Period-over-period leaders ──────────────────────────────────

        [TestMethod]
        public void Comparison_leaders_line_emitted_when_CompareWith_set()
        {
            _ctx.Sales.AddRange(
                new Sale { ID = Guid.NewGuid(), Region = "北", Period = "this", Amount = 500m },
                new Sale { ID = Guid.NewGuid(), Region = "南", Period = "this", Amount = 200m },
                new Sale { ID = Guid.NewGuid(), Region = "北", Period = "last", Amount = 400m }, // +25%
                new Sale { ID = Guid.NewGuid(), Region = "南", Period = "last", Amount = 250m }  // -20%
            );
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                Filters = new() { new FilterCondition { Field = "Period", Operator = FilterOperator.Eq, Value = "this" } },
                CompareWith = new ComparisonRequest
                {
                    Filters = new() { new FilterCondition { Field = "Period", Operator = FilterOperator.Eq, Value = "last" } },
                },
                IncludeInsights = true,
            }, _whitelist);

            var compareLine = resp.Insights.FirstOrDefault(s => s.Contains("與對比期相比"));
            Assert.IsNotNull(compareLine);
            StringAssert.Contains(compareLine, "北");
            StringAssert.Contains(compareLine, "+25.0%");
            StringAssert.Contains(compareLine, "南");
            StringAssert.Contains(compareLine, "-20.0%");
        }

        [TestMethod]
        public void Comparison_line_skipped_when_no_CompareWith()
        {
            _ctx.Sales.Add(new Sale { ID = Guid.NewGuid(), Region = "北", Period = "x", Amount = 100m });
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeInsights = true,
            }, _whitelist);

            Assert.IsFalse(resp.Insights.Any(s => s.Contains("與對比期相比")));
        }

        // ── Pareto concentration ────────────────────────────────────────

        [TestMethod]
        public void Pareto_line_emitted_when_top_20pct_carry_at_least_80pct()
        {
            // 5 groups, top one carries 80%+ of total → Pareto should fire.
            _ctx.Sales.AddRange(
                new Sale { ID = Guid.NewGuid(), Region = "Big",  Period = "x", Amount = 9000m },
                new Sale { ID = Guid.NewGuid(), Region = "Two",  Period = "x", Amount = 250m },
                new Sale { ID = Guid.NewGuid(), Region = "Three",Period = "x", Amount = 250m },
                new Sale { ID = Guid.NewGuid(), Region = "Four", Period = "x", Amount = 250m },
                new Sale { ID = Guid.NewGuid(), Region = "Five", Period = "x", Amount = 250m }
            );
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeInsights = true,
            }, _whitelist);

            var paretoLine = resp.Insights.FirstOrDefault(s => s.Contains("Pareto"));
            Assert.IsNotNull(paretoLine);
        }

        [TestMethod]
        public void Pareto_line_skipped_when_distribution_is_even()
        {
            // 5 groups, evenly distributed → Pareto must not fire.
            _ctx.Sales.AddRange(
                new Sale { ID = Guid.NewGuid(), Region = "A", Period = "x", Amount = 100m },
                new Sale { ID = Guid.NewGuid(), Region = "B", Period = "x", Amount = 100m },
                new Sale { ID = Guid.NewGuid(), Region = "C", Period = "x", Amount = 100m },
                new Sale { ID = Guid.NewGuid(), Region = "D", Period = "x", Amount = 100m },
                new Sale { ID = Guid.NewGuid(), Region = "E", Period = "x", Amount = 100m }
            );
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeInsights = true,
            }, _whitelist);

            Assert.IsFalse(resp.Insights.Any(s => s.Contains("Pareto")));
        }

        [TestMethod]
        public void Pareto_skipped_for_small_N_groups()
        {
            // Only 3 groups — Pareto math is not meaningful.
            _ctx.Sales.AddRange(
                new Sale { ID = Guid.NewGuid(), Region = "Big", Period = "x", Amount = 9000m },
                new Sale { ID = Guid.NewGuid(), Region = "Two", Period = "x", Amount = 100m },
                new Sale { ID = Guid.NewGuid(), Region = "Three", Period = "x", Amount = 100m }
            );
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeInsights = true,
            }, _whitelist);

            Assert.IsFalse(resp.Insights.Any(s => s.Contains("Pareto")),
                "Pareto requires at least 5 groups to be statistically meaningful.");
        }

        // ── Outliers ────────────────────────────────────────────────────

        [TestMethod]
        public void Outlier_line_emitted_when_zScore_exceeds_two()
        {
            // 9 ordinary groups around 100 + 1 outlier at 10000.
            // With N = 10, the maximum achievable z for a lone outlier
            // is sqrt(N-1) ≈ 3.0, comfortably above the threshold.
            _ctx.Sales.AddRange(
                new Sale { ID = Guid.NewGuid(), Region = "A", Period = "x", Amount = 100m },
                new Sale { ID = Guid.NewGuid(), Region = "B", Period = "x", Amount = 110m },
                new Sale { ID = Guid.NewGuid(), Region = "C", Period = "x", Amount = 90m },
                new Sale { ID = Guid.NewGuid(), Region = "D", Period = "x", Amount = 105m },
                new Sale { ID = Guid.NewGuid(), Region = "E", Period = "x", Amount = 95m },
                new Sale { ID = Guid.NewGuid(), Region = "F", Period = "x", Amount = 102m },
                new Sale { ID = Guid.NewGuid(), Region = "G", Period = "x", Amount = 98m },
                new Sale { ID = Guid.NewGuid(), Region = "H", Period = "x", Amount = 100m },
                new Sale { ID = Guid.NewGuid(), Region = "I", Period = "x", Amount = 100m },
                new Sale { ID = Guid.NewGuid(), Region = "Outlier", Period = "x", Amount = 10000m }
            );
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeInsights = true,
            }, _whitelist);

            var outlierLine = resp.Insights.FirstOrDefault(s => s.Contains("離群值"));
            Assert.IsNotNull(outlierLine);
            StringAssert.Contains(outlierLine, "Outlier");
        }

        [TestMethod]
        public void Outlier_line_skipped_for_uniform_data()
        {
            _ctx.Sales.AddRange(
                new Sale { ID = Guid.NewGuid(), Region = "A", Period = "x", Amount = 100m },
                new Sale { ID = Guid.NewGuid(), Region = "B", Period = "x", Amount = 105m },
                new Sale { ID = Guid.NewGuid(), Region = "C", Period = "x", Amount = 95m },
                new Sale { ID = Guid.NewGuid(), Region = "D", Period = "x", Amount = 100m }
            );
            _ctx.SaveChanges();

            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeInsights = true,
            }, _whitelist);

            Assert.IsFalse(resp.Insights.Any(s => s.Contains("離群值")));
        }

        // ── Edge: empty rows ────────────────────────────────────────────

        [TestMethod]
        public void Empty_result_emits_empty_Insights_list()
        {
            // No data → no insights, but the property should still be a
            // non-null empty list so the UI can iterate without a null check.
            var resp = Engine().Execute(_ctx.Sales, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                IncludeInsights = true,
            }, _whitelist);

            Assert.IsNotNull(resp.Insights);
            Assert.AreEqual(0, resp.Insights.Count);
        }
    }
}
