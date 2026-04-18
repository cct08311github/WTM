#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Cache;

namespace WalkingTec.Mvvm.Core.Test.Cache
{
    /// <summary>
    /// Tests for issue #826: ILookupCacheService.GetStats observability API.
    /// Uses the same fixture types (CityCode / StatusDict / OrderRecord /
    /// NoWarmDict) declared in LookupCacheTests.cs.
    /// </summary>
    [TestClass]
    public class LookupCacheStatsTests
    {
        // ── GetStats(Type) — unregistered types ──────────────────────────

        [TestMethod]
        public void GetStats_returns_null_for_type_not_marked_CacheLookup()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = TestHelper.Create(conn);

            Assert.IsNull(svc.GetStats(typeof(OrderRecord)));
        }

        [TestMethod]
        public void GetStats_returns_zero_counters_before_first_access()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = TestHelper.Create(conn);

            var stats = svc.GetStats(typeof(CityCode))!;
            Assert.IsNotNull(stats);
            Assert.AreEqual("WalkingTec.Mvvm.Core.Test.Cache.CityCode", stats.EntityTypeName);
            Assert.AreEqual(0, stats.Hits);
            Assert.AreEqual(0, stats.Misses);
            Assert.AreEqual(0, stats.InvalidateCount);
            Assert.AreEqual(0, stats.CurrentlyCachedTenantKeys);
            Assert.IsNull(stats.LastAccessAt);
            Assert.IsNull(stats.LastWarmAt);
            Assert.IsNull(stats.LastInvalidatedAt);
            Assert.AreEqual(10, stats.TtlMinutesConfigured);
            Assert.IsTrue(stats.WarmOnStartup);
        }

        // ── Hit / Miss counters ──────────────────────────────────────────

        [TestMethod]
        public void GetAll_first_call_records_miss_and_sets_LastAccessAt()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { Name = "Taipei", Province = "TW" });
            ctx.SaveChanges();

            svc.GetAll<CityCode>(ctx);

            var stats = svc.GetStats(typeof(CityCode))!;
            Assert.AreEqual(0, stats.Hits);
            Assert.AreEqual(1, stats.Misses);
            Assert.IsNotNull(stats.LastAccessAt);
            Assert.IsNotNull(stats.LastWarmAt, "SetCache should have recorded a warm timestamp on first load.");
            Assert.AreEqual(1, stats.CurrentlyCachedTenantKeys);
        }

        [TestMethod]
        public void GetAll_subsequent_calls_record_hits()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { Name = "Taipei", Province = "TW" });
            ctx.SaveChanges();

            svc.GetAll<CityCode>(ctx);   // miss
            svc.GetAll<CityCode>(ctx);   // hit
            svc.GetAll<CityCode>(ctx);   // hit
            svc.GetAll<CityCode>(ctx);   // hit

            var stats = svc.GetStats(typeof(CityCode))!;
            Assert.AreEqual(3, stats.Hits);
            Assert.AreEqual(1, stats.Misses);
        }

        [TestMethod]
        public async Task GetAllAsync_records_counters_same_as_sync()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { Name = "Taipei", Province = "TW" });
            ctx.SaveChanges();

            await svc.GetAllAsync<CityCode>(ctx);
            await svc.GetAllAsync<CityCode>(ctx);

            var stats = svc.GetStats(typeof(CityCode))!;
            Assert.AreEqual(1, stats.Hits);
            Assert.AreEqual(1, stats.Misses);
        }

        // ── Invalidate ────────────────────────────────────────────────────

        [TestMethod]
        public void Invalidate_increments_count_and_clears_currently_cached()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { Name = "Taipei", Province = "TW" });
            ctx.SaveChanges();

            svc.GetAll<CityCode>(ctx);       // cache warm (1 tenant key)
            Assert.AreEqual(1, svc.GetStats(typeof(CityCode))!.CurrentlyCachedTenantKeys);

            svc.Invalidate<CityCode>();       // tenantId null → drops "_" key

            var stats = svc.GetStats(typeof(CityCode))!;
            Assert.AreEqual(1, stats.InvalidateCount);
            Assert.IsNotNull(stats.LastInvalidatedAt);
            Assert.AreEqual(0, stats.CurrentlyCachedTenantKeys);
        }

        [TestMethod]
        public void InvalidateType_clears_all_tenant_keys()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.StatusDicts.Add(new StatusDict { Code = "A" });
            ctx.SaveChanges();

            // StatusDict has TenantIsolation=true → warm 3 tenants
            svc.GetAll<StatusDict>(ctx, tenantId: "t1");
            svc.GetAll<StatusDict>(ctx, tenantId: "t2");
            svc.GetAll<StatusDict>(ctx, tenantId: "t3");
            Assert.AreEqual(3, svc.GetStats(typeof(StatusDict))!.CurrentlyCachedTenantKeys);

            svc.InvalidateType(typeof(StatusDict));

            var stats = svc.GetStats(typeof(StatusDict))!;
            Assert.AreEqual(1, stats.InvalidateCount);
            Assert.AreEqual(0, stats.CurrentlyCachedTenantKeys);
        }

        // ── GetStats() — all types ───────────────────────────────────────

        [TestMethod]
        public void GetStats_all_returns_entry_for_every_registered_type()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (_, svc, _) = TestHelper.Create(conn);

            var all = svc.GetStats();

            // Registered cacheable types in the test assembly:
            //   CityCode, StatusDict, NoWarmDict  (OrderRecord is NOT marked)
            Assert.AreEqual(3, all.Count, "Expected 3 registered cacheable types.");
            CollectionAssert.AllItemsAreNotNull((System.Collections.ICollection)all);
            // Sorted by FullName ascending
            var names = all.Select(s => s.EntityTypeName).ToArray();
            CollectionAssert.AreEqual(names.OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);
        }

        [TestMethod]
        public void GetStats_all_preserves_individual_counter_state()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { Name = "Taipei", Province = "TW" });
            ctx.SaveChanges();

            svc.GetAll<CityCode>(ctx);       // 1 miss on CityCode
            svc.GetAll<CityCode>(ctx);       // 1 hit on CityCode
            // StatusDict untouched

            var all = svc.GetStats();
            var cityStats = all.Single(s => s.EntityTypeName.EndsWith("CityCode"));
            var statusStats = all.Single(s => s.EntityTypeName.EndsWith("StatusDict"));

            Assert.AreEqual(1, cityStats.Hits);
            Assert.AreEqual(1, cityStats.Misses);
            Assert.AreEqual(0, statusStats.Hits);
            Assert.AreEqual(0, statusStats.Misses);
        }

        // ── Stats query itself is NOT a hit ──────────────────────────────

        [TestMethod]
        public void GetStats_does_not_count_as_hit_or_miss()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { Name = "Taipei", Province = "TW" });
            ctx.SaveChanges();

            svc.GetAll<CityCode>(ctx);   // warm: 1 miss
            _ = svc.GetStats(typeof(CityCode));
            _ = svc.GetStats();
            _ = svc.GetStats(typeof(CityCode));

            var stats = svc.GetStats(typeof(CityCode))!;
            Assert.AreEqual(0, stats.Hits, "GetStats must not increment Hits.");
            Assert.AreEqual(1, stats.Misses, "GetStats must not increment Misses.");
        }

        // ── RefreshAsync + LastWarmAt ────────────────────────────────────

        [TestMethod]
        public async Task RefreshAsync_updates_LastWarmAt()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, _) = TestHelper.Create(conn);
            ctx.CityCodes.Add(new CityCode { Name = "Taipei", Province = "TW" });
            ctx.SaveChanges();

            svc.GetAll<CityCode>(ctx);                   // warm1
            var warm1 = svc.GetStats(typeof(CityCode))!.LastWarmAt;
            Assert.IsNotNull(warm1);

            // Ensure at least 2 clock ticks pass so warm2 can differ from warm1
            // on platforms where DateTimeOffset.UtcNow has 100ns granularity
            // but the test might be running fast enough to hit the same tick.
            await Task.Delay(10);

            await svc.RefreshAsync<CityCode>(ctx);        // reload
            var warm2 = svc.GetStats(typeof(CityCode))!.LastWarmAt;
            Assert.IsNotNull(warm2);
            Assert.IsTrue(warm2 >= warm1, "LastWarmAt should advance on Refresh.");
        }
    }
}
