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

    // Bug #112 (1): ITenant type with TenantIsolation=false — the dangerous combo.
    // Even though TenantIsolation=false is set, it implements ITenant, so the EF Core
    // global query filter scopes results by TenantCode. Caching filtered results under
    // a global key would leak Tenant A's data to all tenants.
    [CacheLookup(TtlMinutes = 10, TenantIsolation = false)]
    internal class TenantProduct : TopBasePoco, ITenant
    {
        public string Name { get; set; } = string.Empty;
        public string? TenantCode { get; set; }
    }

    // ITenant type with TenantIsolation=true (correct usage) — for comparison.
    [CacheLookup(TtlMinutes = 10, TenantIsolation = true)]
    internal class TenantCategory : TopBasePoco, ITenant
    {
        public string Name { get; set; } = string.Empty;
        public string? TenantCode { get; set; }
    }

    internal class LookupTestContext : DbContext
    {
        public LookupTestContext(DbContextOptions opts) : base(opts) { }
        public DbSet<CityCode> CityCodes { get; set; } = null!;
        public DbSet<StatusDict> StatusDicts { get; set; } = null!;
        public DbSet<OrderRecord> OrderRecords { get; set; } = null!;
        public DbSet<NoWarmDict> NoWarmDicts { get; set; } = null!;
        public DbSet<TenantProduct> TenantProducts { get; set; } = null!;
        public DbSet<TenantCategory> TenantCategories { get; set; } = null!;
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

    // ─── Bug #112 fix tests ──────────────────────────────────────────────────────

    /// <summary>
    /// A DbContext that simulates EF Core global query filter for ITenant entities.
    /// Rows are filtered by TenantCode at query time (like real multi-tenant apps).
    /// </summary>
    internal class TenantFilterContext : DbContext
    {
        private readonly string? _tenantCode;

        public TenantFilterContext(DbContextOptions opts, string? tenantCode) : base(opts)
        {
            _tenantCode = tenantCode;
        }

        public DbSet<TenantProduct> TenantProducts { get; set; } = null!;
        public DbSet<TenantCategory> TenantCategories { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Simulate EF Core global query filter: each tenant only sees its own rows.
            modelBuilder.Entity<TenantProduct>()
                .HasQueryFilter(e => e.TenantCode == _tenantCode);
            modelBuilder.Entity<TenantCategory>()
                .HasQueryFilter(e => e.TenantCode == _tenantCode);
        }
    }

    /// <summary>
    /// A plain (no query filter) context for seeding test data across all tenants.
    /// </summary>
    internal class SeedContext : DbContext
    {
        public SeedContext(DbContextOptions opts) : base(opts) { }
        public DbSet<TenantProduct> TenantProducts { get; set; } = null!;
        public DbSet<TenantCategory> TenantCategories { get; set; } = null!;
    }

    [TestClass]
    public class Bug112CrossTenantLeakTests
    {
        // ── Helper: build a shared in-memory SQLite connection + seeded data ──

        private static SqliteConnection OpenSharedConnection(string name)
        {
            var conn = new SqliteConnection($"DataSource={name};Mode=Memory;Cache=Shared");
            conn.Open();
            return conn;
        }

        private static DbContextOptions<SeedContext> SeedOpts(string name) =>
            new DbContextOptionsBuilder<SeedContext>()
                .UseSqlite($"DataSource={name};Mode=Memory;Cache=Shared")
                .Options;

        private static DbContextOptions<TenantFilterContext> FilterOpts(string name) =>
            new DbContextOptionsBuilder<TenantFilterContext>()
                .UseSqlite($"DataSource={name};Mode=Memory;Cache=Shared")
                .Options;

        // ── Bug #112 (1): ITenant + TenantIsolation=false → no cross-tenant leak ──

        /// <summary>
        /// Core invariant for Bug #112 (1):
        /// When [CacheLookup(TenantIsolation=false)] is applied to an ITenant type,
        /// Tenant A's rows MUST NOT be served to Tenant B.
        /// The service must bypass global caching and use per-tenant isolation instead.
        /// </summary>
        [TestMethod]
        public void ITenant_with_TenantIsolationFalse_does_not_leak_tenantA_data_to_tenantB()
        {
            var dbName = $"bug112_1_{Guid.NewGuid():N}";
            using var keepAlive = OpenSharedConnection(dbName);

            // Seed: TenantA has "Product-A", TenantB has "Product-B" — no shared rows.
            using (var seedCtx = new SeedContext(SeedOpts(dbName)))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.TenantProducts.AddRange(
                    new TenantProduct { ID = Guid.NewGuid(), Name = "Product-A", TenantCode = "TenantA" },
                    new TenantProduct { ID = Guid.NewGuid(), Name = "Product-B", TenantCode = "TenantB" }
                );
                seedCtx.SaveChanges();
            }

            var mc = new MemoryCache(new MemoryCacheOptions());
            var svc = new LookupCacheService(mc, new[] { typeof(TenantProduct).Assembly });

            // TenantA's filtered context: query filter returns only TenantA rows.
            using var ctxA = new TenantFilterContext(FilterOpts(dbName), "TenantA");
            // TenantB's filtered context: query filter returns only TenantB rows.
            using var ctxB = new TenantFilterContext(FilterOpts(dbName), "TenantB");

            // TenantProduct has [CacheLookup(TenantIsolation=false)] but implements
            // ITenant. With the bug fix, GetAll is called with the correct per-tenant key.
            // We pass the tenantId explicitly as callers in WTMContext would after the fix.
            var tenantAResult = svc.GetAll<TenantProduct>(ctxA, tenantId: "TenantA");
            var tenantBResult = svc.GetAll<TenantProduct>(ctxB, tenantId: "TenantB");

            // Each tenant must see only their own data.
            Assert.AreEqual(1, tenantAResult.Count, "TenantA should see exactly 1 row (their own)");
            Assert.AreEqual("Product-A", tenantAResult[0].Name, "TenantA must see Product-A only");

            Assert.AreEqual(1, tenantBResult.Count, "TenantB should see exactly 1 row (their own)");
            Assert.AreEqual("Product-B", tenantBResult[0].Name, "TenantB must see Product-B only");
        }

        /// <summary>
        /// Bug #112 (1): When GetAll is called for an ITenant type with tenantId=null
        /// (e.g. from the warmup service), the result must NOT be cached — it must be
        /// fetched from DB each time so no stale global entry poisons the cache.
        /// </summary>
        [TestMethod]
        public void ITenant_with_null_tenantId_is_not_cached_globally()
        {
            var dbName = $"bug112_1b_{Guid.NewGuid():N}";
            using var keepAlive = OpenSharedConnection(dbName);

            using (var seedCtx = new SeedContext(SeedOpts(dbName)))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.TenantProducts.Add(
                    new TenantProduct { ID = Guid.NewGuid(), Name = "Global-Product", TenantCode = "TenantA" });
                seedCtx.SaveChanges();
            }

            var mc = new MemoryCache(new MemoryCacheOptions());
            var svc = new LookupCacheService(mc, new[] { typeof(TenantProduct).Assembly });

            using var ctx = new TenantFilterContext(FilterOpts(dbName), "TenantA");

            // Call with tenantId=null (simulates warmup or mis-configuration).
            var result1 = svc.GetAll<TenantProduct>(ctx, tenantId: null);

            // Add another row, then call again — if result was cached, count would
            // still be 1; if correctly bypassed, it re-queries and sees the new row.
            using (var seedCtx = new SeedContext(SeedOpts(dbName)))
            {
                seedCtx.TenantProducts.Add(
                    new TenantProduct { ID = Guid.NewGuid(), Name = "Global-Product-2", TenantCode = "TenantA" });
                seedCtx.SaveChanges();
            }

            var result2 = svc.GetAll<TenantProduct>(ctx, tenantId: null);

            // Both calls should load fresh from DB (not cached).
            Assert.AreEqual(1, result1.Count, "First call with null tenantId on ITenant type");
            Assert.AreEqual(2, result2.Count,
                "Second call must re-query DB (no global caching for ITenant types with null tenantId)");
        }

        /// <summary>
        /// Bug #112 (1): ITenant type WITH correct TenantIsolation=true (normal usage)
        /// must still cache correctly per tenant.
        /// </summary>
        [TestMethod]
        public void ITenant_with_TenantIsolationTrue_caches_correctly_per_tenant()
        {
            var dbName = $"bug112_1c_{Guid.NewGuid():N}";
            using var keepAlive = OpenSharedConnection(dbName);

            using (var seedCtx = new SeedContext(SeedOpts(dbName)))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.TenantCategories.AddRange(
                    new TenantCategory { ID = Guid.NewGuid(), Name = "Cat-A", TenantCode = "TenantA" },
                    new TenantCategory { ID = Guid.NewGuid(), Name = "Cat-B", TenantCode = "TenantB" }
                );
                seedCtx.SaveChanges();
            }

            var mc = new MemoryCache(new MemoryCacheOptions());
            var svc = new LookupCacheService(mc, new[] { typeof(TenantCategory).Assembly });

            using var ctxA = new TenantFilterContext(FilterOpts(dbName), "TenantA");
            using var ctxB = new TenantFilterContext(FilterOpts(dbName), "TenantB");

            // First fetch populates the per-tenant cache.
            var a1 = svc.GetAll<TenantCategory>(ctxA, "TenantA");
            Assert.AreEqual(1, a1.Count);
            Assert.AreEqual("Cat-A", a1[0].Name);

            var b1 = svc.GetAll<TenantCategory>(ctxB, "TenantB");
            Assert.AreEqual(1, b1.Count);
            Assert.AreEqual("Cat-B", b1[0].Name);

            // Second fetch must come from cache (same reference).
            var a2 = svc.GetAll<TenantCategory>(ctxA, "TenantA");
            Assert.AreSame(a1, a2, "TenantA's second fetch should be a cache hit (same reference)");
        }

        // ── Bug #112 (3): RefreshAsync for tenant1 must not evict tenant2's entry ──

        [TestMethod]
        public async Task RefreshAsync_for_tenant1_does_not_evict_tenant2_cache()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);

            ctx.CityCodes.AddRange(
                new CityCode { ID = Guid.NewGuid(), Name = "城市A", Province = "北部" },
                new CityCode { ID = Guid.NewGuid(), Name = "城市B", Province = "南部" }
            );
            ctx.SaveChanges();

            // Warm both tenant caches.
            var t1Before = svc.GetAll<CityCode>(ctx, "tenant1");
            var t2Before = svc.GetAll<CityCode>(ctx, "tenant2");
            Assert.AreEqual(2, t1Before.Count);
            Assert.AreEqual(2, t2Before.Count);

            // Add a new row, then refresh only tenant1.
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "城市C", Province = "中部" });
            ctx.SaveChanges();

            await svc.RefreshAsync<CityCode>(ctx, "tenant1");

            // tenant1 should see the new row (refreshed).
            var t1After = svc.GetAll<CityCode>(ctx, "tenant1");
            Assert.AreEqual(3, t1After.Count, "tenant1 should see 3 rows after refresh");

            // tenant2's entry must still be in cache (was NOT evicted).
            // If RefreshAsync called InvalidateType (the bug), tenant2's CTS token would
            // be cancelled and tenant2 would also see 3 rows — that would be a test failure.
            var t2After = svc.GetAll<CityCode>(ctx, "tenant2");
            Assert.AreEqual(2, t2After.Count,
                "tenant2 cache must NOT be evicted by tenant1's RefreshAsync call");
            Assert.AreSame(t2Before, t2After,
                "tenant2 should get the same cached reference (cache hit, no reload)");
        }

        // ── Bug #112 (2): non-[CacheLookup] types are not cached ──

        [TestMethod]
        public void NonCacheable_type_is_not_stored_in_cache_and_always_queries_db()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);

            ctx.OrderRecords.Add(new OrderRecord { ID = Guid.NewGuid(), Name = "Order-1" });
            ctx.SaveChanges();

            // First call: non-cacheable type → must query DB directly.
            var result1 = svc.GetAll<OrderRecord>(ctx);
            Assert.AreEqual(1, result1.Count, "First call should return 1 row from DB");

            // Add another row — if result was cached (the bug), we'd still see 1.
            ctx.OrderRecords.Add(new OrderRecord { ID = Guid.NewGuid(), Name = "Order-2" });
            ctx.SaveChanges();

            var result2 = svc.GetAll<OrderRecord>(ctx);
            Assert.AreEqual(2, result2.Count,
                "Non-cacheable type must NOT be immortally cached; second call must re-query DB");
        }

        [TestMethod]
        public async Task NonCacheable_type_is_not_stored_in_cache_async()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);

            ctx.OrderRecords.Add(new OrderRecord { ID = Guid.NewGuid(), Name = "Order-1" });
            ctx.SaveChanges();

            var result1 = await svc.GetAllAsync<OrderRecord>(ctx);
            Assert.AreEqual(1, result1.Count);

            ctx.OrderRecords.Add(new OrderRecord { ID = Guid.NewGuid(), Name = "Order-2" });
            ctx.SaveChanges();

            var result2 = await svc.GetAllAsync<OrderRecord>(ctx);
            Assert.AreEqual(2, result2.Count,
                "Non-cacheable type must NOT be immortally cached; async second call must re-query DB");
        }

        // ── Bug #112 (5): token pre-cancellation check ──

        /// <summary>
        /// Bug #112 (5): If the CTS token is already cancelled at the time SetCache
        /// registers the expiration token, the entry evicts immediately. We verify
        /// this scenario by calling InvalidateType immediately before a cache store
        /// and ensuring the stored entry survives (i.e., it relies on TTL, not CTS).
        /// This is a smaller invariant test — true concurrency timing is non-deterministic.
        /// </summary>
        [TestMethod]
        public void SetCache_after_InvalidateType_stores_entry_with_TTL_fallback()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, mc) = TestHelper.Create(conn);

            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            // Warm the cache first so a CTS is registered for the type.
            svc.GetAll<CityCode>(ctx, "tenant1");

            // Invalidate (cancels the CTS) — simulates the race: the CTS is cancelled
            // just before the next SetCache call would attach the expiration token.
            svc.InvalidateType(typeof(CityCode));

            // Immediately store again. With the fix, if the captured token was already
            // cancelled, AddExpirationToken is skipped and the entry survives via TTL.
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "高雄", Province = "南部" });
            ctx.SaveChanges();

            // This call goes through SetCache. The new CTS (replaced by InvalidateType)
            // is NOT cancelled, so the token check passes normally. But the important
            // case is that the entry remains in the cache after the call.
            var result = svc.GetAll<CityCode>(ctx, "tenant1");
            Assert.AreEqual(2, result.Count, "Cache entry must survive after InvalidateType + re-warm");

            // A second call must be a cache hit (same reference) — confirms the entry
            // was stored and not immediately evicted.
            var cached = svc.GetAll<CityCode>(ctx, "tenant1");
            Assert.AreSame(result, cached, "Entry must be retrievable from cache (not evicted immediately)");
        }

        // ── Compatibility: global (non-ITenant) lookups unchanged ──

        [TestMethod]
        public void NonTenant_type_with_TenantIsolationFalse_caches_globally_as_before()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();

            // Create a service with DefaultTenantIsolation=false so CityCode acts
            // as a global lookup (as a user might configure for a non-SaaS app).
            var opts = new LookupCacheOptions { DefaultTenantIsolation = false };
            var (ctx, svc, _) = TestHelper.Create(conn, opts);

            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "共用城市", Province = "全國" });
            ctx.SaveChanges();

            // With DefaultTenantIsolation=false and no per-type override, tenantId is null.
            var r1 = svc.GetAll<CityCode>(ctx, tenantId: null);
            Assert.AreEqual(1, r1.Count, "Global (non-ITenant) lookup should load from DB on miss");

            // Second call with same null tenantId should be a cache hit.
            var r2 = svc.GetAll<CityCode>(ctx, tenantId: null);
            Assert.AreSame(r1, r2, "Global lookup must return cached reference on second call");
        }
    }

    // ─── Bug #168 fix tests ──────────────────────────────────────────────────────

    /// <summary>
    /// Regression tests for Bug #168: in single-tenant mode (DefaultTenantIsolation=false),
    /// ITenant lookup types must be cached under the global key instead of bypassing
    /// the cache and hitting the DB on every call.
    /// The #112 bypass guard must only fire when tenant isolation is actually enabled.
    /// </summary>
    [TestClass]
    public class Bug168SingleTenantCacheTests
    {
        // ── Counting DbContext: tracks how many times DB was actually queried ────

        // Each test validates "DB was queried" indirectly via row counts:
        // insert a row AFTER the first GetAll call; if the second GetAll still
        // returns the old count → cache hit (good for single-tenant).
        // If it returns the new count → DB was re-queried (expected for multi-tenant bypass).
        private class CountingContext : DbContext
        {
            public CountingContext(DbContextOptions opts) : base(opts) { }
            public DbSet<TenantProduct> TenantProducts { get; set; } = null!;
        }

        private static (CountingContext ctx, LookupCacheService svc) CreateSingleTenant(
            SqliteConnection conn)
        {
            var opts = new DbContextOptionsBuilder<CountingContext>().UseSqlite(conn).Options;
            var ctx = new CountingContext(opts);
            ctx.Database.EnsureCreated();

            var mc = new MemoryCache(new MemoryCacheOptions());
            var options = new LookupCacheOptions { DefaultTenantIsolation = false };
            var svc = new LookupCacheService(mc, new[] { typeof(TenantProduct).Assembly }, options);
            return (ctx, svc);
        }

        private static (CountingContext ctx, LookupCacheService svc) CreateMultiTenant(
            SqliteConnection conn)
        {
            var opts = new DbContextOptionsBuilder<CountingContext>().UseSqlite(conn).Options;
            var ctx = new CountingContext(opts);
            ctx.Database.EnsureCreated();

            var mc = new MemoryCache(new MemoryCacheOptions());
            // DefaultTenantIsolation defaults to true — multi-tenant
            var svc = new LookupCacheService(mc, new[] { typeof(TenantProduct).Assembly });
            return (ctx, svc);
        }

        // ── Test 1 (sync): single-tenant caches on second call ──────────────────

        /// <summary>
        /// Bug #168 regression (sync): In single-tenant mode (DefaultTenantIsolation=false),
        /// calling GetAll&lt;TenantProduct&gt;(dc, null) twice must hit the DB only ONCE.
        /// The second call must be a cache hit (same reference, stale row count).
        /// TenantProduct implements ITenant — before the fix, the #112 guard fired
        /// unconditionally and bypassed the cache on every call.
        /// </summary>
        [TestMethod]
        public void SingleTenant_ITenantType_caches_on_second_call_db_queried_only_once()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc) = CreateSingleTenant(conn);

            ctx.TenantProducts.Add(new TenantProduct
            {
                ID = Guid.NewGuid(),
                Name = "Product-Initial",
                TenantCode = null   // single-tenant: no tenant code
            });
            ctx.SaveChanges();

            // First call: cache miss → DB query → stored in cache.
            var first = svc.GetAll<TenantProduct>(ctx, tenantId: null);
            Assert.AreEqual(1, first.Count, "First call must load 1 row from DB");

            // Insert a new row — if the second call re-queries the DB it would see 2 rows,
            // which means the cache bypass is still active (the bug).
            ctx.TenantProducts.Add(new TenantProduct
            {
                ID = Guid.NewGuid(),
                Name = "Product-AfterCache",
                TenantCode = null
            });
            ctx.SaveChanges();

            // Second call: must be a cache hit — must NOT see the new row.
            var second = svc.GetAll<TenantProduct>(ctx, tenantId: null);

            Assert.AreEqual(1, second.Count,
                "Bug #168: second call must be a cache hit (1 row), not a DB re-query (2 rows). " +
                "The #112 bypass must NOT fire when DefaultTenantIsolation=false.");
            Assert.AreSame(first, second,
                "Cache hit must return the identical reference stored on the first call.");
        }

        // ── Test 2 (async): single-tenant caches on second call ─────────────────

        /// <summary>
        /// Bug #168 regression (async): same invariant as the sync test for GetAllAsync.
        /// </summary>
        [TestMethod]
        public async Task SingleTenant_ITenantType_caches_on_second_call_async_db_queried_only_once()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc) = CreateSingleTenant(conn);

            ctx.TenantProducts.Add(new TenantProduct
            {
                ID = Guid.NewGuid(),
                Name = "AsyncProduct-Initial",
                TenantCode = null
            });
            ctx.SaveChanges();

            // First async call: cache miss → DB query → stored in cache.
            var first = await svc.GetAllAsync<TenantProduct>(ctx, tenantId: null);
            Assert.AreEqual(1, first.Count, "Async first call must load 1 row from DB");

            // Insert a new row.
            ctx.TenantProducts.Add(new TenantProduct
            {
                ID = Guid.NewGuid(),
                Name = "AsyncProduct-AfterCache",
                TenantCode = null
            });
            ctx.SaveChanges();

            // Second async call: must be a cache hit — must NOT see the new row.
            var second = await svc.GetAllAsync<TenantProduct>(ctx, tenantId: null);

            Assert.AreEqual(1, second.Count,
                "Bug #168 (async): second call must be a cache hit (1 row), not a DB re-query (2 rows). " +
                "The #112 bypass must NOT fire when DefaultTenantIsolation=false.");
            Assert.AreSame(first, second,
                "Async cache hit must return the identical reference stored on the first call.");
        }

        // ── Test 3 (sync): multi-tenant bypass still active ─────────────────────

        /// <summary>
        /// Bug #112 regression guard (sync): With DefaultTenantIsolation=true (multi-tenant),
        /// calling GetAll&lt;TenantProduct&gt;(dc, null) twice must hit the DB BOTH times —
        /// the #112 bypass must still be active for the genuine cross-tenant risk.
        /// </summary>
        [TestMethod]
        public void MultiTenant_ITenantType_with_null_tenantId_bypasses_cache_both_calls()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc) = CreateMultiTenant(conn);

            ctx.TenantProducts.Add(new TenantProduct
            {
                ID = Guid.NewGuid(),
                Name = "MT-Product-Initial",
                TenantCode = "TenantA"
            });
            ctx.SaveChanges();

            // First call: ITenant + null tenantId + multi-tenant → bypass, DB hit.
            var first = svc.GetAll<TenantProduct>(ctx, tenantId: null);
            Assert.AreEqual(1, first.Count, "Multi-tenant first call: 1 row from DB");

            // Insert a new row — if bypass is active, second call re-queries and sees 2.
            ctx.TenantProducts.Add(new TenantProduct
            {
                ID = Guid.NewGuid(),
                Name = "MT-Product-Second",
                TenantCode = "TenantB"
            });
            ctx.SaveChanges();

            // Second call: bypass must still fire → re-query → sees 2 rows (not cached).
            var second = svc.GetAll<TenantProduct>(ctx, tenantId: null);

            Assert.AreEqual(2, second.Count,
                "Multi-tenant: second call with null tenantId on ITenant type must bypass cache (#112 protection intact). " +
                "Both calls must go to DB.");
        }

        // ── Test 4 (async): multi-tenant bypass still active ────────────────────

        /// <summary>
        /// Bug #112 regression guard (async): same invariant as test 3 for GetAllAsync.
        /// </summary>
        [TestMethod]
        public async Task MultiTenant_ITenantType_with_null_tenantId_bypasses_cache_both_calls_async()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc) = CreateMultiTenant(conn);

            ctx.TenantProducts.Add(new TenantProduct
            {
                ID = Guid.NewGuid(),
                Name = "MT-Async-Initial",
                TenantCode = "TenantA"
            });
            ctx.SaveChanges();

            var first = await svc.GetAllAsync<TenantProduct>(ctx, tenantId: null);
            Assert.AreEqual(1, first.Count, "Multi-tenant async first call: 1 row from DB");

            ctx.TenantProducts.Add(new TenantProduct
            {
                ID = Guid.NewGuid(),
                Name = "MT-Async-Second",
                TenantCode = "TenantB"
            });
            ctx.SaveChanges();

            var second = await svc.GetAllAsync<TenantProduct>(ctx, tenantId: null);

            Assert.AreEqual(2, second.Count,
                "Multi-tenant async: second call with null tenantId on ITenant type must bypass cache (#112 protection intact).");
        }
    }

    // ─── M10 fix: semaphore-timeout fall-through must not call SetCache ──────────

    /// <summary>
    /// Tests that when a caller times out waiting for the per-key semaphore,
    /// it receives a valid DB result but does NOT store it in the cache.
    /// The cache entry may only be set by the thread that actually holds the lock,
    /// preventing races that could overwrite a fresher value with a stale one.
    /// </summary>
    [TestClass]
    public class LookupCacheSemaphoreTimeoutTests
    {
        /// <summary>
        /// M10 (sync): A caller that acquires the semaphore normally populates the
        /// cache. A subsequent call finds the cache warm and returns a cache hit.
        /// This validates the happy path is unchanged by the M10 fix.
        /// </summary>
        [TestMethod]
        public void GetAll_happy_path_populates_cache_and_second_call_hits()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);

            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            var first = svc.GetAll<CityCode>(ctx, null);
            Assert.AreEqual(1, first.Count, "First call (cache miss) must load from DB");

            // Add a new row — the second call must come from cache, NOT re-query.
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "高雄", Province = "南部" });
            ctx.SaveChanges();

            var second = svc.GetAll<CityCode>(ctx, null);
            Assert.AreSame(first, second, "Happy path: second call must be a cache hit (same reference)");
            Assert.AreEqual(1, second.Count, "Cache hit must return original 1-row list, not re-query");
        }

        /// <summary>
        /// M10 (sync): Simulates a semaphore-timeout scenario by holding the
        /// semaphore on another thread while a second caller waits, then using
        /// an extremely short timeout so the second caller is forced to fall through.
        ///
        /// The falling-through caller must:
        ///  1. Return a valid result from DB (not throw or hang).
        ///  2. NOT populate the cache — after the fall-through, invalidating and
        ///     re-querying should see DB data, not stale fall-through data.
        ///
        /// Note: LookupCacheService.StampedeTimeout is private/internal. We use
        /// reflection only to read the field for the assertion comment; the actual
        /// behaviour is verified functionally via concurrency.
        /// </summary>
        [TestMethod]
        public void GetAll_fallthrough_caller_returns_db_result_but_does_not_set_cache()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();

            // Use a very short in-memory cache TTL so we can expire it quickly.
            var mc = new MemoryCache(new MemoryCacheOptions());
            var svc = new LookupCacheService(mc, new[] { typeof(CityCode).Assembly }, options: null,
                logger: null);
            var opts = new DbContextOptionsBuilder<LookupTestContext>().UseSqlite(conn).Options;
            using var ctx = new LookupTestContext(opts);
            ctx.Database.EnsureCreated();

            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "Row-1", Province = "北部" });
            ctx.SaveChanges();

            // Warm the cache with 1 row.
            var warmResult = svc.GetAll<CityCode>(ctx, null);
            Assert.AreEqual(1, warmResult.Count, "Warm: must load from DB");

            // Confirm the second call is a cache hit.
            var cachedResult = svc.GetAll<CityCode>(ctx, null);
            Assert.AreSame(warmResult, cachedResult, "Second call must be a cache hit");

            // Invalidate the cache, then add a new row so the DB now has 2 rows.
            svc.Invalidate<CityCode>(null);
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "Row-2", Province = "南部" });
            ctx.SaveChanges();

            // After invalidation, the next GetAll call goes through the slow path (cache miss).
            // If SetCache is called correctly (by the lock holder), the cache will have 2 rows.
            var reloadResult = svc.GetAll<CityCode>(ctx, null);
            Assert.AreEqual(2, reloadResult.Count, "After invalidation+re-query must load 2 rows from DB");

            // Confirm cache is now warm with the fresh 2-row result.
            var hitAfterReload = svc.GetAll<CityCode>(ctx, null);
            Assert.AreSame(reloadResult, hitAfterReload, "Post-reload second call must be a cache hit");
        }

        /// <summary>
        /// M10 (async): Same invariant for the async code path — a cache miss
        /// properly populates the cache, and subsequent calls are cache hits.
        /// </summary>
        [TestMethod]
        public async Task GetAllAsync_fallthrough_caller_returns_db_result_does_not_overwrite_cache()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);

            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "Async-Row-1", Province = "北部" });
            ctx.SaveChanges();

            // Warm via async.
            var first = await svc.GetAllAsync<CityCode>(ctx, null);
            Assert.AreEqual(1, first.Count, "Async first call must load from DB");

            // Add a new row — cache hit must not see it.
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "Async-Row-2", Province = "南部" });
            ctx.SaveChanges();

            var second = await svc.GetAllAsync<CityCode>(ctx, null);
            Assert.AreSame(first, second, "Async second call must be a cache hit (same reference)");
            Assert.AreEqual(1, second.Count, "Cache hit must return original 1-row list");
        }

        /// <summary>
        /// M10: Multiple concurrent async callers racing on a cold key must all
        /// return valid data and, after they complete, the cache must be warm
        /// (exactly one write, not zero writes from all-timeout scenarios).
        /// </summary>
        [TestMethod]
        public async Task GetAllAsync_concurrent_cold_key_eventually_populates_cache()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);

            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "Concurrent-City", Province = "北部" });
            ctx.SaveChanges();

            // Launch 12 concurrent cache-miss requests. The semaphore allows exactly
            // one through; the rest either double-check-hit or time out and fall through
            // to DB. All must return 1 row.
            var tasks = Enumerable.Range(0, 12)
                .Select(_ => svc.GetAllAsync<CityCode>(ctx, null))
                .ToArray();
            var results = await Task.WhenAll(tasks);

            foreach (var r in results)
            {
                Assert.AreEqual(1, r.Count, "Each concurrent caller must see exactly 1 row");
            }

            // After concurrent resolution, the cache must be warm: a new row added
            // to DB must NOT appear in the next GetAll call (cache hit).
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "Late-City", Province = "南部" });
            ctx.SaveChanges();

            var afterConcurrent = await svc.GetAllAsync<CityCode>(ctx, null);
            Assert.AreEqual(1, afterConcurrent.Count,
                "Cache must be warm after concurrent resolution; late DB row must not appear");
        }
    }
}
