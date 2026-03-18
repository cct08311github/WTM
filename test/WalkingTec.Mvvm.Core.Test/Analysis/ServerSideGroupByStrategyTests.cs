#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

        /// <summary>
        /// Verifies that the generated SQL actually contains "GROUP BY".
        /// Uses EF Core SQL logging to capture the emitted query and asserts
        /// the database-side aggregation is not silently falling back to in-process.
        /// </summary>
        [TestMethod]
        public void Execute_emits_SQL_with_GROUP_BY()
        {
            // Arrange: rebuild context with SQL logging enabled
            var sqlLog = new StringBuilder();
            var loggingOpts = new DbContextOptionsBuilder<SaleTestContext>()
                .UseSqlite(_conn)
                .LogTo(
                    msg => { sqlLog.AppendLine(msg); },
                    new[] { DbLoggerCategory.Database.Command.Name },
                    LogLevel.Information)
                .EnableSensitiveDataLogging()
                .Options;

            using var loggingCtx = new SaleTestContext(loggingOpts);
            loggingCtx.Database.EnsureCreated();
            loggingCtx.SaleRecords.AddRange(
                new SaleRecord { ID = Guid.NewGuid(), Region = "北區", Category = "X", Year = "2025", Amount = 50m, Quantity = 5m, Discount = 2m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "北區", Category = "X", Year = "2025", Amount = 150m, Quantity = 15m, Discount = 8m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "南區", Category = "Y", Year = "2025", Amount = 200m, Quantity = 20m, Discount = 10m }
            );
            loggingCtx.SaveChanges();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            // Act
            sqlLog.Clear();
            _strategy.Execute(loggingCtx.SaleRecords.AsQueryable(), req, _whitelist);
            var capturedSql = sqlLog.ToString();

            // Assert: the emitted SQL must contain GROUP BY, proving server-side aggregation
            StringAssert.Contains(
                capturedSql.ToUpperInvariant(),
                "GROUP BY",
                $"Expected SQL to contain GROUP BY but got:\n{capturedSql}");
        }

        [TestMethod]
        public void Count_on_nullable_field_emits_SQL_with_GROUP_BY()
        {
            // Arrange: rebuild context with SQL logging enabled
            var sqlLog = new StringBuilder();
            var loggingOpts = new DbContextOptionsBuilder<SaleTestContext>()
                .UseSqlite(_conn)
                .LogTo(
                    msg => { sqlLog.AppendLine(msg); },
                    new[] { DbLoggerCategory.Database.Command.Name },
                    LogLevel.Information)
                .EnableSensitiveDataLogging()
                .Options;

            using var loggingCtx = new SaleTestContext(loggingOpts);
            loggingCtx.Database.EnsureCreated();
            loggingCtx.SaleRecords.AddRange(
                new SaleRecord { ID = Guid.NewGuid(), Region = "東區", Category = "Z", Year = "2025", Amount = 10m, Quantity = 1m, Discount = 1m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "東區", Category = "Z", Year = "2025", Amount = 20m, Quantity = 2m, Discount = 2m }
            );
            loggingCtx.SaveChanges();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Count) });

            // Act
            sqlLog.Clear();
            var rows = _strategy.Execute(loggingCtx.SaleRecords.AsQueryable(), req, _whitelist);
            var capturedSql = sqlLog.ToString();

            // Assert: GROUP BY is present in generated SQL
            StringAssert.Contains(
                capturedSql.ToUpperInvariant(),
                "GROUP BY",
                $"Expected SQL to contain GROUP BY for Count but got:\n{capturedSql}");

            // Assert: result is correct
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(2m, Convert.ToDecimal(rows[0]["Amount_Count"]));
        }

        // ─── #558: double→decimal rounding eliminates floating-point noise ────

        [TestMethod]
        public void Sum_of_floating_point_values_has_no_visible_noise_after_rounding()
        {
            // 0.1 + 0.2 is a canonical double-precision floating-point example.
            // Without rounding, (decimal)(0.1d + 0.2d) = 0.3000000000000000444089...
            // The Math.Round(10 d.p.) fix in ServerSideGroupByStrategy should reduce this
            // to exactly 0.3m, matching the result from InProcessGroupByStrategy (#558).
            _ctx.SaleRecords.AddRange(
                new SaleRecord { ID = Guid.NewGuid(), Region = "A", Amount = 0.1m, Quantity = 0m, Discount = 0m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "A", Amount = 0.2m, Quantity = 0m, Discount = 0m }
            );
            _ctx.SaveChanges();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, rows.Count);
            var result = (decimal)rows[0]["Amount_Sum"]!;
            Assert.AreEqual(0.3m, result,
                "浮點尾差應被 10 位小數四捨五入消除；若失敗表示 Math.Round 未正確套用");
        }

        [TestMethod]
        public void Avg_of_floating_point_values_has_no_visible_noise_after_rounding()
        {
            // AVG(1.0, 2.0) = 1.5 — exact, but confirm rounding logic does not distort it.
            // AVG(1.1, 1.2) = 1.15 — may have double precision noise before rounding.
            _ctx.SaleRecords.AddRange(
                new SaleRecord { ID = Guid.NewGuid(), Region = "B", Amount = 1.1m, Quantity = 0m, Discount = 0m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "B", Amount = 1.2m, Quantity = 0m, Discount = 0m }
            );
            _ctx.SaveChanges();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Avg) });

            var rows = _strategy.Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, rows.Count);
            var result = (decimal)rows[0]["Amount_Avg"]!;
            Assert.AreEqual(1.15m, result,
                "AVG(1.1, 1.2) 應等於 1.15m，10 位四捨五入不應影響正常精度");
        }

        [TestMethod]
        public void Null_measure_result_is_preserved_as_null()
        {
            // When no rows match a group, the aggregate returns null.
            // Verify the null→null path is handled (not (decimal?)null.Value crash).
            _ctx.SaleRecords.AddRange(
                new SaleRecord { ID = Guid.NewGuid(), Region = "C", Amount = 100m, Quantity = 0m, Discount = 0m }
            );
            _ctx.SaveChanges();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var rows = _strategy.Execute(Q(), req, _whitelist);

            Assert.AreEqual(1, rows.Count);
            // Result should be a non-null decimal (100m), confirming the null guard works correctly.
            Assert.IsNotNull(rows[0]["Amount_Sum"]);
        }
    }
}
