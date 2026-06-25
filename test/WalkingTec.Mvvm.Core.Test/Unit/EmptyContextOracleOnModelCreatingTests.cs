#nullable enable
// Regression test for #525: EmptyContext.OnModelCreating oracle branch threw InvalidCastException
// on EF Core 10 because ((IConventionModelBuilder)modelBuilder) is no longer valid.
// Fix: use modelBuilder.Model.SetMaxIdentifierLength(30) via the IMutableModel API.
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Unit
{
    [TestClass]
    public class EmptyContextOracleOnModelCreatingTests
    {
        /// <summary>
        /// #525 — EF Core 10 invalidated the (IConventionModelBuilder)ModelBuilder cast.
        /// This test verifies that OnModelCreating for DBType=Oracle:
        ///   1. Does NOT throw InvalidCastException (or any other exception), and
        ///   2. Sets MaxIdentifierLength to 30 on the resulting model.
        ///
        /// The context uses SQLite shared-memory so model creation runs through the
        /// relational pipeline (required for SetMaxIdentifierLength to be available).
        /// DBType is forced to Oracle post-construction so the Oracle branch executes
        /// while the underlying provider remains SQLite.
        /// </summary>
        [TestMethod]
        public void OracleMaxIdentifierLength_ShouldNotThrow_OnEFCore10()
        {
            // Arrange
            using var ctx = new OracleModelCreatingContext();

            // Act — accessing ctx.Model forces OnModelCreating to run.
            Action act = () => _ = ctx.Model;

            // Assert 1: must not throw (InvalidCastException was the regression symptom)
            act.Should().NotThrow("EF Core 10 IMutableModel.SetMaxIdentifierLength must not throw");

            // Assert 2: the length must actually be set to 30
            ctx.Model.GetMaxIdentifierLength().Should().Be(30,
                "OnModelCreating must call SetMaxIdentifierLength(30) for Oracle");
        }

        // ─── Helper ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Minimal EmptyContext subclass that runs OnModelCreating with DBType=Oracle
        /// while using SQLite shared-memory as the actual provider so no Oracle driver
        /// is required and the relational pipeline is available.
        /// </summary>
        private sealed class OracleModelCreatingContext : EmptyContext
        {
            private readonly string _dbName = $"OracleModelCreatingTest_{Guid.NewGuid():N}";

            public OracleModelCreatingContext()
                : base("DataSource=:memory:", DBTypeEnum.SQLite)
            {
                // Force the Oracle branch in OnModelCreating while keeping SQLite as provider.
                DBType = DBTypeEnum.Oracle;
            }

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
                => optionsBuilder.UseSqlite($"DataSource={_dbName}?mode=memory&cache=shared");

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                // Call base — this is the method under test (#525 fix lives here).
                base.OnModelCreating(modelBuilder);
            }
        }
    }
}
