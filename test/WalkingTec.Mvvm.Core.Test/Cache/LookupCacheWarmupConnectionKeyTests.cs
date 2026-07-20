#nullable enable
// #756 regression tests — LookupCacheWarmupService must honor CacheLookupAttribute.ConnectionKey.
//
// Before the fix, ExecuteAsync resolved ONE default-connection DbContext (via ResolveDataContext,
// no cskey) and warmed EVERY [CacheLookup(WarmOnStartup=true)] type against it, ignoring
// CacheLookupAttribute.ConnectionKey entirely. The runtime paths (WTMContext.GetLookup /
// GetLookupAsync / RefreshLookupAsync) all route via CreateDC(cskey: attr.ConnectionKey). The
// consequence is two-fold:
//   (a) non-default-connection types simply fail to warm on every boot;
//   (b) WORSE — the cache key "wtm:lookup:{FullName}:{tenant}" has no connection component, so if
//       a same-named table happens to also exist on the default connection, a default-DB warm can
//       "accidentally succeed" and silently poison the exact key the runtime serves, for the full
//       TTL, with data read from the WRONG database.
//
// These tests drive the real ExecuteAsync (via BackgroundService.StartAsync + ExecuteTask, no
// reflection needed — ExecuteTask has been public on BackgroundService since .NET Core 3.0), so
// they exercise the actual grouping/routing/skip logic end-to-end.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Cache;

namespace WalkingTec.Mvvm.Core.Test.Cache
{
    // ─── Test fixtures ──────────────────────────────────────────────────────────

    [CacheLookup(WarmOnStartup = true)]
    internal class ConnKeyDefaultType : TopBasePoco
    {
        public string Name { get; set; } = string.Empty;
    }

    [CacheLookup(WarmOnStartup = true, ConnectionKey = "alt")]
    internal class ConnKeyAltType : TopBasePoco
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// One shared DbContext class used for BOTH the "default" and the "alt" physical databases.
    /// This is deliberate: it simulates the #756 cache-poisoning hazard where the alt-connection
    /// type's table also exists (by name) on the default connection, so a default-DB warm can
    /// "accidentally succeed" against the wrong data instead of failing loudly.
    /// <para>
    /// Implements <see cref="IDataContext"/> directly (mirroring
    /// LookupCacheWarmupServiceProdDiTests.FakeDbContext) so it can round-trip through
    /// RecordingWtmContext.CreateDC, whose declared return type is <see cref="IDataContext"/>.
    /// Only the members warm-up actually exercises need real behaviour; the rest throw.
    /// </para>
    /// </summary>
    internal class WarmupConnKeyContext : DbContext, IDataContext
    {
        public WarmupConnKeyContext(DbContextOptions opts) : base(opts) { }
        public DbSet<ConnKeyDefaultType> Defaults { get; set; } = null!;
        public DbSet<ConnKeyAltType> Alts { get; set; } = null!;

        public string? TenantCode => null;
        public bool IsFake { get; set; }
        public bool IsDebug { get; set; }
        public bool EnableSensitiveQueryLogging { get; set; }
        public string? CurrentUserCode { get; set; }
        public string CSName { get; set; } = string.Empty;
        public DBTypeEnum DBType { get; set; } = DBTypeEnum.Memory;
        public void AddEntity<T>(T entity) where T : TopBasePoco => throw new NotSupportedException();
        public void UpdateEntity<T>(T entity) where T : TopBasePoco => throw new NotSupportedException();
        public void DeleteEntity<T>(T entity) where T : TopBasePoco => throw new NotSupportedException();
        public void CascadeDelete<T>(T entity) where T : TreePoco => throw new NotSupportedException();
        public void UpdateProperty<T>(T entity, System.Linq.Expressions.Expression<Func<T, object>> fieldExp) where T : TopBasePoco => throw new NotSupportedException();
        public void UpdateProperty<T>(T entity, string fieldName) where T : TopBasePoco => throw new NotSupportedException();
        public System.Threading.Tasks.Task<bool> DataInit(object? allModel, bool isSpa) => System.Threading.Tasks.Task.FromResult(false);
        public void EnsureCreate() { }
        public IDataContext CreateNew() => throw new NotSupportedException();
        public IDataContext ReCreate(Microsoft.Extensions.Logging.ILoggerFactory? logger = null) => throw new NotSupportedException();
        public System.Data.DataTable RunSP(string command, params object[] paras) => throw new NotSupportedException();
        public System.Collections.Generic.IEnumerable<TElement> RunSP<TElement>(string command, params object[] paras) => throw new NotSupportedException();
        public System.Data.DataTable RunSQL(string command, params object[] paras) => throw new NotSupportedException();
        public System.Collections.Generic.IEnumerable<TElement> RunSQL<TElement>(string sql, params object[] paras) => throw new NotSupportedException();
        public System.Data.DataTable Run(string sql, System.Data.CommandType commandType, params object[] paras) => throw new NotSupportedException();
        public System.Collections.Generic.IEnumerable<TElement> Run<TElement>(string sql, System.Data.CommandType commandType, params object[] paras) => throw new NotSupportedException();
        public object CreateCommandParameter(string name, object value, System.Data.ParameterDirection dir) => throw new NotSupportedException();
        public void SetLoggerFactory(Microsoft.Extensions.Logging.ILoggerFactory factory) { }
        public void SetTenantCode(string? tc) { }
    }

