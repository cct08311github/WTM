#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Cache;

namespace WalkingTec.Mvvm.Core.Test.Cache
{
    // ─── Test fixtures ──────────────────────────────────────────────────────────

    [CacheLookup(TtlMinutes = 10, WarmOnStartup = true)]
    internal class CityCode : TopBasePoco
    {
        public string Name { get; set; } = string.Empty;
        public string Province { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }

    [CacheLookup(TtlMinutes = 5, TenantIsolation = true)]
    internal class StatusDict : TopBasePoco
    {
        public string Code { get; set; } = string.Empty;
    }

    // 未標記 [CacheLookup]
    internal class OrderRecord : TopBasePoco
    {
        public string Name { get; set; } = string.Empty;
    }

    // 標記 [CacheLookup] 但 WarmOnStartup = false
    [CacheLookup(TtlMinutes = 5, WarmOnStartup = false)]
    internal class NoWarmDict : TopBasePoco
    {
        public string Code { get; set; } = string.Empty;
    }

    internal class LookupTestContext : DbContext
    {
        public LookupTestContext(DbContextOptions opts) : base(opts) { }
        public DbSet<CityCode> CityCodes { get; set; } = null!;
        public DbSet<StatusDict> StatusDicts { get; set; } = null!;
        public DbSet<OrderRecord> OrderRecords { get; set; } = null!;
        public DbSet<NoWarmDict> NoWarmDicts { get; set; } = null!;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    internal static class TestHelper
    {
        public static (LookupTestContext ctx, LookupCacheService svc, IMemoryCache mc) Create(
            SqliteConnection conn, LookupCacheOptions? options = null)
        {
            var opts = new DbContextOptionsBuilder<LookupTestContext>().UseSqlite(conn).Options;
            var ctx = new LookupTestContext(opts);
            ctx.Database.EnsureCreated();

            var mc = new MemoryCache(new MemoryCacheOptions());
            // 只掃描含有測試 fixture 型別的 assembly
            var svc = new LookupCacheService(mc, new[] { typeof(CityCode).Assembly }, options);
            return (ctx, svc, mc);
        }
    }

    // ─── Tests ──────────────────────────────────────────────────────────────────

    [TestClass]
    public class LookupCacheServiceTests
    {
        // ── IsCacheable ─────────────────────────────────────────────────────────

        [TestMethod]
        public void IsCacheable_returns_true_for_attributed_type()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = TestHelper.Create(conn);

            Assert.IsTrue(svc.IsCacheable(typeof(CityCode)));
            Assert.IsTrue(svc.IsCacheable(typeof(StatusDict)));
        }

        [TestMethod]
        public void IsCacheable_returns_false_for_unattributed_type()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = TestHelper.Create(conn);

            Assert.IsFalse(svc.IsCacheable(typeof(OrderRecord)));
        }

        // ── GetWarmupTypes ──────────────────────────────────────────────────────

        [TestMethod]
        public void GetWarmupTypes_includes_only_WarmOnStartup_true()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = TestHelper.Create(conn);

            var types = svc.GetWarmupTypes();

            Assert.IsTrue(types.Contains(typeof(CityCode)), "CityCode has WarmOnStartup=true");
            // StatusDict 未設定 WarmOnStartup，預設為 true 所以也應包含
            Assert.IsTrue(types.Contains(typeof(StatusDict)));
            // NoWarmDict 明確設定 WarmOnStartup=false，不應出現在清單中
            Assert.IsFalse(types.Contains(typeof(NoWarmDict)), "NoWarmDict has WarmOnStartup=false and must be excluded");
        }

        // ── GetAll — cache miss → DB ─────────────────────────────────────────────

