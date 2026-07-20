#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    /// <summary>
    /// Tests for issue #757: RefreshTokenRetentionService — expired/revoked
    /// TTL sweeps, batched deletion, schedule calculation, and the
    /// reuse-attack-tripwire safety invariant. Mirrors
    /// ActionLogRetentionTests.cs's fixture pattern 1:1 (intentionally not
    /// shared with it).
    /// </summary>
    [TestClass]
    public class RefreshTokenRetentionTests
    {
        // ── ComputeNextRun ────────────────────────────────────────────────

        [TestMethod]
        public void ComputeNextRun_before_target_hour_schedules_today()
        {
            var now = new DateTimeOffset(2026, 4, 18, 1, 30, 0, TimeSpan.Zero);
            var next = RefreshTokenRetentionService.ComputeNextRun(now, hour: 4);
            Assert.AreEqual(new DateTimeOffset(2026, 4, 18, 4, 0, 0, TimeSpan.Zero), next);
        }

        [TestMethod]
        public void ComputeNextRun_after_target_hour_schedules_tomorrow()
        {
            var now = new DateTimeOffset(2026, 4, 18, 5, 30, 0, TimeSpan.Zero);
            var next = RefreshTokenRetentionService.ComputeNextRun(now, hour: 4);
            Assert.AreEqual(new DateTimeOffset(2026, 4, 19, 4, 0, 0, TimeSpan.Zero), next);
        }

        [TestMethod]
        public void ComputeNextRun_exactly_at_target_hour_schedules_tomorrow()
        {
            var now = new DateTimeOffset(2026, 4, 18, 4, 0, 0, TimeSpan.Zero);
            var next = RefreshTokenRetentionService.ComputeNextRun(now, hour: 4);
            Assert.AreEqual(new DateTimeOffset(2026, 4, 19, 4, 0, 0, TimeSpan.Zero), next);
        }

        [TestMethod]
        public void ComputeNextRun_preserves_offset_from_now()
        {
            var now = new DateTimeOffset(2026, 4, 18, 1, 30, 0, TimeSpan.FromHours(8));
            var next = RefreshTokenRetentionService.ComputeNextRun(now, hour: 4);
            Assert.AreEqual(TimeSpan.FromHours(8), next.Offset);
        }

        // ── ComputeNextRun — out-of-range hour never crashes the host ──────
        // Regression coverage for the adversarial review finding on #757:
        // RunAtLocalHour is operator-supplied config with no upstream
        // validation, re-read every loop iteration via IOptionsMonitor.
        // Before the fix, hour=24 (or any value outside 0-23) threw
        // ArgumentOutOfRangeException from the DateTimeOffset constructor,
        // uncaught by ExecuteAsync's try/catch, which stops the whole host
        // under the default BackgroundServiceExceptionBehavior.StopHost.

        [TestMethod]
        public void ComputeNextRun_hour_24_does_not_throw_and_clamps_to_23()
        {
            var now = new DateTimeOffset(2026, 4, 18, 1, 30, 0, TimeSpan.Zero);
            var next = RefreshTokenRetentionService.ComputeNextRun(now, hour: 24);
            Assert.AreEqual(new DateTimeOffset(2026, 4, 18, 23, 0, 0, TimeSpan.Zero), next);
        }

        [TestMethod]
        public void ComputeNextRun_negative_hour_does_not_throw_and_clamps_to_0()
        {
            var now = new DateTimeOffset(2026, 4, 18, 1, 30, 0, TimeSpan.Zero);
            var next = RefreshTokenRetentionService.ComputeNextRun(now, hour: -1);
            Assert.AreEqual(new DateTimeOffset(2026, 4, 19, 0, 0, 0, TimeSpan.Zero), next);
        }

        [TestMethod]
        public void ComputeNextRun_extreme_out_of_range_hour_does_not_throw()
        {
            var now = new DateTimeOffset(2026, 4, 18, 1, 30, 0, TimeSpan.Zero);
            // int.MaxValue / int.MinValue must not overflow or throw when clamped.
            Assert.AreEqual(
                new DateTimeOffset(2026, 4, 18, 23, 0, 0, TimeSpan.Zero),
                RefreshTokenRetentionService.ComputeNextRun(now, hour: int.MaxValue));
            Assert.AreEqual(
                new DateTimeOffset(2026, 4, 19, 0, 0, 0, TimeSpan.Zero),
                RefreshTokenRetentionService.ComputeNextRun(now, hour: int.MinValue));
        }

        // ── RunRetentionOnceAsync — expired sweep ───────────────────────────

        [TestMethod]
        public async Task RunRetentionOnce_expired_sweep_deletes_only_rows_beyond_window()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            var now = fakeTime.GetUtcNow().UtcDateTime;
            fixture.Seed(ctx =>
            {
                // Beyond the 30-day expired window — should be deleted.
                ctx.Set<RefreshTokenEntity>().Add(NewToken(now.AddDays(-200), revokedUtc: null));
                // Inside the 30-day expired window — should be kept.
                ctx.Set<RefreshTokenEntity>().Add(NewToken(now.AddDays(-10), revokedUtc: null));
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            var options = new RefreshTokenRetentionOptions { ExpiredDays = 30, RevokedDays = 0, BatchSize = 1000 };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);

            Assert.AreEqual(1, result.ExpiredDeleted);
            Assert.AreEqual(0, result.RevokedDeleted);
            using var verify = fixture.NewContext();
            Assert.AreEqual(1, verify.Set<RefreshTokenEntity>().Count());
        }

        [TestMethod]
        public async Task RunRetentionOnce_keeps_active_rows()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            var now = fakeTime.GetUtcNow().UtcDateTime;
            fixture.Seed(ctx =>
            {
                // Active token: not expired, not revoked. Far in the future
                // so it can never match the expired-cutoff predicate.
                ctx.Set<RefreshTokenEntity>().Add(NewToken(now.AddDays(7), revokedUtc: null));
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            var options = new RefreshTokenRetentionOptions { ExpiredDays = 30, RevokedDays = 30, BatchSize = 1000 };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);

            Assert.AreEqual(0, result.TotalDeleted);
            using var verify = fixture.NewContext();
            Assert.AreEqual(1, verify.Set<RefreshTokenEntity>().Count());
        }

        [TestMethod]
        public async Task RunRetentionOnce_keeps_expired_but_inside_window_rows()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            var now = fakeTime.GetUtcNow().UtcDateTime;
            fixture.Seed(ctx =>
            {
                // Expired 5 days ago — well inside a 30-day ExpiredDays window.
                ctx.Set<RefreshTokenEntity>().Add(NewToken(now.AddDays(-5), revokedUtc: null));
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            var options = new RefreshTokenRetentionOptions { ExpiredDays = 30, RevokedDays = 0, BatchSize = 1000 };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);

            Assert.AreEqual(0, result.ExpiredDeleted);
            using var verify = fixture.NewContext();
            Assert.AreEqual(1, verify.Set<RefreshTokenEntity>().Count());
        }

        // ── RunRetentionOnceAsync — revoked sweep ───────────────────────────

        [TestMethod]
        public async Task RunRetentionOnce_revoked_sweep_deletes_old_revoked_and_expired_rows()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            var now = fakeTime.GetUtcNow().UtcDateTime;
            fixture.Seed(ctx =>
            {
                // Revoked 60 days ago AND already expired (60 days ago) —
                // beyond RevokedDays=30 window and expired: must be deleted.
                ctx.Set<RefreshTokenEntity>().Add(NewToken(now.AddDays(-60), revokedUtc: now.AddDays(-60)));
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            var options = new RefreshTokenRetentionOptions { ExpiredDays = 0, RevokedDays = 30, BatchSize = 1000 };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);

            Assert.AreEqual(1, result.RevokedDeleted);
            using var verify = fixture.NewContext();
            Assert.AreEqual(0, verify.Set<RefreshTokenEntity>().Count());
        }

        [TestMethod]
        public async Task RunRetentionOnce_safety_invariant_revoked_but_unexpired_rows_never_deleted()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            var now = fakeTime.GetUtcNow().UtcDateTime;
            fixture.Seed(ctx =>
            {
                // Revoked 60 days ago (far beyond RevokedDays=1) but the row's
                // ExpiresUtc is still in the future — the reuse-attack
                // tripwire's live evidence. Must survive regardless of how
                // aggressive RevokedDays is configured.
                ctx.Set<RefreshTokenEntity>().Add(NewToken(now.AddDays(7), revokedUtc: now.AddDays(-60)));
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            var options = new RefreshTokenRetentionOptions { ExpiredDays = 0, RevokedDays = 1, BatchSize = 1000 };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);

            Assert.AreEqual(0, result.RevokedDeleted,
                "A revoked-but-unexpired row must never be deleted, no matter how small RevokedDays is.");
            using var verify = fixture.NewContext();
            Assert.AreEqual(1, verify.Set<RefreshTokenEntity>().Count());
        }

        // ── Independent knob disabling ───────────────────────────────────────

        [TestMethod]
        public async Task RunRetentionOnce_each_knob_zero_or_negative_disables_independently()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            var now = fakeTime.GetUtcNow().UtcDateTime;
            fixture.Seed(ctx =>
            {
                // Would be deleted by the expired sweep if enabled.
                ctx.Set<RefreshTokenEntity>().Add(NewToken(now.AddDays(-200), revokedUtc: null));
                // Would be deleted by the revoked sweep if enabled.
                ctx.Set<RefreshTokenEntity>().Add(NewToken(now.AddDays(-200), revokedUtc: now.AddDays(-200)));
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            var options = new RefreshTokenRetentionOptions { ExpiredDays = 0, RevokedDays = -1, BatchSize = 1000 };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);

            Assert.AreEqual(0, result.ExpiredDeleted, "ExpiredDays <= 0 must disable the expired sweep.");
            Assert.AreEqual(0, result.RevokedDeleted, "RevokedDays <= 0 must disable the revoked sweep.");
            using var verify = fixture.NewContext();
            Assert.AreEqual(2, verify.Set<RefreshTokenEntity>().Count());
        }

        // ── Batch drain ────────────────────────────────────────────────────

        [TestMethod]
        public async Task RunRetentionOnce_batch_loop_drains_large_set()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            var now = fakeTime.GetUtcNow().UtcDateTime;
            fixture.Seed(ctx =>
            {
                var oldDate = now.AddDays(-200);
                var rows = Enumerable.Range(0, 1200)
                    .Select(_ => NewToken(oldDate, revokedUtc: null))
                    .ToArray();
                ctx.Set<RefreshTokenEntity>().AddRange(rows);
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            // BatchSize (500) is smaller than the seeded row count (1200) by
            // more than 2x, forcing at least 3 ExecuteDeleteAsync iterations.
            var options = new RefreshTokenRetentionOptions { ExpiredDays = 30, RevokedDays = 0, BatchSize = 500 };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);

            Assert.AreEqual(1200, result.ExpiredDeleted);
            using var verify = fixture.NewContext();
            Assert.AreEqual(0, verify.Set<RefreshTokenEntity>().Count());
        }

        // ── Null options ───────────────────────────────────────────────────

        [TestMethod]
        public async Task RunRetentionOnce_null_options_throws()
        {
            using var fixture = new RetentionFixture();
            var svc = fixture.BuildService(new FakeTimeProvider());
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
                svc.RunRetentionOnceAsync(null!, CancellationToken.None));
        }

        // ── Test scaffolding ──────────────────────────────────────────────

        private static RefreshTokenEntity NewToken(DateTime expiresUtc, DateTime? revokedUtc) => new()
        {
            ID = Guid.NewGuid(),
            Token = Guid.NewGuid().ToString("N"),
            ITCode = "tester",
            ExpiresUtc = expiresUtc,
            CreatedUtc = expiresUtc.AddDays(-7),
            RevokedUtc = revokedUtc,
        };

        /// <summary>
        /// Owns a keep-alive SQLite connection backing the shared in-memory
        /// database used across scoped DbContext instances. SQLite is
        /// required because EF Core InMemory does not implement
        /// <c>ExecuteDeleteAsync</c>.
        /// </summary>
        private sealed class RetentionFixture : IDisposable
        {
            private readonly string _dbName = "refreshtoken_retention_" + Guid.NewGuid().ToString("N");
            private readonly SqliteConnection _keepAlive;

            public RetentionFixture()
            {
                _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
                _keepAlive.Open();
                using var ctx = NewContext();
                ctx.Database.EnsureCreated();
            }

            public RefreshTokenRetentionOnlyContext NewContext() => new(_dbName);

            public void Seed(Action<RefreshTokenRetentionOnlyContext> seed)
            {
                using var ctx = NewContext();
                seed(ctx);
            }

            public RefreshTokenRetentionService BuildService(FakeTimeProvider fakeTime)
            {
                var dbName = _dbName;
                var services = new ServiceCollection();
                services.AddTransient<IDataContext>(_ => new RefreshTokenRetentionOnlyContext(dbName));
                services.AddSingleton<ILogger<RefreshTokenRetentionService>>(NullLogger<RefreshTokenRetentionService>.Instance);
                services.AddSingleton<IOptionsMonitor<RefreshTokenRetentionOptions>>(
                    new TestOptionsMonitor<RefreshTokenRetentionOptions>(new RefreshTokenRetentionOptions()));
                services.AddSingleton<TimeProvider>(fakeTime);
                services.AddSingleton<RefreshTokenRetentionService>();
                return services.BuildServiceProvider().GetRequiredService<RefreshTokenRetentionService>();
            }

            public void Dispose() => _keepAlive.Dispose();
        }

        private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
        {
            public TestOptionsMonitor(T value) { CurrentValue = value; }
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable OnChange(Action<T, string?> listener) => DummyDisposable.Instance;
            private sealed class DummyDisposable : IDisposable
            {
                public static readonly DummyDisposable Instance = new();
                public void Dispose() { }
            }
        }
    }

    /// <summary>
    /// Minimal DbContext implementing <see cref="IDataContext"/> for
    /// RefreshTokenRetention tests. Only the <c>FrameworkRefreshTokens</c>
    /// table is mapped, so there is no collision with the framework entity
    /// scan. The service under test only touches
    /// <c>Set&lt;RefreshTokenEntity&gt;()</c>; everything else throws
    /// <see cref="NotSupportedException"/> to flag unexpected usage.
    /// </summary>
    internal sealed class RefreshTokenRetentionOnlyContext : DbContext, IDataContext
    {
        private readonly string _dbName;

        public RefreshTokenRetentionOnlyContext(string dbName)
        {
            _dbName = dbName;
        }

        public DbSet<RefreshTokenEntity> FrameworkRefreshTokens { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite($"DataSource={_dbName}?mode=memory&cache=shared");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RefreshTokenEntity>();
        }

        // ── IDataContext minimal surface ──────────────────────────────────
        // Only Set<T>() and SaveChanges(Async) are wired — retention
        // service does not call anything else.

        bool IDataContext.IsFake { get; set; }
        bool IDataContext.IsDebug { get; set; }
        bool IDataContext.EnableSensitiveQueryLogging { get; set; }
        string? IDataContext.CurrentUserCode { get; set; }
        string? IDataContext.TenantCode => null;
        DBTypeEnum IDataContext.DBType { get => DBTypeEnum.SQLite; set { } }
        string IDataContext.CSName { get => _dbName; set { } }

        void IDataContext.AddEntity<T>(T entity) => Set<T>().Add(entity);
        void IDataContext.UpdateEntity<T>(T entity) => Set<T>().Update(entity);
        void IDataContext.UpdateProperty<T>(T entity, Expression<Func<T, object>> fieldExp) => throw new NotSupportedException();
        void IDataContext.UpdateProperty<T>(T entity, string fieldName) => throw new NotSupportedException();
        void IDataContext.DeleteEntity<T>(T entity) => Set<T>().Remove(entity);
        void IDataContext.CascadeDelete<T>(T entity) => throw new NotSupportedException();
        DbSet<T> IDataContext.Set<T>() where T : class => Set<T>();

        Task<bool> IDataContext.DataInit(object? AllModel, bool IsSpa) => Task.FromResult(false);
        void IDataContext.EnsureCreate() { Database.EnsureCreated(); }
        IDataContext IDataContext.CreateNew() => new RefreshTokenRetentionOnlyContext(_dbName);
        IDataContext IDataContext.ReCreate(ILoggerFactory? _logger) => new RefreshTokenRetentionOnlyContext(_dbName);
        DataTable IDataContext.RunSP(string command, params object[] paras) => throw new NotSupportedException();
        IEnumerable<TElement> IDataContext.RunSP<TElement>(string command, params object[] paras) => throw new NotSupportedException();
        DataTable IDataContext.RunSQL(string command, params object[] paras) => throw new NotSupportedException();
        IEnumerable<TElement> IDataContext.RunSQL<TElement>(string sql, params object[] paras) => throw new NotSupportedException();
        DataTable IDataContext.Run(string sql, CommandType commandType, params object[] paras) => throw new NotSupportedException();
        IEnumerable<TElement> IDataContext.Run<TElement>(string sql, CommandType commandType, params object[] paras) => throw new NotSupportedException();
        object IDataContext.CreateCommandParameter(string name, object value, ParameterDirection dir) => throw new NotSupportedException();
        void IDataContext.SetLoggerFactory(ILoggerFactory factory) { }
        void IDataContext.SetTenantCode(string? tc) { }
    }
}
