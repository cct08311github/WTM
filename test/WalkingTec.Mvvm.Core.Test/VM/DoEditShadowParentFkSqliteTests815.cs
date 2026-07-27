#nullable enable
// Issue #815 eighth round — round 8's review found a NEW unhandled exception introduced by
// round 8 itself, on input that worked before that round:
//
//   For an ISubFile collection whose PARENT FK is an EF shadow property (no backing CLR
//   property on the item type — e.g. a downstream model that omits an explicit `ParentId`
//   scalar and lets EF create the FK by convention), LoadExistingSubItemFileIds's round-8
//   parent-scoping (`itemType.GetSingleProperty(fkname)` — see that method's own doc comment)
//   correctly fails closed and restores nothing. But when that means EVERY rejected item in the
//   posted collection gets dropped, the collection can end up EMPTY, and DoEditPreparePart2's
//   `else if (... list2?.Count() == 0)` branch (BaseCRUDVM.cs, ~line 1090) resolves the SAME
//   fkname via `DC.GetFKName<TModel>` and calls
//   `Expression.MakeMemberAccess(pe, ftype.GetSingleProperty(fkname)!)` with NO null check —
//   for a shadow FK that throws an unhandled ArgumentNullException (a null MemberInfo) straight
//   out of DoEdit. Pre-round-8, the same rejected item could be restored by the OLD unscoped
//   lookup (a global id match), keeping the collection non-empty and never reaching this branch
//   — so this is genuinely input that worked before round 8's parent-scoping fix.
//
// The fix (BaseCRUDVM.DoEditPreparePart2, the `else if` empty-collection branch): resolve
// `ftype.GetSingleProperty(fkname)` once, and when it comes back null (the shadow-property
// shape), skip the cascade-delete for this parent — log a warning and continue — instead of
// building an Expression.MakeMemberAccess with a null MemberInfo. This matches the fail-closed
// posture LoadExistingSubItemFileIds already uses for the identical shape: "cannot parent-scope,
// so leave existing children untouched" rather than guessing at a query this method cannot
// safely construct.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Test-only parent entity whose child collection's FK is deliberately left as an EF SHADOW
    /// property — <see cref="ShadowFkChild"/> declares no explicit parent-id scalar, so EF Core's
    /// own convention creates the FK column with no backing CLR property on the dependent type.
    /// This is the exact shape #815's round 8 review flagged: <c>DC.GetFKName&lt;T&gt;</c>
    /// resolves a name from EF's model metadata (which knows about shadow properties), but
    /// <c>itemType.GetSingleProperty(fkname)</c> (plain reflection over the CLR type) returns
    /// null for it. No in-repo model, demo, or known downstream app (BMS) has this shape — see
    /// the framework-generality note in Issue #815's eighth round.
    /// </summary>
    [Table("zz_shadowfk_parent")]
    public class ShadowFkParent : TopBasePoco
    {
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.StringLength(100)]
        public string? Name { get; set; }

        public List<ShadowFkChild>? Children { get; set; }
    }

    [Table("zz_shadowfk_child")]
    public class ShadowFkChild : TopBasePoco, ISubFile
    {
        public Guid FileId { get; set; }
        public FileAttachment? File { get; set; }
        public int Order { get; set; }
    }

    internal class ShadowFkContext : FrameworkContext
    {
        public DbSet<ShadowFkParent> ShadowFkParents { get; set; } = null!;
        public DbSet<ShadowFkChild> ShadowFkChildren { get; set; } = null!;

        public ShadowFkContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    [TestClass]
    public class DoEditShadowParentFkSqliteTests815
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"shadowfkparent_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new ShadowFkContext(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        private static FileAttachment SeedFile(ShadowFkContext ctx, string tenantCode)
        {
            var file = new FileAttachment
            {
                ID = Guid.NewGuid(),
                FileName = "file.txt",
                FileExt = "txt",
                SaveMode = "database",
                TenantCode = tenantCode,
                UploadTime = DateTime.UtcNow,
                Length = 4
            };
            ctx.Set<FileAttachment>().Add(file);
            ctx.SaveChanges();
            return file;
        }

        [TestMethod]
        [Description("#815 eighth round (SQLite, FK-enforcing): editing a parent whose ISubFile collection's FK is an EF shadow property, with a rejected (unresolvable) sub-item that empties the posted collection, must not throw an unhandled exception out of DoEdit, and must leave the existing child untouched rather than silently deleting it via a query the shadow shape cannot safely construct")]
        public void DoEdit_ShadowParentFk_RejectedSubItemEmptiesCollection_NoUnhandledException_ExistingChildUntouched()
        {
            Guid parentId, legitFileId, victimFileId, existingChildId;

            using (var ctx = new ShadowFkContext(ConnectionString, DBTypeEnum.SQLite))
            {
                legitFileId = SeedFile(ctx, "TENANT_EDITOR").ID;
                victimFileId = SeedFile(ctx, "TENANT_VICTIM").ID;

                var parent = new ShadowFkParent { Name = "Parent" };
                ctx.Set<ShadowFkParent>().Add(parent);
                ctx.SaveChanges();
                parentId = parent.ID;

                var child = new ShadowFkChild { FileId = legitFileId, Order = 1 };
                parent.Children = new List<ShadowFkChild> { child };
                ctx.SaveChanges();
                existingChildId = child.ID;
            }

            // Sanity: confirm the reachability premise itself before asserting on DoEdit — the
            // FK name EF resolves for the Children navigation must have NO backing CLR property
            // on ShadowFkChild. If this ever fails, EF stopped treating this shape as a shadow
            // property and the rest of this test is no longer exercising round 8's defect.
            using (var probeCtx = new ShadowFkContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var fkname = ((IDataContext)probeCtx).GetFKName<ShadowFkParent>(nameof(ShadowFkParent.Children));
                Assert.IsFalse(string.IsNullOrEmpty(fkname), "sanity: EF must have resolved SOME FK name for the Children navigation");
                Assert.IsNull(typeof(ShadowFkChild).GetProperty(fkname), "sanity: that FK name must NOT have a backing CLR property on ShadowFkChild — otherwise this isn't the shadow-property shape #815's round 8 review flagged");
            }

            var editorDc = new ShadowFkContext(ConnectionString, DBTypeEnum.SQLite);
            editorDc.SetTenantCode("TENANT_EDITOR");
            var vm = new BaseCRUDVM<ShadowFkParent>
            {
                Wtm = MockWtmContext.CreateWtmContext(editorDc, "editor")
            };
            vm.Entity = new ShadowFkParent
            {
                ID = parentId,
                Name = "Renamed",
                Children = new List<ShadowFkChild>
                {
                    // Forges the existing child's own id with an unresolvable cross-tenant
                    // FileId. LoadExistingSubItemFileIds cannot parent-scope a shadow FK (no CLR
                    // property to build the lookup query against), so it fails closed and
                    // restores nothing — this item gets DROPPED, emptying the posted collection
                    // entirely. Pre-fix, that routed DoEditPreparePart2 into the crashing
                    // `else if (... .Count() == 0)` branch.
                    new ShadowFkChild { ID = existingChildId, FileId = victimFileId, Order = 1 }
                }
            };

            // Pre-fix: this call itself throws an unhandled ArgumentNullException out of
            // Expression.MakeMemberAccess(pe, null) in DoEditPreparePart2's empty-collection
            // branch, because ftype.GetSingleProperty(fkname) is null for the shadow FK. If the
            // guard regresses, this line is where the test fails.
            vm.DoEdit(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict, "#815 eighth round: must not surface as a concurrency conflict");

            using var checkCtx = new ShadowFkContext(ConnectionString, DBTypeEnum.SQLite);
            var reloadedParent = checkCtx.Set<ShadowFkParent>().FirstOrDefault(x => x.ID == parentId);
            Assert.IsNotNull(reloadedParent, "#815 eighth round: the edit must complete without throwing — the parent row must still exist");
            Assert.AreEqual("Renamed", reloadedParent!.Name,
                "#815 eighth round: the request's own legitimate scalar change must be persisted, not lost to an aborted save");

            var existingChildAfter = checkCtx.Set<ShadowFkChild>().FirstOrDefault(x => x.ID == existingChildId);
            Assert.IsNotNull(existingChildAfter,
                "#815 eighth round: the pre-existing child must not be silently deleted — a shadow FK this method cannot safely query against must fail closed by leaving it untouched, not by guessing at a delete query");
            Assert.AreEqual(legitFileId, existingChildAfter!.FileId,
                "#815 eighth round: the pre-existing child's FileId must be untouched — never overwritten with the forged victim id");
        }
    }
}
