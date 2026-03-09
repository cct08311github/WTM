#nullable enable
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
    [TestClass]
    public class ServerSideGroupByStrategyTests
    {
        // ─── 測試模型 ──────────────────────────────────────────────────────────

        private class SaleRecord : TopBasePoco
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

        private class SaleTestContext : DbContext
        {
            public SaleTestContext(DbContextOptions opts) : base(opts) { }
            public DbSet<SaleRecord> SaleRecords { get; set; } = null!;
        }

        // ─── 基礎設施 ──────────────────────────────────────────────────────────

        private SqliteConnection _conn = null!;
        private SaleTestContext _ctx = null!;
        private ServerSideGroupByStrategy _strategy = null!;
        private Dictionary<string, AnalysisFieldMeta> _whitelist = null!;

        [TestInitialize]
        public void Setup()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();

            var opts = new DbContextOptionsBuilder<SaleTestContext>()
                .UseSqlite(_conn).Options;

            _ctx = new SaleTestContext(opts);
            _ctx.Database.EnsureCreated();

            _strategy = new ServerSideGroupByStrategy();
            _whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord))
                .ToDictionary(f => f.FieldName);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _ctx.Dispose();
            _conn.Dispose();
        }

        private IQueryable<SaleRecord> Q() => _ctx.SaleRecords.AsQueryable();

        private static AnalysisQueryRequest Req(
            string[]? dims = null,
            (string field, AggregateFunc func)[]? msrs = null)
        {
            return new AnalysisQueryRequest
            {
                Dimensions = dims?.ToList() ?? new List<string>(),
                Measures = msrs?.Select(m => new MeasureRequest { Field = m.field, Func = m.func }).ToList()
                           ?? new List<MeasureRequest>(),
                Filters = new List<FilterCondition>()
            };
        }

        private void SeedBasicData()
        {
            _ctx.SaleRecords.AddRange(
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Year = "2025", Amount = 100m, Quantity = 10m, Discount = 5m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Year = "2025", Amount = 200m, Quantity = 20m, Discount = 10m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "B", Year = "2026", Amount = 300m, Quantity = 30m, Discount = 15m }
            );
            _ctx.SaveChanges();
        }

        // ─── 測試 ──────────────────────────────────────────────────────────────

        [TestMethod]
        public void OneDimension_Sum_groups_correctly()
        {
            SeedBasicData();
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(Q(), req, _whitelist);

            Assert.AreEqual(2, rows.Count);
            var huaDong = rows.Single(r => r["Region"]?.ToString() == "華東");
            Assert.AreEqual(300m, Convert.ToDecimal(huaDong["Amount_Sum"]));
            var huaNan = rows.Single(r => r["Region"]?.ToString() == "華南");
            Assert.AreEqual(300m, Convert.ToDecimal(huaNan["Amount_Sum"]));
        }

        [TestMethod]
        public void TwoDimensions_Count_and_Avg()
        {
            SeedBasicData();
            var req = Req(
                dims: new[] { "Region", "Category" },
                msrs: new[]
                {
                    ("Amount", AggregateFunc.Count),
                    ("Amount", AggregateFunc.Avg)
                });

            var rows = _strategy.Execute(Q(), req, _whitelist);

            Assert.AreEqual(2, rows.Count);

            var huaDongA = rows.Single(r =>
                r["Region"]?.ToString() == "華東" &&
                r["Category"]?.ToString() == "A");
            Assert.AreEqual(2m, Convert.ToDecimal(huaDongA["Amount_Count"]));
            Assert.AreEqual(150m, Convert.ToDecimal(huaDongA["Amount_Avg"]));

            var huaNanB = rows.Single(r =>
                r["Region"]?.ToString() == "華南" &&
                r["Category"]?.ToString() == "B");
            Assert.AreEqual(1m, Convert.ToDecimal(huaNanB["Amount_Count"]));
            Assert.AreEqual(300m, Convert.ToDecimal(huaNanB["Amount_Avg"]));
        }

        [TestMethod]
        public void ThreeDimensions_all_five_aggregates()
        {
            SeedBasicData();

            // Test Sum
            var reqSum = Req(
                dims: new[] { "Region", "Category", "Year" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });
            var rowsSum = _strategy.Execute(Q(), reqSum, _whitelist);
            Assert.AreEqual(2, rowsSum.Count);
            var g1Sum = rowsSum.Single(r =>
                r["Region"]?.ToString() == "華東" &&
                r["Category"]?.ToString() == "A" &&
                r["Year"]?.ToString() == "2025");
            Assert.AreEqual(300m, Convert.ToDecimal(g1Sum["Amount_Sum"]));

            // Test Count
            var reqCount = Req(
                dims: new[] { "Region", "Category", "Year" },
                msrs: new[] { ("Amount", AggregateFunc.Count) });
            var rowsCount = _strategy.Execute(Q(), reqCount, _whitelist);
            var g1Count = rowsCount.Single(r =>
                r["Region"]?.ToString() == "華東" &&
                r["Year"]?.ToString() == "2025");
            Assert.AreEqual(2m, Convert.ToDecimal(g1Count["Amount_Count"]));

            // Test Avg
            var reqAvg = Req(
                dims: new[] { "Region", "Category", "Year" },
                msrs: new[] { ("Amount", AggregateFunc.Avg) });
            var rowsAvg = _strategy.Execute(Q(), reqAvg, _whitelist);
            var g1Avg = rowsAvg.Single(r =>
                r["Region"]?.ToString() == "華東" &&
                r["Year"]?.ToString() == "2025");
            Assert.AreEqual(150m, Convert.ToDecimal(g1Avg["Amount_Avg"]));

            // Test Max
            var reqMax = Req(
                dims: new[] { "Region", "Category", "Year" },
                msrs: new[] { ("Amount", AggregateFunc.Max) });
            var rowsMax = _strategy.Execute(Q(), reqMax, _whitelist);
            var g1Max = rowsMax.Single(r =>
                r["Region"]?.ToString() == "華東" &&
                r["Year"]?.ToString() == "2025");
            Assert.AreEqual(200m, Convert.ToDecimal(g1Max["Amount_Max"]));

            // Test Min
            var reqMin = Req(
                dims: new[] { "Region", "Category", "Year" },
                msrs: new[] { ("Amount", AggregateFunc.Min) });
            var rowsMin = _strategy.Execute(Q(), reqMin, _whitelist);
            var g1Min = rowsMin.Single(r =>
                r["Region"]?.ToString() == "華東" &&
                r["Year"]?.ToString() == "2025");
            Assert.AreEqual(100m, Convert.ToDecimal(g1Min["Amount_Min"]));
        }

        [TestMethod]
        public void Empty_table_returns_empty_results()
        {
            // Don't seed any data
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(Q(), req, _whitelist);

            Assert.AreEqual(0, rows.Count);
        }

        [TestMethod]
        public void Zero_dimensions_returns_empty()
        {
            SeedBasicData();
            var req = Req(
                dims: new string[0],
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            // ServerSideGroupByStrategy returns empty for zero dimensions
            // (the engine handles zero-dim aggregation separately via InProcess)
            var rows = _strategy.Execute(Q(), req, _whitelist);

            Assert.AreEqual(0, rows.Count);
        }

        [TestMethod]
        public void Results_match_InProcessGroupByStrategy()
        {
            SeedBasicData();
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[]
                {
                    ("Amount", AggregateFunc.Sum),
                    ("Amount", AggregateFunc.Count),
                    ("Amount", AggregateFunc.Avg)
                });

            var serverRows = _strategy.Execute(Q(), req, _whitelist);
            var inProcessRows = new InProcessGroupByStrategy().Execute(Q(), req, _whitelist);

            Assert.AreEqual(inProcessRows.Count, serverRows.Count,
                "Row count should match InProcess strategy");

            foreach (var inRow in inProcessRows)
            {
                var region = inRow["Region"]?.ToString();
                var serverRow = serverRows.Single(r => r["Region"]?.ToString() == region);

                Assert.AreEqual(
                    Convert.ToDecimal(inRow["Amount_Sum"]),
                    Convert.ToDecimal(serverRow["Amount_Sum"]),
                    $"Sum mismatch for {region}");
                Assert.AreEqual(
                    Convert.ToDecimal(inRow["Amount_Count"]),
                    Convert.ToDecimal(serverRow["Amount_Count"]),
                    $"Count mismatch for {region}");
                Assert.AreEqual(
                    Convert.ToDecimal(inRow["Amount_Avg"]),
                    Convert.ToDecimal(serverRow["Amount_Avg"]),
                    $"Avg mismatch for {region}");
            }
        }

        [TestMethod]
        public void ThreeMeasures_different_fields()
        {
            SeedBasicData();
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[]
                {
                    ("Amount", AggregateFunc.Sum),
                    ("Quantity", AggregateFunc.Avg),
                    ("Discount", AggregateFunc.Max)
                });

            var rows = _strategy.Execute(Q(), req, _whitelist);

            Assert.AreEqual(2, rows.Count);
            var huaDong = rows.Single(r => r["Region"]?.ToString() == "華東");
            Assert.AreEqual(300m, Convert.ToDecimal(huaDong["Amount_Sum"]));
            Assert.AreEqual(15m, Convert.ToDecimal(huaDong["Quantity_Avg"]));
            Assert.AreEqual(10m, Convert.ToDecimal(huaDong["Discount_Max"]));
        }
    }
}
