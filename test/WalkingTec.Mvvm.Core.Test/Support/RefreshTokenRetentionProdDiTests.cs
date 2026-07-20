#nullable enable
// #757 — mirrors the #727 ActionLogRetentionProdDiTests.cs pattern for RefreshTokenRetentionService.
//
// AddWtmContext only ever registers services.TryAddScoped<IDataContext, NullContext>() as a
// placeholder default. If RefreshTokenRetentionService resolved
// scope.ServiceProvider.GetService<IDataContext>() directly instead of going through
// WTMContext.CreateDC() first, every daily sweep would call NullContext.Set<RefreshTokenEntity>(),
// which throws NotImplementedException, swallowed by ExecuteAsync's per-iteration catch — the
// sweep would silently delete zero rows in every real deployment, forever.
//
// RefreshTokenRetentionTests.cs's RetentionFixture.BuildService() registers a real IDataContext
// directly via services.AddTransient<IDataContext>(...) — never NullContext, never WTMContext —
// so it only ever exercises the DI-fallback branch. This test builds the DI container the way
// AddWtmContext actually does (IDataContext -> NullContext placeholder + a real WTMContext whose
// CreateDC() returns a real DbContext) and proves the sweep genuinely deletes rows through that
// WTMContext-primary path.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    [TestClass]
    public class RefreshTokenRetentionProdDiTests
    {
        private sealed class FixedOptionsMonitor<T> : Microsoft.Extensions.Options.IOptionsMonitor<T>
        {
            public FixedOptionsMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;
            private sealed class NullDisposable : IDisposable
            {
                public static readonly NullDisposable Instance = new();
                public void Dispose() { }
            }
        }

        /// <summary>
        /// WTMContext subclass whose <see cref="WTMContext.CreateDC"/> is overridden to hand
        /// back a real SQLite-backed <see cref="RefreshTokenRetentionOnlyContext"/> — mirrors how
        /// a real app's WTMContext.CreateDC() (built from Configs.Connections) hands back a real
        /// consumer DataContext, without needing full CS-reflection-based DI wiring.
        /// </summary>
        private sealed class FakeWtmContext : WTMContext
        {
            private readonly string _dbName;
            public FakeWtmContext(string dbName) : base(null) => _dbName = dbName;

            public override IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true)
                => new RefreshTokenRetentionOnlyContext(_dbName);
        }

        [TestMethod]
        public async Task RunRetentionOnce_ThroughProdLikeDI_WTMContextPrimaryPath_ActuallyDeletesRows()
        {
            var dbName = "refreshtoken_proddi_" + Guid.NewGuid().ToString("N");
            using var keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
            keepAlive.Open();
            using (var initCtx = new RefreshTokenRetentionOnlyContext(dbName))
            {
                initCtx.Database.EnsureCreated();
            }

            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            var now = fakeTime.GetUtcNow().UtcDateTime;
            using (var seedCtx = new RefreshTokenRetentionOnlyContext(dbName))
            {
                seedCtx.Set<RefreshTokenEntity>().Add(new RefreshTokenEntity
                {
                    ID = Guid.NewGuid(),
                    Token = Guid.NewGuid().ToString("N"),
                    ITCode = "tester",
                    ExpiresUtc = now.AddDays(-200),
                    CreatedUtc = now.AddDays(-207),
                    RevokedUtc = null,
                });
                seedCtx.SaveChanges();
            }

            // Build the DI container the way AddWtmContext actually does: IDataContext ->
            // NullContext placeholder ONLY (no consumer override), plus a real WTMContext.
            var services = new ServiceCollection();
            services.TryAddScoped<IDataContext, NullContext>();
            services.AddScoped<WTMContext>(_ => new FakeWtmContext(dbName));
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<RefreshTokenRetentionService>>(
                NullLogger<RefreshTokenRetentionService>.Instance);
            services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<RefreshTokenRetentionOptions>>(
                new FixedOptionsMonitor<RefreshTokenRetentionOptions>(new RefreshTokenRetentionOptions()));
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddSingleton<RefreshTokenRetentionService>();
            var provider = services.BuildServiceProvider();
            var svc = provider.GetRequiredService<RefreshTokenRetentionService>();

            var options = new RefreshTokenRetentionOptions
            {
                ExpiredDays = 30,
                RevokedDays = 30,
                BatchSize = 1000,
            };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);

            // If this resolved NullContext instead of routing through WTMContext.CreateDC(), this
            // would have thrown NotImplementedException inside the expired sweep
            // (NullContext.Set<RefreshTokenEntity>()), and RunRetentionOnceAsync would have
            // propagated it — the daily sweep would never delete anything in a real deployment.
            Assert.AreEqual(1, result.ExpiredDeleted);
            using var verify = new RefreshTokenRetentionOnlyContext(dbName);
            Assert.AreEqual(0, verify.Set<RefreshTokenEntity>().Count(),
                "The sweep must have genuinely reached the real SQLite database via WTMContext.CreateDC(), not NullContext.");
        }
    }
}
