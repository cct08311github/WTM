#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    [TestClass]
    public class AnalysisCacheTests
    {
        // ─── NullAnalysisCache ──────────────────────────────────────────────

        [TestMethod]
        public void NullCache_TryGet_always_returns_false()
        {
            var cache = new NullAnalysisCache();
            var response = new AnalysisQueryResponse { QueryHash = "ABCD1234ABCD1234" };
            cache.Set("ABCD1234ABCD1234", response);

            var hit = cache.TryGet("ABCD1234ABCD1234", out var cached);

            Assert.IsFalse(hit);
            Assert.IsNull(cached);
        }

        // ─── MemoryAnalysisCache ────────────────────────────────────────────

        [TestMethod]
        public void MemoryCache_Set_then_TryGet_returns_cached_response()
        {
            var mc = new MemoryCache(new MemoryCacheOptions());
            var cache = new MemoryAnalysisCache(mc);
            var response = MakeResponse("HASH0001");

            cache.Set("HASH0001", response);
            var hit = cache.TryGet("HASH0001", out var cached);

            Assert.IsTrue(hit);
            Assert.IsNotNull(cached);
            Assert.AreEqual("HASH0001", cached!.QueryHash);
            Assert.AreEqual(response.TotalCount, cached.TotalCount);
        }

        [TestMethod]
        public void MemoryCache_Invalidate_removes_specific_entry()
        {
            var mc = new MemoryCache(new MemoryCacheOptions());
            var cache = new MemoryAnalysisCache(mc);
            cache.Set("KEY_A", MakeResponse("KEY_A"));
            cache.Set("KEY_B", MakeResponse("KEY_B"));

            cache.Invalidate("KEY_A");

            Assert.IsFalse(cache.TryGet("KEY_A", out _));
            Assert.IsTrue(cache.TryGet("KEY_B", out _));
        }

        [TestMethod]
        public void MemoryCache_InvalidateAll_clears_everything()
        {
            var mc = new MemoryCache(new MemoryCacheOptions());
            var cache = new MemoryAnalysisCache(mc);
            cache.Set("KEY_A", MakeResponse("KEY_A"));
            cache.Set("KEY_B", MakeResponse("KEY_B"));

            cache.InvalidateAll();

            Assert.IsFalse(cache.TryGet("KEY_A", out _));
            Assert.IsFalse(cache.TryGet("KEY_B", out _));
        }

        [TestMethod]
        public void MemoryCache_expired_entry_is_not_returned()
        {
            var mc = new MemoryCache(new MemoryCacheOptions());
            var cache = new MemoryAnalysisCache(mc);
            cache.Set("SHORT", MakeResponse("SHORT"), ttl: TimeSpan.FromMilliseconds(1));

            Thread.Sleep(50);

            Assert.IsFalse(cache.TryGet("SHORT", out _));
        }

        // ─── Engine + Cache 整合 ────────────────────────────────────────────

        private class SaleRecord : TopBasePoco
        {
            [Dimension(DisplayName = "地區")]
            public string Region { get; set; } = string.Empty;

            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count,
                     DisplayName = "金額")]
            public decimal Amount { get; set; }
        }

        private class SaleTestContext : DbContext
        {
            public SaleTestContext(DbContextOptions opts) : base(opts) { }
            public DbSet<SaleRecord> SaleRecords { get; set; } = null!;
        }

        [TestMethod]
        public void Engine_with_cache_returns_cached_on_second_call()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var opts = new DbContextOptionsBuilder<SaleTestContext>().UseSqlite(conn).Options;
            using var ctx = new SaleTestContext(opts);
            ctx.Database.EnsureCreated();
            ctx.SaleRecords.Add(new SaleRecord
            {
                ID = Guid.NewGuid(), Region = "北", Amount = 100m
            });
            ctx.SaveChanges();

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord));
            var mc = new MemoryCache(new MemoryCacheOptions());
            var cache = new MemoryAnalysisCache(mc);
            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default, cache);

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>()
            };

            var result1 = engine.Execute(ctx.SaleRecords.AsQueryable(), req, whitelist);
            Assert.AreEqual(1, result1.Rows.Count);
            Assert.AreEqual(100m, Convert.ToDecimal(result1.Rows[0]["Amount_Sum"]));

            // 新增資料後，因快取命中，第二次查詢應回傳與第一次相同的結果
            ctx.SaleRecords.Add(new SaleRecord
            {
                ID = Guid.NewGuid(), Region = "南", Amount = 200m
            });
            ctx.SaveChanges();

            var result2 = engine.Execute(ctx.SaleRecords.AsQueryable(), req, whitelist);

            // 快取命中：結果應與第一次一致（1 列，非 2 列）
            Assert.AreEqual(1, result2.Rows.Count);
            Assert.AreSame(result1, result2);
        }

        [TestMethod]
        public void Engine_without_cache_works_as_before()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var opts = new DbContextOptionsBuilder<SaleTestContext>().UseSqlite(conn).Options;
            using var ctx = new SaleTestContext(opts);
            ctx.Database.EnsureCreated();
            ctx.SaleRecords.Add(new SaleRecord
            {
                ID = Guid.NewGuid(), Region = "北", Amount = 100m
            });
            ctx.SaveChanges();

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord));
            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default); // 無快取

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>()
            };

            var result = engine.Execute(ctx.SaleRecords.AsQueryable(), req, whitelist);

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(100m, Convert.ToDecimal(result.Rows[0]["Amount_Sum"]));
            Assert.AreEqual(16, result.QueryHash.Length);
        }

        // ─── Helpers ────────────────────────────────────────────────────────

        private static AnalysisQueryResponse MakeResponse(string hash)
        {
            return new AnalysisQueryResponse
            {
                QueryHash = hash,
                Columns = new List<string> { "Col1" },
                Rows = new List<Dictionary<string, object?>>
                {
                    new Dictionary<string, object?> { ["Col1"] = "val" }
                },
                TotalCount = 1,
                Truncated = false
            };
        }
    }
}
