#nullable enable
// #727 regression test — audit of the #721 IDataContext→NullContext DI-resolution gap,
// applied to ActionLogRetentionService.
//
// AddWtmContext only ever registers services.TryAddScoped<IDataContext, NullContext>() as a
// placeholder default. Before #727, ActionLogRetentionService resolved
// scope.ServiceProvider.GetService<IDataContext>() directly (falling back to throwing
// InvalidOperationException only if that resolution somehow returned null, which it never
// does — NullContext is a real, non-null object). Every daily sweep called
// NullContext.Set<ActionLog>(), which throws NotImplementedException, swallowed by
// ExecuteAsync's per-iteration catch — the sweep silently deleted zero rows in every real
// deployment, forever.
//
// The existing ActionLogRetentionTests.cs suite could not have caught this: its
// RetentionFixture.BuildService() registers a real IDataContext directly via
// services.AddTransient<IDataContext>(...) — never NullContext, never WTMContext — so it only
// ever exercised the DI-fallback branch, exactly mirroring how TokenTestFixture masked #721.
//
// This test builds the DI container the way AddWtmContext actually does (IDataContext ->
// NullContext placeholder + a real WTMContext whose CreateDC() returns a real DbContext) and
// proves the sweep now genuinely deletes rows through that path.

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
    public class ActionLogRetentionProdDiTests
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
        /// back a real SQLite-backed <see cref="RetentionOnlyContext"/> — mirrors how a real
        /// app's WTMContext.CreateDC() (built from Configs.Connections) hands back a real
        /// consumer DataContext, without needing full CS-reflection-based DI wiring.
        /// </summary>
        private sealed class FakeWtmContext : WTMContext
        {
            private readonly string _dbName;
            public FakeWtmContext(string dbName) : base(null) => _dbName = dbName;

            public override IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true)
                => new RetentionOnlyContext(_dbName);
        }

        [TestMethod]
        public async Task RunRetentionOnce_ThroughProdLikeDI_WTMContextPrimaryPath_ActuallyDeletesRows()
        {
            var dbName = "actionlog_proddi_" + Guid.NewGuid().ToString("N");
            using var keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
            keepAlive.Open();
            using (var initCtx = new RetentionOnlyContext(dbName))
            {
                initCtx.Database.EnsureCreated();
            }

            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            var now = fakeTime.GetUtcNow().UtcDateTime;
            using (var seedCtx = new RetentionOnlyContext(dbName))
            {
                seedCtx.Set<ActionLog>().Add(new ActionLog
                {
                    ID = Guid.NewGuid(),
                    ActionName = "old-normal",
                    ActionTime = now.AddDays(-200),
                    LogType = ActionLogTypesEnum.Normal,
                });
                seedCtx.SaveChanges();
            }

            // Build the DI container the way AddWtmContext actually does: IDataContext ->
            // NullContext placeholder ONLY (no consumer override), plus a real WTMContext.
            var services = new ServiceCollection();
            services.TryAddScoped<IDataContext, NullContext>();
            services.AddScoped<WTMContext>(_ => new FakeWtmContext(dbName));
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ActionLogRetentionService>>(
                NullLogger<ActionLogRetentionService>.Instance);
            services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ActionLogRetentionOptions>>(
                new FixedOptionsMonitor<ActionLogRetentionOptions>(new ActionLogRetentionOptions()));
            services.AddSingleton<TimeProvider>(fakeTime);
            services.AddSingleton<ActionLogRetentionService>();
            var provider = services.BuildServiceProvider();
            var svc = provider.GetRequiredService<ActionLogRetentionService>();

            var options = new ActionLogRetentionOptions
            {
                NormalDays = 30,
                ExceptionDays = 90,
                DebugDays = 10,
                JobDays = 30,
                BatchSize = 1000,
            };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);

            // Before #727: this would have thrown NotImplementedException inside DeleteOneType
            // (NullContext.Set<ActionLog>()), and RunRetentionOnceAsync would have propagated
            // it — the daily sweep would never delete anything in a real deployment.
            Assert.AreEqual(1, result.NormalDeleted);
            using var verify = new RetentionOnlyContext(dbName);
            Assert.AreEqual(0, verify.Set<ActionLog>().Count(),
                "The sweep must have genuinely reached the real SQLite database via WTMContext.CreateDC(), not NullContext.");
        }
    }
}
