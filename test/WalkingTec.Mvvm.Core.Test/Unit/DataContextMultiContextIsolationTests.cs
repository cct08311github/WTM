#nullable enable
// Regression tests for Issues #450 and #452:
//   #450: Pass 1 in DataContext.OnModelCreating used Utils.GetAllModels() — the GLOBAL
//   set of entity types from ALL DbContext subclasses — and force-registered them all
//   into the current context's model.  In an app with two contexts sharing an assembly,
//   keyless / no-primary-key entities from the secondary context were injected into the
//   primary context's model, causing EF ValidateNonNullPrimaryKeys to throw
//   "requires a primary key to be defined".
//
//   #452: The file-attachment FK loop ALSO used Utils.GetAllModels(), so foreign-context
//   entities that have a FileAttachment property were registered into the primary context's
//   model, causing spurious tables in migrations.
//
//   Both fixes scope all model-builder work to the DbSet<T> properties declared on THIS
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

    // ── Secondary context entity WITH a FileAttachment property (#452) ────────

    /// <summary>
    /// A normal TopBasePoco entity that belongs ONLY to the secondary context
    /// AND has a FileAttachment navigation property.
    ///
    /// If the #452 regression is present, the file-attachment FK loop uses
    /// Utils.GetAllModels() and registers this entity into PrimaryTestContext's
    /// model, causing a spurious table in migrations.
    /// </summary>
    internal class ForeignEntityWithAttachment : BasePoco
    {
        public string Title { get; set; } = "";
        public Guid? AttachmentId { get; set; }
        public FileAttachment? Attachment { get; set; }
    }

    /// <summary>
    /// Secondary context that declares ForeignEntityWithAttachment (has FileAttachment).
    /// This simulates the pattern where a secondary context has document/file entities.
    /// </summary>
    internal class SecondaryFileContext : WalkingTec.Mvvm.Core.Test.DataContext
    {
        public DbSet<ForeignEntityWithAttachment> ForeignAttachments { get; set; } = null!;

        public SecondaryFileContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

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

    /// <summary>
    /// Regression tests for Issue #452: the file-attachment FK loop in
    /// DataContext.OnModelCreating previously used Utils.GetAllModels() (the global
    /// set), which caused foreign-context entities with FileAttachment properties to
    /// be registered into THIS context's model, producing spurious tables in migrations.
    ///
    /// The fix scopes the file-attachment FK loop to the same per-context
    /// thisContextDbSetTypes set used by Pass 1 registration (#450).
    /// </summary>
    [TestClass]
    public class DataContextFileAttachFkScopeTests
    {
        /// <summary>
        /// Core regression for #452: PrimaryTestContext must not contain
        /// ForeignEntityWithAttachment (which has a FileAttachment property but
        /// belongs only to SecondaryFileContext).
        /// </summary>
        [TestMethod]
        [Description("#452 regression: PrimaryTestContext model must not contain SecondaryFileContext's entity that has a FileAttachment property")]
        public void PrimaryContext_Model_DoesNotContain_ForeignEntityWithFileAttachment()
        {
            var seed = Guid.NewGuid().ToString("N");

            using var ctx = new PrimaryTestContext(seed, DBTypeEnum.Memory);
            ctx.Database.EnsureCreated();

            var entityTypeNames = ctx.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToList();

            CollectionAssert.DoesNotContain(entityTypeNames, nameof(ForeignEntityWithAttachment),
                $"PrimaryTestContext's model must not contain '{nameof(ForeignEntityWithAttachment)}' " +
                "— that entity (which has a FileAttachment property) belongs exclusively to SecondaryFileContext. " +
                "If present, it means the file-attachment FK loop is still using Utils.GetAllModels() (#452).");
        }

        /// <summary>
        /// Confirm SecondaryFileContext's own entity with a FileAttachment IS contained
        /// in SecondaryFileContext's model (the scoping must not break the secondary context).
        /// </summary>
        [TestMethod]
        [Description("#452: SecondaryFileContext model must contain its own FileAttachment entity")]
        public void SecondaryFileContext_Model_ContainsOwnFileAttachmentEntity()
        {
            var seed = Guid.NewGuid().ToString("N");

            using var ctx = new SecondaryFileContext(seed, DBTypeEnum.Memory);
            ctx.Database.EnsureCreated();

            var entityTypeNames = ctx.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToList();

            CollectionAssert.Contains(entityTypeNames, nameof(ForeignEntityWithAttachment),
                $"SecondaryFileContext must contain its own entity '{nameof(ForeignEntityWithAttachment)}'.");
        }

        /// <summary>
        /// Confirm that the primary test context's own Student entity (which has a
        /// FileAttachment Photo property) still gets its FK registered correctly,
        /// i.e., the file-attachment FK loop is not broken by the scoping fix.
        /// </summary>
        [TestMethod]
        [Description("#452: primary context own entities with FileAttachment still get FK registered")]
        public void PrimaryContext_OwnStudentEntity_WithFileAttachment_IsRegisteredInModel()
        {
            var seed = Guid.NewGuid().ToString("N");

            // WalkingTec.Mvvm.Core.Test.DataContext includes DbSet<Student>,
            // and Student has a FileAttachment Photo property.
            using var ctx = new WalkingTec.Mvvm.Core.Test.DataContext(seed, DBTypeEnum.Memory);
            ctx.Database.EnsureCreated();

            var entityTypeNames = ctx.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToList();

            CollectionAssert.Contains(entityTypeNames, nameof(WalkingTec.Mvvm.Core.Test.Student),
                "The test DataContext must contain Student — which has a FileAttachment Photo property.");

            // Verify the FK for Student.Photo is configured as Restrict (not Cascade)
            var studentEntityType = ctx.Model.FindEntityType(typeof(WalkingTec.Mvvm.Core.Test.Student));
            Assert.IsNotNull(studentEntityType, "Student entity type must be found in model.");

            var photoFk = studentEntityType!
                .GetForeignKeys()
                .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(FileAttachment));
            Assert.IsNotNull(photoFk,
                "Student must have a foreign key pointing to FileAttachment " +
                "(the file-attachment FK loop must run for this context's own entities).");
            Assert.AreEqual(Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict, photoFk!.DeleteBehavior,
                "Student -> FileAttachment FK must have DeleteBehavior.Restrict (set by the file-attachment FK loop).");
        }
    }
}
