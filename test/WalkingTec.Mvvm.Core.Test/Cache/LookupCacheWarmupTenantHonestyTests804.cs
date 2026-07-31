#nullable enable
// Issue #804 — LookupCacheWarmupService honesty tests: warmup must not claim success when
// nothing was actually cached.
//
// Verified directly against LookupCacheWarmupService.cs / LookupCacheService.cs before writing
// these tests (not against the issue text): WarmTypesAsync's hard-coded
// `new object?[] { dc, null, stoppingToken }` call always warms with tenantId=null. For any
// WarmOnStartup type that ALSO implements ITenant, with DefaultTenantIsolation in effect (the
// default — LookupCacheOptions.DefaultTenantIsolation), GetAll/GetAllAsync's own #112(1)/#168
// bypass (`_forcedTenantIsolationTypes.Contains(typeof(T)) && tenantId == null &&
// DefaultTenantIsolation`) returns the DB result directly WITHOUT ever calling SetCache. Before
// this fix, that call completing without throwing was indistinguishable — from WarmTypesAsync's
// point of view — from an actual cache write, so it logged "Lookup cache warmed: {TypeName}" and
// ExecuteAsync unconditionally logged "Lookup cache warm-up completed." afterward, even though
// the type's cache entry was never populated. A success log that fires whether or not anything
// succeeded is a false assurance, not a smaller bug than the stampede defect in the same file.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Cache;

namespace WalkingTec.Mvvm.Core.Test.Cache
{
    /// <summary>
    /// Minimal IDataContext-compliant DbContext base for the #804 warmup-honesty tests — mirrors
    /// the boilerplate shape <c>WarmupConnKeyContext</c>
    /// (<c>LookupCacheWarmupConnectionKeyTests.cs</c>) and <c>FakeDbContext</c>
    /// (<c>LookupCacheWarmupServiceProdDiTests.cs</c>) already use in this test suite. Only the
    /// members warm-up actually exercises need real behaviour; the rest throw.
    /// </summary>
    internal abstract class WarmupHonestyContextBase804 : DbContext, IDataContext
    {
        protected WarmupHonestyContextBase804(DbContextOptions opts) : base(opts) { }

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
        public Task<bool> DataInit(object? allModel, bool isSpa) => Task.FromResult(false);
        public void EnsureCreate() { }
        public IDataContext CreateNew() => throw new NotSupportedException();
        public IDataContext ReCreate(ILoggerFactory? logger = null) => throw new NotSupportedException();
        public System.Data.DataTable RunSP(string command, params object[] paras) => throw new NotSupportedException();
        public IEnumerable<TElement> RunSP<TElement>(string command, params object[] paras) => throw new NotSupportedException();
        public System.Data.DataTable RunSQL(string command, params object[] paras) => throw new NotSupportedException();
        public IEnumerable<TElement> RunSQL<TElement>(string sql, params object[] paras) => throw new NotSupportedException();
        public System.Data.DataTable Run(string sql, System.Data.CommandType commandType, params object[] paras) => throw new NotSupportedException();
        public IEnumerable<TElement> Run<TElement>(string sql, System.Data.CommandType commandType, params object[] paras) => throw new NotSupportedException();
        public object CreateCommandParameter(string name, object value, System.Data.ParameterDirection dir) => throw new NotSupportedException();
        public void SetLoggerFactory(ILoggerFactory factory) { }
        public void SetTenantCode(string? tc) { }
    }

    /// <summary>Only maps CityCode (non-ITenant, WarmOnStartup=true) — used to prove the honest
    /// "actually cached" path still works for a type warmup CAN legitimately warm.</summary>
    internal sealed class CacheableOnlyContext804 : WarmupHonestyContextBase804
    {
        public CacheableOnlyContext804(DbContextOptions opts) : base(opts) { }
        public DbSet<CityCode> CityCodes { get; set; } = null!;
    }

    /// <summary>Only maps TenantCategory (ITenant, WarmOnStartup=true) — deliberately does NOT
    /// map CityCode or any other WarmOnStartup type in the assembly, so every OTHER type the
    /// assembly-wide registry scan picks up fails "not part of the model" and cannot accidentally
    /// count as warmed. This isolates the assertion to "0 types were actually cached".</summary>
    internal sealed class TenantIsolatedOnlyContext804 : WarmupHonestyContextBase804
    {
        public TenantIsolatedOnlyContext804(DbContextOptions opts) : base(opts) { }
        public DbSet<TenantCategory> TenantCategories { get; set; } = null!;
    }

    internal static class WarmupHonestyTestHelper804
    {
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

        /// <summary>Drives the BackgroundService through its public IHostedService surface —
        /// mirrors WarmupConnKeyTestHelper.RunWarmupAsync in LookupCacheWarmupConnectionKeyTests.cs.</summary>
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

