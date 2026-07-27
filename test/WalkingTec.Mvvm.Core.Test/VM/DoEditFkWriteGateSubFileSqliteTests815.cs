#nullable enable
// Issue #815 fourth round — code review (same-vendor Claude + cross-vendor gpt-5.6-sol, both
// REQUEST_CHANGES on the same defect): the DoEdit_/DoEditAsync_/DoAdd_ForgedCrossTenantSubItemFileId_*
// write-time-gate tests previously lived in DeletedFileIdsSubFileCollectionTests815.cs on EF
// InMemory, which does NOT enforce foreign keys. Their load-bearing assertions —
// Assert.AreEqual(Guid.Empty, reloaded.Attachments![0].FileId, ...) — locked in a behaviour that
// cannot occur on any real database: ISubFile mandates a non-nullable Guid FileId plus a
// FileAttachment? navigation, and DataContext.OnModelCreating explicitly leaves ISubFile types on
// EF's default convention (it `continue`s past them), so a real FK constraint to FileAttachment
// always exists. Guid.Empty matches no FileAttachment row, so on any FK-enforcing provider the old
// production code's "clear to Guid.Empty" was not a controlled rejection — DoEdit silently rolled
// back the ENTIRE save (losing the legitimate parent edit too) and DoAdd threw an unhandled
// DbUpdateException. See .claude/rules/testing.md's "EF InMemory limits" section, which now
// documents this exact incident.
//
// The fix (BaseCRUDVM.RejectUnresolvableFileAttachmentReferences / ApplyFileAttachmentResolution)
// no longer writes Guid.Empty: it drops the unresolvable sub-item out of the POSTED collection
// before Utils.CheckDifference/the toadd loop (DoEditPrepare) or the cascade-add (DoAddPrepare)
// ever sees it. These tests run that fix on the FK-enforcing SQLite ProductSubFileContext fixture
// (declared in DoRealDeleteAsyncSubFileTests.cs, same assembly/namespace — EnsureCreated() on it
// does not trip over the unrelated conflicting FKs in the full test DataContext's schema) and
// assert what the FIXED code actually produces:
//   - the parent's legitimate scalar change is still saved (no silent whole-save rollback),
//   - the forged sub-item is absent from the posted collection (no invalid FK ever reaches SaveChanges),
//   - the victim's FileAttachment row survives untouched.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    [TestClass]
    public class DoEditFkWriteGateSubFileSqliteTests815
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"fkwritegate_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        private static FileAttachment SeedVictimFile(ProductSubFileContext ctx)
        {
            var file = new FileAttachment
            {
                ID = Guid.NewGuid(),
                FileName = "victim.txt",
                FileExt = "txt",
                SaveMode = "database",
                TenantCode = "TENANT_VICTIM",
                UploadTime = DateTime.UtcNow,
                Length = 4
            };
            ctx.Set<FileAttachment>().Add(file);
            ctx.SaveChanges();
            return file;
        }

        [TestMethod]
        [Description("#815 fourth round (SQLite, FK-enforcing): DoEdit drops a forged cross-tenant ISubFile.FileId instead of writing an invalid FK — the legitimate scalar edit still saves and the victim's file survives")]
        public void DoEdit_ForgedCrossTenantSubItemFileId_DroppedFromPostedCollection_ScalarEditStillSaves_VictimFileSurvives()
        {
            Guid productId, victimFileId;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var victim = SeedVictimFile(ctx);
                victimFileId = victim.ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                ctx.SaveChanges();
                productId = product.ID;
                // No pre-existing ProductAttachment row — the attacker's forged item below is the
                // ONLY sub-item ever posted, so if the fix works nothing is inserted at all.
            }

            var attackerDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.Entity = new Product
            {
                ID = productId,
                // The legitimate part of this same request: a plain scalar rename.
                Name = "Renamed",
                // Reviewer's probe: forge a sub-item FileId pointing at a victim's cross-tenant
                // file, posted through the same DoEditPrepare the scalar PhotoId fix patches.
                Attachments = new List<ProductAttachment>
                {
                    new ProductAttachment { FileId = victimFileId, Order = 1 }
                }
            };

            // On the pre-fourth-round code (clear to Guid.Empty instead of dropping the item),
            // this throws/rolls back on a real FK-enforcing provider — SQLite here included. If
            // that regresses, this call itself fails the test before any assertion below runs.
            vm.DoEdit(updateAllFields: true);
            Assert.IsFalse(vm.IsConcurrencyConflict, "editing the attacker's own row must still succeed");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .First(x => x.ID == productId);

            Assert.AreEqual("Renamed", reloaded.Name,
                "#815 fourth round: the legitimate scalar change in the SAME request must still " +
                "be saved — a forged sub-item must never cause a silent whole-save rollback");
            Assert.IsTrue(reloaded.Attachments == null || reloaded.Attachments.Count == 0,
                "#815 fourth round: the forged sub-item must be dropped from the posted " +
                "collection before it ever reaches SaveChanges — nothing should have been " +
                "inserted for it at all (no Guid.Empty row, no forged-FK row)");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist — nothing ever touched it");
        }

        [TestMethod]
        [Description("#815 fourth round async (SQLite, FK-enforcing): same as the sync DoEdit version above")]
        public async Task DoEditAsync_ForgedCrossTenantSubItemFileId_DroppedFromPostedCollection_ScalarEditStillSaves_VictimFileSurvives()
        {
            Guid productId, victimFileId;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var victim = SeedVictimFile(ctx);
                victimFileId = victim.ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                await ctx.SaveChangesAsync();
                productId = product.ID;
            }

            var attackerDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.Entity = new Product
            {
                ID = productId,
                Name = "Renamed",
                Attachments = new List<ProductAttachment>
                {
                    new ProductAttachment { FileId = victimFileId, Order = 1 }
                }
            };

            await vm.DoEditAsync(updateAllFields: true);
            Assert.IsFalse(vm.IsConcurrencyConflict, "editing the attacker's own row must still succeed");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = await checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .FirstAsync(x => x.ID == productId);

            Assert.AreEqual("Renamed", reloaded.Name,
                "#815 fourth round async: the legitimate scalar change must still be saved");
            Assert.IsTrue(reloaded.Attachments == null || reloaded.Attachments.Count == 0,
                "#815 fourth round async: the forged sub-item must be dropped, never written");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist");
        }

        [TestMethod]
        [Description("#815 fourth round (SQLite, FK-enforcing): DoAdd drops a forged cross-tenant ISubFile.FileId on a brand-new row instead of throwing on the invalid FK")]
        public void DoAdd_ForgedCrossTenantSubItemFileId_DroppedFromPostedCollection_RowStillCreated_VictimFileSurvives()
        {
            Guid victimFileId;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var victim = SeedVictimFile(ctx);
                victimFileId = victim.ID;
            }

            var attackerDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            var newId = Guid.NewGuid();
            vm.Entity = new Product
            {
                ID = newId,
                Name = "NewWidget",
                Attachments = new List<ProductAttachment>
                {
                    new ProductAttachment { FileId = victimFileId, Order = 1 }
                }
            };

            // On the pre-fourth-round code (clear to Guid.Empty) this throws an unhandled
            // DbUpdateException on a real FK-enforcing provider — DoAdd has no try/catch around
            // SaveChanges. If that regresses, this call itself fails the test.
            vm.DoAdd();

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .FirstOrDefault(x => x.ID == newId);

            Assert.IsNotNull(reloaded, "the new row itself must still be created");
            Assert.IsTrue(reloaded!.Attachments == null || reloaded.Attachments.Count == 0,
                "#815 fourth round: a forged cross-tenant sub-item FileId on Add must be dropped " +
                "from the posted collection — never persisted, not even as an invalid FK");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file must still exist");
        }
    }
}
