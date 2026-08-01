#nullable enable
// Issue #875: the SAME defect Issue #828 fixed, one call site over. LoadExistingSubItemFileIds
// (BaseCRUDVM.cs) looks up which of a batch of REJECTED sub-items (posted with an unresolvable
// FileId) already exist as a DB row for the SAME parent, so ApplyFileAttachmentResolution can
// RESTORE an existing child's real FileId in place instead of dropping it (Issue #815
// sixth/seventh round). Before this fix, an exception from that lookup query (provider
// parameter cap, a timeout, a transient connection drop, ...) was caught, logged, and the
// method fell through to `return result` with whatever it had (possibly nothing) —
// indistinguishable from "the query ran fine and confirmed none of these rejected items have an
// existing DB row". ApplyFileAttachmentResolution then unconditionally DROPPED every rejected
// item instead of restoring it. When that empties the whole posted collection,
// DoEditPreparePart2's `else if (... .Count() == 0)` branch physically deletes EVERY existing
// child row for that parent, and DoEdit reports success.
//
// The fix reuses the exact contract Issue #828 established for the earlier resolution-query
// failure: LoadExistingSubItemFileIds now returns a Succeeded flag alongside its result, and
// ApplyFileAttachmentResolution rejects the WHOLE request (MSD error, no SaveChanges) when
// Succeeded is false, instead of ever treating a query failure as "confirmed, nothing to
// restore".
//
// This test uses a DbCommandInterceptor to make ONLY the sub-item lookup SELECT (against
// zz_product_attachment) throw on a real FK-enforcing SQLite fixture (never EF InMemory —
// .claude/rules/testing.md), while leaving the earlier FileAttachment resolution query
// untouched (it must succeed and correctly classify the posted FileId as unresolvable — the
// ordinary #815 sixth/seventh round scenario, not a resolution-query failure). It asserts what
// the FIXED code must do: the entity's PRE-EXISTING child survives untouched, with its ORIGINAL
// FileId, and the caller is told the request failed. The positive control in the same test
// method proves the SAME re-post, unarmed, still saves normally AND correctly restores the
// existing child's FileId in place (rather than dropping it) — the fix must reject a lookup
// failure, not turn every rejected-but-restorable item into a rejection.

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
    public class ExistingSubItemLookupFailureGateTests875
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"subitemlookupfail875_{Guid.NewGuid():N}";
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
        [Description("#875: an existing-sub-item lookup query FAILURE must reject the whole edit and leave the existing child untouched, never silently drop it — positive control in the same test proves the normal path still saves AND restores the child's FileId in place")]
        public void DoEdit_ExistingSubItemLookupThrows_ExistingChildSurvives_RequestRejected_PositiveControlStillSaves()
        {
            // ── Arrange: a Product with ONE pre-existing, legitimate child ──
            Guid productId, existingAttachmentId, file1Id;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                ctx.SetTenantCode("TENANT_A"); // Issue #824: scope the seed context to the file(s)' own tenant so seeding a dependent row that references them does not itself trip FileAttachmentSaveChangesGuard.
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

            // A FileId that never resolves for this caller — not seeded at all. Posting the
            // EXISTING child's own ID together with this bogus FileId is the ordinary #815
            // sixth/seventh round scenario: ApplyFileAttachmentResolution must look up whether
            // existingAttachmentId already exists as a DB row (it does) and RESTORE its real
            // FileId in place, never drop the item outright.
            var bogusFileId = Guid.NewGuid();
            var interceptor = new SubItemLookupFailureInterceptor();

            // ── Positive control: the SAME re-post, interceptor UNARMED, must still save AND
            // restore the child's real FileId (not the posted bogus one, not dropped) ──
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
                    "positive control: an unarmed sub-item lookup must save normally, not be rejected");
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
                    "positive control: the existing child's REAL FileId must be restored in place, " +
                    "not overwritten with the posted bogus one and not left dangling");
            }

            // ── Act: arm the interceptor so ONLY the sub-item lookup query throws ──
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
                // Re-posts the EXISTING child's id with an unresolvable FileId — before the fix,
                // a lookup-query exception here fell through to an empty "no existing row found"
                // result, the item was DROPPED from the posted collection, and
                // DoEditPreparePart2's empty-collection branch deleted the existing child while
                // DoEdit reported success.
                Attachments = new List<ProductAttachment>
                {
                    new ProductAttachment { ID = existingAttachmentId, ProductId = productId, FileId = bogusFileId, Order = 1 }
                }
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#875: the caller must be told the request failed when the sub-item lookup itself " +
                "failed — a dependency failure must never be reported as success");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var afterOutage = checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .First(x => x.ID == productId);

            Assert.AreEqual("Renamed-control", afterOutage.Name,
                "#875: the scalar edit attempted during the lookup outage must NOT have been " +
                "saved — the whole request is rejected, not partially applied");
            Assert.IsNotNull(afterOutage.Attachments,
                "#875: the entity's PRE-EXISTING child must survive a lookup-query failure untouched");
            Assert.AreEqual(1, afterOutage.Attachments!.Count,
                "#875: a dependency failure must never silently delete data the caller never " +
                "asked to remove — the pre-existing child must still exist");
            Assert.AreEqual(existingAttachmentId, afterOutage.Attachments!.Single().ID,
                "#875: the surviving child must be the SAME row, unchanged");
            Assert.AreEqual(file1Id, afterOutage.Attachments!.Single().FileId,
                "#875: the surviving child's FileId must be UNCHANGED — never overwritten with " +
                "the rejected bogus value the outage prevented from being validated");
        }

        [TestMethod]
        [Description("#875 async: same as the sync DoEdit version above")]
        public async Task DoEditAsync_ExistingSubItemLookupThrows_ExistingChildSurvives_RequestRejected_PositiveControlStillSaves()
        {
            Guid productId, existingAttachmentId, file1Id;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                ctx.SetTenantCode("TENANT_A"); // Issue #824: scope the seed context to the file(s)' own tenant so seeding a dependent row that references them does not itself trip FileAttachmentSaveChangesGuard.
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
            var interceptor = new SubItemLookupFailureInterceptor();

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
                    "positive control (async): an unarmed sub-item lookup must save normally");
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
                "#875 async: the caller must be told the request failed when the sub-item lookup " +
                "itself failed");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var afterOutage = await checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .FirstAsync(x => x.ID == productId);

            Assert.AreEqual("Renamed-control", afterOutage.Name,
                "#875 async: the scalar edit attempted during the lookup outage must NOT have " +
                "been saved");
            Assert.IsNotNull(afterOutage.Attachments,
                "#875 async: the entity's PRE-EXISTING child must survive untouched");
            Assert.AreEqual(1, afterOutage.Attachments!.Count,
                "#875 async: the pre-existing child must still exist");
            Assert.AreEqual(existingAttachmentId, afterOutage.Attachments!.Single().ID,
                "#875 async: the surviving child must be the SAME row, unchanged");
            Assert.AreEqual(file1Id, afterOutage.Attachments!.Single().FileId,
                "#875 async: the surviving child's FileId must be UNCHANGED");
        }

        [TestMethod]
        [Description("#875: a rejected-item count above the batch size still resolves (restores) correctly across multiple chunked queries — chunking must not itself narrow or drop a legitimate restore")]
        public void DoEdit_RejectedCountAboveBatchSize_AllChunksRestore_NoDataLoss()
        {
            // FileAttachmentResolutionBatchSize is 500 (private, shared with the #828 resolution
            // query) — use a count comfortably above any reasonable batch size so this proves
            // multi-chunk lookup end-to-end without depending on the exact private constant.
            const int childCount = 1300;

            Guid productId;
            var attachmentIds = new List<Guid>(childCount);
            var fileIds = new List<Guid>(childCount);
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                ctx.SetTenantCode("TENANT_A"); // Issue #824: scope the seed context to the file(s)' own tenant so seeding a dependent row that references them does not itself trip FileAttachmentSaveChangesGuard.
                var files = new List<FileAttachment>(childCount);
                for (int i = 0; i < childCount; i++)
                {
                    var f = new FileAttachment
                    {
                        ID = Guid.NewGuid(),
                        FileName = $"bulk{i}.txt",
                        FileExt = "txt",
                        SaveMode = "database",
                        TenantCode = "TENANT_A",
                        UploadTime = DateTime.UtcNow,
                        Length = 4
                    };
                    files.Add(f);
                    fileIds.Add(f.ID);
                }
                ctx.Set<FileAttachment>().AddRange(files);
                ctx.SaveChanges();

                var product = new Product { Name = "BulkWidget" };
                ctx.Set<Product>().Add(product);
                ctx.SaveChanges();
                productId = product.ID;

                var attachments = new List<ProductAttachment>(childCount);
                for (int i = 0; i < childCount; i++)
                {
                    attachments.Add(new ProductAttachment { ProductId = productId, FileId = fileIds[i], Order = i });
                }
                ctx.Set<ProductAttachment>().AddRange(attachments);
                ctx.SaveChanges();
                attachmentIds.AddRange(attachments.Select(a => a.ID));
            }

            // Re-post every existing child with a BOGUS FileId — every single one is rejected by
            // the resolution step, so LoadExistingSubItemFileIds must look up (and restore) all
            // childCount ids, well above one batch.
            var bogusFileId = Guid.NewGuid();

            var dc = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            dc.SetTenantCode("TENANT_A");
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(dc, "owner")
            };
            vm.Entity = new Product
            {
                ID = productId,
                Name = "BulkWidget-renamed",
                Attachments = attachmentIds
                    .Select((id, i) => new ProductAttachment { ID = id, ProductId = productId, FileId = bogusFileId, Order = i })
                    .ToList()
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#875: a large but entirely restorable batch of rejected items must resolve " +
                "across chunks without being rejected");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .First(x => x.ID == productId);

            Assert.AreEqual("BulkWidget-renamed", reloaded.Name);
            Assert.IsNotNull(reloaded.Attachments);
            Assert.AreEqual(childCount, reloaded.Attachments!.Count,
                "#875: chunking the existing-sub-item lookup must not drop any restorable child");
            CollectionAssert.AreEquivalent(
                fileIds,
                reloaded.Attachments!.Select(x => x.FileId).ToList(),
                "#875: every child's REAL FileId must be restored, not left as the posted bogus value");
        }
    }

    /// <summary>
    /// Issue #875: throws exactly ONCE after being armed, on the first SELECT against the
    /// zz_product_attachment table, then auto-disarms — simulating a single transient failure
    /// (a provider parameter cap, a timeout, a transient connection drop, ...) rather than a
    /// sustained outage. One-shot is deliberate, not merely convenient: LoadExistingSubItemFileIds
    /// is the ONLY caller that queries this table before the file-reference gate decides whether
    /// to continue in the FIXED code, so a single throw is all the fixed guard ever needs to see.
    /// But UNDER THE MUTANT (which neutralizes that guard), the request proceeds and
    /// DoEditPreparePart2 issues its OWN, unrelated, un-guarded query against the SAME table
    /// moments later — if this interceptor kept throwing indefinitely, that second query would
    /// also throw, surfacing as an unhandled exception instead of the mutant's actual real-world
    /// consequence (a silently completed delete). Auto-disarming lets that second query succeed
    /// normally under the mutant, reproducing the true pre-fix behaviour: the existing child gets
    /// silently deleted and DoEdit reports success — exactly what the test's assertions detect.
    /// Deliberately does NOT match "FileAttachment" (the table the earlier #828-fixed resolution
    /// query hits), so this interceptor cannot be confused with
    /// FileAttachmentResolutionFailureInterceptor and leaves that earlier, unrelated query free
    /// to succeed normally.
    /// </summary>
    internal sealed class SubItemLookupFailureInterceptor : DbCommandInterceptor
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
            if (sql.IndexOf("zz_product_attachment", StringComparison.OrdinalIgnoreCase) < 0) return;
            _armed = false; // one-shot — see the class doc comment for why
            throw new InvalidOperationException(
                "Simulated existing-sub-item lookup query failure (Issue #875 test)");
        }
    }
}