        /// <summary>
        /// Builds a mock ILogger&lt;LookupCacheWarmupService&gt; that captures every (level,
        /// formatted message) pair. Same ILogger.Log-callback-plus-formatter-invoke pattern
        /// already used by this repo's WatermarkTests.cs, extended to also materialize the actual
        /// interpolated message text (not just the level) via the formatter delegate.
        /// </summary>
        public static (Mock<ILogger<LookupCacheWarmupService>> Mock, List<(LogLevel Level, string Message)> Messages)
            CreateCapturingLogger()
        {
            var messages = new List<(LogLevel Level, string Message)>();
            var mock = new Mock<ILogger<LookupCacheWarmupService>>();
            mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            mock.Setup(l => l.Log(
                    It.IsAny<LogLevel>(),
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback<LogLevel, EventId, object, Exception?, Delegate>((level, _, state, ex, formatter) =>
                {
                    var message = (string)formatter.DynamicInvoke(state, ex)!;
                    messages.Add((level, message));
                });
            return (mock, messages);
        }
    }

    [TestClass]
    public class LookupCacheWarmupTenantHonestyTests804
    {
        [TestMethod]
        public async Task Warmup_NonTenantType_ActuallyCachesAndServesLookupWithoutFurtherLoaderHit()
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            conn.Open();
            var opts = new DbContextOptionsBuilder<CacheableOnlyContext804>().UseSqlite(conn).Options;
            using var ctx = new CacheableOnlyContext804(opts);
            ctx.Database.EnsureCreated();
            ctx.CityCodes.Add(new CityCode { ID = Guid.NewGuid(), Name = "台北", Province = "北部" });
            ctx.SaveChanges();

            var mc = new MemoryCache(new MemoryCacheOptions());
            var cacheService = new LookupCacheService(mc, new[] { typeof(CityCode).Assembly });

            var services = new ServiceCollection();
            services.AddScoped<IDataContext>(_ => ctx);
            using var provider = services.BuildServiceProvider();

            var (loggerMock, messages) = WarmupHonestyTestHelper804.CreateCapturingLogger();
            var warmup = new LookupCacheWarmupService(
                provider, cacheService, WarmupHonestyTestHelper804.AlreadyStartedLifetime(), loggerMock.Object);

            await WarmupHonestyTestHelper804.RunWarmupAsync(warmup);

            // Positive claim: a lookup for a type that WAS actually warmed is served from cache.
            Assert.IsTrue(
                mc.TryGetValue<IReadOnlyList<CityCode>>(WarmupHonestyTestHelper804.CacheKey(typeof(CityCode)), out var cached));
            Assert.AreEqual(1, cached!.Count);
            Assert.AreEqual("台北", cached[0].Name);

            // "Served from cache without hitting the loader": a subsequent GetAll call for the
            // same (null) tenant returns the SAME reference — a genuine DB re-query would build a
            // new List<CityCode> instance, never the warmed one.
            var served = cacheService.GetAll<CityCode>(ctx, tenantId: null);
            Assert.AreSame(cached, served,
                "A lookup for a tenant the warmup service actually warmed must be served from " +
                "cache (same reference), not re-queried.");

            // Because something WAS actually cached, the honest "completed" log must fire.
            Assert.IsTrue(
                messages.Exists(m => m.Level == LogLevel.Information && m.Message.Contains("completed")),
                "When at least one type is actually cached, the warm-up-completed log must fire.");
        }

        [TestMethod]
        public async Task Warmup_OnlyTenantIsolatedTypeRegistered_NeverLogsCompleted_AndCachesNothing()
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            conn.Open();
            var opts = new DbContextOptionsBuilder<TenantIsolatedOnlyContext804>().UseSqlite(conn).Options;
            using var ctx = new TenantIsolatedOnlyContext804(opts);
            ctx.Database.EnsureCreated();
            // Seed a row — if the bug reintroduces itself and the DB actually gets queried and
            // "successfully" cached, this proves it: an empty table would otherwise let an
            // accidental real query masquerade as a correctly-skipped one (empty list either way).
            ctx.TenantCategories.Add(new TenantCategory { ID = Guid.NewGuid(), Name = "Cat-A", TenantCode = "TenantA" });
            ctx.SaveChanges();

            var mc = new MemoryCache(new MemoryCacheOptions());
            var cacheService = new LookupCacheService(mc, new[] { typeof(TenantCategory).Assembly });
            Assert.IsTrue(cacheService.DefaultTenantIsolation, "This test's premise requires the default (true).");

            var services = new ServiceCollection();
            services.AddScoped<IDataContext>(_ => ctx);
            using var provider = services.BuildServiceProvider();

            var (loggerMock, messages) = WarmupHonestyTestHelper804.CreateCapturingLogger();
            var warmup = new LookupCacheWarmupService(
                provider, cacheService, WarmupHonestyTestHelper804.AlreadyStartedLifetime(), loggerMock.Object);

            await WarmupHonestyTestHelper804.RunWarmupAsync(warmup);

            // Nothing may be cached — TenantCategory implements ITenant and warmup only ever
            // calls with tenantId=null, which LookupCacheService's own #112(1)/#168 bypass turns
            // into a direct DB read that is never written to the cache.
            Assert.IsFalse(
                mc.TryGetValue<IReadOnlyList<TenantCategory>>(WarmupHonestyTestHelper804.CacheKey(typeof(TenantCategory)), out _),
                "Bug #804: a tenant-isolated WarmOnStartup type must never be cached under the " +
                "null-tenant key by this single-tenant-only warm-up service.");

            // Bug #804's core claim: the success log must not fire when nothing was cached.
            Assert.IsFalse(
                messages.Exists(m => m.Message.Contains("completed")),
                "Bug #804: the warm-up-completed success log must never fire when nothing was " +
                "actually cached — it is a false assurance otherwise.");

            // The honest counterpart must fire instead.
            Assert.IsTrue(
                messages.Exists(m => m.Level == LogLevel.Warning && m.Message.Contains("0 of")),
                "Bug #804: warm-up must say plainly that 0 types were cached, not stay silent.");
        }
    }
}
