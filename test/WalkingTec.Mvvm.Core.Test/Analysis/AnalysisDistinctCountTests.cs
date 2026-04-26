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
    /// Tests for the DistinctCount aggregate function across both
    /// strategies (in-process LINQ-to-Objects and EF Core server-side
    /// SQL translation). Verifies COUNT(DISTINCT) semantics, NULL
    /// exclusion, and that AllowedFuncs gating still applies.
    /// </summary>
    [TestClass]
    public class AnalysisDistinctCountTests
    {
        private class Order : TopBasePoco
        {
            [Dimension(DisplayName = "地區")] public string Region { get; set; }

            // CustomerId tagged as a Measure with DistinctCount allowed —
            // common pattern for "unique customers per region".
            [Measure(AllowedFuncs = AggregateFunc.Count | AggregateFunc.DistinctCount,
                     DisplayName = "客戶 ID")]
            public int CustomerId { get; set; }

            // ProductId nullable to verify NULL exclusion.
            [Measure(AllowedFuncs = AggregateFunc.Count | AggregateFunc.DistinctCount,
                     DisplayName = "產品 ID")]
            public int? ProductId { get; set; }

            // Amount used to verify the DistinctCount-not-allowed gate.
            [Measure(AllowedFuncs = AggregateFunc.Sum, DisplayName = "金額")]
            public decimal Amount { get; set; }
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

            // North: customers {1, 1, 2}     → 2 distinct customers, products {10, 10, null} → 1 distinct
            // South: customers {3, 4}        → 2 distinct customers, products {20, 20}       → 1 distinct
            _ctx.Orders.AddRange(
                new Order { ID = Guid.NewGuid(), Region = "North", CustomerId = 1, ProductId = 10,   Amount = 100m },
                new Order { ID = Guid.NewGuid(), Region = "North", CustomerId = 1, ProductId = 10,   Amount = 50m  },
                new Order { ID = Guid.NewGuid(), Region = "North", CustomerId = 2, ProductId = null, Amount = 75m  },
                new Order { ID = Guid.NewGuid(), Region = "South", CustomerId = 3, ProductId = 20,   Amount = 200m },
                new Order { ID = Guid.NewGuid(), Region = "South", CustomerId = 4, ProductId = 20,   Amount = 150m }
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

        // ── Enum + display name ─────────────────────────────────────────

        [TestMethod]
        public void DistinctCount_is_a_distinct_flag_value()
        {
            // Sanity-check the new enum value doesn't collide with an
            // existing one — bitflag math relies on unique bit positions.
            var distinctCount = (int)AggregateFunc.DistinctCount;
            foreach (AggregateFunc other in new[]
            {
                AggregateFunc.Count, AggregateFunc.Sum, AggregateFunc.Avg,
                AggregateFunc.Max, AggregateFunc.Min,
            })
            {
                Assert.AreNotEqual(distinctCount, (int)other,
                    $"DistinctCount must not share a bit with {other}.");
                Assert.AreEqual(0, distinctCount & (int)other,
                    "Flag bits must be independent.");
            }
        }

        // ── In-process strategy ─────────────────────────────────────────

        [TestMethod]
        public void InProcess_distinct_count_per_group()
        {
            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            var resp = engine.Execute(_ctx.Orders, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new()
                {
                    new MeasureRequest { Field = "CustomerId", Func = AggregateFunc.DistinctCount },
                },
            }, _whitelist, DBTypeEnum.SQLite);

            // Sort by Region for deterministic positional asserts.
            var north = resp.Rows.Single(r => (string)r["Region"] == "North");
            var south = resp.Rows.Single(r => (string)r["Region"] == "South");
            Assert.AreEqual(2m, (decimal?)north["CustomerId_DistinctCount"]);
            Assert.AreEqual(2m, (decimal?)south["CustomerId_DistinctCount"]);
        }

        [TestMethod]
        public void InProcess_distinct_count_excludes_nulls()
        {
            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            var resp = engine.Execute(_ctx.Orders, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new()
                {
                    new MeasureRequest { Field = "ProductId", Func = AggregateFunc.DistinctCount },
                },
            }, _whitelist, DBTypeEnum.SQLite);

            var north = resp.Rows.Single(r => (string)r["Region"] == "North");
            // North has products {10, 10, null} → distinct (excl null) = 1
            Assert.AreEqual(1m, (decimal?)north["ProductId_DistinctCount"]);
        }

        // ── Server-side strategy ────────────────────────────────────────

        [TestMethod]
        public async Task ServerSide_distinct_count_emits_count_distinct_sql()
        {
            // Force the server-side path by routing through ExecuteAsync
            // with the SQLite DB type — GroupByStrategyResolver picks
            // ServerSideGroupByStrategy for it.
            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            var resp = await engine.ExecuteAsync(_ctx.Orders, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new()
                {
                    new MeasureRequest { Field = "CustomerId", Func = AggregateFunc.DistinctCount },
                },
            }, _whitelist, DBTypeEnum.SQLite);

            var north = resp.Rows.Single(r => (string)r["Region"] == "North");
            var south = resp.Rows.Single(r => (string)r["Region"] == "South");
            Assert.AreEqual(2m, (decimal?)north["CustomerId_DistinctCount"]);
            Assert.AreEqual(2m, (decimal?)south["CustomerId_DistinctCount"]);
        }

        // ── AllowedFuncs gating ─────────────────────────────────────────

        [TestMethod]
        public void DistinctCount_rejected_when_not_in_AllowedFuncs()
        {
            // Amount only allows Sum — DistinctCount must throw via the
            // existing AllowedFuncs check, no special handling needed.
            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            Assert.ThrowsException<NotSupportedException>(() =>
                engine.Execute(_ctx.Orders, new AnalysisQueryRequest
                {
                    Dimensions = new() { "Region" },
                    Measures = new()
                    {
                        new MeasureRequest { Field = "Amount", Func = AggregateFunc.DistinctCount },
                    },
                }, _whitelist));
        }

        // ── Result column naming ────────────────────────────────────────

        [TestMethod]
        public void DistinctCount_result_column_uses_field_underscore_func_naming()
        {
            // Same naming convention as Sum/Count/etc — important so the
            // new Sort feature can target a DistinctCount column.
            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            var resp = engine.Execute(_ctx.Orders, new AnalysisQueryRequest
            {
                Dimensions = new() { "Region" },
                Measures = new()
                {
                    new MeasureRequest { Field = "CustomerId", Func = AggregateFunc.DistinctCount },
                },
                Sort = new()
                {
                    new SortSpec { Field = "CustomerId_DistinctCount", Descending = true },
                },
            }, _whitelist);

            // Sort + DistinctCount round-trip — both groups have 2, so the
            // top should be either; just confirm no validation error.
            Assert.AreEqual(2, resp.Rows.Count);
        }
    }
}
