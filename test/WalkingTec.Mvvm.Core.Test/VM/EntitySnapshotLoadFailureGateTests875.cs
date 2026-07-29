#nullable enable
// Issue #875 (third site, found by the exhaustive pass the issue required before fixing the
// named LoadExistingSubItemFileIds site): BaseCRUDVM.LoadEntitySnapshot/LoadEntitySnapshotAsync
// catch ANY exception from the pre-edit snapshot query and returned a bare `null` —
// indistinguishable from the two LEGITIMATE reasons that method already returns null (Entity.ID
// not set; the row was genuinely deleted concurrently). DoEdit/DoEditAsync/DoDelete/
// DoDeleteAsync pass that null on as `preSaveSnapshot` into
// RejectUnresolvableFileAttachmentReferences(Async) -> ApplyFileAttachmentResolution, which
// reads `preSaveSnapshot == null` as "this is an Add, there is no prior DB state" and, on that
// reading, SKIPS the LoadExistingSubItemFileIds restore-vs-drop lookup entirely for every
// rejected sub-item (its own `preSaveSnapshot != null ? ... : []` guard) — even though this is
// really an EDIT of a row that still exists. A caller re-posting an EXISTING child with an
// unresolvable FileId is then unconditionally DROPPED instead of restored, which, when it
// empties the whole posted collection, routes into DoEditPreparePart2's `Count()==0` branch and
// physically deletes EVERY existing child row for the parent — the identical Issue #828/#875
// data-loss chain, reached through a spurious Add-shape misclassification of a real Edit instead
// of a resolution-query failure.
//
// The fix reuses the same contract Issue #828/#875 already established: LoadEntitySnapshot(Async)
// now returns a Succeeded flag alongside the snapshot, and the four Edit/Delete callers reject
// the whole request (MSD error, no SaveChanges/DoEditPrepare at all) when Succeeded is false,
// instead of ever letting an ambiguous null reach the file-reference gate.
//
// This test uses a DbCommandInterceptor to make ONLY the pre-edit snapshot SELECT (against the
// exact quoted "zz_product" table — never the "zz_product_attachment" sub-table the other two
// #875/#828 tests exercise) throw on a real FK-enforcing SQLite fixture. It asserts what the
// FIXED code must do: the entity's PRE-EXISTING child survives untouched, with its ORIGINAL
// FileId, and the caller is told the request failed. The positive control in the same test
// method proves the SAME re-post, unarmed, still saves normally AND correctly restores the
// existing child's FileId in place.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    [TestClass]
    public class EntitySnapshotLoadFailureGateTests875
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"snapshotloadfail875_{Guid.NewGuid():N}";
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

        private static FileAttachment SeedFile(ProductSubFileContext ctx, string name)
        {
            var file = new FileAttachment
            {
                ID = Guid.NewGuid(),
                FileName = name,
                FileExt = "txt",
                SaveMode = "database",
                TenantCode = "TENANT_A",
                UploadTime = DateTime.UtcNow,
                Length = 4
            };
            ctx.Set<FileAttachment>().Add(file);
            ctx.SaveChanges();
            return file;
        }

        [TestMethod]
        [Description("#875 (third site): a pre-edit snapshot-load query FAILURE must reject the whole edit, never fall through as if this were an Add with no prior state — positive control in the same test proves the normal path still saves and restores the existing child's FileId in place")]
        public void DoEdit_EntitySnapshotLoadThrows_ExistingChildSurvives_RequestRejected_PositiveControlStillSaves()
        {
            Guid productId, existingAttachmentId, file1Id;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                file1Id = SeedFile(ctx, "keep1.txt").ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                ctx.SaveChanges();
                productId = product.ID;

                var attachment = new ProductAttachment { ProductId = productId, FileId = file1Id, Order = 1 };
                ctx.Set<ProductAttachment>().Add(attachment);
                ctx.SaveChanges();
                existingAttachmentId = attachment.ID;
            }

            var bogusFileId = Guid.NewGuid();
            var interceptor = new EntitySnapshotLoadFailureInterceptor();

            // ── Positive control: the SAME re-post, interceptor UNARMED, must still save AND
            // restore the child's real FileId ──
            using (var controlDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite, interceptor))
            {
                controlDc.SetTenantCode("TENANT_A");
                var controlVm = new BaseCRUDVM<Product>
                {
                    Wtm = MockWtmContext.CreateWtmContext(controlDc, "owner")
                };
                controlVm.Entity = new Product
                {
                    ID = productId,
                    Name = "Renamed-control",
                    Attachments = new List<ProductAttachment>
                    {
                        new ProductAttachment { ID = existingAttachmentId, ProductId = productId, FileId = bogusFileId, Order = 1 }
                    }
                };
                controlVm.DoEdit(updateAllFields: true);

                Assert.IsTrue(controlVm.MSD == null || controlVm.MSD.Count == 0,
                    "positive control: an unarmed snapshot load must save normally, not be rejected");
            }

            using (var checkAfterControl = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var reloaded = checkAfterControl.Set<Product>()
                    .Include(x => x.Attachments)
                    .First(x => x.ID == productId);
                Assert.AreEqual("Renamed-control", reloaded.Name,
                    "positive control: the legitimate scalar edit must be saved");
                Assert.AreEqual(1, reloaded.Attachments!.Count,
                    "positive control: the existing child must not be dropped");
                Assert.AreEqual(file1Id, reloaded.Attachments!.Single().FileId,
                    "positive control: the existing child's REAL FileId must be restored in place");
            }

            // ── Act: arm the interceptor so ONLY the pre-edit snapshot query throws ──
            interceptor.Arm();
            var attackerDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite, interceptor);
            attackerDc.SetTenantCode("TENANT_A");
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "owner")
            };
            vm.Entity = new Product
            {
                ID = productId,
                Name = "Renamed-during-outage",
                // Same re-post as the positive control — before the fix, a snapshot-load-query
                // exception here silently made this Edit look like an Add to
                // ApplyFileAttachmentResolution (preSaveSnapshot == null), which skips the
                // restore-vs-drop lookup entirely and DROPS this rejected item, emptying the
                // posted collection and triggering DoEditPreparePart2's delete-every-child branch.
                Attachments = new List<ProductAttachment>
                {
                    new ProductAttachment { ID = existingAttachmentId, ProductId = productId, FileId = bogusFileId, Order = 1 }
                }
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#875 (third site): the caller must be told the request failed when the pre-edit " +
                "snapshot load itself failed — a dependency failure must never be reported as success");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var afterOutage = checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .First(x => x.ID == productId);

            Assert.AreEqual("Renamed-control", afterOutage.Name,
                "#875 (third site): the scalar edit attempted during the snapshot-load outage " +
                "must NOT have been saved");
            Assert.IsNotNull(afterOutage.Attachments,
                "#875 (third site): the entity's PRE-EXISTING child must survive a snapshot-load " +
                "failure untouched");
            Assert.AreEqual(1, afterOutage.Attachments!.Count,
                "#875 (third site): a dependency failure must never silently delete data the " +
                "caller never asked to remove — the pre-existing child must still exist");
            Assert.AreEqual(existingAttachmentId, afterOutage.Attachments!.Single().ID,
                "#875 (third site): the surviving child must be the SAME row, unchanged");
            Assert.AreEqual(file1Id, afterOutage.Attachments!.Single().FileId,
                "#875 (third site): the surviving child's FileId must be UNCHANGED");
        }

        [TestMethod]
        [Description("#875 (third site) async: same as the sync DoEditAsync version above")]
        public async Task DoEditAsync_EntitySnapshotLoadThrows_ExistingChildSurvives_RequestRejected_PositiveControlStillSaves()
        {
            Guid productId, existingAttachmentId, file1Id;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                file1Id = SeedFile(ctx, "keep1.txt").ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                await ctx.SaveChangesAsync();
                productId = product.ID;

                var attachment = new ProductAttachment { ProductId = productId, FileId = file1Id, Order = 1 };
                ctx.Set<ProductAttachment>().Add(attachment);
                await ctx.SaveChangesAsync();
                existingAttachmentId = attachment.ID;
            }

            var bogusFileId = Guid.NewGuid();
            var interceptor = new EntitySnapshotLoadFailureInterceptor();

            using (var controlDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite, interceptor))
            {
                controlDc.SetTenantCode("TENANT_A");
                var controlVm = new BaseCRUDVM<Product>
                {
                    Wtm = MockWtmContext.CreateWtmContext(controlDc, "owner")
                };
                controlVm.Entity = new Product
                {
                    ID = productId,
                    Name = "Renamed-control",
                    Attachments = new List<ProductAttachment>
                    {
                        new ProductAttachment { ID = existingAttachmentId, ProductId = productId, FileId = bogusFileId, Order = 1 }
                    }
                };
                await controlVm.DoEditAsync(updateAllFields: true);

                Assert.IsTrue(controlVm.MSD == null || controlVm.MSD.Count == 0,
                    "positive control (async): an unarmed snapshot load must save normally");
            }

            using (var checkAfterControl = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var reloaded = await checkAfterControl.Set<Product>()
                    .Include(x => x.Attachments)
                    .FirstAsync(x => x.ID == productId);
                Assert.AreEqual("Renamed-control", reloaded.Name,
                    "positive control (async): the legitimate scalar edit must be saved");
                Assert.AreEqual(1, reloaded.Attachments!.Count,
                    "positive control (async): the existing child must not be dropped");
                Assert.AreEqual(file1Id, reloaded.Attachments!.Single().FileId,
                    "positive control (async): the existing child's REAL FileId must be restored in place");
            }

            interceptor.Arm();
            var attackerDc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite, interceptor);
            attackerDc.SetTenantCode("TENANT_A");
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "owner")
            };
            vm.Entity = new Product
            {
                ID = productId,
                Name = "Renamed-during-outage",
                Attachments = new List<ProductAttachment>
                {
                    new ProductAttachment { ID = existingAttachmentId, ProductId = productId, FileId = bogusFileId, Order = 1 }
                }
            };

            await vm.DoEditAsync(updateAllFields: true);

            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#875 (third site) async: the caller must be told the request failed when the " +
                "pre-edit snapshot load itself failed");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var afterOutage = await checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .FirstAsync(x => x.ID == productId);

            Assert.AreEqual("Renamed-control", afterOutage.Name,
                "#875 (third site) async: the scalar edit attempted during the snapshot-load " +
                "outage must NOT have been saved");
            Assert.IsNotNull(afterOutage.Attachments,
                "#875 (third site) async: the entity's PRE-EXISTING child must survive untouched");
            Assert.AreEqual(1, afterOutage.Attachments!.Count,
                "#875 (third site) async: the pre-existing child must still exist");
            Assert.AreEqual(existingAttachmentId, afterOutage.Attachments!.Single().ID,
                "#875 (third site) async: the surviving child must be the SAME row, unchanged");
            Assert.AreEqual(file1Id, afterOutage.Attachments!.Single().FileId,
                "#875 (third site) async: the surviving child's FileId must be UNCHANGED");
        }
    }

    /// <summary>
    /// Issue #875 (third site): throws once armed, on every SELECT against the exact quoted
    /// "zz_product" table (deliberately anchored with the closing quote so it never matches
    /// "zz_product_attachment", the sub-table the other two #875/#828 interceptors/tests target)
    /// — simulating BaseCRUDVM.LoadEntitySnapshot/LoadEntitySnapshotAsync's pre-edit snapshot
    /// query failing for a reason that has nothing to do with whether the row still exists (a
    /// provider parameter cap, a timeout, a transient connection drop, ...).
    /// </summary>
    internal sealed class EntitySnapshotLoadFailureInterceptor : DbCommandInterceptor
    {
        private volatile bool _armed;

        public void Arm() => _armed = true;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            MaybeThrow(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeThrow(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void MaybeThrow(string sql)
        {
            if (!_armed) return;
            if (sql.IndexOf("\"zz_product\"", StringComparison.OrdinalIgnoreCase) < 0) return;
            throw new InvalidOperationException(
                "Simulated pre-edit entity snapshot load failure (Issue #875 third-site test)");
        }
    }
}
