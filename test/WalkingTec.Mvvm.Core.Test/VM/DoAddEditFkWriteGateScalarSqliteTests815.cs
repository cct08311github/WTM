#nullable enable
// Issue #815 sixth round — two edge cases of the fourth/fifth round's write-time FK gate,
// both proven by an executed probe against the FK-enforcing SQLite ProductSubFileContext
// fixture (declared in DoRealDeleteAsyncSubFileTests.cs, same assembly/namespace):
//
//   1. The SCALAR branch of ApplyFileAttachmentResolution reverted an unresolvable posted FK
//      to `preSaveSnapshot`'s prior value, or to `null` when there is no snapshot (Add). That
//      revert-to-null assumes the FK property can HOLD null. Any ISubFile-shaped TModel used
//      directly as its own CRUD VM (e.g. ProductAttachment, or a code-generator-produced
//      sub-table VM) has a non-nullable `Guid FileId` — reflection's
//      `PropertyInfo.SetValue(Entity, null)` on a non-nullable value type does not throw, it
//      silently coerces to `default(Guid)` == Guid.Empty (see the "reflprobe" evidence in this
//      PR's description). That is exactly the unresolvable-FK write the whole gate exists to
//      prevent: DoAdd has no try/catch around SaveChanges, so it surfaces as an unhandled
//      DbUpdateException.
//
//   2. The Edit-path ISubFile-collection branch dropped every rejected item out of the posted
//      collection. That is correct for a brand-new forged item, but when the rejected item's id
//      matches an EXISTING DB child (a mixed re-post: same child id, forged FileId), dropping it
//      removes that id from the list Utils.CheckDifference (run by DoEditPreparePart2) diffs
//      against the DB — the untouched row is classified `toremove` and DC.DeleteEntity
//      physically deletes it. A forged FileId on ONE sub-item silently deleted a totally
//      unrelated, legitimate child row while DoEdit reported success.
//
// The fix (BaseCRUDVM.ApplyFileAttachmentResolution / LoadExistingSubItemFileIds):
//   1. rejects the WHOLE request via MSD.AddModelError when the FK is a non-nullable value type
//      with no legitimate prior value to revert to — DoAdd/DoAddAsync/DoEdit/DoEditAsync skip
//      SaveChanges entirely in that case;
//   2. restores an existing child's ACTUAL stored FileId in place (looked up by id, one batched
//      query per sub-collection property) instead of dropping it, and keeps the drop only for
//      items with no existing DB counterpart.
//
// See .claude/rules/testing.md's "EF InMemory limits" section for why this needs the
// FK-enforcing SQLite fixture, not EF InMemory.

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
    public class DoAddEditFkWriteGateScalarSqliteTests815
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"fkwritegatescalar_{Guid.NewGuid():N}";
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

        private static FileAttachment SeedFile(ProductSubFileContext ctx, string tenantCode)
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

        // ── Item 1: scalar non-nullable FK, DoAdd ──────────────────────────────────────────

        [TestMethod]
        [Description("#815 sixth round (SQLite, FK-enforcing): DoAdd on an ISubFile-shaped TModel (ProductAttachment used directly as its own CRUD VM) rejects the whole request instead of writing Guid.Empty into its non-nullable scalar FileId")]
        public void DoAdd_SubFileTModel_UnresolvableScalarFileId_RequestRejected_NothingPersisted()
        {
            Guid productId, victimFileId;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var victim = SeedFile(ctx, "TENANT_VICTIM");
                victimFileId = victim.ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                ctx.SaveChanges();
                productId = product.ID;
            }

            var attackerDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<ProductAttachment>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            var newId = Guid.NewGuid();
            vm.Entity = new ProductAttachment
            {
                ID = newId,
                ProductId = productId,
                FileId = victimFileId,
                Order = 1
            };

            // Pre-sixth-round code wrote Guid.Empty here; DoAdd has no try/catch around
            // SaveChanges, so on a real FK-enforcing provider this threw an unhandled
            // DbUpdateException. If that regresses, this call itself fails the test.
            vm.DoAdd();

            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#815 sixth round: the caller must be told the request failed");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductAttachment>().FirstOrDefault(x => x.ID == newId);
            Assert.IsNull(reloaded,
                "#815 sixth round: a non-nullable scalar FK with no legitimate prior value to " +
                "revert to must reject the whole request — nothing should be persisted, not " +
                "even a row with FileId == Guid.Empty");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist");
        }

        [TestMethod]
        [Description("#815 sixth round async (SQLite, FK-enforcing): same as the sync DoAdd version above")]
        public async Task DoAddAsync_SubFileTModel_UnresolvableScalarFileId_RequestRejected_NothingPersisted()
        {
            Guid productId, victimFileId;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var victim = SeedFile(ctx, "TENANT_VICTIM");
                victimFileId = victim.ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                await ctx.SaveChangesAsync();
                productId = product.ID;
            }

            var attackerDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<ProductAttachment>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            var newId = Guid.NewGuid();
            vm.Entity = new ProductAttachment
            {
                ID = newId,
                ProductId = productId,
                FileId = victimFileId,
                Order = 1
            };

            await vm.DoAddAsync();

            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#815 sixth round async: the caller must be told the request failed");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = await checkCtx.Set<ProductAttachment>().FirstOrDefaultAsync(x => x.ID == newId);
            Assert.IsNull(reloaded,
                "#815 sixth round async: nothing should be persisted, not even Guid.Empty");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist");
        }

        // ── Item 1: scalar non-nullable FK, DoEdit with no prior row ───────────────────────

        [TestMethod]
        [Description("#815 sixth round (SQLite, FK-enforcing): DoEdit on an ISubFile-shaped TModel with no pre-existing row (preSaveSnapshot is null, same as Add) rejects the whole request instead of writing Guid.Empty")]
        public void DoEdit_SubFileTModel_UnresolvableScalarFileId_NoPriorRow_RequestRejected_NothingPersisted()
        {
            Guid productId, victimFileId;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var victim = SeedFile(ctx, "TENANT_VICTIM");
                victimFileId = victim.ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                ctx.SaveChanges();
                productId = product.ID;
            }

            var attackerDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<ProductAttachment>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            // No ProductAttachment row exists with this id — LoadEntitySnapshot() returns null,
            // exactly like the Add path's preSaveSnapshot.
            var missingId = Guid.NewGuid();
            vm.Entity = new ProductAttachment
            {
                ID = missingId,
                ProductId = productId,
                FileId = victimFileId,
                Order = 1
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict,
                "the gate must reject before ever reaching SaveChanges, not surface as a concurrency conflict");
            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#815 sixth round: the caller must be told the request failed");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductAttachment>().FirstOrDefault(x => x.ID == missingId);
            Assert.IsNull(reloaded, "#815 sixth round: nothing should be persisted, not even Guid.Empty");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist");
        }

        // ── Item 2: mixed re-post on the Edit path ─────────────────────────────────────────

        [TestMethod]
        [Description("#815 sixth round (SQLite, FK-enforcing): a mixed re-post — an EXISTING child id combined with a forged cross-tenant FileId — must restore the child's own prior FileId in place instead of Utils.CheckDifference classifying the untouched row as removed")]
        public void DoEdit_MixedRepost_ExistingChildSameIdForgedFileId_ChildRowSurvivesWithOriginalFileId()
        {
            Guid productId, existingChildId, legitFileId, victimFileId;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var legit = SeedFile(ctx, "TENANT_EDITOR");
                legitFileId = legit.ID;
                var victim = SeedFile(ctx, "TENANT_VICTIM");
                victimFileId = victim.ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                ctx.SaveChanges();
                productId = product.ID;

                var child = new ProductAttachment { ProductId = productId, FileId = legitFileId, Order = 1 };
                ctx.Set<ProductAttachment>().Add(child);
                ctx.SaveChanges();
                existingChildId = child.ID;
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
                Attachments = new List<ProductAttachment>
                {
                    // SAME id as the existing child — but a forged, cross-tenant FileId.
                    new ProductAttachment { ID = existingChildId, FileId = victimFileId, Order = 1 }
                }
            };

            vm.DoEdit(updateAllFields: true);
            Assert.IsFalse(vm.IsConcurrencyConflict, "editing the attacker's own row must still succeed");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var reloadedProduct = checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .First(x => x.ID == productId);

            Assert.AreEqual("Renamed", reloadedProduct.Name,
                "the legitimate scalar change in the same request must still be saved");
            Assert.IsNotNull(reloadedProduct.Attachments,
                "#815 sixth round: the existing child row must survive — a forged FileId must " +
                "not make Utils.CheckDifference classify it as removed");
            Assert.AreEqual(1, reloadedProduct.Attachments!.Count,
                "#815 sixth round: the existing child row must survive, not be physically deleted");
            var reloadedChild = reloadedProduct.Attachments!.Single();
            Assert.AreEqual(existingChildId, reloadedChild.ID);
            Assert.AreEqual(legitFileId, reloadedChild.FileId,
                "#815 sixth round: the child's ORIGINAL FileId must be restored — never the " +
                "forged victim FileId, and never Guid.Empty");

            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist — nothing ever touched it");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == legitFileId),
                "the legitimate file must obviously still exist");
        }

        [TestMethod]
        [Description("#815 sixth round async: same mixed re-post scenario as above, through DoEditAsync")]
        public async Task DoEditAsync_MixedRepost_ExistingChildSameIdForgedFileId_ChildRowSurvivesWithOriginalFileId()
        {
            Guid productId, existingChildId, legitFileId, victimFileId;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var legit = SeedFile(ctx, "TENANT_EDITOR");
                legitFileId = legit.ID;
                var victim = SeedFile(ctx, "TENANT_VICTIM");
                victimFileId = victim.ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                await ctx.SaveChangesAsync();
                productId = product.ID;

                var child = new ProductAttachment { ProductId = productId, FileId = legitFileId, Order = 1 };
                ctx.Set<ProductAttachment>().Add(child);
                await ctx.SaveChangesAsync();
                existingChildId = child.ID;
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
                    new ProductAttachment { ID = existingChildId, FileId = victimFileId, Order = 1 }
                }
            };

            await vm.DoEditAsync(updateAllFields: true);
            Assert.IsFalse(vm.IsConcurrencyConflict, "editing the attacker's own row must still succeed");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var reloadedProduct = await checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .FirstAsync(x => x.ID == productId);

            Assert.AreEqual("Renamed", reloadedProduct.Name);
            Assert.IsNotNull(reloadedProduct.Attachments);
            Assert.AreEqual(1, reloadedProduct.Attachments!.Count,
                "#815 sixth round async: the existing child row must survive");
            var reloadedChild = reloadedProduct.Attachments!.Single();
            Assert.AreEqual(existingChildId, reloadedChild.ID);
            Assert.AreEqual(legitFileId, reloadedChild.FileId,
                "#815 sixth round async: the child's ORIGINAL FileId must be restored");

            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == legitFileId),
                "the legitimate file must obviously still exist");
        }
    }
}
