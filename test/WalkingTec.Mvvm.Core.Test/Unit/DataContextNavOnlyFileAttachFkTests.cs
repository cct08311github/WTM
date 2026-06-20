#nullable enable
// Regression test for Issue #458:
//   The FileAttachment FK-restrict loop in DataContext.OnModelCreating previously only
//   iterated thisContextDbSetTypes (the direct DbSet<T> declarations on the context).
//   EF also discovers entities via navigation properties — a Child entity navigated
//   from a Parent DbSet, but not itself declared as DbSet<Child>, is present in
//   modelBuilder.Model.GetEntityTypes() but was absent from thisContextDbSetTypes.
//   Such navigation-discovered entities got EF's default Cascade delete behavior
//   on their FileAttachment FK instead of Restrict — a data-loss risk.
//
//   Fix (#458): moved the FK-restrict loop to after Pass 1 and changed it to iterate
//   modelBuilder.Model.GetEntityTypes() (the full discovered model) so navigation-only
//   entities also get DeleteBehavior.Restrict on their FileAttachment FKs.
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Unit
{
    // ── Entities ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parent entity — declared as DbSet in NavOnlyParentContext.
    /// Has a collection of NavOnlyChild via navigation.
    /// </summary>
    internal class NavOnlyParent : BasePoco
    {
        public string Name { get; set; } = "";
        public List<NavOnlyChild>? Children { get; set; }
    }

    /// <summary>
    /// Child entity — NOT declared as DbSet anywhere; only discovered via
    /// NavOnlyParent.Children navigation property.
    /// Has a FileAttachment FK — this is the property that must get Restrict behavior.
    /// </summary>
    internal class NavOnlyChild : BasePoco
    {
        public Guid? AttachmentId { get; set; }
        public FileAttachment? Attachment { get; set; }

        public Guid? ParentId { get; set; }
        public NavOnlyParent? Parent { get; set; }
    }

    // ── Context ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Context that only declares NavOnlyParent as a DbSet.
    /// NavOnlyChild is NOT a DbSet — it is discovered via navigation only.
    /// </summary>
    internal class NavOnlyParentContext : WalkingTec.Mvvm.Core.Test.DataContext
    {
        public DbSet<NavOnlyParent> NavOnlyParents { get; set; } = null!;

        public NavOnlyParentContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [TestClass]
    public class DataContextNavOnlyFileAttachFkTests
    {
        /// <summary>
        /// Core regression for #458: NavOnlyChild is discovered via navigation only
        /// (not declared as DbSet). Its FileAttachment FK must be configured as Restrict,
        /// not EF's default Cascade.
        /// </summary>
        [TestMethod]
        [Description("#458 regression: navigation-discovered entity's FileAttachment FK must be DeleteBehavior.Restrict")]
        public void NavOnlyChild_NavigationDiscovered_FileAttachFK_IsRestrict()
        {
            var seed = Guid.NewGuid().ToString("N");

            using var ctx = new NavOnlyParentContext(seed, DBTypeEnum.Memory);
            ctx.Database.EnsureCreated();

            // NavOnlyChild must be in the model (EF discovered it via navigation)
            var childEntityType = ctx.Model.FindEntityType(typeof(NavOnlyChild));
            Assert.IsNotNull(childEntityType,
                "NavOnlyChild must be discovered by EF via the NavOnlyParent.Children navigation property.");

            // The FK to FileAttachment must exist and must be Restrict
            var attachFk = childEntityType!
                .GetForeignKeys()
                .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(FileAttachment));

            Assert.IsNotNull(attachFk,
                "NavOnlyChild must have a FK pointing to FileAttachment. " +
                "This implies the FK-restrict loop processed the navigation-discovered entity.");

            Assert.AreEqual(DeleteBehavior.Restrict, attachFk!.DeleteBehavior,
                "#458 regression: NavOnlyChild -> FileAttachment FK must have DeleteBehavior.Restrict. " +
                "If Cascade or ClientCascade is found, the FK-restrict loop is still only iterating DbSet<T> types " +
                "and missing navigation-discovered entities.");
        }

        /// <summary>
        /// No-regression check: entities declared as DbSet (e.g. Student with FileAttachment Photo)
        /// must still get DeleteBehavior.Restrict after the fix.
        /// </summary>
        [TestMethod]
        [Description("#458 no-regression: DbSet-declared entities with FileAttachment still get Restrict after fix")]
        public void DbSetDeclared_FileAttachFK_IsStillRestrict_NoRegression()
        {
            var seed = Guid.NewGuid().ToString("N");

            // WalkingTec.Mvvm.Core.Test.DataContext has DbSet<Student>,
            // and Student has FileAttachment Photo.
            using var ctx = new WalkingTec.Mvvm.Core.Test.DataContext(seed, DBTypeEnum.Memory);
            ctx.Database.EnsureCreated();

            var studentEntityType = ctx.Model.FindEntityType(typeof(WalkingTec.Mvvm.Core.Test.Student));
            Assert.IsNotNull(studentEntityType, "Student entity type must be found in model.");

            var photoFk = studentEntityType!
                .GetForeignKeys()
                .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(FileAttachment));

            Assert.IsNotNull(photoFk,
                "Student must have a FK to FileAttachment (the Photo property). " +
                "This FK must have been configured by the FK-restrict loop.");

            Assert.AreEqual(DeleteBehavior.Restrict, photoFk!.DeleteBehavior,
                "Student -> FileAttachment FK must still be DeleteBehavior.Restrict after the #458 fix. " +
                "If this fails, the new loop broke the existing DbSet-declared entity behavior.");
        }
    }
}
