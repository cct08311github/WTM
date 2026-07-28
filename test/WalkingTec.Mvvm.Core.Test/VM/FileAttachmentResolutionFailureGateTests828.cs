#nullable enable
// Issue #828: ResolveFileAttachmentIdsForCaller (and its async twin) used to catch ANY
// exception from the batched FileAttachment resolution query and return an EMPTY resolved
// set — indistinguishable from "the query succeeded and none of these candidate ids exist".
// ApplyFileAttachmentResolution then applied its per-item narrowing rules (revert scalar /
// drop-or-restore sub-item, #815 fourth/sixth/seventh round) to EVERY posted candidate as if
// each one had genuinely failed to resolve. When a posted ISubFile collection's every item
// happens to be a resolution candidate — the ORDINARY case, since a caller editing an entity
// normally re-posts its own unchanged children too — every item got dropped, emptying the
// WHOLE posted collection. DoEditPreparePart2's `else if (... .Count() == 0)` branch (see the
// "fourth round" doc paragraph on RejectUnresolvableFileAttachmentReferences) then physically
// deletes EVERY existing child row for that parent, and DoEdit reports success. A dependency
// failure that has nothing to do with any candidate id's legitimacy — SQL Server's 2100
// parameter cap (real again under EF Core 10's default multi-parameter Contains() translation),
// a timeout, a transient connection drop — must never be able to reach that branch.
//
// The fix distinguishes "the resolution QUERY failed" from "the query succeeded and these ids
// don't exist": ResolveFileAttachmentIdsForCaller now returns a Succeeded flag alongside the
// resolved set, and RejectUnresolvableFileAttachmentReferences rejects the WHOLE request when
// Succeeded is false — the same "nothing persisted, MSD carries the error" contract the
// required-FK-with-no-prior-value path already uses (#815 sixth/seventh round) — instead of
// letting ApplyFileAttachmentResolution narrow per item on an ambiguous empty set.
//
// This test uses a DbCommandInterceptor to make the FileAttachment resolution SELECT throw on
// a real FK-enforcing SQLite fixture (never EF InMemory — .claude/rules/testing.md's "EF
// InMemory limits" section) and asserts what the FIXED code must do: the entity's PRE-EXISTING
// children survive untouched and the caller is told the request failed. The positive control in
// the same test method proves the SAME re-post, unarmed, still saves normally — the fix must
// reject a resolution failure, not turn every edit into a rejection.

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
    public class FileAttachmentResolutionFailureGateTests828
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"fileresolvefail828_{Guid.NewGuid():N}";
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
        [Description("#828: a batched FileAttachment resolution query FAILURE must reject the whole edit, never silently delete every existing child — positive control in the same test proves the normal path still saves")]
        public void DoEdit_ResolutionQueryThrows_ExistingChildrenSurvive_RequestRejected_PositiveControlStillSaves()
        {
            // ── Arrange: a Product with TWO pre-existing, legitimate children ──
            Guid productId;
            Guid file1Id, file2Id;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                file1Id = SeedFile(ctx, "keep1.txt").ID;
                file2Id = SeedFile(ctx, "keep2.txt").ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                ctx.SaveChanges();
                productId = product.ID;

                ctx.Set<ProductAttachment>().AddRange(
                    new ProductAttachment { ProductId = productId, FileId = file1Id, Order = 1 },
                    new ProductAttachment { ProductId = productId, FileId = file2Id, Order = 2 });
                ctx.SaveChanges();
            }

            var interceptor = new FileAttachmentResolutionFailureInterceptor();

            // ── Positive control: the SAME re-post, interceptor UNARMED, must still save ──
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
                        new ProductAttachment { ProductId = productId, FileId = file1Id, Order = 1 },
                        new ProductAttachment { ProductId = productId, FileId = file2Id, Order = 2 }
                    }
                };
                controlVm.DoEdit(updateAllFields: true);

                Assert.IsTrue(controlVm.MSD == null || controlVm.MSD.Count == 0,
                    "positive control: an unarmed resolution query must save normally, not be rejected");
            }

            using (var checkAfterControl = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var reloaded = checkAfterControl.Set<Product>().First(x => x.ID == productId);
                Assert.AreEqual("Renamed-control", reloaded.Name,
                    "positive control: the legitimate scalar edit must be saved");
            }

            // ── Act: arm the interceptor so the FileAttachment resolution query throws ──
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
                // Re-posts the SAME two pre-existing, entirely legitimate children — the normal
                // "the user did not touch their sub-items" case. Before the fix, a resolution-
                // query exception here narrowed to an empty resolved set, both items were dropped
                // from the posted collection, and DoEditPreparePart2's empty-collection branch
                // deleted both existing rows while DoEdit reported success.
                Attachments = new List<ProductAttachment>
                {
                    new ProductAttachment { ProductId = productId, FileId = file1Id, Order = 1 },
                    new ProductAttachment { ProductId = productId, FileId = file2Id, Order = 2 }
                }
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#828: the caller must be told the request failed when resolution itself failed — " +
                "a dependency failure must never be reported as success");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var afterOutage = checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .First(x => x.ID == productId);

            Assert.AreEqual("Renamed-control", afterOutage.Name,
                "#828: the scalar edit attempted during the resolution outage must NOT have been " +
                "saved — the whole request is rejected, not partially applied");
            Assert.IsNotNull(afterOutage.Attachments,
                "#828: the entity's PRE-EXISTING children must survive a resolution-query " +
                "failure untouched");
            Assert.AreEqual(2, afterOutage.Attachments!.Count,
                "#828: a dependency failure must never silently delete data the caller never " +
                "asked to remove — both pre-existing children must still exist");
            CollectionAssert.AreEquivalent(
                new[] { file1Id, file2Id },
                afterOutage.Attachments!.Select(x => x.FileId).ToArray(),
                "#828: the surviving children must be the SAME two files, unchanged");
        }

        [TestMethod]
        [Description("#828 async: same as the sync DoEdit version above")]
        public async Task DoEditAsync_ResolutionQueryThrows_ExistingChildrenSurvive_RequestRejected_PositiveControlStillSaves()
        {
            Guid productId;
            Guid file1Id, file2Id;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                file1Id = SeedFile(ctx, "keep1.txt").ID;
                file2Id = SeedFile(ctx, "keep2.txt").ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                await ctx.SaveChangesAsync();
                productId = product.ID;

                ctx.Set<ProductAttachment>().AddRange(
                    new ProductAttachment { ProductId = productId, FileId = file1Id, Order = 1 },
                    new ProductAttachment { ProductId = productId, FileId = file2Id, Order = 2 });
                await ctx.SaveChangesAsync();
            }

            var interceptor = new FileAttachmentResolutionFailureInterceptor();

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
                        new ProductAttachment { ProductId = productId, FileId = file1Id, Order = 1 },
                        new ProductAttachment { ProductId = productId, FileId = file2Id, Order = 2 }
                    }
                };
                await controlVm.DoEditAsync(updateAllFields: true);

                Assert.IsTrue(controlVm.MSD == null || controlVm.MSD.Count == 0,
                    "positive control (async): an unarmed resolution query must save normally");
            }

            using (var checkAfterControl = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var reloaded = await checkAfterControl.Set<Product>().FirstAsync(x => x.ID == productId);
                Assert.AreEqual("Renamed-control", reloaded.Name,
                    "positive control (async): the legitimate scalar edit must be saved");
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
                    new ProductAttachment { ProductId = productId, FileId = file1Id, Order = 1 },
                    new ProductAttachment { ProductId = productId, FileId = file2Id, Order = 2 }
                }
            };

            await vm.DoEditAsync(updateAllFields: true);

            Assert.IsTrue(vm.MSD != null && vm.MSD.Count > 0,
                "#828 async: the caller must be told the request failed when resolution itself failed");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var afterOutage = await checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .FirstAsync(x => x.ID == productId);

            Assert.AreEqual("Renamed-control", afterOutage.Name,
                "#828 async: the scalar edit attempted during the resolution outage must NOT have " +
                "been saved");
            Assert.IsNotNull(afterOutage.Attachments,
                "#828 async: the entity's PRE-EXISTING children must survive untouched");
            Assert.AreEqual(2, afterOutage.Attachments!.Count,
                "#828 async: both pre-existing children must still exist");
            CollectionAssert.AreEquivalent(
                new[] { file1Id, file2Id },
                afterOutage.Attachments!.Select(x => x.FileId).ToArray(),
                "#828 async: the surviving children must be the SAME two files, unchanged");
        }

        [TestMethod]
        [Description("#828: candidate count above the batch size still resolves correctly across multiple chunked queries — chunking must not itself narrow or drop a legitimate reference")]
        public void DoEdit_CandidateCountAboveBatchSize_AllChunksResolve_NoDataLoss()
        {
            // FileAttachmentResolutionBatchSize is 500 (private) — use a count comfortably above
            // any reasonable batch size so this proves multi-chunk resolution end-to-end without
            // depending on the exact private constant value.
            const int childCount = 1300;

            Guid productId;
            var fileIds = new List<Guid>(childCount);
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
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
            }

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
                Attachments = fileIds
                    .Select((id, i) => new ProductAttachment { ProductId = productId, FileId = id, Order = i })
                    .ToList()
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#828: a large but entirely legitimate batch of candidates must resolve across " +
                "chunks without being rejected");

            using var checkCtx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<Product>()
                .Include(x => x.Attachments)
                .First(x => x.ID == productId);

            Assert.AreEqual("BulkWidget-renamed", reloaded.Name);
            Assert.IsNotNull(reloaded.Attachments);
            Assert.AreEqual(childCount, reloaded.Attachments!.Count,
                "#828: chunking the resolution query must not drop any legitimate candidate");
        }
    }

    /// <summary>
    /// Issue #828: throws once armed, on every SELECT against the FileAttachment table —
    /// simulating BaseCRUDVM.ResolveFileAttachmentIdsForCaller's batched Contains() query
    /// failing for a reason that has nothing to do with whether the candidate ids are
    /// legitimate (SQL Server's 2100-parameter cap under EF Core 10's default multi-parameter
    /// translation, a timeout, a transient connection drop, ...).
    /// </summary>
    internal sealed class FileAttachmentResolutionFailureInterceptor : DbCommandInterceptor
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
            if (sql.IndexOf("FileAttachment", StringComparison.OrdinalIgnoreCase) < 0) return;
            throw new InvalidOperationException(
                "Simulated FileAttachment resolution query failure (Issue #828 test)");
        }
    }
}
