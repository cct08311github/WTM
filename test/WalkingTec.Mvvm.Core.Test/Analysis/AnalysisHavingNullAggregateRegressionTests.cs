#nullable enable
using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// Regression for Issue #381 fix #10: the HAVING null/missing-aggregate branch
    /// previously did an unconditional <c>break</c>, skipping all subsequent
    /// HAVING filters when the first filter on a null/absent field had operator
    /// NotEq (which passes). The fix changes that path to <c>continue</c>.
    /// </summary>
    [TestClass]
    public class AnalysisHavingNullAggregateRegressionTests
    {
        private class SalesRecord : TopBasePoco
        {
            [Dimension(DisplayName = "Region")] public string Region { get; set; } = "";
            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "Amount")]
            public decimal Amount { get; set; }
        }

        private class SalesCtx : DbContext
        {
            public SalesCtx(DbContextOptions opts) : base(opts) { }
            public DbSet<SalesRecord> Records { get; set; } = null!;
        }

        private SqliteConnection _conn = null!;
        private SalesCtx _ctx = null!;
        private System.Collections.Generic.IEnumerable<AnalysisFieldMeta> _whitelist = null!;

        [TestInitialize]
        public void Setup()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            var opts = new DbContextOptionsBuilder<SalesCtx>().UseSqlite(_conn).Options;
            _ctx = new SalesCtx(opts);
            _ctx.Database.EnsureCreated();
            _whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesRecord));
        }

        [TestCleanup]
        public void Cleanup()
        {
            _ctx?.Dispose();
            _conn?.Dispose();
        }

        private static AnalysisQueryEngine Engine() => new(GroupByStrategyResolver.Default);

        /// <summary>
        /// Two HAVING filters: filter1 is NotEq (passes for all groups, should
        /// <c>continue</c> not <c>break</c>); filter2 is Lt (rejects some groups).
        ///
        /// Pre-fix: 3 rows returned (filter2 was skipped after filter1's break).
        /// Post-fix: 1 row returned (filter2 correctly executes after filter1).
        /// </summary>
        [TestMethod]
        public void NotEq_PassThrough_Does_Not_Skip_Subsequent_Filters()
        {
            _ctx.Records.AddRange(
                new SalesRecord { ID = Guid.NewGuid(), Region = "A", Amount = 100m },
                new SalesRecord { ID = Guid.NewGuid(), Region = "B", Amount = 200m },
                new SalesRecord { ID = Guid.NewGuid(), Region = "C", Amount = 300m }
            );
            _ctx.SaveChanges();

            // filter1: Amount_Sum != 999  → all three pass (none is 999; should continue to filter2)
            // filter2: Amount_Sum <  200  → only A(100) passes
            // Pre-fix result: 3 rows (break after filter1 skipped filter2)
            // Post-fix result: 1 row  (continue lets filter2 execute)
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions    = new() { "Region" },
                Measures      = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.NotEq, Value = "999" },
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Lt,    Value = "200" },
                },
            }, _whitelist);

            Assert.AreEqual(1, resp.Rows.Count,
                "Second HAVING filter (Lt 200) must execute after a passing NotEq; " +
                "only group A (100) satisfies both conditions.");
            Assert.AreEqual("A", (string)resp.Rows[0]["Region"]);
        }

        /// <summary>
        /// Verifies that a NotEq filter that itself REJECTS a row (because the
        /// aggregate equals the filter value) still causes the row to be excluded.
        /// </summary>
        [TestMethod]
        public void NotEq_That_Rejects_Row_Excludes_Row()
        {
            _ctx.Records.AddRange(
                new SalesRecord { ID = Guid.NewGuid(), Region = "A", Amount = 100m },
                new SalesRecord { ID = Guid.NewGuid(), Region = "B", Amount = 200m }
            );
            _ctx.SaveChanges();

            // Amount_Sum == 100 → NotEq 100 rejects row A (100 == 100 means "not not-equal")
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions    = new() { "Region" },
                Measures      = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.NotEq, Value = "100" },
                },
            }, _whitelist);

            Assert.AreEqual(1, resp.Rows.Count, "Only B (200) should remain; A (100) == 100 fails NotEq 100.");
            Assert.AreEqual("B", (string)resp.Rows[0]["Region"]);
        }

        /// <summary>
        /// Three chained filters where all must apply — verifies the continue
        /// path works for multiple successive filters after a NotEq pass.
        /// </summary>
        [TestMethod]
        public void Three_Chained_Filters_All_Evaluate()
        {
            _ctx.Records.AddRange(
                new SalesRecord { ID = Guid.NewGuid(), Region = "A", Amount = 100m },
                new SalesRecord { ID = Guid.NewGuid(), Region = "B", Amount = 250m },
                new SalesRecord { ID = Guid.NewGuid(), Region = "C", Amount = 500m }
            );
            _ctx.SaveChanges();

            // filter1: Amount_Sum != 999 → all pass (continue)
            // filter2: Amount_Sum >=  100 → all pass (continue)
            // filter3: Amount_Sum <   300 → A(100) and B(250) pass; C(500) fails
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions    = new() { "Region" },
                Measures      = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.NotEq, Value = "999" },
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Gte,   Value = "100" },
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Lt,    Value = "300" },
                },
            }, _whitelist);

            Assert.AreEqual(2, resp.Rows.Count,
                "All three filters must evaluate; A(100) and B(250) pass, C(500) is excluded.");
        }
    }
}
