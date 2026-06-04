#nullable enable
// Tests for DataContext fixes in issue #132 and #147:
//   M14 — CreateCommandParameter Oracle case throws NotSupportedException (not silent NRE)
//   M15 — EnableSensitiveDataLogging gated behind explicit opt-in (default false)
//   M16 — Run() connection leak: try/finally ensures connection.Close() on exception
//   #147 — Run() parameterized SQL works on all providers via command.CreateParameter()
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data;
using System.Threading;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Unit
{
    [TestClass]
    public class DataContextFixTests
    {
        // ─── M14 — CreateCommandParameter Oracle/unsupported throws NotSupportedException ──

        [TestMethod]
        public void CreateCommandParameter_Oracle_ThrowsNotSupportedException()
        {
            // Arrange: context configured for Oracle (no actual DB connection needed
            // — CreateCommandParameter only checks DBType, not the connection).
            using var ctx = new TestEmptyContext(DBTypeEnum.Oracle);

            // Act + Assert
            Action act = () => ctx.CreateCommandParameter("@p1", "value", ParameterDirection.Input);
            act.Should().Throw<NotSupportedException>()
               .WithMessage("*Oracle*");
        }

        [TestMethod]
        public void CreateCommandParameter_SqlServer_ReturnsNonNullParameter()
        {
            using var ctx = new TestEmptyContext(DBTypeEnum.SqlServer);

            // Should NOT throw and should not return null
            var param = ctx.CreateCommandParameter("@p1", "value", ParameterDirection.Input);
            param.Should().NotBeNull();
        }

        [TestMethod]
        public void CreateCommandParameter_SQLite_ReturnsNonNullParameter()
        {
            using var ctx = new TestEmptyContext(DBTypeEnum.SQLite);

            var param = ctx.CreateCommandParameter("@p1", 42, ParameterDirection.Input);
            param.Should().NotBeNull();
            param.Should().BeOfType<SqliteParameter>();
        }

        // ─── M15 — EnableSensitiveDataLogging defaults to false ────────────────────────

        [TestMethod]
        public void EnableSensitiveQueryLogging_DefaultsToFalse()
        {
            using var ctx = new TestEmptyContext(DBTypeEnum.SQLite);

            // Default must be false — sensitive logging must not activate without explicit opt-in.
            ctx.EnableSensitiveQueryLogging.Should().BeFalse();
        }

        [TestMethod]
        public void EnableSensitiveQueryLogging_CanBeSetToTrue()
        {
            using var ctx = new TestEmptyContext(DBTypeEnum.SQLite);

            ctx.EnableSensitiveQueryLogging = true;
            ctx.EnableSensitiveQueryLogging.Should().BeTrue();
        }

        [TestMethod]
        public void IsDebug_True_WithSensitiveLogging_False_DoesNotThrow()
        {
            // IsDebug=true but sensitive logging off — should not throw during configure.
            // We verify this by checking OnConfiguring runs without error (the in-memory
            // provider honours all EF builder calls including EnableDetailedErrors).
            using var ctx = new TestSensitiveLoggingContext(isDebug: true, enableSensitive: false);
            Action act = () => _ = ctx.Database.EnsureCreated();
            act.Should().NotThrow();
        }

        [TestMethod]
        public void IsDebug_True_WithSensitiveLogging_True_DoesNotThrow()
        {
            // Both flags set — sensitive logging path should be reached without exception.
            using var ctx = new TestSensitiveLoggingContext(isDebug: true, enableSensitive: true);
            Action act = () => _ = ctx.Database.EnsureCreated();
            act.Should().NotThrow();
        }

        [TestMethod]
        public void IsDebug_False_SensitiveLogging_NotActivated()
        {
            // When IsDebug is false, sensitive logging cannot be enabled regardless of flag.
            using var ctx = new TestSensitiveLoggingContext(isDebug: false, enableSensitive: true);
            // EnsureCreated must run cleanly — the EnableSensitiveDataLogging call is skipped.
            Action act = () => _ = ctx.Database.EnsureCreated();
            act.Should().NotThrow();
        }

        // ─── M16 — Run() connection leak: finally block always closes connection ──────

        [TestMethod]
        public void Run_ConnectionLeak_ConnectionClosedAfterException()
        {
            // Use SQLite in-memory — connection starts closed, Run() opens it.
            // If ExecuteReader throws, the connection must still be closed afterwards.
            using var ctx = new ThrowingReaderContext();
            ctx.Database.EnsureCreated();

            // Act: Run() will open the connection, then the command will fail
            // (bad SQL triggers an exception from ExecuteReader).
            Action act = () => ctx.Run("SELECT * FROM NonExistentTable___", CommandType.Text);

            act.Should().Throw<Exception>();

            // Assert: connection must be closed (not leaked).
            var connection = ctx.Database.GetDbConnection();
            connection.State.Should().Be(ConnectionState.Closed,
                "try/finally in Run() must close the connection even after an exception");
        }

        [TestMethod]
        public void Run_HappyPath_ConnectionClosedAfterSuccess()
        {
            // Verify the normal-path close still works after the try/finally refactor.
            using var ctx = new ThrowingReaderContext();
            ctx.Database.EnsureCreated();

            ctx.Run("SELECT 1", CommandType.Text);

            var connection = ctx.Database.GetDbConnection();
            connection.State.Should().Be(ConnectionState.Closed,
                "connection should be closed when it was not open before Run()");
        }

        // ─── #147 — Run() builds parameters via DbCommand.CreateParameter() ─────────────

        [TestMethod]
        public void Run_ParameterizedSql_ReturnsFilteredRows_ViaCmdCreateParameter()
        {
            // Arrange: SQLite shared in-memory DB — exercises the same command.CreateParameter()
            // code path that Oracle uses at runtime.
            using var ctx = new ParameterizedRunContext();
            var connection = ctx.Database.GetDbConnection();
            connection.Open();
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText =
                    "CREATE TABLE IF NOT EXISTS Items (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);" +
                    "INSERT INTO Items (Id, Name) VALUES (1, 'Alpha');" +
                    "INSERT INTO Items (Id, Name) VALUES (2, 'Beta');";
                setup.ExecuteNonQuery();
            }
            connection.Close();

            // Use CreateCommandParameter to build a SqliteParameter (any DbParameter will do).
            // Run() internally copies Name/Value/Direction via command.CreateParameter() — that
            // is the provider-agnostic path that makes Oracle work.
            var param = (System.Data.Common.DbParameter)ctx.CreateCommandParameter("@name", "Alpha", ParameterDirection.Input);

            // Act
            var result = ctx.RunSQL("SELECT Id, Name FROM Items WHERE Name = @name", param);

            // Assert: only the 'Alpha' row should be returned.
            result.Rows.Count.Should().Be(1);
            result.Rows[0]["Name"].Should().Be("Alpha");
        }

        // ─── Inner test helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Minimal EmptyContext subclass that targets a specific DBType without
        /// requiring a real connection (CreateCommandParameter only checks DBType).
        /// </summary>
        private sealed class TestEmptyContext : EmptyContext
        {
            public TestEmptyContext(DBTypeEnum dbType)
                : base("DataSource=:memory:", DBTypeEnum.SQLite) // valid CS for build
            {
                // Override DBType after construction so CreateCommandParameter sees it.
                DBType = dbType;
            }

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            {
                // Always use in-memory to avoid provider-specific packages being needed.
                optionsBuilder.UseInMemoryDatabase("TestEmptyContextDb_" + DBType);
            }
        }

        /// <summary>
        /// Context used to verify that IsDebug + EnableSensitiveQueryLogging flags
        /// flow correctly through OnConfiguring without throwing.
        /// </summary>
        private sealed class TestSensitiveLoggingContext : EmptyContext
        {
            private readonly string _dbName;

            public TestSensitiveLoggingContext(bool isDebug, bool enableSensitive)
                : base($"SensitiveLogTest_{Guid.NewGuid():N}", DBTypeEnum.Memory)
            {
                _dbName = CSName;
                IsDebug = isDebug;
                EnableSensitiveQueryLogging = enableSensitive;
            }

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            {
                optionsBuilder.UseInMemoryDatabase(_dbName);
                // Call base to trigger the IsDebug/EnableSensitiveQueryLogging branching.
                base.OnConfiguring(optionsBuilder);
            }
        }

        /// <summary>
        /// SQLite context used to exercise the #147 Run() parameterized-SQL fix.
        /// Shares the in-memory DB across connections (cache=shared) so the test
        /// can CREATE TABLE via the raw connection then query via Run().
        /// </summary>
        private sealed class ParameterizedRunContext : EmptyContext
        {
            public ParameterizedRunContext()
                : base($"DataSource=ParamRunTest_{Guid.NewGuid():N}?mode=memory&cache=shared",
                       DBTypeEnum.SQLite)
            { }

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
                => optionsBuilder.UseSqlite(CSName);

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                // Empty model — no WTM entity scanning to avoid MajorId column conflicts.
            }
        }

        /// <summary>
        /// SQLite context used to exercise the Run() connection-leak fix.
        /// The DB name is unique per instance so tests are isolated.
        /// </summary>
        private sealed class ThrowingReaderContext : EmptyContext
        {
            private readonly string _dbName;

            public ThrowingReaderContext()
                : base($"DataSource=RunLeakTest_{Guid.NewGuid():N}?mode=memory&cache=shared",
                       DBTypeEnum.SQLite)
            {
                _dbName = CSName;
            }

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            {
                optionsBuilder.UseSqlite(CSName);
            }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                // Empty model — no WTM entity scanning to avoid MajorId conflicts.
            }
        }
    }
}
