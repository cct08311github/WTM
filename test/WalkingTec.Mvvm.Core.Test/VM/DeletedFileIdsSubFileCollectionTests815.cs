#nullable enable
// Issue #815 third round: RejectUnresolvableFileAttachmentReferences (BaseCRUDVM.DoAddPrepare /
// DoEditPrepare) only ever inspected TModel's own SCALAR FileAttachment-typed properties. A
// reviewer proved the identical unvalidated write is reachable through an ISubFile COLLECTION
// property instead:
//
//   Product.Attachments = [ new ProductAttachment { FileId = <a TENANT_VICTIM file GUID> } ]
//
// posted through the very DoEditPrepare/DoAddPrepare the scalar fix patches. Worse than the
// scalar case: GetOwnFileIds/FilterLegitimateDeletedFileIds never covered sub-item collections
// either (see that method's doc comment), so this route had NEITHER the write-time gate NOR the
// DeletedFileIds entity-reference layer — only the sink-level WtmFileProvider.DeleteFileTenantScoped
// call inside DoRealDelete/DoRealDeleteAsync/DoBatchDelete/DoBatchDeleteAsync's own ISubFile walk
// (added in the #815 second rework) ever stood in the way, and it was never exercised by a test
// through the collection route — every existing #815 victim-survives test used the SCALAR PhotoId
// FK on Student/StudentTop.
//
// These tests assert on the SURVIVING FileAttachment row in the database, not on a status code or
// an exception, per the issue's instructions.
//
// #815 fourth round: the three DoEdit_/DoEditAsync_/DoAdd_ForgedCrossTenantSubItemFileId_* write-
// time-gate tests that used to live in THIS file moved to
// DoEditFkWriteGateSubFileSqliteTests815.cs, on the FK-enforcing SQLite ProductSubFileContext
// fixture instead of EF InMemory — see that file's header comment and
// .claude/rules/testing.md's "EF InMemory limits" section for why: InMemory does not enforce
// foreign keys, so their old assertion that a rejected sub-item's FileId "persisted" as
// Guid.Empty was false assurance for a value no FK-enforcing database would ever have accepted.
// The DoBatchDelete(Async) coverage tests below are UNAFFECTED and stay on InMemory: they seed a
// forged reference to a file that legitimately EXISTS (just cross-tenant, not Guid.Empty/absent),
// so no FK constraint is ever at stake for them.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    [TestClass]
    public class DeletedFileIdsSubFileCollectionTests815
    {
        private static FileAttachment SeedFile(DataContext dc, string tenantCode)
        {
            var file = new FileAttachment
            {
                ID = Guid.NewGuid(),
                FileName = "f.txt",
                FileExt = "txt",
                SaveMode = "database",
                TenantCode = tenantCode,
                UploadTime = DateTime.UtcNow,
                Length = 4
            };
            dc.Set<FileAttachment>().Add(file);
            dc.SaveChanges();
            return file;
        }

        private static Product SeedProduct(DataContext dc)
        {
            var product = new Product { ID = Guid.NewGuid(), Name = "Widget" };
            dc.Set<Product>().Add(product);
            dc.SaveChanges();
            return product;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Write-time gate: a forged sub-item FileId must be rejected before it is ever written.
        // #815 fourth round: the three DoEdit_/DoEditAsync_/DoAdd_ForgedCrossTenantSubItemFileId_*
        // tests that used to live here MOVED to DoEditFkWriteGateSubFileSqliteTests815.cs (the
        // FK-enforcing SQLite fixture) — see this file's header comment for why.
        // ─────────────────────────────────────────────────────────────────────

        // ─────────────────────────────────────────────────────────────────────
        // Coverage completion for the delete sinks: DoBatchDelete/DoBatchDeleteAsync already
        // route their ISubFile walk through DeleteFileTenantScoped (#815 second rework), but no
        // existing test exercised that call through the COLLECTION route — every prior
        // victim-survives test used the scalar PhotoId FK on Student/StudentTop. These are not
        // new production code (see docs/production-readiness.md — the sink-level control
        // predates this round); they close a test-coverage gap the reviewer flagged: the
        // sub-item route was previously proven only by inspection, not by a passing/failing test.
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#815 third round coverage: DoBatchDelete must not delete a victim's cross-tenant file reached through a pre-existing forged ISubFile.FileId")]
        public void DoBatchDelete_PreExistingForgedCrossTenantSubItemFileId_VictimFileSurvives()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid productId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                var product = SeedProduct(seedCtx);
                productId = product.ID;
                // Bypasses the VM entirely — simulates data written before this fix (or by any
                // other path RejectUnresolvableFileAttachmentReferences does not cover).
                seedCtx.Set<ProductAttachment>().Add(new ProductAttachment
                {
                    ProductId = productId,
                    FileId = victimFileId,
                    Order = 1
                });
                seedCtx.SaveChanges();
            }

            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseBatchVM<Product, BaseVM>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker"),
                Ids = [productId.ToString()]
            };

            var result = vm.DoBatchDelete();

            Assert.IsTrue(result, "the batch delete itself should succeed");
            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815 third round: DoBatchDelete's DeleteFileTenantScoped call on the ISubFile " +
                "walk must refuse to resolve the victim's cross-tenant file");
            Assert.IsFalse(checkCtx.Set<Product>().IgnoreQueryFilters().Any(x => x.ID == productId),
                "sanity check: the Product row itself must actually have been deleted");
        }

        [TestMethod]
        [Description("#815 third round coverage async: same as the sync DoBatchDelete version above")]
        public async Task DoBatchDeleteAsync_PreExistingForgedCrossTenantSubItemFileId_VictimFileSurvives()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid productId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                var product = SeedProduct(seedCtx);
                productId = product.ID;
                seedCtx.Set<ProductAttachment>().Add(new ProductAttachment
                {
                    ProductId = productId,
                    FileId = victimFileId,
                    Order = 1
                });
                seedCtx.SaveChanges();
            }

            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseBatchVM<Product, BaseVM>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker"),
                Ids = [productId.ToString()]
            };

            var result = await vm.DoBatchDeleteAsync();

            Assert.IsTrue(result, "the batch delete itself should succeed");
            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815 third round async: the victim's cross-tenant file must survive");
            Assert.IsFalse(checkCtx.Set<Product>().IgnoreQueryFilters().Any(x => x.ID == productId),
                "sanity check: the Product row itself must actually have been deleted");
        }
    }
}
