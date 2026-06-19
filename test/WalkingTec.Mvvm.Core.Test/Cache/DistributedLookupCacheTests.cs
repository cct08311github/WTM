#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Cache;

namespace WalkingTec.Mvvm.Core.Test.Cache
{
    // ─── Test fixtures (reuse type names from LookupCacheTests under a different namespace scope)

    [CacheLookup(TtlMinutes = 10, WarmOnStartup = true)]
    internal class DCity : TopBasePoco
    {
        public string Name { get; set; } = string.Empty;
        public string Province { get; set; } = string.Empty;
    }

    [CacheLookup(TtlMinutes = 5, TenantIsolation = true)]
    internal class DStatus : TopBasePoco
    {
        public string Code { get; set; } = string.Empty;
    }

    // Uncacheable (no [CacheLookup])
    internal class DOrder : TopBasePoco
    {
        public string Ref { get; set; } = string.Empty;
    }

    internal class DistLookupTestContext : DbContext
    {
        public DistLookupTestContext(DbContextOptions opts) : base(opts) { }
        public DbSet<DCity> DCities { get; set; } = null!;
        public DbSet<DStatus> DStatuses { get; set; } = null!;
        public DbSet<DOrder> DOrders { get; set; } = null!;
    }

    // ─── Factory helpers ─────────────────────────────────────────────────────────

    internal static class DistTestHelper
    {
        /// <summary>
        /// Creates a DistributedLookupCacheService backed by MemoryDistributedCache
        /// (the IDistributedCache impl that ships with Microsoft.Extensions.Caching.Memory)
        /// and a SQLite shared-memory DbContext for reliable isolation.
        /// </summary>
        public static (DistLookupTestContext ctx, DistributedLookupCacheService svc, IDistributedCache distCache)
            Create(SqliteConnection conn, LookupCacheOptions? options = null)
        {
            var opts = new DbContextOptionsBuilder<DistLookupTestContext>().UseSqlite(conn).Options;
            var ctx = new DistLookupTestContext(opts);
            ctx.Database.EnsureCreated();

            // MemoryDistributedCache is IDistributedCache backed by IMemoryCache —
            // perfect for unit tests without a real Redis instance.
            var distCache = new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

            var svc = new DistributedLookupCacheService(
                distCache,
                new[] { typeof(DCity).Assembly },
                options);

            return (ctx, svc, distCache);
        }
    }

    // ─── Tests ──────────────────────────────────────────────────────────────────

    [TestClass]
    public class DistributedLookupCacheServiceTests
    {
        // ── IsCacheable ─────────────────────────────────────────────────────────

        [TestMethod]
        public void IsCacheable_returns_true_for_attributed_type()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = DistTestHelper.Create(conn);

            Assert.IsTrue(svc.IsCacheable(typeof(DCity)));
            Assert.IsTrue(svc.IsCacheable(typeof(DStatus)));
        }

        [TestMethod]
        public void IsCacheable_returns_false_for_unattributed_type()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = DistTestHelper.Create(conn);