        [TestMethod]
        public void GetAll_cache_miss_loads_from_db()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            var result = svc.GetAll<CityCode>(ctx, tenantId: null);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("台北", result[0].Name);
        }

        // ── GetAll — cache hit ──────────────────────────────────────────────────

        [TestMethod]
        public void GetAll_second_call_returns_cached_without_hitting_db()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "高雄", Province = "南部" });
            ctx.SaveChanges();

            var first = svc.GetAll<CityCode>(ctx, tenantId: null);

            // 加入新資料，但快取命中，第二次不應看到新資料
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台中", Province = "中部" });
            ctx.SaveChanges();

            var second = svc.GetAll<CityCode>(ctx, tenantId: null);

            Assert.AreEqual(1, second.Count, "Cache hit: should return original cached list");
            Assert.AreSame(first, second, "Should be identical reference from cache");
        }

        // ── GetAll returns IReadOnlyList ─────────────────────────────────────────

        [TestMethod]
        public void GetAll_returns_IReadOnlyList()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            IReadOnlyList<CityCode> result = svc.GetAll<CityCode>(ctx, tenantId: null);

            Assert.IsInstanceOfType(result, typeof(IReadOnlyList<CityCode>));
            Assert.AreEqual(1, result.Count);
        }

        // ── Invalidate<T> ────────────────────────────────────────────────────────

        [TestMethod]
        public void Invalidate_clears_specific_tenant_key()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            // 暖機 main tenant 快取
            var before = svc.GetAll<CityCode>(ctx, tenantId: null);
            Assert.AreEqual(1, before.Count);

            // 失效
            svc.Invalidate<CityCode>(tenantId: null);

            // 加入新資料後重新查詢應看到 2 筆
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "高雄", Province = "南部" });
            ctx.SaveChanges();
            var after = svc.GetAll<CityCode>(ctx, tenantId: null);

            Assert.AreEqual(2, after.Count, "After invalidation, should reload from DB");
        }

        // ── InvalidateType ──────────────────────────────────────────────────────

        [TestMethod]
        public void InvalidateType_clears_all_tenant_keys_via_cts()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "A", Province = "北部" });
            ctx.SaveChanges();

            // 暖機兩個不同租戶的快取
            svc.GetAll<CityCode>(ctx, "tenant1");
            svc.GetAll<CityCode>(ctx, "tenant2");

            // 使用 InvalidateType 清除所有租戶（透過 CTS cancel）
            svc.InvalidateType(typeof(CityCode));

            // 新增資料後，兩個租戶都應看到新資料
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "B", Province = "南部" });
            ctx.SaveChanges();

            var t1 = svc.GetAll<CityCode>(ctx, "tenant1");
            var t2 = svc.GetAll<CityCode>(ctx, "tenant2");

            Assert.AreEqual(2, t1.Count, "tenant1 cache should be invalidated");
            Assert.AreEqual(2, t2.Count, "tenant2 cache should be invalidated");
        }

        // ── Tenant isolation ────────────────────────────────────────────────────

        [TestMethod]
        public void GetAll_different_tenants_have_isolated_cache_keys()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "共用A", Province = "北部" });
            ctx.SaveChanges();

            var forT1 = svc.GetAll<CityCode>(ctx, "tenant1");

            // 加入新資料後 tenant2 第一次查詢應看到 2 筆（cache miss）
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "共用B", Province = "南部" });
            ctx.SaveChanges();

            var forT2 = svc.GetAll<CityCode>(ctx, "tenant2");

            Assert.AreEqual(1, forT1.Count, "tenant1 cache should be isolated");
            Assert.AreEqual(2, forT2.Count, "tenant2 gets fresh load");
        }

        // ── GetAllAsync ─────────────────────────────────────────────────────────

        [TestMethod]
        public async Task GetAllAsync_returns_same_data_as_GetAll()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台南", Province = "南部" });
            ctx.SaveChanges();

            var sync = svc.GetAll<CityCode>(ctx, null);

            // 失效後重新非同步載入
            svc.Invalidate<CityCode>(null);
            var async_ = await svc.GetAllAsync<CityCode>(ctx, null);

            Assert.AreEqual(sync.Count, async_.Count);
            Assert.AreEqual(sync[0].Name, async_[0].Name);
        }

        // ── GetAttribute ────────────────────────────────────────────────────────

        [TestMethod]
        public void GetAttribute_returns_attribute_for_cacheable_type()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = TestHelper.Create(conn);

            var attr = svc.GetAttribute(typeof(CityCode));

            Assert.IsNotNull(attr);
            Assert.AreEqual(10, attr!.TtlMinutes);
        }

        [TestMethod]
        public void GetAttribute_returns_null_for_non_cacheable_type()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = TestHelper.Create(conn);

            var attr = svc.GetAttribute(typeof(OrderRecord));

            Assert.IsNull(attr);
        }

        // ── DefaultTenantIsolation ──────────────────────────────────────────────

        [TestMethod]
        public void DefaultTenantIsolation_is_true_by_default()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = TestHelper.Create(conn);

            Assert.IsTrue(svc.DefaultTenantIsolation);
        }

        [TestMethod]
        public void DefaultTenantIsolation_can_be_overridden_via_options()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var options = new LookupCacheOptions { DefaultTenantIsolation = false };
            var (_, svc, _) = TestHelper.Create(conn, options);

            Assert.IsFalse(svc.DefaultTenantIsolation);
        }
    }

    // ─── Stampede + RefreshAsync ─────────────────────────────────────────────────

    [TestClass]
    public class StampedeAndRefreshTests
    {
        [TestMethod]
        public async Task GetAllAsync_concurrent_misses_only_query_db_once_via_semaphore()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            // 並發 10 個 cache miss 請求
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => svc.GetAllAsync<CityCode>(ctx, null))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            // 所有結果應相同（同一個快取參考）
            foreach (var r in results)
            {
                Assert.AreEqual(1, r.Count);
                Assert.AreEqual("台北", r[0].Name);
            }

            // 驗證快取命中：再查一次應回傳相同參考
            var cached = svc.GetAll<CityCode>(ctx, null);
            Assert.AreSame(results[0], cached, "All should return the same cached reference");
        }

        [TestMethod]
        public void GetAll_concurrent_misses_only_query_db_once_via_semaphore()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "高雄", Province = "南部" });
            ctx.SaveChanges();

            // 並發 10 個同步 cache miss
            var results = new IReadOnlyList<CityCode>[10];
            var threads = Enumerable.Range(0, 10).Select(i => new Thread(() =>
            {
                results[i] = svc.GetAll<CityCode>(ctx, null);
            })).ToArray();

            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            foreach (var r in results)
            {
                Assert.IsNotNull(r);
                Assert.AreEqual(1, r!.Count);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_invalidates_and_reloads()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            // 暖機
            var first = svc.GetAll<CityCode>(ctx, null);
            Assert.AreEqual(1, first.Count);

            // 新增資料後 Refresh
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "高雄", Province = "南部" });
            ctx.SaveChanges();

            await svc.RefreshAsync<CityCode>(ctx, null);

            // 應立即看到新資料，且快取已填入（不需再查 DB）
            var after = svc.GetAll<CityCode>(ctx, null);
            Assert.AreEqual(2, after.Count, "RefreshAsync should reload from DB immediately");
        }

        [TestMethod]
        public async Task RefreshAsync_fills_cache_so_next_call_is_hit()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "A", Province = "北部" });
            ctx.SaveChanges();

            await svc.RefreshAsync<CityCode>(ctx, null);

            // 新增資料但不 refresh — 快取命中應看到舊資料
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "B", Province = "南部" });
            ctx.SaveChanges();

            var cached = svc.GetAll<CityCode>(ctx, null);
            Assert.AreEqual(1, cached.Count, "Cache should be hit after RefreshAsync");
        }
    }

    // ─── FrameworkContext 整合測試（SaveChanges 自動失效）────────────────────────

    [TestClass]
    public class FrameworkContextInvalidationTests
    {
        /// <summary>
        /// FrameworkContext 子類別，加入測試用實體。
        /// SaveChanges 繼承自 FrameworkContext 並透過 LookupCacheService property 自動失效快取。
        /// </summary>
        private class TestFwContext : FrameworkContext
        {
            public TestFwContext(DbContextOptions opts) : base(opts) { }
            public DbSet<CityCode> CityCodes { get; set; } = null!;
            public DbSet<OrderRecord> OrderRecords { get; set; } = null!;

            // EmptyContext.OnConfiguring 在 options 已配置時仍會嘗試設 SqlServer，加此保護
            protected override void OnConfiguring(DbContextOptionsBuilder b)
            {
                if (b.IsConfigured) return;
                base.OnConfiguring(b);
            }

            // 跳過 FrameworkContext.OnModelCreating 中的 Utils.GetAllModels()，
            // 避免掃描所有已載入 assembly 造成 MajorId 等重複欄名衝突。
            // EF Core 透過 DbSet<> 屬性自動探索實體，不需要額外配置。
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                // intentionally no base call
            }
        }

        [TestMethod]
        public void SaveChanges_on_cacheable_entity_auto_invalidates_via_FrameworkContext()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var mc = new MemoryCache(new MemoryCacheOptions());
            var svc = new LookupCacheService(mc, new[] { typeof(CityCode).Assembly });

            var opts = new DbContextOptionsBuilder<TestFwContext>().UseSqlite(conn).Options;
            using var ctx = new TestFwContext(opts);
            ctx.Database.EnsureCreated();
            ctx.LookupCacheService = svc; // property injection（模擬 WTMContext 設定）

            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            // 暖機快取
            var first = svc.GetAll<CityCode>(ctx, null);
            Assert.AreEqual(1, first.Count);

            // 透過 FrameworkContext.SaveChanges 自動失效
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "高雄", Province = "南部" });
            ctx.SaveChanges();

            var second = svc.GetAll<CityCode>(ctx, null);
            Assert.AreEqual(2, second.Count, "Cache should auto-invalidate via FrameworkContext.SaveChanges");
        }

        [TestMethod]
        public void SaveChanges_on_non_cacheable_entity_does_not_invalidate_lookup_caches()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var mc = new MemoryCache(new MemoryCacheOptions());
            var svc = new LookupCacheService(mc, new[] { typeof(CityCode).Assembly });

            var opts = new DbContextOptionsBuilder<TestFwContext>().UseSqlite(conn).Options;
            using var ctx = new TestFwContext(opts);
            ctx.Database.EnsureCreated();
            ctx.LookupCacheService = svc;

            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            // 暖機快取
            var cached = svc.GetAll<CityCode>(ctx, null);
            Assert.AreEqual(1, cached.Count);

            // 寫入非快取型別 OrderRecord，不應影響 CityCode 快取
            ctx.OrderRecords.Add(new OrderRecord { ID = Guid.NewGuid(), Name = "訂單1" });
            ctx.SaveChanges();

            var stillCached = svc.GetAll<CityCode>(ctx, null);
            Assert.AreSame(cached, stillCached, "CityCode cache should not be invalidated by OrderRecord write");
        }

        [TestMethod]
        public void SaveChanges_without_LookupCacheService_set_completes_normally()
        {
            // LookupCacheService 未設定時，SaveChanges 仍正常運作（不 throw）
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var opts = new DbContextOptionsBuilder<TestFwContext>().UseSqlite(conn).Options;
            using var ctx = new TestFwContext(opts);
            ctx.Database.EnsureCreated();
            // 故意不設定 LookupCacheService

            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台南", Province = "南部" });
            var count = ctx.SaveChanges();

            Assert.AreEqual(1, count);
        }
    }

    // ─── Attribute tests ─────────────────────────────────────────────────────────

    [TestClass]
    public class CacheLookupAttributeTests
    {
        [TestMethod]
        public void Attribute_default_values_are_correct()
        {
            var attr = new CacheLookupAttribute();

            Assert.AreEqual(30, attr.TtlMinutes);
            Assert.IsNull(attr.TenantIsolationOrNull, "TenantIsolation should be null (unset) by default");
            Assert.IsTrue(attr.WarmOnStartup);
            Assert.IsNull(attr.ConnectionKey, "ConnectionKey should be null by default");
        }

        [TestMethod]
        public void Attribute_custom_values_are_applied()
        {
            var attr = new CacheLookupAttribute
            {
                TtlMinutes = 120,
                TenantIsolation = false,
                WarmOnStartup = false
            };

            Assert.AreEqual(120, attr.TtlMinutes);
            Assert.IsFalse(attr.TenantIsolation);
            Assert.IsFalse(attr.TenantIsolationOrNull);
            Assert.IsFalse(attr.WarmOnStartup);
        }

        [TestMethod]
        public void Attribute_TenantIsolation_null_by_default()
        {
            var attr = new CacheLookupAttribute();

            Assert.IsNull(attr.TenantIsolationOrNull,
                "TenantIsolation should be null (unset) by default, meaning use global default");
        }

        [TestMethod]
        public void Attribute_TenantIsolation_explicit_true_overrides_null()
        {
            var attr = new CacheLookupAttribute { TenantIsolation = true };

            Assert.IsTrue(attr.TenantIsolationOrNull);
            Assert.IsTrue(attr.TenantIsolation);
        }

        [TestMethod]
        public void Attribute_TenantIsolation_explicit_false_overrides_null()
        {
            var attr = new CacheLookupAttribute { TenantIsolation = false };

            Assert.IsFalse(attr.TenantIsolationOrNull);
            Assert.IsFalse(attr.TenantIsolation);
        }

        [TestMethod]
        public void Attribute_ConnectionKey_null_by_default()
        {
            var attr = new CacheLookupAttribute();

            Assert.IsNull(attr.ConnectionKey);
        }

        [TestMethod]
        public void Attribute_ConnectionKey_can_be_set()
        {
            var attr = new CacheLookupAttribute { ConnectionKey = "orss" };

            Assert.AreEqual("orss", attr.ConnectionKey);
        }

        [TestMethod]
        public void Attribute_is_not_inherited_by_subclass()
        {
            // Inherited = false — 子類別不自動繼承快取設定
            var subAttr = typeof(CityCode).GetCustomAttributes(typeof(CacheLookupAttribute), inherit: false);
            var inheritedAttr = typeof(CityCode).GetCustomAttributes(typeof(CacheLookupAttribute), inherit: true);

            Assert.AreEqual(1, subAttr.Length);
            Assert.AreEqual(1, inheritedAttr.Length);
        }
    }
}
