#nullable enable
// Issue #975 -- DistributedLookupCacheService.RefreshAsync<T> must skip unregistered
// (non-[CacheLookup]) types instead of writing them to the distributed cache.
//
// Re-derived directly against DistributedLookupCacheService.cs before writing this test (per
// this issue's own instruction not to assume #944's implementation transfers): GetAll/GetAllAsync
// (DistributedLookupCacheService.cs lines ~188 sync / ~245 async) both open with the SAME first
// check -- `if (!_registry.ContainsKey(typeof(T))) return LoadFromDb...` -- before touching the
// distributed cache at all. RefreshAsync<T> (post-#943-fix -- the `if (!acquired)` semaphore
// timeout guard from #943 is already present) has NO equivalent check and proceeds straight to
// Invalidate<T>/LoadFromDbAsync/SetDistributedAsync for ANY T, registered or not. This is the
// exact same structural gap #944 fixed in the sibling in-memory LookupCacheService.RefreshAsync.
//
// Where this ticket differs from #944 (confirmed by reading BuildCacheEntryOptions, not assumed):
// SetDistributedAsync -> BuildCacheEntryOptions(attr) falls back to `TimeSpan.FromMinutes(30)`
// when `_registry.TryGetValue(entityType, out var a)` fails (i.e. the type is unregistered) --
// see DistributedLookupCacheService.cs's BuildCacheEntryOptions. So an unregistered type written
// here is NOT immortal the way #944's IMemoryCache entry was (LookupCacheService.SetCache only
// sets AbsoluteExpirationRelativeToNow when the registry lookup succeeds, so an unregistered
// type there got NO TTL at all). Here it self-expires after a bounded 30 minutes. That is why
// this is a separate issue instead of being folded into #944 -- the consequence (bounded-TTL
// pollution + bypassing the IsCacheable contract, vs. an immortal cache entry) differs even
// though the missing guard is structurally identical. Still a real defect: an unregistered type
// should never be written to the cache at all, regardless of how long the wrongly-cached entry
// would have lived.
//
// "Unregistered" is defined precisely by IsCacheable(Type) == _registry.ContainsKey(entityType),
// re-derived from THIS class's own registry field, not assumed from #944. DOrder (no
// [CacheLookup] attribute, defined in DistributedLookupCacheTests.cs, same namespace/assembly,
// `internal`) is reused directly as the unregistered fixture -- that file's own
// Uncacheable_type_always_loads_from_db test already establishes DOrder as the "no [CacheLookup]"
// case for this service. DCity ([CacheLookup(TtlMinutes = 10, WarmOnStartup = true)], same file)
// is reused as the registered-type negative control.

using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.Cache
{
    [TestClass]
    public class DistributedLookupCacheRefreshAsyncRegistryCheckTests975
    {
        // BuildKey is private on DistributedLookupCacheService
        // (`$"wtm:lookup:{type.FullName}:{tid}"`, tid = tenantId ?? "_" when tenantId is
        // null/empty -- see DistributedLookupCacheService.cs's own BuildKey and its XML doc
        // comment's "快取鍵格式" line). Reconstructed here inline rather than via reflection into
        // the private method, per this repo's convention of not reflecting into implementation
        // details for tests -- the format is simple, stable, and already documented.
        private static string ExpectedKey(Type type, string? tenantId = null) =>
            $"wtm:lookup:{type.FullName}:{(string.IsNullOrEmpty(tenantId) ? "_" : tenantId)}";

        [TestMethod]
        public async Task RefreshAsync_UnregisteredType_DoesNotWriteToCache()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, distCache) = DistTestHelper.Create(conn);

            ctx.DOrders.Add(new DOrder { ID = Guid.NewGuid(), Ref = "Unregistered-Order" });
            ctx.SaveChanges();

            await svc.RefreshAsync<DOrder>(ctx, null);

            var key = ExpectedKey(typeof(DOrder));
            // Bug #975: DOrder has no [CacheLookup] attribute (IsCacheable == false).
            // RefreshAsync must be a no-op for it -- nothing may ever land in the distributed
            // cache under this key. Unlike #944's IMemoryCache entry, an unregistered type
            // written here would NOT be immortal (BuildCacheEntryOptions falls back to a bounded
            // 30-minute TTL when the type is not in _registry) -- but it must still never be
            // written at all, because doing so bypasses the IsCacheable contract GetAll/
            // GetAllAsync already enforce for the exact same type.
            Assert.IsNull(distCache.Get(key),
                "Bug #975: RefreshAsync<T> must not write an unregistered (non-[CacheLookup]) " +
                "type to the distributed cache -- even though such an entry would self-expire " +
                "after the bounded 30-minute fallback TTL (unlike #944's immortal IMemoryCache " +
                "entry), it must never be written in the first place.");
        }

        [TestMethod]
        public async Task RefreshAsync_RegisteredType_StillCachesNormally_NegativeControl()
        {
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var (ctx, svc, distCache) = DistTestHelper.Create(conn);

            ctx.DCities.Add(new DCity { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            await svc.RefreshAsync<DCity>(ctx, null);

            var key = ExpectedKey(typeof(DCity));
            // Negative control / boundary: a REGISTERED type ([CacheLookup] present) must still
            // be cached by RefreshAsync exactly as before -- the #975 fix only skips unregistered
            // types; it must not touch this path. This must stay green both before and after the
            // #975 fix (the fix only ADDS a bypass that returns before reaching BuildKey/
            // SetDistributedAsync for unregistered T -- a registered T never enters that new
            // branch).
            Assert.IsNotNull(distCache.Get(key),
                "A registered [CacheLookup] type must still be written to the distributed cache " +
                "by RefreshAsync.");

            // Confirm the cached content is actually usable (round-trips through GetAllAsync),
            // not just that some bytes happen to be present under the key.
            var cached = await svc.GetAllAsync<DCity>(ctx, null);
            Assert.AreEqual(1, cached.Count, "RefreshAsync must cache the freshly loaded data.");
            Assert.AreEqual("台北", cached[0].Name);
        }
    }
}
