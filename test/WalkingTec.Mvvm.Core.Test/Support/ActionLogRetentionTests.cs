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
    /// Tests for issue #832: ActionLogRetentionService — per-type TTL,
    /// batched deletion, schedule calculation.
    /// </summary>
    [TestClass]
    public class ActionLogRetentionTests
    {
        // ── ComputeNextRun ────────────────────────────────────────────────

        [TestMethod]
        public void ComputeNextRun_before_target_hour_schedules_today()
        {
            var now = new DateTimeOffset(2026, 4, 18, 1, 30, 0, TimeSpan.Zero);
            var next = ActionLogRetentionService.ComputeNextRun(now, hour: 3);
            Assert.AreEqual(new DateTimeOffset(2026, 4, 18, 3, 0, 0, TimeSpan.Zero), next);
        }

        [TestMethod]
        public void ComputeNextRun_after_target_hour_schedules_tomorrow()
        {
            var now = new DateTimeOffset(2026, 4, 18, 5, 30, 0, TimeSpan.Zero);
            var next = ActionLogRetentionService.ComputeNextRun(now, hour: 3);
            Assert.AreEqual(new DateTimeOffset(2026, 4, 19, 3, 0, 0, TimeSpan.Zero), next);
        }

        [TestMethod]
        public void ComputeNextRun_exactly_at_target_hour_schedules_tomorrow()
        {
            var now = new DateTimeOffset(2026, 4, 18, 3, 0, 0, TimeSpan.Zero);
            var next = ActionLogRetentionService.ComputeNextRun(now, hour: 3);
            Assert.AreEqual(new DateTimeOffset(2026, 4, 19, 3, 0, 0, TimeSpan.Zero), next);
        }

        [TestMethod]
        public void ComputeNextRun_preserves_offset_from_now()
        {
            var now = new DateTimeOffset(2026, 4, 18, 1, 30, 0, TimeSpan.FromHours(8));
            var next = ActionLogRetentionService.ComputeNextRun(now, hour: 3);
            Assert.AreEqual(TimeSpan.FromHours(8), next.Offset);
        }

        // ── RunRetentionOnceAsync ─────────────────────────────────────────

        [TestMethod]
        public async Task RunRetentionOnce_deletes_only_expired_rows_per_type()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            fixture.Seed(ctx =>
            {
                var now = new DateTime(2026, 4, 18, 0, 0, 0, DateTimeKind.Utc);
                foreach (var type in new[]
                {
                    ActionLogTypesEnum.Normal,
                    ActionLogTypesEnum.Exception,
                    ActionLogTypesEnum.Debug,
                    ActionLogTypesEnum.Job,
                })
                {
                    ctx.Set<ActionLog>().Add(new ActionLog
                    {
                        ID = Guid.NewGuid(),
                        ActionName = $"recent-{type}",
                        ActionTime = now.AddDays(-5),
                        LogType = type,
                    });
                    ctx.Set<ActionLog>().Add(new ActionLog
                    {
                        ID = Guid.NewGuid(),
                        ActionName = $"old-{type}",
                        ActionTime = now.AddDays(-200),
                        LogType = type,
                    });
                }
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            var options = new ActionLogRetentionOptions
            {
                NormalDays = 30,
                ExceptionDays = 90,
                DebugDays = 10,
                JobDays = 30,
                BatchSize = 1000,
            };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);

            Assert.AreEqual(1, result.NormalDeleted);
            Assert.AreEqual(1, result.ExceptionDeleted);
            Assert.AreEqual(1, result.DebugDeleted);
            Assert.AreEqual(1, result.JobDeleted);
            Assert.AreEqual(4, result.TotalDeleted);

            using var verify = fixture.NewContext();
            var remaining = verify.Set<ActionLog>().AsNoTracking().ToList();
            Assert.AreEqual(4, remaining.Count);
            var cutoff = fakeTime.GetUtcNow().UtcDateTime.AddDays(-10);
            foreach (var row in remaining)
            {
                Assert.IsTrue(row.ActionTime >= cutoff,
                    "Every kept row must be inside the tightest TTL of its type.");
            }
        }

        [TestMethod]
        public async Task RunRetentionOnce_zero_days_disables_retention_for_that_type()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            fixture.Seed(ctx =>
            {
                var now = new DateTime(2026, 4, 18, 0, 0, 0, DateTimeKind.Utc);
                foreach (var type in new[]
                {
                    ActionLogTypesEnum.Normal,
                    ActionLogTypesEnum.Exception,
                    ActionLogTypesEnum.Debug,
                    ActionLogTypesEnum.Job,
                })
                {
                    ctx.Set<ActionLog>().Add(new ActionLog
                    {
                        ID = Guid.NewGuid(),
                        ActionName = $"old-{type}",
                        ActionTime = now.AddDays(-200),
                        LogType = type,
                    });
                }
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            var options = new ActionLogRetentionOptions
            {
                NormalDays = 0,
                ExceptionDays = 0,
                DebugDays = 10,
                JobDays = 0,
                BatchSize = 1000,
            };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);
            Assert.AreEqual(0, result.NormalDeleted);
            Assert.AreEqual(0, result.ExceptionDeleted);
            Assert.AreEqual(1, result.DebugDeleted);
            Assert.AreEqual(0, result.JobDeleted);
        }

        [TestMethod]
        public async Task RunRetentionOnce_batch_loop_drains_large_set()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            fixture.Seed(ctx =>
            {
                var oldDate = new DateTime(2025, 3, 18, 0, 0, 0, DateTimeKind.Utc);
                var rows = Enumerable.Range(0, 20_000).Select(i => new ActionLog
                {
                    ID = Guid.NewGuid(),
                    ActionName = $"a{i}",
                    ActionTime = oldDate,
                    LogType = ActionLogTypesEnum.Normal,
                }).ToArray();
                ctx.Set<ActionLog>().AddRange(rows);
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            var options = new ActionLogRetentionOptions
            {
                NormalDays = 90,
                ExceptionDays = 0,
                DebugDays = 0,
                JobDays = 0,
                BatchSize = 5000,
            };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);
            Assert.AreEqual(20_000, result.NormalDeleted);
            using var verify = fixture.NewContext();
            Assert.AreEqual(0, verify.Set<ActionLog>().Count());
        }

        [TestMethod]
        public async Task RunRetentionOnce_does_not_delete_rows_inside_TTL()
        {
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero));
            using var fixture = new RetentionFixture();
            fixture.Seed(ctx =>
            {
                var now = fakeTime.GetUtcNow().UtcDateTime;
                for (int i = 0; i < 10; i++)
                {
                    ctx.Set<ActionLog>().Add(new ActionLog
                    {
                        ID = Guid.NewGuid(),
                        ActionName = "recent",
                        ActionTime = now.AddDays(-1),
                        LogType = ActionLogTypesEnum.Normal,
                    });
                }
                ctx.SaveChanges();
            });

            var svc = fixture.BuildService(fakeTime);
            var options = new ActionLogRetentionOptions { NormalDays = 30, ExceptionDays = 0, DebugDays = 0, JobDays = 0 };

            var result = await svc.RunRetentionOnceAsync(options, CancellationToken.None);
            Assert.AreEqual(0, result.NormalDeleted);
            using var verify = fixture.NewContext();
            Assert.AreEqual(10, verify.Set<ActionLog>().Count());
        }

        [TestMethod]
        public async Task RunRetentionOnce_null_options_throws()
        {
            using var fixture = new RetentionFixture();
            var svc = fixture.BuildService(new FakeTimeProvider());
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
                svc.RunRetentionOnceAsync(null!, CancellationToken.None));
        }

        // ── Test scaffolding ──────────────────────────────────────────────

        /// <summary>
        /// Owns a keep-alive SQLite connection backing the shared in-memory
        /// database used across scoped DbContext instances. SQLite is
        /// required because EF Core InMemory does not implement
        /// <c>ExecuteDeleteAsync</c>.
        /// </summary>
        private sealed class RetentionFixture : IDisposable
        {
            private readonly string _dbName = "actionlog_retention_" + Guid.NewGuid().ToString("N");
            private readonly SqliteConnection _keepAlive;

            public RetentionFixture()
            {
                _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
                _keepAlive.Open();
                using var ctx = NewContext();
                ctx.Database.EnsureCreated();
            }

            public RetentionOnlyContext NewContext() => new(_dbName);

            public void Seed(Action<RetentionOnlyContext> seed)
            {
                using var ctx = NewContext();
                seed(ctx);
            }

            public ActionLogRetentionService BuildService(FakeTimeProvider fakeTime)
            {
                var dbName = _dbName;
                var services = new ServiceCollection();
                services.AddTransient<IDataContext>(_ => new RetentionOnlyContext(dbName));
                services.AddSingleton<ILogger<ActionLogRetentionService>>(NullLogger<ActionLogRetentionService>.Instance);
                services.AddSingleton<IOptionsMonitor<ActionLogRetentionOptions>>(
                    new TestOptionsMonitor<ActionLogRetentionOptions>(new ActionLogRetentionOptions()));
                services.AddSingleton<TimeProvider>(fakeTime);
                services.AddSingleton<ActionLogRetentionService>();
                return services.BuildServiceProvider().GetRequiredService<ActionLogRetentionService>();
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
    /// retention tests. Only the <c>ActionLogs</c> table is mapped, so
    /// there is no collision with the framework entity scan. The service
    /// under test only touches <c>Set&lt;ActionLog&gt;()</c> and
    /// <c>SaveChanges</c>; everything else throws
    /// <see cref="NotSupportedException"/> to flag unexpected usage.
    /// </summary>
    internal sealed class RetentionOnlyContext : DbContext, IDataContext
    {
        private readonly string _dbName;

        public RetentionOnlyContext(string dbName)
        {
            _dbName = dbName;
        }

        public DbSet<ActionLog> ActionLogs { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite($"DataSource={_dbName}?mode=memory&cache=shared");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ActionLog>();
        }

        // ── IDataContext minimal surface ──────────────────────────────────
        // Only Set<T>() and SaveChanges(Async) are wired — retention
        // service does not call anything else.

        bool IDataContext.IsFake { get; set; }
        bool IDataContext.IsDebug { get; set; }
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
        IDataContext IDataContext.CreateNew() => new RetentionOnlyContext(_dbName);
        IDataContext IDataContext.ReCreate(ILoggerFactory? _logger) => new RetentionOnlyContext(_dbName);
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
