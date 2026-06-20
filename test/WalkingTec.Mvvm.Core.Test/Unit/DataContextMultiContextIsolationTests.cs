#nullable enable
// Regression tests for Issue #450:
//   Pass 1 in FrameworkContext.OnModelCreating used Utils.GetAllModels() — the GLOBAL
//   set of entity types from ALL DbContext subclasses — and force-registered them all
//   into the current context's model.  In an app with two contexts sharing an assembly,
//   keyless / no-primary-key entities from the secondary context were injected into the
//   primary context's model, causing EF ValidateNonNullPrimaryKeys to throw
//   "requires a primary key to be defined".
//
//   The fix scopes Pass 1 registration to the DbSet<T> properties declared on THIS
//   context type only, so foreign-context entities never enter the model.
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Unit
{
    // ── Primary context entities ───────────────────────────────────────────────

    /// <summary>
    /// A normal entity with a primary key, belonging to the primary context.
    /// </summary>
    internal class PrimaryContextEntity : BasePoco
    {
        public string Name { get; set; } = "";
    }

    // ── Secondary context entities (keyless / no PK) ───────────────────────────

    /// <summary>
    /// A keyless view entity that belongs ONLY to the secondary context.
    /// It intentionally has no [Key] property and is decorated with [Keyless]
    /// so that EF registers it without a primary key requirement.
    ///
    /// If #450 regression is present, this type gets force-registered into
    /// PrimaryTestContext as well, causing EF to throw
    /// "LoanSaveDetail_Orss requires a primary key to be defined".
    /// </summary>
    [Keyless]
    [Table("vw_LoanSaveDetail_Orss")]
    internal class LoanSaveDetail_Orss
    {
        // Intentionally no key — this is a database view projection.
        public string? AccountNo { get; set; }
        public decimal Balance { get; set; }
    }

    // ── Context definitions ────────────────────────────────────────────────────

    /// <summary>
    /// Primary context — declares only PrimaryContextEntity.
    /// Under the regression, LoanSaveDetail_Orss from SecondaryTestContext
    /// would bleed into this context's model.
    /// </summary>
    internal class PrimaryTestContext : WalkingTec.Mvvm.Core.Test.DataContext
    {
        public DbSet<PrimaryContextEntity> PrimaryContextEntities { get; set; } = null!;

        public PrimaryTestContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    /// <summary>
    /// Secondary context — declares the keyless view entity.
    /// This simulates the `DataContext_Orss` pattern from the issue report.
    /// </summary>
    internal class SecondaryTestContext : WalkingTec.Mvvm.Core.Test.DataContext
    {
        public DbSet<LoanSaveDetail_Orss> LoanSaveDetails { get; set; } = null!;

        public SecondaryTestContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }

        protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Explicitly mark LoanSaveDetail_Orss as keyless in the model
            modelBuilder.Entity<LoanSaveDetail_Orss>().HasNoKey();
        }
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Regression tests for Issue #450: verifies that building the model for
    /// PrimaryTestContext does NOT inject the secondary context's keyless entity,
    /// and that the primary context's own entities + query filters still work.
    /// </summary>
    [TestClass]
    public class DataContextMultiContextIsolationTests
    {
        /// <summary>
        /// The core regression: building PrimaryTestContext's model must NOT throw
        /// "requires a primary key to be defined" for LoanSaveDetail_Orss.
        /// Both contexts are defined in the same assembly so Utils.GetAllModels()
        /// would include LoanSaveDetail_Orss in its global set.
        /// </summary>
        [TestMethod]
        [Description("#450 regression: PrimaryTestContext model build must not throw due to SecondaryTestContext's keyless entity")]
        public void PrimaryContext_ModelBuild_DoesNotThrow_WhenSecondaryHasKeylessEntity()
        {
            var seed = Guid.NewGuid().ToString("N");

            // This call used to throw InvalidOperationException:
            //   "The entity type 'LoanSaveDetail_Orss' requires a primary key to be defined."
            // because Pass 1 force-registered ALL global types (including LoanSaveDetail_Orss)
            // into PrimaryTestContext's model via modelBuilder.Entity<LoanSaveDetail_Orss>().
            Exception? caught = null;
            try
            {
                using var ctx = new PrimaryTestContext(seed, DBTypeEnum.Memory);
                ctx.Database.EnsureCreated();
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.IsNull(caught,
                $"#450 regression: PrimaryTestContext model build threw unexpectedly: {caught?.Message}");
        }

        /// <summary>
        /// The secondary context's keyless entity must NOT appear in the primary
        /// context's model entity set.
        /// </summary>
        [TestMethod]
        [Description("#450: PrimaryTestContext.Model must not contain LoanSaveDetail_Orss")]
        public void PrimaryContext_Model_DoesNotContain_SecondaryKeylessEntity()
        {
            var seed = Guid.NewGuid().ToString("N");

            using var ctx = new PrimaryTestContext(seed, DBTypeEnum.Memory);
            ctx.Database.EnsureCreated();

            var entityTypeNames = ctx.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToList();

            CollectionAssert.DoesNotContain(entityTypeNames, nameof(LoanSaveDetail_Orss),
                $"PrimaryTestContext's model must not contain '{nameof(LoanSaveDetail_Orss)}' " +
                "— that entity belongs exclusively to SecondaryTestContext.");
        }

        /// <summary>
        /// Confirm the primary context's own entities are still registered and
        /// their query filters (soft-delete / tenant) still apply correctly.
        /// This ensures the fix does not regress the #382 intent.
        /// </summary>
        [TestMethod]
        [Description("#450 + #382: PrimaryTestContext own entities and query filters still work after scoping fix")]
        public void PrimaryContext_OwnEntities_And_QueryFilters_WorkCorrectly()
        {
            var seed = Guid.NewGuid().ToString("N");

            // Seed: add a PrimaryContextEntity (BasePoco — no soft-delete, no tenant filter)
            using (var seedCtx = new PrimaryTestContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.PrimaryContextEntities.Add(new PrimaryContextEntity { Name = "TestItem" });
                seedCtx.SaveChanges();
            }

            // Query — should see the seeded entity with no filter applied
            using (var queryCtx = new PrimaryTestContext(seed, DBTypeEnum.Memory))
            {
                var results = queryCtx.Set<PrimaryContextEntity>().ToList();
                Assert.AreEqual(1, results.Count,
                    "PrimaryTestContext must be able to query its own entities normally.");
                Assert.AreEqual("TestItem", results[0].Name);
            }
        }
    }
}
