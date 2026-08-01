#nullable enable
// Issue #944 — LookupCacheService.RefreshAsync<T> must skip unregistered (non-[CacheLookup])
// types instead of caching them forever.
//
// Confirmed directly against LookupCacheService.cs before writing this test: GetAll/GetAllAsync
// both open with the SAME first check (see the "Bug #112 (2)" comment at the top of each,
// LookupCacheService.cs :167-173 sync / :259-263 async): if the type is not in `_registry`
// (i.e. IsCacheable(typeof(T)) == false), load straight from the DB and return — never touch
// the cache. RefreshAsync (pre-#944-fix, post-#804-fix — the `if (!acquired)` timeout guard is
// already present) has no equivalent check at all and proceeds straight to
// Invalidate<T>/LoadFromDbAsync/SetCache for ANY T, registered or not.
//
// Why this is a real defect, re-derived from SetCache's own code (not assumed from the issue
// text): SetCache<T> (private helper) only sets entry.AbsoluteExpirationRelativeToNow when
// `_registry.TryGetValue(typeof(T), out var attr)` succeeds. For an unregistered T that branch
// is skipped, so the entry gets NO TTL — and no working CTS-based invalidation path either,
// since nothing ever calls InvalidateType for a type nobody registered as a lookup type. The
// entry then sits in IMemoryCache until process restart or size-based eviction pressure. This
// is reachable in production via WTMContext.RefreshLookupAsync<T>(), which calls
// svc.GetAttribute(typeof(T)) (returns null for an unregistered type, no guard) and then
// unconditionally calls svc.RefreshAsync<T>(...) regardless.
//
// "Unregistered" is defined precisely by IsCacheable(Type) == _registry.ContainsKey(entityType)
// — OrderRecord (defined in LookupCacheTests.cs, same namespace) has no [CacheLookup] attribute
// and is reused directly here as the unregistered fixture, per that file's own "未標記
// [CacheLookup]" comment. CityCode ([CacheLookup(TtlMinutes = 10, WarmOnStartup = true)], also
// from LookupCacheTests.cs) is reused as the registered-type negative control.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.Cache
{
    [TestClass]
    public class LookupCacheRefreshAsyncRegistryCheckTests944
    {
        // BuildKey is private on LookupCacheService (`$"wtm:lookup:{type.FullName}:{tid}"`,
        // tid = tenantId ?? "_" when tenantId is null/empty — see LookupCacheService.cs's own
        // BuildKey). Reconstructed here inline rather than via reflection into the private
        // method, per this repo's convention of not reflecting into implementation details for
        // tests — the format is simple, stable, and already documented in this file's own XML
        // doc comment ("快取鍵格式：wtm:lookup:{type.FullName}:{tenantId}").
        private static string ExpectedKey(Type type, string? tenantId = null) =>
            $"wtm:lookup:{type.FullName}:{(string.IsNullOrEmpty(tenantId) ? "_" : tenantId)}";

        [TestMethod]
        public async Task RefreshAsync_UnregisteredType_DoesNotWriteToCache()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, mc) = TestHelper.Create(conn);

            ctx.OrderRecords.Add(new OrderRecord { ID = Guid.NewGuid(), Name = "Unregistered-Order" });
            ctx.SaveChanges();

            await svc.RefreshAsync<OrderRecord>(ctx, null);

            var key = ExpectedKey(typeof(OrderRecord));
            // Bug #944: OrderRecord has no [CacheLookup] attribute (IsCacheable == false).
            // RefreshAsync must be a no-op for it — nothing may ever land in the cache under this
            // key, because an unregistered type's entry would get no TTL (SetCache's TTL branch
            // is gated on _registry.TryGetValue succeeding) and no working invalidation path, so
            // it would live in IMemoryCache immortally.
            Assert.IsFalse(mc.TryGetValue(key, out _),
                "Bug #944: RefreshAsync<T> must not write an unregistered (non-[CacheLookup]) " +
                "type to the cache — such an entry would never expire (SetCache only sets a TTL " +
                "for registered types) and has no working invalidation path.");
        }

        [TestMethod]
        public async Task RefreshAsync_RegisteredType_StillCachesNormally_NegativeControl()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, mc) = TestHelper.Create(conn);

            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            await svc.RefreshAsync<CityCode>(ctx, null);

            var key = ExpectedKey(typeof(CityCode));
            // Negative control / boundary: a REGISTERED type ([CacheLookup] present) must still
            // be cached by RefreshAsync exactly as before — the #944 fix only skips unregistered
            // types; it must not touch this path. This must stay green both before and after the
            // #944 fix (the fix only ADDS a bypass that returns before reaching BuildKey/SetCache
            // for unregistered T — a registered T never enters that new branch).
            Assert.IsTrue(mc.TryGetValue<IReadOnlyList<CityCode>>(key, out var cached),
                "A registered [CacheLookup] type must still be cached by RefreshAsync.");
            Assert.IsNotNull(cached);
            Assert.AreEqual(1, cached!.Count, "RefreshAsync must cache the freshly loaded data.");
            Assert.AreEqual("台北", cached[0].Name);
        }
    }
}
