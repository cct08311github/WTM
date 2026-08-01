#nullable enable
// Issue #943 — DistributedLookupCacheService stampede-protection RefreshAsync semaphore-timeout
// tests. Verbatim same-shape defect as #804 (LookupCacheService.RefreshAsync, fixed in
// 0459cf90f), confirmed by direct comparison of the two files before writing this test:
//
// DistributedLookupCacheService.RefreshAsync<T> (pre-fix, current lines ~345-362) computed
// `bool acquired = await semaphore.WaitAsync(StampedeTimeout, ct)` but never checked `acquired`
// before proceeding to Invalidate<T>(tenantId) / LoadFromDbAsync / SetDistributedAsync /
// RecordWarm — exactly the gap #804 closed in LookupCacheService.RefreshAsync. This class's own
// GetAll/GetAllAsync (lines ~178-287) already have the correct `if (!acquired)` fallback shape
// (return the DB result without caching), so RefreshAsync here is the same kind of "one call
// site never got the fix" gap, just in the distributed sibling file.
//
// One real difference from #804 that this issue also had to fix: unlike LookupCacheService,
// this class's StampedeTimeout was STILL the hardcoded
// `private static readonly TimeSpan StampedeTimeout = TimeSpan.FromSeconds(10)` — never wired
// to LookupCacheOptions.StampedeTimeout the way #804 wired LookupCacheService's field, even
// though the constructor already accepts and stores `LookupCacheOptions? options` in `_options`.
// Without wiring it, there would be no way to test the timeout branch without a real 10-second
// wait — the same testability reason #804 called out. See the fix's own comment above the
// StampedeTimeout property in DistributedLookupCacheService.cs.
//
// Reuses StampedeTestHelper804.CreateFileWalContext, CountingDelayReaderInterceptor804,
// StampedeCityContext, and CityCode directly from LookupCacheStampedeRefreshTimeoutTests804.cs
// (same namespace, all internal) — RefreshAsync<T>(DbContext dc, ...) does not care which
// concrete DbContext subtype or which lookup-cache-service implementation it is called through,
// so none of that fixture is duplicated here.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Cache;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Cache
{
    [TestClass]
    public class DistributedLookupCacheStampedeRefreshTimeoutTests943
    {
        [TestMethod]
        public async Task RefreshAsync_TimesOutWhileAnotherCallerHoldsTheLock_ThrowsAndDoesNotWriteCache()
        {
            var dbPath = SqliteSharedMemoryFixture.NewFileDbPath("Stampede943a");
            try
            {
                using (SqliteSharedMemoryFixture.CreateFileWalDatabase(dbPath))
                {
                    using var schemaCtx = StampedeTestHelper804.CreateFileWalContext(dbPath);
                    schemaCtx.Database.EnsureCreated();
                    schemaCtx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "Distributed-Holder-City", Province = "南部" });
                    schemaCtx.SaveChanges();
                }

                var distCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
                // Bug #943: a short StampedeTimeout makes the timeout branch trigger
                // deterministically and quickly instead of waiting out the 10s production
                // default — same 80ms value #804's own test already proved reliable.
                var options = new LookupCacheOptions { StampedeTimeout = TimeSpan.FromMilliseconds(80) };
                var svc = new DistributedLookupCacheService(distCache, new[] { typeof(CityCode).Assembly }, options);

                var counter = new int[1];
                var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var holderInterceptor = new CountingDelayReaderInterceptor804(
                    counter, "CityCode", TimeSpan.FromMilliseconds(500), holderStarted);
                using var holderCtx = StampedeTestHelper804.CreateFileWalContext(dbPath, holderInterceptor);

                var refreshInterceptor = new CountingDelayReaderInterceptor804(counter, "CityCode");
                using var refreshCtx = StampedeTestHelper804.CreateFileWalContext(dbPath, refreshInterceptor);

                // Holder: a slow GetAllAsync cache-miss load that holds the per-key semaphore for
                // ~500ms — long enough that RefreshAsync's 80ms StampedeTimeout genuinely elapses
                // while the holder still owns the lock.
                var holderTask = Task.Run(() => svc.GetAllAsync<CityCode>(holderCtx, null));
                await holderStarted.Task; // holder's SELECT has started -> it now owns the semaphore

                // With the fix: RefreshAsync must throw instead of falling through to
                // Invalidate/LoadFromDbAsync/SetDistributedAsync without holding the lock.
                await Assert.ThrowsExceptionAsync<TimeoutException>(
                    () => svc.RefreshAsync<CityCode>(refreshCtx, null));

                var holderResult = await holderTask;

                // The counter is shared across holderCtx and refreshCtx. If RefreshAsync had
                // fallen through (the bug), it would have called LoadFromDbAsync using its OWN
                // (fast, no-delay) refreshCtx, incrementing this counter to 2 well before the
                // holder's 500ms delay elapses. With the fix, RefreshAsync throws BEFORE ever
                // calling LoadFromDbAsync, so only the holder's one load is ever counted.
                Assert.AreEqual(1, counter[0],
                    "Bug #943: only the lock holder's load may run. A timed-out RefreshAsync call " +
                    "must not load from the DB at all — it must throw before reaching LoadFromDbAsync.");

                // The distributed cache must hold exactly the holder's result — untouched by the
                // timed-out caller, which never got a chance to race a second write against it.
                // DistributedLookupCacheService has no direct IMemoryCache.TryGetValue equivalent,
                // so read back via GetAllAsync (a cache hit, since the holder already populated it)
                // and confirm the returned data matches the holder's own result by value.
                var afterBoth = await svc.GetAllAsync<CityCode>(holderCtx, null);
                Assert.AreEqual(holderResult.Count, afterBoth.Count,
                    "The distributed cache must hold the legitimate lock holder's result, not a " +
                    "stale write from the timed-out caller.");
                Assert.AreEqual(holderResult[0].Name, afterBoth[0].Name,
                    "The distributed cache must hold the legitimate lock holder's result, not a " +
                    "stale write from the timed-out caller.");
            }
            finally
            {
                SqliteSharedMemoryFixture.DeleteFileDatabase(dbPath);
            }
        }

        // Bug #943 negative control: an operation that legitimately completes within the
        // timeout (no contention) must still succeed and cache correctly. The fix only changes
        // behavior inside the `if (!acquired)` branch — never entered when the semaphore is
        // acquired immediately — so this must stay green both before and after the fix.
        // (DistributedLookupCacheTests.cs's own RefreshAsync_repopulates_cache_with_fresh_data
        // already covers this shape against DCity/MemoryDistributedCache; this version is
        // written directly against the SAME fixtures as the timeout test above — CityCode +
        // file-WAL StampedeCityContext — so both tests in this file are self-contained and use
        // one consistent set of fixtures.)
        [TestMethod]
        public async Task RefreshAsync_NoContention_CompletesAndCachesNormally_NegativeControl()
        {
            var dbPath = SqliteSharedMemoryFixture.NewFileDbPath("Stampede943b");
            try
            {
                using (SqliteSharedMemoryFixture.CreateFileWalDatabase(dbPath))
                {
                    using var schemaCtx = StampedeTestHelper804.CreateFileWalContext(dbPath);
                    schemaCtx.Database.EnsureCreated();
                    schemaCtx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "No-Contention-City", Province = "北部" });
                    schemaCtx.SaveChanges();
                }

                var distCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
                var options = new LookupCacheOptions { StampedeTimeout = TimeSpan.FromMilliseconds(80) };
                var svc = new DistributedLookupCacheService(distCache, new[] { typeof(CityCode).Assembly }, options);

                using var ctx = StampedeTestHelper804.CreateFileWalContext(dbPath);

                // No contention: RefreshAsync should complete normally (acquired == true path),
                // never throwing TimeoutException.
                await svc.RefreshAsync<CityCode>(ctx, null);

                var result = await svc.GetAllAsync<CityCode>(ctx, null);
                Assert.AreEqual(1, result.Count,
                    "RefreshAsync without contention must complete and populate the distributed cache.");
                Assert.AreEqual("No-Contention-City", result[0].Name);
            }
            finally
            {
                SqliteSharedMemoryFixture.DeleteFileDatabase(dbPath);
            }
        }
    }
}
