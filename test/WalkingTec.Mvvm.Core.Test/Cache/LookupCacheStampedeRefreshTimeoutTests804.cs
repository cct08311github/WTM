#nullable enable
// Issue #804 — LookupCacheService stampede-protection + RefreshAsync semaphore-timeout tests.
//
// Two things verified directly against LookupCacheService.cs (not against the issue text) before
// writing these tests:
//
// (1) RefreshAsync<T> (pre-fix :377-397) computed `acquired` from
//     `semaphore.WaitAsync(StampedeTimeout, ct)` but never checked it before proceeding to
//     Invalidate<T>/LoadFromDbAsync/SetCache — the ONLY one of GetAll/GetAllAsync/RefreshAsync's
//     three semaphore-timeout call sites that skipped the check the #112(4)/M10 fix already
//     applies correctly at :207-234 (sync GetAll) and :293-313 (async GetAllAsync). A timed-out
//     RefreshAsync caller does not hold the lock, so writing to the cache anyway races the
//     legitimate holder's own write — the exact stampede the semaphore exists to prevent,
//     arriving as a lost update instead of a redundant query.
//
// (2) The general stampede-protection invariant GetAll/GetAllAsync already implement correctly
//     is pinned here too, with an EXACT invocation-count assertion — "the loader ran once", not
//     "fewer than N times". This repo has previously shipped a concurrency test that only
//     asserted a weaker `0`/`< N` bound and treated that as proof of correctness; it is not.
//
// Both tests hold multiple DbContext instances ACTIVELY RACING against the same underlying
// database, so both use SqliteTestDbMode.FileWal (via SqliteSharedMemoryFixture) rather than
// shared-cache in-memory — see that fixture's own remarks for why shared-cache in-memory's
// connection pooling and coarse table locking make it unsafe for exactly this shape of test.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Cache;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Cache
{
    /// <summary>
    /// Counts every SELECT executed against a given table name (case-insensitive substring match
    /// on <see cref="DbCommand.CommandText"/> — mirrors the filtering pattern
    /// <c>EntitySnapshotLoadFailureInterceptor</c>/<c>SubItemLookupFailureInterceptor</c> already
    /// use elsewhere in this test suite) and, when configured with a delay, awaits it before
    /// letting the read proceed — simulating a slow loader. Optionally signals a
    /// <see cref="TaskCompletionSource"/> the instant the FIRST matching read starts, so a test
    /// can know deterministically that the caller now owns the per-key semaphore (it only reaches
    /// <c>LoadFromDbAsync</c> after acquiring the lock) instead of guessing via a sleep.
    /// </summary>
    internal sealed class CountingDelayReaderInterceptor804 : DbCommandInterceptor
    {
        private readonly int[] _counter;
        private readonly string _tableNameFilter;
        private readonly TimeSpan _delay;
        private readonly TaskCompletionSource? _startedSignal;

        public CountingDelayReaderInterceptor804(
            int[] sharedCounter,
            string tableNameFilter,
            TimeSpan delay = default,
            TaskCompletionSource? startedSignal = null)
        {
            _counter = sharedCounter;
            _tableNameFilter = tableNameFilter;
            _delay = delay;
            _startedSignal = startedSignal;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.IndexOf(_tableNameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Interlocked.Increment(ref _counter[0]);
                _startedSignal?.TrySetResult();
                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                }
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal class StampedeCityContext : DbContext
    {
        public StampedeCityContext(DbContextOptions opts) : base(opts) { }
        public DbSet<CityCode> CityCodes { get; set; } = null!;
    }

    internal static class StampedeTestHelper804
    {
        /// <summary>
        /// Builds a StampedeCityContext against a file-WAL database at <paramref name="dbPath"/>,
        /// mirroring WfEngineTestContext's FileWal wiring (EngineTests.cs) — real per-connection
        /// concurrency, required because these tests hold multiple DbContext instances racing
        /// against the same database.
        /// </summary>
        public static StampedeCityContext CreateFileWalContext(string dbPath, params IInterceptor[] interceptors)
        {
            var connStr = SqliteSharedMemoryFixture.BuildFileWalConnectionString(dbPath);
            var builder = new DbContextOptionsBuilder<StampedeCityContext>()
                .UseSqlite(connStr, o => o.ExecutionStrategy(deps => new SqliteBusyRetryExecutionStrategy(deps)));

            var all = new List<IInterceptor> { new SqliteBusyTimeoutInterceptor() };
            all.AddRange(interceptors);
            builder.AddInterceptors(all);

            return new StampedeCityContext(builder.Options);
        }
    }

    [TestClass]
    public class LookupCacheStampedeRefreshTimeoutTests804
    {
        [TestMethod]
        public async Task GetAllAsync_NConcurrentCacheMisses_WithSlowLoader_LoaderRunsExactlyOnce()
        {
            var dbPath = SqliteSharedMemoryFixture.NewFileDbPath("Stampede804a");
            try
            {
                using (SqliteSharedMemoryFixture.CreateFileWalDatabase(dbPath))
                {
                    using var schemaCtx = StampedeTestHelper804.CreateFileWalContext(dbPath);
                    schemaCtx.Database.EnsureCreated();
                    schemaCtx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "N-Concurrent-City", Province = "北部" });
                    schemaCtx.SaveChanges();
                }

                var mc = new MemoryCache(new MemoryCacheOptions());
                var svc = new LookupCacheService(mc, new[] { typeof(CityCode).Assembly });

                var counter = new int[1];
                const int n = 6;
                var contexts = new StampedeCityContext[n];
                try
                {
                    for (var i = 0; i < n; i++)
                    {
                        var interceptor = new CountingDelayReaderInterceptor804(
                            counter, "CityCode", TimeSpan.FromMilliseconds(200));
                        contexts[i] = StampedeTestHelper804.CreateFileWalContext(dbPath, interceptor);
                    }

                    var tasks = new Task<IReadOnlyList<CityCode>>[n];
                    for (var i = 0; i < n; i++)
                    {
                        tasks[i] = svc.GetAllAsync<CityCode>(contexts[i], null);
                    }

                    var results = await Task.WhenAll(tasks);

                    Assert.AreEqual(1, counter[0],
                        "Bug #804 regression pin: N concurrent cache-miss callers on a cold key with a " +
                        "slow loader must result in EXACTLY ONE real DB load (the semaphore holder's) — " +
                        "not zero (which would only prove absence of a crash, not correctness) and not " +
                        "more than one (which is the stampede this protection exists to prevent).");

                    foreach (var r in results)
                    {
                        Assert.AreEqual(1, r.Count);
                        Assert.AreEqual("N-Concurrent-City", r[0].Name);
                        Assert.AreSame(results[0], r, "All concurrent callers must observe the same cached reference.");
                    }
                }
                finally
                {
                    foreach (var c in contexts) c?.Dispose();
                }
            }
            finally
            {
                SqliteSharedMemoryFixture.DeleteFileDatabase(dbPath);
            }
        }

        [TestMethod]
        public async Task RefreshAsync_TimesOutWhileAnotherCallerHoldsTheLock_ThrowsAndDoesNotWriteCache()
        {
            var dbPath = SqliteSharedMemoryFixture.NewFileDbPath("Stampede804b");
            try
            {
                using (SqliteSharedMemoryFixture.CreateFileWalDatabase(dbPath))
                {
                    using var schemaCtx = StampedeTestHelper804.CreateFileWalContext(dbPath);
                    schemaCtx.Database.EnsureCreated();
                    schemaCtx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "Holder-City", Province = "南部" });
                    schemaCtx.SaveChanges();
                }

                var mc = new MemoryCache(new MemoryCacheOptions());
                // Bug #804: a short StampedeTimeout makes the timeout branch trigger
                // deterministically and quickly instead of waiting out the 10s production default.
                var options = new LookupCacheOptions { StampedeTimeout = TimeSpan.FromMilliseconds(80) };
                var svc = new LookupCacheService(mc, new[] { typeof(CityCode).Assembly }, options);

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
                // Invalidate/LoadFromDbAsync/SetCache without holding the lock.
                await Assert.ThrowsExceptionAsync<TimeoutException>(
                    () => svc.RefreshAsync<CityCode>(refreshCtx, null));

                var holderResult = await holderTask;

                // The counter is shared across holderCtx and refreshCtx. If RefreshAsync had
                // fallen through (the bug), it would have called LoadFromDbAsync using its OWN
                // (fast, no-delay) refreshCtx, incrementing this counter to 2 well before the
                // holder's 500ms delay elapses. With the fix, RefreshAsync throws BEFORE ever
                // calling LoadFromDbAsync, so only the holder's one load is ever counted.
                Assert.AreEqual(1, counter[0],
                    "Bug #804: only the lock holder's load may run. A timed-out RefreshAsync call " +
                    "must not load from the DB at all — it must throw before reaching LoadFromDbAsync.");

                // The cache must contain exactly the holder's result — untouched by the timed-out
                // caller, which never got a chance to race a second write against it.
                Assert.IsTrue(
                    mc.TryGetValue<IReadOnlyList<CityCode>>(
                        $"wtm:lookup:{typeof(CityCode).FullName}:_", out var cached));
                Assert.AreSame(holderResult, cached,
                    "The cache must hold the legitimate lock holder's result, not a stale write " +
                    "from the timed-out caller.");
            }
            finally
            {
                SqliteSharedMemoryFixture.DeleteFileDatabase(dbPath);
            }
        }
    }
}
