#nullable enable
// Issue #815 seventh round — a code review of ApplyFileAttachmentResolution enumerated every
// branch of the sixth round's fix and found three more defects, each proven by an executed probe
// against the FK-enforcing SQLite fixture (never EF InMemory — .claude/rules/testing.md's "EF
// InMemory limits" section explains why):
//
//   1. [HIGH] The scalar rejection predicate at ~line 2006 was `fkType.IsValueType &&
//      Nullable.GetUnderlyingType(fkType) == null` — CLR nullability only. `[Required] Guid?
//      PhotoId` (or a fluent IsRequired()) makes EF map a CLR-nullable FK to a NOT NULL column,
//      so that shape's old predicate said "safe to write null", fell through to
//      `SetValue(Entity, null)`, and produced an unhandled DbUpdateException out of
//      DoAdd/DoAddAsync — the exact SqliteException 19 "NOT NULL constraint failed" the sixth
//      round's fix was supposed to prevent, one shape over.
//
//   2. [MEDIUM] LoadExistingSubItemFileIds was called unconditionally, and a match's item was
//      restored (kept) regardless of Add vs Edit. But the restore is only correct on Edit — only
//      DoEditPreparePart2 runs Utils.CheckDifference at all. On Add, a kept item whose id matches
//      an existing DB row is cascade-inserted by the Add path's own straight Add loop, which
//      knows nothing about "restore vs new" — a duplicate-PK insert, unhandled out of DoAdd.
//
//   3. [LOW] LoadExistingSubItemFileIds was not scoped to the parent being edited, so a rejected
//      item whose id belonged to a DIFFERENT parent's existing child row was also restored and
//      kept. DoEditPreparePart2's own sub-table query IS parent-scoped, so that kept item was
//      invisible to it, got classified `toadd`, and PK-violated there too — aborting the WHOLE
//      edit (including the request's own legitimate scalar change) instead of just dropping one
//      forged item.
//
// The fix (BaseCRUDVM.ApplyFileAttachmentResolution / LoadExistingSubItemFileIds):
//   1. asks DC.Model.FindEntityType(typeof(TModel))?.FindProperty(fkProperty.Name)?.IsNullable
//      instead of the FK property's CLR type; a property EF cannot find at all fails closed
//      (treated as required).
//   2. only calls LoadExistingSubItemFileIds when preSaveSnapshot != null (the Edit path); Add
//      always falls through to the drop path, same as the fourth round's original behaviour.
//   3. LoadExistingSubItemFileIds now filters by the same parent FK DoEditPreparePart2's own
//      sub-table query filters by (resolved via DC.GetFKName<TModel>); when that FK cannot be
//      resolved for a relationship shape, it returns nothing rather than fall back to the old
//      unscoped lookup.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Test-only entity with a REQUIRED scalar FileAttachment FK whose backing property is still
    /// CLR-nullable (<c>Guid?</c>) — the exact shape that defeated the sixth round's CLR-type-only
    /// predicate. <c>[Required]</c> makes EF map <c>PhotoId</c> to a NOT NULL column even though
    /// its CLR type can hold <see langword="null"/>.
    /// </summary>
    [Table("zz_required_photo_owner")]
    public class ProductWithRequiredPhoto : TopBasePoco
    {
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.StringLength(100)]
        public string? Name { get; set; }

        [System.ComponentModel.DataAnnotations.Required]
        public Guid? PhotoId { get; set; }
        public FileAttachment? Photo { get; set; }
    }

    /// <summary>
    /// Test-only entity with a plain OPTIONAL scalar FileAttachment FK (no <c>[Required]</c>) —
    /// the S4/S5 non-regression control: an unresolvable reference with no legitimate prior value
    /// must still be allowed to persist as <see langword="null"/>, exactly as before this round.
    /// </summary>
    [Table("zz_optional_photo_owner")]
    public class ProductWithOptionalPhoto : TopBasePoco
    {
        [System.ComponentModel.DataAnnotations.StringLength(100)]
        public string? Name { get; set; }

        public Guid? PhotoId { get; set; }
        public FileAttachment? Photo { get; set; }
    }

    // ── Minimal context used by this test class ────────────────────────────────────────────
    // Reuses Product/ProductAttachment (declared in DoRealDeleteAsyncSubFileTests.cs, same
    // assembly/namespace) for the ISubFile-collection (C2) tests, and adds the two models above
    // for the scalar (S4/S5) tests. Extends FrameworkContext directly, not the full test
    // DataContext, so EnsureCreated() does not trip over unrelated conflicting FKs.
    internal class RequiredFkGateContext : FrameworkContext
    {
        public DbSet<Product> Products { get; set; } = null!;
        public DbSet<ProductAttachment> ProductAttachments { get; set; } = null!;
        public DbSet<ProductWithRequiredPhoto> RequiredPhotoOwners { get; set; } = null!;
        public DbSet<ProductWithOptionalPhoto> OptionalPhotoOwners { get; set; } = null!;

        public RequiredFkGateContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    [TestClass]
    public class FkWriteGateSeventhRoundSqliteTests815
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"fkwritegate7th_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        private static FileAttachment SeedFile(RequiredFkGateContext ctx, string tenantCode)
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

        // ── Item 1 (S5): [Required] Guid? scalar FK, DoAdd ─────────────────────────────────

        [TestMethod]
        [Description("#815 seventh round (SQLite, FK-enforcing): DoAdd on a [Required] Guid? scalar FK rejects the whole request instead of writing null into a NOT NULL column")]
        public void DoAdd_RequiredScalarFileId_Unresolvable_RequestRejected_NothingPersisted()
        {
            Guid victimFileId;
            using (var ctx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite))
            {
                victimFileId = SeedFile(ctx, "TENANT_VICTIM").ID;
            }

            var attackerDc = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<ProductWithRequiredPhoto>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            var newId = Guid.NewGuid();
            vm.Entity = new ProductWithRequiredPhoto
            {
                ID = newId,
                Name = "Widget",
                PhotoId = victimFileId
            };

            // Pre-seventh-round code (CLR-type-only predicate) said "Guid? is nullable, safe to
            // write null" here and fell through to SetValue(Entity, null); DoAdd has no
            // try/catch around SaveChanges, so on this FK-enforcing NOT NULL column that threw an
            // unhandled DbUpdateException. If that regresses, this call itself fails the test.
            vm.DoAdd();

            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#815 seventh round: the caller must be told the request failed");

            using var checkCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithRequiredPhoto>().FirstOrDefault(x => x.ID == newId);
            Assert.IsNull(reloaded,
                "#815 seventh round: a required FK column with no legitimate prior value to " +
                "revert to must reject the whole request — nothing should be persisted");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist");
        }

        [TestMethod]
        [Description("#815 seventh round async: same as the sync DoAdd version above")]
        public async Task DoAddAsync_RequiredScalarFileId_Unresolvable_RequestRejected_NothingPersisted()
        {
            Guid victimFileId;
            using (var ctx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite))
            {
                victimFileId = SeedFile(ctx, "TENANT_VICTIM").ID;
            }

            var attackerDc = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<ProductWithRequiredPhoto>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            var newId = Guid.NewGuid();
            vm.Entity = new ProductWithRequiredPhoto
            {
                ID = newId,
                Name = "Widget",
                PhotoId = victimFileId
            };

            await vm.DoAddAsync();

            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#815 seventh round async: the caller must be told the request failed");

            using var checkCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = await checkCtx.Set<ProductWithRequiredPhoto>().FirstOrDefaultAsync(x => x.ID == newId);
            Assert.IsNull(reloaded, "#815 seventh round async: nothing should be persisted");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist");
        }

        // ── Item 1 (S5): [Required] Guid? scalar FK, DoEdit with no prior row ──────────────

        [TestMethod]
        [Description("#815 seventh round (SQLite, FK-enforcing): DoEdit with no pre-existing row (preSaveSnapshot is null, same as Add) rejects the whole request on a required scalar FK")]
        public void DoEdit_RequiredScalarFileId_NoPriorRow_Unresolvable_RequestRejected_NothingPersisted()
        {
            Guid victimFileId;
            using (var ctx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite))
            {
                victimFileId = SeedFile(ctx, "TENANT_VICTIM").ID;
            }

            var attackerDc = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<ProductWithRequiredPhoto>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            // No row exists with this id — LoadEntitySnapshot() returns null, exactly like Add's
            // preSaveSnapshot.
            var missingId = Guid.NewGuid();
            vm.Entity = new ProductWithRequiredPhoto
            {
                ID = missingId,
                Name = "Widget",
                PhotoId = victimFileId
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict,
                "the gate must reject before ever reaching SaveChanges, not surface as a concurrency conflict");
            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#815 seventh round: the caller must be told the request failed");

            using var checkCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithRequiredPhoto>().FirstOrDefault(x => x.ID == missingId);
            Assert.IsNull(reloaded, "#815 seventh round: nothing should be persisted");
        }

        // ── S4/S5 non-regression: plain optional Guid? FK still persists NULL ──────────────

        [TestMethod]
        [Description("#815 seventh round non-regression (SQLite, FK-enforcing): DoAdd on a plain OPTIONAL Guid? scalar FK (no [Required]) still persists null for an unresolvable reference with no prior value — the seventh round's EF-model check must not over-reject an actually-optional column")]
        public void DoAdd_OptionalScalarFileId_Unresolvable_PersistsNull_RequestNotRejected()
        {
            Guid victimFileId;
            using (var ctx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite))
            {
                victimFileId = SeedFile(ctx, "TENANT_VICTIM").ID;
            }

            var attackerDc = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<ProductWithOptionalPhoto>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            var newId = Guid.NewGuid();
            vm.Entity = new ProductWithOptionalPhoto
            {
                ID = newId,
                Name = "Widget",
                PhotoId = victimFileId
            };

            vm.DoAdd();

            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#815 seventh round non-regression: a genuinely optional FK column must not be rejected at the request level");

            using var checkCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().FirstOrDefault(x => x.ID == newId);
            Assert.IsNotNull(reloaded, "the row must have been persisted — an optional FK's unresolvable reference clears to null, it does not reject the request");
            Assert.IsNull(reloaded!.PhotoId, "the unresolvable reference must clear to null, never keep the forged cross-tenant id");
        }

        // ── Item 2 (C2-Add): a rejected sub-item on the ADD path must be dropped, never restored ──

        [TestMethod]
        [Description("#815 seventh round (SQLite, FK-enforcing): a rejected ISubFile item on the ADD path must be DROPPED even when its id happens to match an existing child row for the same (new) parent id — restoring it there means Utils.CheckDifference, which only runs on Edit, never sees it, so DoAdd's own cascade-add would duplicate-PK-insert")]
        public void DoAdd_RejectedSubItemId_MatchesExistingRowForSameNewParentId_Dropped_NoDuplicateKeyInsert()
        {
            Guid parentId = Guid.NewGuid();
            Guid existingChildId, legitFileId, victimFileId;

            using (var ctx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite))
            {
                legitFileId = SeedFile(ctx, "TENANT_ATTACKER").ID;
                victimFileId = SeedFile(ctx, "TENANT_VICTIM").ID;

                // Seed a Product + child under parentId, then physically orphan the child by
                // deleting the Product row with FK enforcement OFF (so the DB-level ON DELETE
                // CASCADE this FK's convention would otherwise apply does not fire) — this frees
                // parentId for reuse as a brand-new Add's own id below, with NO competing parent
                // row, while a child row whose ProductId column value is still literally
                // `parentId` remains in the database. This isolates the ADD-path restore defect
                // (item 2) from the parent-scoping fix (item 3): item 3's parent-scoped lookup
                // WILL find this child (its stored ProductId really is parentId, matching the new
                // Add's own Entity.GetID()) — proving the guard that must stop it is specifically
                // "do not call the restore lookup at all on Add", not the scoping itself.
                var seedProduct = new Product { ID = parentId, Name = "ToBeOrphaned" };
                ctx.Set<Product>().Add(seedProduct);
                ctx.SaveChanges();

                var child = new ProductAttachment { ProductId = parentId, FileId = legitFileId, Order = 1 };
                ctx.Set<ProductAttachment>().Add(child);
                ctx.SaveChanges();
                existingChildId = child.ID;

                ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
                ctx.Database.ExecuteSqlRaw("DELETE FROM zz_product WHERE ID = {0};", parentId);
                ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
            }

            // Sanity: the parent row is gone, the child row (still bearing ProductId = parentId)
            // survives, untouched, as an orphan.
            using (var sanityCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite))
            {
                Assert.IsNull(sanityCtx.Set<Product>().FirstOrDefault(x => x.ID == parentId), "sanity: the parent row must be gone before the attacker's Add");
                var orphan = sanityCtx.Set<ProductAttachment>().FirstOrDefault(x => x.ID == existingChildId);
                Assert.IsNotNull(orphan, "sanity: the orphaned child row must still exist");
                Assert.AreEqual(legitFileId, orphan!.FileId, "sanity: the orphaned child's FileId must be untouched before the attacker's Add");
            }

            var attackerDc = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.Entity = new Product
            {
                // Reuses the now-freed parentId — the attacker's brand-new Add just happens to
                // land on the same id the orphaned child's ProductId column still points at.
                ID = parentId,
                Name = "New Product",
                Attachments = new List<ProductAttachment>
                {
                    // Forges the orphaned child's own id with an unresolvable cross-tenant
                    // FileId. Pre-item-2-fix, this id "matched an existing row" under
                    // item 3's parent-scoped lookup (ProductId == parentId) and was restored /
                    // kept — then cascade-inserted by DoAdd's Add loop as a NEW row with an
                    // ALREADY-EXISTING primary key, a UNIQUE constraint violation.
                    new ProductAttachment { ID = existingChildId, FileId = victimFileId, Order = 1 }
                }
            };

            // If item 2's guard regresses, this call itself throws the unhandled
            // DbUpdateException (SQLite UNIQUE constraint failed) proving the defect.
            vm.DoAdd();

            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#815 seventh round: DoAdd itself must succeed — dropping a rejected sub-item is not a request-level rejection");

            using var checkCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            var newProduct = checkCtx.Set<Product>().FirstOrDefault(x => x.ID == parentId);
            Assert.IsNotNull(newProduct, "#815 seventh round: the new Product row must have been created");

            var orphanAfter = checkCtx.Set<ProductAttachment>().FirstOrDefault(x => x.ID == existingChildId);
            Assert.IsNotNull(orphanAfter, "#815 seventh round: the pre-existing orphaned child row must still exist");
            Assert.AreEqual(legitFileId, orphanAfter!.FileId,
                "#815 seventh round: the pre-existing child row's FileId must be untouched — never overwritten with the forged victim id, never duplicated");

            var newProductChildren = checkCtx.Set<ProductAttachment>().Where(x => x.ProductId == parentId).ToList();
            Assert.AreEqual(1, newProductChildren.Count,
                "#815 seventh round: only the ONE pre-existing child row may exist for this parent id — the forged item must have been dropped, not duplicate-inserted");
        }

        // ── Item 3 (C2-cross-parent): a rejected sub-item id belonging to ANOTHER parent must
        //    be dropped, never restored, on Edit ──────────────────────────────────────────────

        [TestMethod]
        [Description("#815 seventh round (SQLite, FK-enforcing): a rejected ISubFile item whose id belongs to a DIFFERENT parent's existing child must be dropped, not restored — restoring it made DoEditPreparePart2's own parent-scoped CheckDifference classify it `toadd` and PK-violate, aborting the whole edit including the request's own legitimate scalar change")]
        public void DoEdit_RejectedSubItemId_BelongsToDifferentParent_Dropped_LegitimateScalarChangeSurvives()
        {
            Guid parentAId, parentBId, childAId, legitFileA, victimFileId;

            using (var ctx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite))
            {
                legitFileA = SeedFile(ctx, "TENANT_EDITOR").ID;
                victimFileId = SeedFile(ctx, "TENANT_VICTIM").ID;

                var parentA = new Product { Name = "ParentA" };
                ctx.Set<Product>().Add(parentA);
                var parentB = new Product { Name = "ParentB" };
                ctx.Set<Product>().Add(parentB);
                ctx.SaveChanges();
                parentAId = parentA.ID;
                parentBId = parentB.ID;

                var childA = new ProductAttachment { ProductId = parentAId, FileId = legitFileA, Order = 1 };
                ctx.Set<ProductAttachment>().Add(childA);
                ctx.SaveChanges();
                childAId = childA.ID;
                // Parent B intentionally has NO children of its own.
            }

            var attackerDc = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.Entity = new Product
            {
                ID = parentBId,
                // The legitimate part of this same request: a plain scalar rename on Parent B.
                Name = "Renamed",
                Attachments = new List<ProductAttachment>
                {
                    // Forges Parent A's own child id, with an unresolvable cross-tenant FileId,
                    // posted as if it belonged to Parent B.
                    new ProductAttachment { ID = childAId, FileId = victimFileId, Order = 1 }
                }
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict, "must not surface as a concurrency conflict");
            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#815 seventh round: dropping a cross-parent sub-item is not a request-level rejection — DoEdit must succeed");

            using var checkCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            var reloadedB = checkCtx.Set<Product>().FirstOrDefault(x => x.ID == parentBId);
            Assert.IsNotNull(reloadedB);
            Assert.AreEqual("Renamed", reloadedB!.Name,
                "#815 seventh round: the SAME request's legitimate scalar rename must survive — pre-fix, the cross-parent PK violation rolled back the WHOLE edit, losing this too");

            var reloadedChildA = checkCtx.Set<ProductAttachment>().FirstOrDefault(x => x.ID == childAId);
            Assert.IsNotNull(reloadedChildA, "#815 seventh round: Parent A's own child row must still exist — never deleted, never duplicated");
            Assert.AreEqual(parentAId, reloadedChildA!.ProductId, "#815 seventh round: Parent A's child must still belong to Parent A, never reparented to Parent B");
            Assert.AreEqual(legitFileA, reloadedChildA.FileId, "#815 seventh round: Parent A's child's FileId must be untouched — never overwritten with the forged victim id");

            var parentBChildren = checkCtx.Set<ProductAttachment>().Where(x => x.ProductId == parentBId).ToList();
            Assert.AreEqual(0, parentBChildren.Count,
                "#815 seventh round: Parent B must end up with NO children — the forged item was dropped, not restored/kept, and never reparented Parent A's row to Parent B");
        }
    }
}