            Assert.IsFalse(svc.IsCacheable(typeof(DOrder)));
        }

        // ── GetAll — cache miss loads from DB ─────────────────────────────────

        [TestMethod]
        public void GetAll_cache_miss_loads_from_db()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            var result = svc.GetAll<DCity>(ctx, tenantId: null);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("台北", result[0].Name);
        }

        // ── GetAll — cache hit (second call returns cached) ───────────────────

        [TestMethod]
        public void GetAll_second_call_returns_cached_without_hitting_db()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "高雄", Province = "南部" });
            ctx.SaveChanges();

            var first = svc.GetAll<DCity>(ctx, tenantId: null);

            // Add new data — but the cache was already populated, so second call must not see it
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "台中", Province = "中部" });
            ctx.SaveChanges();

            var second = svc.GetAll<DCity>(ctx, tenantId: null);

            Assert.AreEqual(1, second.Count, "Distributed cache hit: should return cached list");
        }

        // ── GetAll async — cache miss ─────────────────────────────────────────

        [TestMethod]
        public async Task GetAllAsync_cache_miss_loads_from_db()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "花蓮", Province = "東部" });
            ctx.SaveChanges();

            var result = await svc.GetAllAsync<DCity>(ctx);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("花蓮", result[0].Name);
        }

        // ── GetAll async — cache hit ──────────────────────────────────────────

        [TestMethod]
        public async Task GetAllAsync_second_call_returns_cached()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "基隆", Province = "北部" });
            ctx.SaveChanges();

            await svc.GetAllAsync<DCity>(ctx); // populate

            // Add extra data; the cache should serve the stale snapshot
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "桃園", Province = "北部" });
            ctx.SaveChanges();

            var second = await svc.GetAllAsync<DCity>(ctx);

            Assert.AreEqual(1, second.Count, "Distributed cache hit: stale snapshot expected");
        }

        // ── Serialization round-trip ──────────────────────────────────────────

        [TestMethod]
        public void GetAll_serialization_roundtrip_preserves_fields()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            var id = Guid.NewGuid();
            ctx.DCities.Add(new DCity { ID = id, Name = "台南", Province = "南部" });
            ctx.SaveChanges();

            // First call populates the distributed cache (miss + serialize)
            svc.GetAll<DCity>(ctx, tenantId: null);

            // Second call deserializes from distributed cache (hit path)
            var result = svc.GetAll<DCity>(ctx, tenantId: null);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(id, result[0].ID, "ID must survive serialization round-trip");
            Assert.AreEqual("台南", result[0].Name, "Name must survive serialization round-trip");
            Assert.AreEqual("南部", result[0].Province, "Province must survive serialization round-trip");
        }

        // ── Invalidate<T> — per-key deletion ─────────────────────────────────

        [TestMethod]
        public void Invalidate_clears_key_and_triggers_miss_on_next_call()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "屏東", Province = "南部" });
            ctx.SaveChanges();

            svc.GetAll<DCity>(ctx, tenantId: null); // populate

            // Add a new row, then invalidate so next call sees it
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "宜蘭", Province = "東部" });
            ctx.SaveChanges();

            svc.Invalidate<DCity>(tenantId: null);

            var result = svc.GetAll<DCity>(ctx, tenantId: null);
            Assert.AreEqual(2, result.Count, "After invalidation, fresh DB data expected");
        }

        // ── Invalidate<T> — per-tenant key ───────────────────────────────────

        [TestMethod]
        public async Task Invalidate_tenant_specific_only_clears_that_tenant()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DStatuses.Add(new DStatus { ID = Guid.NewGuid(), Code = "A" });
            ctx.SaveChanges();

            // Populate caches for two tenants
            await svc.GetAllAsync<DStatus>(ctx, tenantId: "T1");
            await svc.GetAllAsync<DStatus>(ctx, tenantId: "T2");

            // Add new data and invalidate only T1
            ctx.DStatuses.Add(new DStatus { ID = Guid.NewGuid(), Code = "B" });
            ctx.SaveChanges();
            svc.Invalidate<DStatus>(tenantId: "T1");

            var t1Result = await svc.GetAllAsync<DStatus>(ctx, tenantId: "T1");
            var t2Result = await svc.GetAllAsync<DStatus>(ctx, tenantId: "T2");

            Assert.AreEqual(2, t1Result.Count, "T1 invalidated — fresh DB data");
            Assert.AreEqual(1, t2Result.Count, "T2 still cached — stale snapshot");
        }

        // ── InvalidateType — sentinel strategy ───────────────────────────────

        [TestMethod]
        public void InvalidateType_causes_miss_on_next_read_for_all_tenants()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "苗栗", Province = "中部" });
            ctx.SaveChanges();

            svc.GetAll<DCity>(ctx, tenantId: null); // populate

            // Add row, then invalidate ALL entries for DCity
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "新竹", Province = "北部" });
            ctx.SaveChanges();

            svc.InvalidateType(typeof(DCity));

            var result = svc.GetAll<DCity>(ctx, tenantId: null);
            Assert.AreEqual(2, result.Count, "InvalidateType should cause a fresh DB read");
        }

        // ── InvalidateType — sentinel propagates to GetAllAsync ──────────────

        [TestMethod]
        public async Task InvalidateType_async_causes_miss_on_next_async_read()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "嘉義", Province = "南部" });
            ctx.SaveChanges();

            await svc.GetAllAsync<DCity>(ctx); // populate

            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "澎湖", Province = "離島" });
            ctx.SaveChanges();

            svc.InvalidateType(typeof(DCity));

            var result = await svc.GetAllAsync<DCity>(ctx);
            Assert.AreEqual(2, result.Count, "InvalidateType sentinel should invalidate async reads too");
        }

        // ── RefreshAsync ──────────────────────────────────────────────────────

        [TestMethod]
        public async Task RefreshAsync_repopulates_cache_with_fresh_data()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "金門", Province = "離島" });
            ctx.SaveChanges();

            await svc.GetAllAsync<DCity>(ctx); // initial populate

            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "馬祖", Province = "離島" });
            ctx.SaveChanges();

            // RefreshAsync should atomically invalidate + reload
            await svc.RefreshAsync<DCity>(ctx, tenantId: null);

            var result = await svc.GetAllAsync<DCity>(ctx, tenantId: null);
            Assert.AreEqual(2, result.Count, "RefreshAsync should serve the newly loaded data");
        }

        // ── Uncacheable type bypasses cache ───────────────────────────────────

        [TestMethod]
        public void Uncacheable_type_always_loads_from_db()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DOrders.Add(new DOrder { ID = Guid.NewGuid(), Ref = "ORD-001" });
            ctx.SaveChanges();

            var first = svc.GetAll<DOrder>(ctx);

            ctx.DOrders.Add(new DOrder { ID = Guid.NewGuid(), Ref = "ORD-002" });
            ctx.SaveChanges();

            // Because DOrder is not [CacheLookup], second call hits DB directly
            var second = svc.GetAll<DOrder>(ctx);
            Assert.AreEqual(2, second.Count, "Non-cacheable type must not be served from cache");
        }

        // ── GetStats ──────────────────────────────────────────────────────────

        [TestMethod]
        public void GetStats_hit_miss_counts_increment_correctly()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = DistTestHelper.Create(conn);
            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "新北", Province = "北部" });
            ctx.SaveChanges();

            svc.GetAll<DCity>(ctx); // miss
            svc.GetAll<DCity>(ctx); // hit
            svc.GetAll<DCity>(ctx); // hit

            var stats = svc.GetStats(typeof(DCity));
            Assert.IsNotNull(stats);
            Assert.AreEqual(1L, stats.Misses, "One miss expected (initial load)");
            Assert.AreEqual(2L, stats.Hits, "Two hits expected (subsequent reads from cache)");
        }

        [TestMethod]
        public void GetStats_returns_null_for_uncacheable_type()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = DistTestHelper.Create(conn);

            var stats = svc.GetStats(typeof(DOrder));
            Assert.IsNull(stats, "GetStats must return null for non-[CacheLookup] types");
        }

        [TestMethod]
        public void GetStats_list_includes_all_registered_types()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = DistTestHelper.Create(conn);

            var all = svc.GetStats();
            Assert.IsTrue(all.Count >= 2, "At least DCity and DStatus should be in the stats list");
            foreach (var s in all)
            {
                Assert.IsNotNull(s.EntityTypeName);
                Assert.IsTrue(s.TtlMinutesConfigured > 0);
            }
        }

        // ── DefaultTenantIsolation ────────────────────────────────────────────

        [TestMethod]
        public void DefaultTenantIsolation_reflects_options()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = DistTestHelper.Create(conn, new LookupCacheOptions { DefaultTenantIsolation = false });
            Assert.IsFalse(svc.DefaultTenantIsolation);
        }

        // ── GetAttribute ──────────────────────────────────────────────────────

        [TestMethod]
        public void GetAttribute_returns_correct_attribute_for_registered_type()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = DistTestHelper.Create(conn);

            var attr = svc.GetAttribute(typeof(DCity));
            Assert.IsNotNull(attr);
            Assert.AreEqual(10, attr.TtlMinutes);
            Assert.IsTrue(attr.WarmOnStartup);
        }

        [TestMethod]
        public void GetAttribute_returns_null_for_unregistered_type()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = DistTestHelper.Create(conn);

            Assert.IsNull(svc.GetAttribute(typeof(DOrder)));
        }

        // ── GetWarmupTypes ────────────────────────────────────────────────────

        [TestMethod]
        public void GetWarmupTypes_includes_WarmOnStartup_true_types()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = DistTestHelper.Create(conn);

            var types = svc.GetWarmupTypes();
            Assert.IsTrue(types.Any(t => t == typeof(DCity)), "DCity (WarmOnStartup=true) must be included");
        }

        // ── Cross-node: InvalidateType sentinel detected by another instance ──

        [TestMethod]
        public void CrossNode_InvalidateType_DetectedByOtherInstance()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();

            // Create a SHARED MemoryDistributedCache that both nodes will use.
            var sharedDistCache = new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

            // Use the same SQLite connection for both nodes (same DB, simulating shared storage).
            var opts = new DbContextOptionsBuilder<DistLookupTestContext>().UseSqlite(conn).Options;
            var ctxA = new DistLookupTestContext(opts);
            var ctxB = new DistLookupTestContext(opts);
            ctxA.Database.EnsureCreated();

            var nodeA = new DistributedLookupCacheService(
                sharedDistCache,
                new[] { typeof(DCity).Assembly });
            var nodeB = new DistributedLookupCacheService(
                sharedDistCache,
                new[] { typeof(DCity).Assembly });

            // Seed 2 cities initially.
            ctxA.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctxA.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "高雄", Province = "南部" });
            ctxA.SaveChanges();

            // nodeA populates its local + distributed cache.
            var initialCount = nodeA.GetAll<DCity>(ctxA, tenantId: null).Count;
            Assert.AreEqual(2, initialCount, "nodeA should see 2 cities initially");

            // nodeB invalidates the type — writes sentinel to shared distributed cache only.
            // nodeA's process-local SemaphoreSlim state and distributed data are NOT directly touched.
            nodeB.InvalidateType(typeof(DCity));

            // Add a 3rd city to the DB so a fresh load would return 3.
            ctxA.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "台中", Province = "中部" });
            ctxA.SaveChanges();

            // nodeA must detect the distributed sentinel and reload from DB.
            var afterCount = nodeA.GetAll<DCity>(ctxA, tenantId: null).Count;
            Assert.AreEqual(3, afterCount,
                "nodeA must detect nodeB's distributed sentinel and reload 3 cities from DB");

            ctxA.Dispose();
            ctxB.Dispose();
        }
    }
}
