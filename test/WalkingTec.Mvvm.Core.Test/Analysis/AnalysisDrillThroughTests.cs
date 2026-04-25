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
    /// Drill-through helper: given an aggregated result row's dimension
    /// values, produce the IQueryable of source rows that aggregated into
    /// that group. Verifies dimension equality, original-filter
    /// preservation, date-hierarchy label round-trip, and graceful
    /// degradation on bad labels.
    /// </summary>
    [TestClass]
    public class AnalysisDrillThroughTests
    {
        private class Order : TopBasePoco
        {
            [Dimension(DisplayName = "地區")] public string Region { get; set; }
            [Dimension(DisplayName = "下單日")] public DateTime OrderDate { get; set; }
            [Measure(AllowedFuncs = AggregateFunc.Sum, DisplayName = "金額")] public decimal Amount { get; set; }
        }

        private class TestCtx : DbContext
        {
            public TestCtx(DbContextOptions opts) : base(opts) { }
            public DbSet<Order> Orders { get; set; }
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

            _ctx.Orders.AddRange(
                new Order { ID = Guid.NewGuid(), Region = "北", OrderDate = new DateTime(2026, 1, 15), Amount = 100m },
                new Order { ID = Guid.NewGuid(), Region = "北", OrderDate = new DateTime(2026, 2, 10), Amount = 200m },
                new Order { ID = Guid.NewGuid(), Region = "北", OrderDate = new DateTime(2026, 4, 5),  Amount = 300m },
                new Order { ID = Guid.NewGuid(), Region = "南", OrderDate = new DateTime(2026, 1, 20), Amount = 50m  },
                new Order { ID = Guid.NewGuid(), Region = "南", OrderDate = new DateTime(2026, 3, 1),  Amount = 80m  }
            );
            _ctx.SaveChanges();

            _whitelist = AnalysisFieldScanner.ScanModel(typeof(Order));
        }

        [TestCleanup]
        public void Cleanup()
        {
            _ctx?.Dispose();
            _conn?.Dispose();
        }

        // ── Dimension equality ──────────────────────────────────────────

        [TestMethod]
        public void Single_dimension_drill_returns_matching_rows()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
            };

            var drill = AnalysisDrillThrough.BuildQuery(_ctx.Orders, req,
                new Dictionary<string, object?> { ["Region"] = "北" },
                _whitelist);

            var rows = drill.ToList();
            Assert.AreEqual(3, rows.Count);
            Assert.IsTrue(rows.All(r => r.Region == "北"));
        }

        [TestMethod]
        public void Original_filters_are_preserved()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                Filters = new() { new FilterCondition { Field = "Amount", Operator = FilterOperator.Gte, Value = "150" } },
            };

            var drill = AnalysisDrillThrough.BuildQuery(_ctx.Orders, req,
                new Dictionary<string, object?> { ["Region"] = "北" },
                _whitelist);

            var rows = drill.ToList();
            Assert.AreEqual(2, rows.Count, "Should keep only Amount >= 150 within 北 → 200 + 300.");
            Assert.IsTrue(rows.All(r => r.Amount >= 150m));
        }

        // ── Date-hierarchy reversal ─────────────────────────────────────

        [TestMethod]
        public void Date_hierarchy_quarter_label_resolves_to_quarter_range()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new() { "OrderDate" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                DimensionHierarchies = new() { ["OrderDate"] = DateHierarchy.Quarter },
            };

            // Click "2026 Q1" → expect rows with OrderDate in Jan/Feb/Mar 2026.
            var drill = AnalysisDrillThrough.BuildQuery(_ctx.Orders, req,
                new Dictionary<string, object?> { ["OrderDate"] = "2026 Q1" },
                _whitelist);

            var rows = drill.ToList();
            Assert.AreEqual(4, rows.Count, "Q1 2026 includes Jan/Feb/Mar; data has 4 such rows.");
            Assert.IsTrue(rows.All(r => r.OrderDate.Year == 2026 && r.OrderDate.Month <= 3));
        }

        [TestMethod]
        public void Date_hierarchy_month_label_resolves_to_month_range()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new() { "OrderDate" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                DimensionHierarchies = new() { ["OrderDate"] = DateHierarchy.Month },
            };

            var drill = AnalysisDrillThrough.BuildQuery(_ctx.Orders, req,
                new Dictionary<string, object?> { ["OrderDate"] = "2026-01" },
                _whitelist);

            var rows = drill.ToList();
            Assert.AreEqual(2, rows.Count, "Jan 2026 has 北 and 南 rows.");
            Assert.IsTrue(rows.All(r => r.OrderDate.Year == 2026 && r.OrderDate.Month == 1));
        }

        [TestMethod]
        public void Date_hierarchy_day_label_resolves_to_single_day()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new() { "OrderDate" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                DimensionHierarchies = new() { ["OrderDate"] = DateHierarchy.Day },
            };

            var drill = AnalysisDrillThrough.BuildQuery(_ctx.Orders, req,
                new Dictionary<string, object?> { ["OrderDate"] = "2026-01-15" },
                _whitelist);

            var rows = drill.ToList();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(new DateTime(2026, 1, 15), rows[0].OrderDate);
        }

        [TestMethod]
        public void Unparseable_date_label_returns_empty_query_not_throw()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new() { "OrderDate" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                DimensionHierarchies = new() { ["OrderDate"] = DateHierarchy.Month },
            };

            var drill = AnalysisDrillThrough.BuildQuery(_ctx.Orders, req,
                new Dictionary<string, object?> { ["OrderDate"] = "garbage" },
                _whitelist);

            // Should be empty (Take(0)) — not unfiltered (which would
            // wrongly return all rows) and not a thrown exception.
            Assert.AreEqual(0, drill.Count());
        }

        // ── Multi-dimension drill ───────────────────────────────────────

        [TestMethod]
        public void Multi_dimension_drill_AND_combines_all()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new() { "Region", "OrderDate" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                DimensionHierarchies = new() { ["OrderDate"] = DateHierarchy.Month },
            };

            // Click "北" + Jan 2026 → only the Jan 北 row.
            var drill = AnalysisDrillThrough.BuildQuery(_ctx.Orders, req,
                new Dictionary<string, object?>
                {
                    ["Region"] = "北",
                    ["OrderDate"] = "2026-01",
                },
                _whitelist);

            var rows = drill.ToList();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("北", rows[0].Region);
            Assert.AreEqual(1, rows[0].OrderDate.Month);
        }

        // ── Validation ──────────────────────────────────────────────────

        [TestMethod]
        public void Unknown_dimension_in_request_throws_AnalysisFieldNotFoundException()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new() { "NotARealColumn" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
            };

            Assert.ThrowsException<AnalysisFieldNotFoundException>(() =>
                AnalysisDrillThrough.BuildQuery(_ctx.Orders, req,
                    new Dictionary<string, object?> { ["NotARealColumn"] = "x" },
                    _whitelist));
        }

        [TestMethod]
        public void Missing_value_for_dimension_widens_drill_to_all_values()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
            };

            // No "Region" value supplied → no filter on Region; drill
            // returns all rows (subject to original Filters which there
            // are none here).
            var drill = AnalysisDrillThrough.BuildQuery(_ctx.Orders, req,
                new Dictionary<string, object?>(),
                _whitelist);

            Assert.AreEqual(5, drill.Count(), "Empty groupValues → drill widens to entire base set.");
        }

        // ── Null guards ─────────────────────────────────────────────────

        [TestMethod]
        public void Null_arguments_throw_clear_ArgumentNullException()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AnalysisDrillThrough.BuildQuery<Order>(null!, new(), new Dictionary<string, object?>(), _whitelist));
            Assert.ThrowsException<ArgumentNullException>(() =>
                AnalysisDrillThrough.BuildQuery(_ctx.Orders, null!, new Dictionary<string, object?>(), _whitelist));
            Assert.ThrowsException<ArgumentNullException>(() =>
                AnalysisDrillThrough.BuildQuery(_ctx.Orders, new(), null!, _whitelist));
            Assert.ThrowsException<ArgumentNullException>(() =>
                AnalysisDrillThrough.BuildQuery(_ctx.Orders, new(), new Dictionary<string, object?>(), null!));
        }

        // ── DateTruncator.TryParseLabel direct tests ────────────────────

        [TestMethod]
        public void TryParseLabel_year_round_trips_via_FormatKey()
        {
            Assert.IsTrue(DateTruncator.TryParseLabel("2026", DateHierarchy.Year, out var s, out var e));
            Assert.AreEqual(new DateTime(2026, 1, 1), s);
            Assert.AreEqual(new DateTime(2027, 1, 1), e);
        }

        [TestMethod]
        public void TryParseLabel_quarter_round_trips()
        {
            Assert.IsTrue(DateTruncator.TryParseLabel("2026 Q3", DateHierarchy.Quarter, out var s, out var e));
            Assert.AreEqual(new DateTime(2026, 7, 1), s);
            Assert.AreEqual(new DateTime(2026, 10, 1), e);
        }

        [TestMethod]
        public void TryParseLabel_invalid_returns_false_not_throw()
        {
            Assert.IsFalse(DateTruncator.TryParseLabel("not-a-year", DateHierarchy.Year, out _, out _));
            Assert.IsFalse(DateTruncator.TryParseLabel("2026 Q9", DateHierarchy.Quarter, out _, out _),
                "Q9 is out of range; must reject without throwing.");
            Assert.IsFalse(DateTruncator.TryParseLabel("", DateHierarchy.Day, out _, out _));
            Assert.IsFalse(DateTruncator.TryParseLabel(null, DateHierarchy.Year, out _, out _));
            Assert.IsFalse(DateTruncator.TryParseLabel("2026-13", DateHierarchy.Month, out _, out _),
                "Month=13 must reject without throwing ArgumentOutOfRange.");
        }
    }
}
