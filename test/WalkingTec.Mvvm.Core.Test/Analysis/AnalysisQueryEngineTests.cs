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
    /// AnalysisQueryEngine 的單元測試。
    /// </summary>
    [TestClass]
    public class AnalysisQueryEngineTests
    {
        private class SaleRecord : TopBasePoco
        {
            [Dimension(DisplayName = "地區")] public string Region { get; set; }
            [Dimension(DisplayName = "類別")] public string Category { get; set; }
            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg,
                     DisplayName = "金額")]
            public decimal Amount { get; set; }
        }

        private class SaleTestContext : DbContext
        {
            public SaleTestContext(DbContextOptions opts) : base(opts) { }
            public DbSet<SaleRecord> SaleRecords { get; set; }
        }

        private SqliteConnection _conn;
        private SaleTestContext _ctx;
        private IEnumerable<AnalysisFieldMeta> _whitelist;

        /// <summary>
        /// 每個測試前建立 SQLite in-memory DB 並植入基本資料。
        /// </summary>
        [TestInitialize]
        public void Setup()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();

            var opts = new DbContextOptionsBuilder<SaleTestContext>()
                .UseSqlite(_conn)
                .Options;

            _ctx = new SaleTestContext(opts);
            _ctx.Database.EnsureCreated();

            _ctx.SaleRecords.AddRange(
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "B", Amount = 200m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "A", Amount = 300m }
            );
            _ctx.SaveChanges();

            _whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord));
        }

        /// <summary>
        /// 每個測試後釋放資源。
        /// </summary>
        [TestCleanup]
        public void Cleanup()
        {
            _ctx.Dispose();
            _conn.Dispose();
        }

        /// <summary>
        /// 使用不在白名單內的維度欄位時應拋出 InvalidOperationException。
        /// </summary>
        [TestMethod]
        public void Throws_when_dimension_not_in_whitelist()
        {
            var engine = new AnalysisQueryEngine();
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Nonexistent" },
                Measures = new List<MeasureRequest>(),
                Filters = new List<FilterCondition>()
            };

            Assert.ThrowsException<InvalidOperationException>(
                () => engine.Execute(_ctx.SaleRecords.AsQueryable(), req, _whitelist));
        }

        /// <summary>
        /// 使用不被允許的聚合函式時應拋出 InvalidOperationException。
        /// </summary>
        [TestMethod]
        public void Throws_when_measure_func_not_allowed()
        {
            var engine = new AnalysisQueryEngine();
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Max }
                },
                Filters = new List<FilterCondition>()
            };

            Assert.ThrowsException<InvalidOperationException>(
                () => engine.Execute(_ctx.SaleRecords.AsQueryable(), req, _whitelist));
        }

        /// <summary>
        /// Eq 過濾條件應將結果縮減到符合條件的分組數。
        /// </summary>
        [TestMethod]
        public void Filter_Eq_reduces_rows()
        {
            var engine = new AnalysisQueryEngine();
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = "華東" }
                }
            };

            var result = engine.Execute(_ctx.SaleRecords.AsQueryable(), req, _whitelist);

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual("華東", result.Rows[0]["Region"].ToString());
        }

        /// <summary>
        /// 依單一維度 GroupBy 並 Sum 度量，應正確聚合各分組。
        /// </summary>
        [TestMethod]
        public void GroupBy_single_dimension_sum()
        {
            var engine = new AnalysisQueryEngine();
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>()
            };

            var result = engine.Execute(_ctx.SaleRecords.AsQueryable(), req, _whitelist);

            Assert.AreEqual(2, result.Rows.Count);

            var huaDongRow = result.Rows.SingleOrDefault(r => r["Region"].ToString() == "華東");
            Assert.IsNotNull(huaDongRow);
            Assert.AreEqual(300m, Convert.ToDecimal(huaDongRow["Amount_Sum"]));
        }

        /// <summary>
        /// 超過 10000 列時，結果應截斷至 10000 並標記 Truncated=true。
        /// </summary>
        [TestMethod]
        public void Result_truncated_at_10000_rows()
        {
            // 新增 10001 筆唯一 Region 記錄（連同初始 3 筆共 10004 筆，10003 個不同分組）
            var extras = new List<SaleRecord>();
            for (int i = 1; i <= 10001; i++)
            {
                extras.Add(new SaleRecord
                {
                    ID = Guid.NewGuid(),
                    Region = $"R{i}",
                    Category = "X",
                    Amount = i
                });
            }
            _ctx.SaleRecords.AddRange(extras);
            _ctx.SaveChanges();

            var engine = new AnalysisQueryEngine();
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Count }
                },
                Filters = new List<FilterCondition>()
            };

            var result = engine.Execute(_ctx.SaleRecords.AsQueryable(), req, _whitelist);

            Assert.AreEqual(10000, result.Rows.Count);
            Assert.IsTrue(result.Truncated);
        }
    }
}
