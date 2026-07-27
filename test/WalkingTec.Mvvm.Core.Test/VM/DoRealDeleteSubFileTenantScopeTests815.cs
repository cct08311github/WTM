#nullable enable
// Issue #815 third round, coverage completion for DoRealDelete/DoRealDeleteAsync: their
// ISubFile walk already routes through WtmFileProvider.DeleteFileTenantScoped (#815 second
// rework), but no existing test exercised that call through the COLLECTION route — every prior
// victim-survives test used the scalar PhotoId FK on Student. This is not new production code
// (the sink-level control predates this round; see docs/production-readiness.md); it closes a
// test-coverage gap the reviewer flagged when auditing the new ISubFile write-time gate in
// RejectUnresolvableFileAttachmentReferences (BaseCRUDVM.cs).
//
// SQLite shared-memory (not EF InMemory) is used for the same reason as
// DoRealDeleteAsyncSubFileTests.cs: these tests load the entity via .Include(x => x.Attachments)
// on a SEPARATE DbContext instance from the one used to delete, and reuse the
// ProductSubFileContext (Product + ProductAttachment DbSets only, so EnsureCreated() does not
// trip over the unrelated conflicting FKs in the full test DataContext's schema) declared in
// that file (same assembly, same namespace).

using System;
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
    public class DoRealDeleteSubFileTenantScopeTests815
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"subfiletenant_{Guid.NewGuid():N}";
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
        [Description("#815 third round coverage: DoRealDelete must not delete a victim's cross-tenant file reached through a pre-existing forged ISubFile.FileId")]
        public void DoRealDelete_PreExistingForgedCrossTenantSubItemFileId_VictimFileSurvives()
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

                // Bypasses the VM entirely — simulates data written before this fix (or by any
                // other path RejectUnresolvableFileAttachmentReferences does not cover).
                ctx.Set<ProductAttachment>().Add(new ProductAttachment
                {
                    ProductId = productId,
                    FileId = victimFileId,
                    Order = 1
                });
                ctx.SaveChanges();
            }

            Product entityWithNav;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                entityWithNav = ctx.Set<Product>()
                    .Include(x => x.Attachments)
                    .AsNoTracking()
                    .First(x => x.ID == productId);
            }

            var attackerDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.Entity = entityWithNav;

            vm.DoRealDelete();

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815 third round: DoRealDelete's DeleteFileTenantScoped call on the ISubFile " +
                "collection walk must refuse to resolve the victim's cross-tenant file");
            Assert.IsFalse(checkCtx.Set<Product>().IgnoreQueryFilters().Any(x => x.ID == productId),
                "sanity check: the Product row itself must actually have been deleted");
        }

        [TestMethod]
        [Description("#815 third round coverage async: same as the sync DoRealDelete version above")]
        public async Task DoRealDeleteAsync_PreExistingForgedCrossTenantSubItemFileId_VictimFileSurvives()
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

                ctx.Set<ProductAttachment>().Add(new ProductAttachment
                {
                    ProductId = productId,
                    FileId = victimFileId,
                    Order = 1
                });
                ctx.SaveChanges();
            }

            Product entityWithNav;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                entityWithNav = await ctx.Set<Product>()
                    .Include(x => x.Attachments)
                    .AsNoTracking()
                    .FirstAsync(x => x.ID == productId);
            }

            var attackerDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.Entity = entityWithNav;

            await vm.DoRealDeleteAsync();

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815 third round async: DoRealDeleteAsync's DeleteFileTenantScoped call must " +
                "refuse to resolve the victim's cross-tenant file");
            Assert.IsFalse(checkCtx.Set<Product>().IgnoreQueryFilters().Any(x => x.ID == productId),
                "sanity check: the Product row itself must actually have been deleted");
        }
    }
}
