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
    /// Tests for AnalysisQueryRequest.HavingFilters: validation rejects
    /// fields not in the requested measures and unsupported operators;
    /// engine filters post-aggregation per SQL HAVING semantics; pipeline
    /// order is GroupBy → HAVING → Sort → TopN; TotalCount reflects the
    /// post-having group cardinality.
    /// </summary>
    [TestClass]
    public class AnalysisHavingFiltersTests
    {
        private class Record : TopBasePoco
        {
            [Dimension(DisplayName = "地區")] public string Region { get; set; }
            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "金額")]
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

            // Five regions with widely-varied totals — easy assertions.
            _ctx.Records.AddRange(
                new Record { ID = Guid.NewGuid(), Region = "華東", Amount = 500m },
                new Record { ID = Guid.NewGuid(), Region = "華南", Amount = 100m },
                new Record { ID = Guid.NewGuid(), Region = "華北", Amount = 300m },
                new Record { ID = Guid.NewGuid(), Region = "西北", Amount = 200m },
                new Record { ID = Guid.NewGuid(), Region = "西南", Amount = 400m }
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

        // ── Pure validation ─────────────────────────────────────────────

        [TestMethod]
        public void Having_field_not_in_measures_throws()
        {
            var ex = Assert.ThrowsException<AnalysisException>(() =>
                AnalysisQueryEngine.ValidateHavingFilters(new AnalysisQueryRequest
                {
                    Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                    HavingFilters = new() { new HavingFilter { Field = "Other_Sum", Value = "0" } },
                }));
            StringAssert.Contains(ex.Message, "'Other_Sum'");
        }

        [TestMethod]
        public void Having_field_must_use_measure_result_naming()
        {
            // Bare measure name 'Amount' is wrong — must be 'Amount_Sum'.
            Assert.ThrowsException<AnalysisException>(() =>
                AnalysisQueryEngine.ValidateHavingFilters(new AnalysisQueryRequest
                {
                    Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                    HavingFilters = new() { new HavingFilter { Field = "Amount", Value = "0" } },
                }));
        }

        [TestMethod]
        public void Having_unsupported_operator_throws()
        {
            foreach (var op in new[]
            {
                FilterOperator.Contains, FilterOperator.NotContains,
                FilterOperator.In, FilterOperator.NotIn,
            })
            {
                Assert.ThrowsException<AnalysisException>(() =>
                    AnalysisQueryEngine.ValidateHavingFilters(new AnalysisQueryRequest
                    {
                        Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                        HavingFilters = new() { new HavingFilter { Field = "Amount_Sum", Operator = op, Value = "0" } },
                    }), $"Operator {op} should be rejected for HAVING.");
            }
        }

        [TestMethod]
        public void Having_supported_operators_validate_silently()
        {
            foreach (var op in new[]
            {
                FilterOperator.Eq, FilterOperator.NotEq,
                FilterOperator.Gt, FilterOperator.Gte,
                FilterOperator.Lt, FilterOperator.Lte,
            })
            {
                AnalysisQueryEngine.ValidateHavingFilters(new AnalysisQueryRequest
                {
                    Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                    HavingFilters = new() { new HavingFilter { Field = "Amount_Sum", Operator = op, Value = "0" } },
                });
            }
        }

        [TestMethod]
        public void Empty_field_throws()
        {
            Assert.ThrowsException<AnalysisException>(() =>
                AnalysisQueryEngine.ValidateHavingFilters(new AnalysisQueryRequest
                {
                    Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                    HavingFilters = new() { new HavingFilter { Field = "", Value = "0" } },
                }));
        }

        [TestMethod]
        public void Null_or_empty_HavingFilters_validates_silently()
        {
            AnalysisQueryEngine.ValidateHavingFilters(new AnalysisQueryRequest());
            AnalysisQueryEngine.ValidateHavingFilters(new AnalysisQueryRequest
            {
                HavingFilters = new(),
            });
        }

        // ── End-to-end: filter via Gte / Lt / Eq / NotEq ────────────────

        [TestMethod]
        public void Gte_filter_keeps_only_groups_above_threshold()
        {
            // Group totals: 華東 500, 西南 400, 華北 300, 西北 200, 華南 100.
            // HAVING Amount_Sum >= 300 keeps {華東, 西南, 華北}.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Gte, Value = "300" },
                },
            }, _whitelist);

            Assert.AreEqual(3, resp.Rows.Count);
            Assert.AreEqual(3, resp.TotalCount,
                "TotalCount must reflect the post-having group cardinality.");

            var regions = resp.Rows.Select(r => (string)r["Region"]).ToHashSet();
            CollectionAssert.AreEquivalent(new[] { "華東", "西南", "華北" }, regions.ToArray());
        }

        [TestMethod]
        public void Lt_filter_keeps_only_groups_below_threshold()
        {
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Lt, Value = "300" },
                },
            }, _whitelist);

            Assert.AreEqual(2, resp.Rows.Count);
            var regions = resp.Rows.Select(r => (string)r["Region"]).ToHashSet();
            CollectionAssert.AreEquivalent(new[] { "西北", "華南" }, regions.ToArray());
        }

        [TestMethod]
        public void Eq_filter_keeps_only_exact_match()
        {
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Eq, Value = "300" },
                },
            }, _whitelist);

            Assert.AreEqual(1, resp.Rows.Count);
            Assert.AreEqual("華北", resp.Rows[0]["Region"]);
        }

        [TestMethod]
        public void Multiple_filters_AND_together()
        {
            // 200 <= Amount_Sum < 500 keeps {華北 300, 西北 200, 西南 400}.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Gte, Value = "200" },
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Lt, Value = "500" },
                },
            }, _whitelist);

            Assert.AreEqual(3, resp.Rows.Count);
            var regions = resp.Rows.Select(r => (string)r["Region"]).ToHashSet();
            CollectionAssert.AreEquivalent(new[] { "華北", "西北", "西南" }, regions.ToArray());
        }

        // ── Pipeline order: HAVING before Sort + TopN ───────────────────

        [TestMethod]
        public void Having_runs_before_sort_and_topN()
        {
            // HAVING keeps {華東 500, 西南 400, 華北 300}, then DESC sort
            // produces 華東 / 西南 / 華北, then TopN=2 keeps top 2.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Gte, Value = "300" },
                },
                Sort = new()
                {
                    new SortSpec { Field = "Amount_Sum", Descending = true },
                },
                TopN = 2,
            }, _whitelist);

            Assert.AreEqual(2, resp.Rows.Count);
            Assert.AreEqual(3, resp.TotalCount,
                "TotalCount must reflect post-HAVING groups (3), not pre-HAVING (5) or post-TopN (2).");
            Assert.AreEqual("華東", resp.Rows[0]["Region"]);
            Assert.AreEqual("西南", resp.Rows[1]["Region"]);
        }

        // ── Edge cases ──────────────────────────────────────────────────

        [TestMethod]
        public void Unparsable_filter_value_filters_out_all_groups()
        {
            // Conservative rejection — a typo in the request body must
            // not silently widen the result.
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                HavingFilters = new()
                {
                    new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Gte, Value = "abc" },
                },
            }, _whitelist);

            Assert.AreEqual(0, resp.Rows.Count);
            Assert.AreEqual(0, resp.TotalCount);
        }

        [TestMethod]
        public void Default_request_without_having_keeps_existing_behaviour()
        {
            var resp = Engine().Execute(_ctx.Records, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new() { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
            }, _whitelist);

            Assert.AreEqual(5, resp.Rows.Count);
            Assert.AreEqual(5, resp.TotalCount);
        }
    }
}