    /// <summary>
    /// WTMContext double that records every CreateDC(cskey) call and routes to a
    /// caller-supplied resolver — lets tests prove which physical DbContext a given
    /// ConnectionKey group was actually warmed against. Mirrors the FakeWtmContext pattern
    /// in LookupCacheWarmupServiceProdDiTests.cs.
    /// </summary>
    internal sealed class RecordingWtmContext : WTMContext
    {
        public List<string?> CreateDcCalls { get; } = new();
        private readonly Func<string?, IDataContext?> _resolver;

        public RecordingWtmContext(Func<string?, IDataContext?> resolver) : base(null)
        {
            _resolver = resolver;
        }

        public override IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true)
        {
            CreateDcCalls.Add(cskey);
            return _resolver(cskey);
        }
    }

    internal static class WarmupConnKeyTestHelper
    {
        public static WarmupConnKeyContext CreateContext(string dbName)
        {
            var opts = new DbContextOptionsBuilder<WarmupConnKeyContext>()
                .UseInMemoryDatabase(dbName)
                .Options;
            var ctx = new WarmupConnKeyContext(opts);
            ctx.Database.EnsureCreated();
            return ctx;
        }

        /// <summary>ApplicationStarted token that is already cancelled — ExecuteAsync's startup
        /// wait resolves synchronously so the warm-up pass runs immediately under test.</summary>
        public static IHostApplicationLifetime AlreadyStartedLifetime()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var mock = new Mock<IHostApplicationLifetime>();
            mock.Setup(l => l.ApplicationStarted).Returns(cts.Token);
            mock.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);
            mock.Setup(l => l.ApplicationStopped).Returns(CancellationToken.None);
            return mock.Object;
        }

        /// <summary>
        /// Drives the BackgroundService through its public IHostedService surface and awaits the
        /// internal execution task (BackgroundService.ExecuteTask has been public since
        /// .NET Core 3.0) — no reflection, no protected-member access needed.
        /// </summary>
        public static async Task RunWarmupAsync(LookupCacheWarmupService service)
        {
            await service.StartAsync(CancellationToken.None);
            if (service.ExecuteTask != null)
            {
                await service.ExecuteTask;
            }
            await service.StopAsync(CancellationToken.None);
        }

        public static string CacheKey(Type type) => $"wtm:lookup:{type.FullName}:_";
    }

    [TestClass]
    public class LookupCacheWarmupConnectionKeyTests
    {
        // ── (1) Cache-poisoning regression pin ──────────────────────────────────
        //
        // ConnKeyAltType's rows are seeded DIFFERENTLY in the default DB vs. the alt DB. If the
        // fix regresses back to warming everything against the default connection, this test
        // must observe the DEFAULT DB's ("WRONG") row content in the cache and FAIL.

        [TestMethod]
        public async Task NamedConnectionKeyType_warms_from_named_connection_not_default()
        {
            using var defaultDc = WarmupConnKeyTestHelper.CreateContext($"conn756_default_{Guid.NewGuid():N}");
            using var altDc = WarmupConnKeyTestHelper.CreateContext($"conn756_alt_{Guid.NewGuid():N}");

            // Same-named-table hazard: ConnKeyAltType exists (and has data) on BOTH physical DBs.
            defaultDc.Alts.Add(new ConnKeyAltType { ID = Guid.NewGuid(), Name = "WRONG-DefaultDB-Row" });
            defaultDc.Defaults.Add(new ConnKeyDefaultType { ID = Guid.NewGuid(), Name = "DefaultRow" });
            defaultDc.SaveChanges();

            altDc.Alts.Add(new ConnKeyAltType { ID = Guid.NewGuid(), Name = "CORRECT-AltDB-Row" });
            altDc.SaveChanges();

            var mc = new MemoryCache(new MemoryCacheOptions());
            var cacheService = new LookupCacheService(mc, new[] { typeof(ConnKeyDefaultType).Assembly });

            var wtm = new RecordingWtmContext(cskey => cskey == "alt" ? altDc : defaultDc);
            wtm.ConfigInfo!.Connections = [new CS { Key = "alt", Enabled = true }];

            var services = new ServiceCollection();
            services.AddScoped<WTMContext>(_ => wtm);
            using var provider = services.BuildServiceProvider();

            var warmup = new LookupCacheWarmupService(
                provider, cacheService, WarmupConnKeyTestHelper.AlreadyStartedLifetime(),
                NullLogger<LookupCacheWarmupService>.Instance);

            await WarmupConnKeyTestHelper.RunWarmupAsync(warmup);

            // Both connections were consulted: default (null cskey) for the default group and
            // "alt" for the ConnectionKey group.
            CollectionAssert.Contains(wtm.CreateDcCalls, (string?)null);
            CollectionAssert.Contains(wtm.CreateDcCalls, "alt");

            // Inspect the cache directly (no further DB access) to prove the ALT type's warmed
            // rows came from the NAMED (alt) connection, not the default one.
            Assert.IsTrue(
                mc.TryGetValue<IReadOnlyList<ConnKeyAltType>>(
                    WarmupConnKeyTestHelper.CacheKey(typeof(ConnKeyAltType)), out var cachedAlt));
            Assert.IsNotNull(cachedAlt);
            Assert.AreEqual(1, cachedAlt!.Count);
            Assert.AreEqual(
                "CORRECT-AltDB-Row", cachedAlt[0].Name,
                "#756 regression: a ConnectionKey type must warm from its OWN connection — a " +
                "default-DB warm that accidentally succeeds against a same-named table would " +
                "poison the shared runtime cache key for the full TTL.");

            Assert.IsTrue(
                mc.TryGetValue<IReadOnlyList<ConnKeyDefaultType>>(
                    WarmupConnKeyTestHelper.CacheKey(typeof(ConnKeyDefaultType)), out var cachedDefault));
            Assert.AreEqual("DefaultRow", cachedDefault![0].Name);
        }

        // ── (2) Unknown ConnectionKey → logged skip, no throw, default group unaffected ──────

        [TestMethod]
        public async Task UnknownConnectionKey_isSkipped_defaultGroupStillWarmed_noThrow()
        {
            using var defaultDc = WarmupConnKeyTestHelper.CreateContext($"conn756_default_{Guid.NewGuid():N}");
            defaultDc.Defaults.Add(new ConnKeyDefaultType { ID = Guid.NewGuid(), Name = "DefaultRow" });
            defaultDc.SaveChanges();

            var mc = new MemoryCache(new MemoryCacheOptions());
            var cacheService = new LookupCacheService(mc, new[] { typeof(ConnKeyDefaultType).Assembly });

            // "alt" is deliberately NOT registered in ConfigInfo.Connections — unknown key.
            var wtm = new RecordingWtmContext(_ => defaultDc);
            wtm.ConfigInfo!.Connections = [];

            var services = new ServiceCollection();
            services.AddScoped<WTMContext>(_ => wtm);
            using var provider = services.BuildServiceProvider();

            var warmup = new LookupCacheWarmupService(
                provider, cacheService, WarmupConnKeyTestHelper.AlreadyStartedLifetime(),
                NullLogger<LookupCacheWarmupService>.Instance);

            await WarmupConnKeyTestHelper.RunWarmupAsync(warmup);

            // Unknown key must never reach CreateDC — IsKnownConnectionKey rejects it first.
            CollectionAssert.DoesNotContain(wtm.CreateDcCalls, "alt");
            // Default group is independent and must still succeed.
            CollectionAssert.Contains(wtm.CreateDcCalls, (string?)null);

            Assert.IsTrue(
                mc.TryGetValue<IReadOnlyList<ConnKeyDefaultType>>(
                    WarmupConnKeyTestHelper.CacheKey(typeof(ConnKeyDefaultType)), out var cachedDefault),
                "Default-connection types must still warm even when an unrelated ConnectionKey is unknown.");
            Assert.AreEqual("DefaultRow", cachedDefault![0].Name);

            Assert.IsFalse(
                mc.TryGetValue<IReadOnlyList<ConnKeyAltType>>(
                    WarmupConnKeyTestHelper.CacheKey(typeof(ConnKeyAltType)), out _),
                "The unknown-ConnectionKey type must never be warmed (never cached under any DB's data).");
        }

        // ── (3) CreateDC throws InvalidOperationException for the named key → caught ─────────

        [TestMethod]
        public async Task NamedConnectionKey_CreateDcThrows_isCaught_defaultGroupUnaffected()
        {
            using var defaultDc = WarmupConnKeyTestHelper.CreateContext($"conn756_default_{Guid.NewGuid():N}");
            defaultDc.Defaults.Add(new ConnKeyDefaultType { ID = Guid.NewGuid(), Name = "DefaultRow" });
            defaultDc.SaveChanges();

            var mc = new MemoryCache(new MemoryCacheOptions());
            var cacheService = new LookupCacheService(mc, new[] { typeof(ConnKeyDefaultType).Assembly });

            // "alt" IS a known connection key, but it is disabled — WTMContext.CreateDC.cs throws
            // InvalidOperationException for a known-but-disabled connection (line ~81-84).
            var wtm = new RecordingWtmContext(cskey =>
                cskey == "alt"
                    ? throw new InvalidOperationException("Database connection 'alt' is disabled.")
                    : defaultDc);
            wtm.ConfigInfo!.Connections = [new CS { Key = "alt", Enabled = false }];

            var services = new ServiceCollection();
            services.AddScoped<WTMContext>(_ => wtm);
            using var provider = services.BuildServiceProvider();

            var warmup = new LookupCacheWarmupService(
                provider, cacheService, WarmupConnKeyTestHelper.AlreadyStartedLifetime(),
                NullLogger<LookupCacheWarmupService>.Instance);

            // Must complete without throwing — the disabled-connection exception must never
            // escape ExecuteAsync (that would stop the host under
            // BackgroundServiceExceptionBehavior.StopHost).
            await WarmupConnKeyTestHelper.RunWarmupAsync(warmup);

            CollectionAssert.Contains(wtm.CreateDcCalls, "alt");
            CollectionAssert.Contains(wtm.CreateDcCalls, (string?)null);

            Assert.IsTrue(
                mc.TryGetValue<IReadOnlyList<ConnKeyDefaultType>>(
                    WarmupConnKeyTestHelper.CacheKey(typeof(ConnKeyDefaultType)), out var cachedDefault),
                "Default-connection group must be unaffected by the named group's CreateDC failure.");
            Assert.AreEqual("DefaultRow", cachedDefault![0].Name);

            Assert.IsFalse(
                mc.TryGetValue<IReadOnlyList<ConnKeyAltType>>(
                    WarmupConnKeyTestHelper.CacheKey(typeof(ConnKeyAltType)), out _),
                "A ConnectionKey whose CreateDC throws must never be warmed or cached.");
        }

        // ── (4) DI-fallback-only host (no WTMContext) → ConnectionKey type skipped, no throw ──

        [TestMethod]
        public async Task NoWtmContext_NamedConnectionKeyType_isSkipped_noThrow()
        {
            using var defaultDc = WarmupConnKeyTestHelper.CreateContext($"conn756_default_{Guid.NewGuid():N}");
            defaultDc.Defaults.Add(new ConnKeyDefaultType { ID = Guid.NewGuid(), Name = "DefaultRow" });
            defaultDc.SaveChanges();

            var mc = new MemoryCache(new MemoryCacheOptions());
            var cacheService = new LookupCacheService(mc, new[] { typeof(ConnKeyDefaultType).Assembly });

            // No WTMContext registered at all — only a raw IDataContext fallback (mirrors the
            // DI-fallback-only host shape from LookupCacheWarmupServiceProdDiTests).
            var services = new ServiceCollection();
            services.AddScoped<IDataContext>(_ => defaultDc);
            using var provider = services.BuildServiceProvider();

            var warmup = new LookupCacheWarmupService(
                provider, cacheService, WarmupConnKeyTestHelper.AlreadyStartedLifetime(),
                NullLogger<LookupCacheWarmupService>.Instance);

            await WarmupConnKeyTestHelper.RunWarmupAsync(warmup);

            // Default group still warms via the DI IDataContext fallback (ResolveDataContext).
            Assert.IsTrue(
                mc.TryGetValue<IReadOnlyList<ConnKeyDefaultType>>(
                    WarmupConnKeyTestHelper.CacheKey(typeof(ConnKeyDefaultType)), out var cachedDefault),
                "Default-connection types must still warm via the DI IDataContext fallback.");
            Assert.AreEqual("DefaultRow", cachedDefault![0].Name);

            // The ConnectionKey group requires a real WTMContext to resolve a named connection
            // safely — without one it must be skipped, never warmed against the wrong DB.
            Assert.IsFalse(
                mc.TryGetValue<IReadOnlyList<ConnKeyAltType>>(
                    WarmupConnKeyTestHelper.CacheKey(typeof(ConnKeyAltType)), out _),
                "A ConnectionKey type must never be warmed when no WTMContext is available.");
        }

        // ── (5) Adversarial-review regression — WTMContext DI resolution itself throws ────────
        //
        // Post-review fix: WarmNamedGroupsAsync's `scopedProvider.GetService<WTMContext>()` call
        // must be guarded exactly like the identical lookup inside ResolveDataContext (used by
        // WarmDefaultGroupAsync). Before the fix, a throwing scoped WTMContext factory — e.g. a
        // consumer subclass whose constructor graph fails to resolve — escaped WarmNamedGroupsAsync
        // uncaught, propagated out of ExecuteAsync, and under the .NET-default
        // BackgroundServiceExceptionBehavior.StopHost would tear down the whole host at startup.
        // DI does not cache a failed resolution, so the throwing factory fires on every
        // GetService<WTMContext>() call in the scope — both the (already-guarded) default-group
        // path and the (previously-unguarded) named-group path hit it. This test proves neither
        // call site lets the exception escape RunWarmupAsync.

        [TestMethod]
        public async Task WtmContextResolutionThrows_isCaught_noThrow_noCachePoisoning()
        {
            var mc = new MemoryCache(new MemoryCacheOptions());
            var cacheService = new LookupCacheService(mc, new[] { typeof(ConnKeyDefaultType).Assembly });

            var services = new ServiceCollection();
            services.AddScoped<WTMContext>(_ =>
                throw new InvalidOperationException(
                    "Simulated WTMContext constructor-graph failure (e.g. IOptionsMonitor<Configs> " +
                    "re-bind failure or a host-supplied decorator throwing on first resolution)."));
            using var provider = services.BuildServiceProvider();

            var warmup = new LookupCacheWarmupService(
                provider, cacheService, WarmupConnKeyTestHelper.AlreadyStartedLifetime(),
                NullLogger<LookupCacheWarmupService>.Instance);

            // Must complete without throwing — a throwing WTMContext DI resolution must never
            // escape ExecuteAsync (that would stop the host under
            // BackgroundServiceExceptionBehavior.StopHost). Before the fix, this line threw
            // InvalidOperationException out of the named-group path.
            await WarmupConnKeyTestHelper.RunWarmupAsync(warmup);

            // Neither group could resolve a DbContext, so neither type may be cached — resolution
            // failure must degrade to "skip, first request warms lazily", never a partial/poisoned
            // cache entry.
            Assert.IsFalse(
                mc.TryGetValue<IReadOnlyList<ConnKeyDefaultType>>(
                    WarmupConnKeyTestHelper.CacheKey(typeof(ConnKeyDefaultType)), out _),
                "Default-connection type must not be cached when WTMContext resolution itself fails.");
            Assert.IsFalse(
                mc.TryGetValue<IReadOnlyList<ConnKeyAltType>>(
                    WarmupConnKeyTestHelper.CacheKey(typeof(ConnKeyAltType)), out _),
                "ConnectionKey type must not be cached when WTMContext resolution itself fails.");
        }
    }
}
