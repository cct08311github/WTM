#nullable enable
// Issue #824: FileAttachmentSaveChangesGuard is a SaveChanges-level backstop meant to catch every
// write-path bypass of BaseCRUDVM.DoAddPrepare/DoEditPrepare's own per-model gate, regardless of
// which VM/controller/import primitive staged the write. This file proves that claim against each
// of the six concrete bypass paths named in the issue, on a real FK-enforcing SQLite fixture
// (never EF InMemory — .claude/rules/testing.md's "EF InMemory limits" section), plus the
// must-not-reject controls (same-tenant legitimate write, an attachment created in the SAME unit
// of work, and the kill switch).
//
// Bisected against the pre-guard tree (git stash of the EmptyContext.cs SaveChanges wiring): every
// "_RejectedByGuard_" test below FAILED before the guard existed — the forged cross-tenant FK
// landed and the "must not have persisted" assertion went red. See the commit message for the
// captured RED text.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Exceptions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ── Grandchild ISubFile fixture (bypass path #4): a collection TWO levels below the root
    // aggregate. BaseCRUDVM.CollectFileAttachmentCandidates only walks TModel's OWN declared
    // properties for IEnumerable<ISubFile> — a grandchild collection reached through an
    // intermediate List<T> navigation is invisible to it. EF's own Add() cascade still tracks the
    // whole reachable graph (DC!.Set<TModel>().Add(Entity) in BaseCRUDVM.DoAddPrepare), so the
    // grandchild FK still gets INSERTed unless something at the SaveChanges layer catches it. ──

    [Table("zz_gc_order_824")]
    public class GrandchildOrder824 : TopBasePoco
    {
        [StringLength(100)]
        public string? Name { get; set; }
        public List<GrandchildBatch824>? Batches { get; set; }
    }

    [Table("zz_gc_batch_824")]
    public class GrandchildBatch824 : TopBasePoco
    {
        public Guid OrderId { get; set; }
        public GrandchildOrder824? Order { get; set; }
        public List<GrandchildAttachment824>? Attachments { get; set; }
    }

    [Table("zz_gc_attachment_824")]
    public class GrandchildAttachment824 : TopBasePoco, ISubFile
    {
        public Guid BatchId { get; set; }
        public GrandchildBatch824? Batch { get; set; }
        public Guid FileId { get; set; }
        public FileAttachment? File { get; set; }
        public int Order { get; set; }
    }

    // ── Shared context for bypass paths #1 (UpdateEntityList), #4 (grandchild), #5
    // (UpdateProperty primitive), #6 (direct DbSet). Reuses ProductWithOptionalPhoto (declared in
    // FkWriteGateSeventhRoundSqliteTests815.cs, same assembly). Declares BOTH the 2-arg and the
    // 3-arg (string,DBTypeEnum,string?) constructors: BasePagedListVM.UpdateEntityList calls
    // DC.CreateNew() unconditionally, which reflects for the 3-arg constructor specifically
    // (EmptyContext.CreateNew()) — omitting it makes CreateNew() throw a NullReferenceException
    // that has nothing to do with what this test is actually proving. ──
    internal class BypassGuardContext824 : FrameworkContext
    {
        public DbSet<Product> Products { get; set; } = null!;
        public DbSet<ProductAttachment> ProductAttachments { get; set; } = null!;
        public DbSet<ProductWithOptionalPhoto> OptionalPhotoOwners { get; set; } = null!;
        public DbSet<GrandchildOrder824> GrandchildOrders { get; set; } = null!;
        public DbSet<GrandchildBatch824> GrandchildBatches { get; set; } = null!;
        public DbSet<GrandchildAttachment824> GrandchildAttachments { get; set; } = null!;

        public BypassGuardContext824(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
        public BypassGuardContext824(string cs, DBTypeEnum dbType, string? version) : base(cs, dbType, version) { }
    }

    // ── BaseBatchVM.DoBatchEdit(Async) fixture (bypass path #2): a link VM exposing PhotoId,
    // matching the FC["LinkedVM.PhotoId"] convention BaseBatchVMTest.cs already uses for SchoolEdit. ──
    public class OptionalPhotoOwnerEdit824 : BaseVM
    {
        public Guid? PhotoId { get; set; }
    }

    // ── BaseImportVM.BatchSaveData fixture (bypass path #3): mirrors BaseImportVMTest.cs's
    // ImportTestItem/ImportTestDataContext/TestImportVM pattern, with a PhotoId FK added and
    // SetEntityList() overridden to bypass real Excel parsing (test pre-populates EntityList
    // directly — this is still the SAME BatchSaveData column-mapping-to-DbSet.Add code path). ──
    public class ImportPhotoItem824 : BasePoco
    {
        [StringLength(50)]
        public string Name { get; set; } = "";
        public Guid? PhotoId { get; set; }
        // The navigation property is required for EF Core's convention-based FK discovery to
        // create a relationship at all — a bare PhotoId scalar with no matching FileAttachment
        // navigation is just an ordinary column, invisible to both
        // BaseCRUDVM.CollectFileAttachmentCandidates and FileAttachmentSaveChangesGuard's model
        // map (same convention every other FileAttachment FK in this codebase already follows,
        // e.g. Student.PhotoId/Student.Photo).
        public FileAttachment? Photo { get; set; }
    }

    internal class ImportPhotoContext824 : FrameworkContext
    {
        public DbSet<ImportPhotoItem824> ImportPhotoItems { get; set; } = null!;

        public ImportPhotoContext824(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    public class ImportPhotoTemplateVM824 : BaseTemplateVM
    {
        public ExcelPropety Name_Excel = ExcelPropety.CreateProperty<ImportPhotoItem824>(x => x.Name);
        public ExcelPropety PhotoId_Excel = ExcelPropety.CreateProperty<ImportPhotoItem824>(x => x.PhotoId);

        protected override void InitVM() { }
    }

    public class TestImportPhotoVM824 : BaseImportVM<ImportPhotoTemplateVM824, ImportPhotoItem824>
    {
        private readonly List<ImportPhotoItem824> _presetEntities;

        public TestImportPhotoVM824(List<ImportPhotoItem824> entities)
        {
            _presetEntities = entities;
        }

        public override void SetEntityList()
        {
            if (!isEntityListSet)
            {
                EntityList = _presetEntities;
                isEntityListSet = true;
            }
        }
    }

    [TestClass]
    public class FileAttachmentSaveChangesGuardBypassPathTests824
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"bypassguard824_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();
            // The BatchSaveData import test below uses its OWN separate, per-test connection
            // string with ImportPhotoContext824 — a different context TYPE sharing this fixture's
            // connection string would collide on EnsureCreated's table set.
        }

        [TestCleanup]
        public void Cleanup()
        {
            // Defensive: a test that fails mid-assertion must never leave the process-wide kill
            // switch flipped off for every OTHER test in the suite.
            FileAttachmentSaveChangesGuard.Enabled = true;
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        private static FileAttachment SeedFile(BypassGuardContext824 ctx, string tenantCode)
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

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Bypass path #1 — BasePagedListVM.UpdateEntityList
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#824 bypass path 1/6 (BasePagedListVM.UpdateEntityList): a forged cross-tenant PhotoId posted through the grid-edit path must be rejected, not silently written")]
        public void UpdateEntityList_ForgedCrossTenantPhotoFK_RejectedByGuard_NotPersisted()
        {
            Guid ownerId, victimFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                victimFileId = SeedFile(seedCtx, "TENANT_VICTIM").ID;
                var owner = new ProductWithOptionalPhoto { Name = "Orig" };
                seedCtx.Set<ProductWithOptionalPhoto>().Add(owner);
                seedCtx.SaveChanges();
                ownerId = owner.ID;
            }

            var attackerDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BasePagedListVM<ProductWithOptionalPhoto, BaseSearcher>
            {
                DC = attackerDc,
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker"),
                EntityList = new List<ProductWithOptionalPhoto>
                {
                    new ProductWithOptionalPhoto { ID = ownerId, Name = "Renamed", PhotoId = victimFileId }
                }
            };

            // UpdateEntityList has no try/catch around DC.SaveChanges() — the guard's exception
            // propagates straight out, same as a direct DbSet writer would see it.
            Assert.ThrowsException<UnresolvableFileAttachmentReferenceException>(() => vm.UpdateEntityList(updateAllFields: true));

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == ownerId);
            Assert.AreEqual("Orig", reloaded.Name,
                "#824: the whole SaveChanges must be rejected — even the legitimate rename in the same request must not land");
            Assert.IsNull(reloaded.PhotoId, "#824: the forged cross-tenant FK must not have landed");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Bypass path #2 — BaseBatchVM.DoBatchEdit / DoBatchEditAsync
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#824 bypass path 2/6 (BaseBatchVM.DoBatchEdit): a forged cross-tenant PhotoId posted through batch-edit must be rejected, not silently written")]
        public void DoBatchEdit_ForgedCrossTenantPhotoFK_RejectedByGuard_NotPersisted()
        {
            Guid ownerId, victimFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                victimFileId = SeedFile(seedCtx, "TENANT_VICTIM").ID;
                var owner = new ProductWithOptionalPhoto { Name = "Orig" };
                seedCtx.Set<ProductWithOptionalPhoto>().Add(owner);
                seedCtx.SaveChanges();
                ownerId = owner.ID;
            }

            var attackerDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseBatchVM<ProductWithOptionalPhoto, OptionalPhotoOwnerEdit824>
            {
                DC = attackerDc,
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker"),
                LinkedVM = new OptionalPhotoOwnerEdit824 { PhotoId = victimFileId },
                Ids = new[] { ownerId.ToString() }
            };
            vm.FC.Add("LinkedVM.PhotoId", 0);

            // DoBatchEdit wraps its own SaveChanges in try/catch and reports failure via its
            // return value + ErrorMessage, rather than letting the exception propagate.
            var result = vm.DoBatchEdit();

            Assert.IsFalse(result, "#824: the batch edit must be reported as failed when the FK write itself is rejected");

            // #824 adversarial review "other paths" question: DoBatchEdit's own catch calls
            // SetExceptionMessage(e, id: null) -- BaseBatchVM.SetExceptionMessage's body is
            // `if (id != null) {...}`, so with a null id the exception's message is DISCARDED
            // entirely, not merely genericized like UpdateModelProperty's "Sys.EditFailed". The
            // caller sees `result == false` and nothing else -- less distinguishable than
            // UpdateModelProperty, not more. This is documented, pre-existing BaseBatchVM
            // behaviour (applies to ANY exception in this catch, not specific to #824) and is
            // exactly why FileAttachmentSaveChangesGuard now logs server-side regardless of what
            // any particular caller does with the exception it catches.
            Assert.AreEqual(0, vm.ErrorMessage.Count,
                "#824 adversarial review: documents that DoBatchEdit's own SetExceptionMessage(e, " +
                "id: null) discards the guard's exception message entirely for this path -- the " +
                "caller cannot distinguish a cross-tenant attachment rejection from any other " +
                "batch-edit failure via ErrorMessage; only DoBatchEdit()'s own false return value " +
                "signals failure at all. If this assertion ever fails, BaseBatchVM's error-message " +
                "handling changed and this comment (and the CHANGELOG's #824 'other paths' note) " +
                "need to be revisited.");

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == ownerId);
            Assert.IsNull(reloaded.PhotoId, "#824: the forged cross-tenant FK must not have landed via batch edit");
        }

        [TestMethod]
        [Description("#824 bypass path 2/6 async (BaseBatchVM.DoBatchEditAsync): same as the sync version above")]
        public async Task DoBatchEditAsync_ForgedCrossTenantPhotoFK_RejectedByGuard_NotPersisted()
        {
            Guid ownerId, victimFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                victimFileId = SeedFile(seedCtx, "TENANT_VICTIM").ID;
                var owner = new ProductWithOptionalPhoto { Name = "Orig" };
                seedCtx.Set<ProductWithOptionalPhoto>().Add(owner);
                seedCtx.SaveChanges();
                ownerId = owner.ID;
            }

            var attackerDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseBatchVM<ProductWithOptionalPhoto, OptionalPhotoOwnerEdit824>
            {
                DC = attackerDc,
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker"),
                LinkedVM = new OptionalPhotoOwnerEdit824 { PhotoId = victimFileId },
                Ids = new[] { ownerId.ToString() }
            };
            vm.FC.Add("LinkedVM.PhotoId", 0);

            var result = await vm.DoBatchEditAsync();

            Assert.IsFalse(result, "#824 async: the batch edit must be reported as failed when the FK write itself is rejected");

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == ownerId);
            Assert.IsNull(reloaded.PhotoId, "#824 async: the forged cross-tenant FK must not have landed via batch edit");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Bypass path #3 — BaseImportVM.BatchSaveData Excel column mapping
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#824 bypass path 3/6 (BaseImportVM.BatchSaveData): a forged cross-tenant PhotoId mapped from an Excel column must be rejected, not silently written")]
        public void BatchSaveData_ForgedCrossTenantPhotoFK_RejectedByGuard_NotPersisted()
        {
            var importCs = $"DataSource=importphoto824_{Guid.NewGuid():N}?mode=memory&cache=shared";
            using var importKeepAlive = new SqliteConnection(importCs);
            importKeepAlive.Open();

            Guid victimFileId;
            using (var seedCtx = new ImportPhotoContext824(importCs, DBTypeEnum.SQLite))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.SetTenantCode("TENANT_VICTIM");
                var victim = new FileAttachment
                {
                    ID = Guid.NewGuid(),
                    FileName = "victim.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = "TENANT_VICTIM",
                    UploadTime = DateTime.UtcNow,
                    Length = 4
                };
                seedCtx.Set<FileAttachment>().Add(victim);
                seedCtx.SaveChanges();
                victimFileId = victim.ID;
            }

            var attackerDc = new ImportPhotoContext824(importCs, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new TestImportPhotoVM824(new List<ImportPhotoItem824>
            {
                new ImportPhotoItem824 { Name = "Item1", PhotoId = victimFileId }
            })
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "importuser")
            };
            vm.DC = attackerDc;

            var result = vm.BatchSaveData();

            Assert.IsFalse(result, "#824: bulk import must report failure when a mapped FileAttachment FK column is rejected");

            // #824 adversarial review "other paths" question: UNLIKE BaseBatchVM.DoBatchEdit,
            // BaseImportVM.SetExceptionMessage(e, id: null)'s non-DbUpdateException branch has an
            // `else` fallback (`ErrorListVM.EntityList.Add(new ErrorMessage { Index = 0,
            // Message = e.Message })`) that still records the message at Index 0 even when id is
            // null. Since UnresolvableFileAttachmentReferenceException is deliberately NOT a
            // DbUpdateException (see its own doc comment), it takes this branch, so the caller
            // CAN see the guard's own tenant-silent message via ErrorListVM here -- a caller of
            // BatchSaveData is MORE able to distinguish this rejection than a caller of
            // DoBatchEdit, even though neither entry point was designed with #824 in mind.
            Assert.AreEqual(1, vm.ErrorListVM.EntityList.Count,
                "#824 adversarial review: BatchSaveData's SetExceptionMessage DOES preserve the " +
                "guard's exception message (unlike DoBatchEdit's) -- documenting the asymmetry " +
                "between these two bypass paths' diagnosability.");
            StringAssert.Contains(vm.ErrorListVM.EntityList[0].Message, "FileAttachment",
                "#824 adversarial review: the preserved message must be the guard's own " +
                "(tenant-silent) text, confirming a BatchSaveData caller CAN distinguish this " +
                "rejection from an ordinary validation failure, unlike a DoBatchEdit caller.");

            using var checkCtx = new ImportPhotoContext824(importCs, DBTypeEnum.SQLite);
            Assert.IsFalse(checkCtx.Set<ImportPhotoItem824>().Any(x => x.PhotoId == victimFileId),
                "#824: the forged cross-tenant FK must not have landed via the Excel column mapping path");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Bypass path #4 — grandchild IEnumerable<ISubFile>, two levels below the root aggregate
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#824 bypass path 4/6 (grandchild IEnumerable<ISubFile>): a forged cross-tenant FileId on a sub-collection TWO levels below the root TModel — invisible to BaseCRUDVM.CollectFileAttachmentCandidates, which only walks TModel's own declared properties — must still be rejected")]
        public void DoAdd_GrandchildSubFileCollection_ForgedCrossTenantFileId_RejectedByGuard_NotPersisted()
        {
            Guid victimFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                victimFileId = SeedFile(seedCtx, "TENANT_VICTIM").ID;
            }

            var attackerDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<GrandchildOrder824>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            var newOrderId = Guid.NewGuid();
            vm.Entity = new GrandchildOrder824
            {
                ID = newOrderId,
                Name = "Order",
                Batches = new List<GrandchildBatch824>
                {
                    new GrandchildBatch824
                    {
                        Attachments = new List<GrandchildAttachment824>
                        {
                            // Two levels below GrandchildOrder — CollectFileAttachmentCandidates
                            // never inspects this. EF's own Add() cascade still tracks it, so
                            // pre-guard this landed untouched.
                            new GrandchildAttachment824 { FileId = victimFileId }
                        }
                    }
                }
            };

            // DoAdd has no try/catch around SaveChanges — the exception propagates. Because the
            // guard runs BEFORE base.SaveChanges() is ever entered, NOTHING in this graph (not
            // even the root Order/Batch rows) reaches the database.
            Assert.ThrowsException<UnresolvableFileAttachmentReferenceException>(() => vm.DoAdd());

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            Assert.IsFalse(checkCtx.Set<GrandchildOrder824>().Any(x => x.ID == newOrderId),
                "#824: the whole graph must be rejected atomically — not even the root Order row may land");
            Assert.IsFalse(checkCtx.Set<GrandchildAttachment824>().Any(x => x.FileId == victimFileId),
                "#824: the forged grandchild FK must not have landed");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Bypass path #5 — the primitive _FrameworkController.UpdateModelProperty itself uses:
        // IDataContext.UpdateProperty + SaveChanges. This proves the SaveChanges-level guard is a
        // real second line of defense at the PRIMITIVE, independent of that one controller
        // action's own field-level IsFileAttachmentForeignKeyProperty pre-check (Finding 1 gate) —
        // any future caller reusing this same low-level primitive is covered too.
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#824 bypass path 5/6 (the UpdateProperty primitive _FrameworkController.UpdateModelProperty itself calls): a forged cross-tenant PhotoId written via IDataContext.UpdateProperty must be rejected, independent of that controller's own field-level pre-check")]
        public void UpdateProperty_ForgedCrossTenantPhotoFK_RejectedByGuard_NotPersisted()
        {
            Guid ownerId, victimFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                victimFileId = SeedFile(seedCtx, "TENANT_VICTIM").ID;
                var owner = new ProductWithOptionalPhoto { Name = "Orig" };
                seedCtx.Set<ProductWithOptionalPhoto>().Add(owner);
                seedCtx.SaveChanges();
                ownerId = owner.ID;
            }

            var attackerDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var entity = new ProductWithOptionalPhoto { ID = ownerId, PhotoId = victimFileId };

            attackerDc.UpdateProperty(entity, nameof(ProductWithOptionalPhoto.PhotoId));

            Assert.ThrowsException<UnresolvableFileAttachmentReferenceException>(() => attackerDc.SaveChanges());

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == ownerId);
            Assert.IsNull(reloaded.PhotoId,
                "#824: the forged cross-tenant FK must not have landed via the raw UpdateProperty primitive");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Bypass path #6 — a direct DbSet.Add/Update writer, bypassing every VM entirely. Named
        // explicitly in DCExtension's own doc comment (:127) as a known #824 sink the 13-pattern
        // enumeration missed (cross-vendor review Finding 4).
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#824 bypass path 6/6 (direct DbSet.Add writer): a forged cross-tenant PhotoId written via a raw DbSet.Add call, bypassing every VM, must be rejected")]
        public void DirectDbSetAdd_ForgedCrossTenantPhotoFK_RejectedByGuard_NotPersisted()
        {
            Guid victimFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                victimFileId = SeedFile(seedCtx, "TENANT_VICTIM").ID;
            }

            var attackerDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var newId = Guid.NewGuid();
            attackerDc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
            {
                ID = newId,
                Name = "Direct",
                PhotoId = victimFileId
            });

            Assert.ThrowsException<UnresolvableFileAttachmentReferenceException>(() => attackerDc.SaveChanges());

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            Assert.IsFalse(checkCtx.Set<ProductWithOptionalPhoto>().Any(x => x.ID == newId),
                "#824: the forged cross-tenant FK must not have landed via a direct DbSet writer");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Must-not-reject controls
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#824 must-not-reject: a legitimate same-tenant direct DbSet writer must persist normally — cross-vendor review Finding 4's missed direct-DbSet-writer case, positive control")]
        public void DirectDbSetAdd_SameTenantLegitimateFileFK_Persists()
        {
            Guid legitFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_A");
                legitFileId = SeedFile(seedCtx, "TENANT_A").ID;
            }

            var dc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            dc.SetTenantCode("TENANT_A");
            var newId = Guid.NewGuid();
            dc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
            {
                ID = newId,
                Name = "Legit",
                PhotoId = legitFileId
            });

            dc.SaveChanges(); // must NOT throw

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == newId);
            Assert.AreEqual(legitFileId, reloaded.PhotoId,
                "#824 must-not-reject: a same-tenant legitimate FK must persist normally");
        }

        [TestMethod]
        [Description("#824 must-not-reject: a FileAttachment created in the SAME unit of work as its dependent must persist without a resolution query — the 'same-unit-of-work Added attachment' exception")]
        public void DirectDbSetAdd_FileAttachmentAddedInSameUnitOfWork_Persists()
        {
            var dc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            dc.SetTenantCode("TENANT_A");

            var fileId = Guid.NewGuid();
            dc.Set<FileAttachment>().Add(new FileAttachment
            {
                ID = fileId,
                FileName = "new.txt",
                FileExt = "txt",
                SaveMode = "database",
                TenantCode = "TENANT_A",
                UploadTime = DateTime.UtcNow,
                Length = 4
            });
            var ownerId = Guid.NewGuid();
            dc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
            {
                ID = ownerId,
                Name = "Fresh",
                PhotoId = fileId
            });

            dc.SaveChanges(); // both Added in the SAME SaveChanges call — must NOT throw

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == fileId),
                "the newly-uploaded file itself must exist");
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == ownerId);
            Assert.AreEqual(fileId, reloaded.PhotoId,
                "#824 must-not-reject: an attachment created in the same unit of work must be trusted without a DB round trip");
        }

        [TestMethod]
        [Description("#824 kill switch: disabling FileAttachmentSaveChangesGuard.Enabled reopens the forged cross-tenant write this issue exists to close — documents exactly what turning the switch off costs")]
        public void KillSwitch_Disabled_ReopensForgedCrossTenantWrite()
        {
            Guid victimFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                victimFileId = SeedFile(seedCtx, "TENANT_VICTIM").ID;
            }

            FileAttachmentSaveChangesGuard.Enabled = false;
            try
            {
                var attackerDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
                attackerDc.SetTenantCode("TENANT_ATTACKER");
                var newId = Guid.NewGuid();
                attackerDc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
                {
                    ID = newId,
                    Name = "Direct",
                    PhotoId = victimFileId
                });

                attackerDc.SaveChanges(); // guard disabled — must NOT throw

                using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
                var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == newId);
                Assert.AreEqual(victimFileId, reloaded.PhotoId,
                    "#824 kill switch: with Enabled=false, the forged cross-tenant FK lands — this is exactly what #824 looks like reopened, documented here on purpose");
            }
            finally
            {
                FileAttachmentSaveChangesGuard.Enabled = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Finding 4 (adversarial review of PR #978, MUST FIX OR HONESTLY NARROW): the guard used
        // to re-validate EVERY Modified-and-IsModified candidate FK, unconditionally — including
        // ones the editing caller never actually introduced. Two probes proved this broke
        // documented, previously-supported shapes. Both are now must-not-reject regression tests,
        // exercised through the REAL BaseCRUDVM.DoEdit path (WTM's actual detached-attach edit
        // flow), not a direct DbSet write — this is the path both probes were found on.
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#824 Finding 4 probe 1 (must-not-reject): a plain rename of a non-ITenant row whose PhotoId is UNCHANGED and legitimately owned by a DIFFERENT tenant than the editing caller must succeed — the row is shared by design (it is not ITenant), nothing new is being introduced")]
        public void DoEdit_NonTenantRow_UnchangedCrossTenantPhotoFK_RenameSucceeds()
        {
            Guid ownerId, legitFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_A");
                legitFileId = SeedFile(seedCtx, "TENANT_A").ID;
                var owner = new ProductWithOptionalPhoto { Name = "Orig", PhotoId = legitFileId };
                seedCtx.Set<ProductWithOptionalPhoto>().Add(owner);
                seedCtx.SaveChanges();
                ownerId = owner.ID;
            }

            // TENANT_B edits the SAME row (ProductWithOptionalPhoto is not ITenant — there is no
            // tenant boundary on the row itself) and re-posts PhotoId UNCHANGED, renaming only.
            var editorDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            editorDc.SetTenantCode("TENANT_B");
            var vm = new BaseCRUDVM<ProductWithOptionalPhoto>
            {
                Wtm = MockWtmContext.CreateWtmContext(editorDc, "editor")
            };
            vm.Entity = new ProductWithOptionalPhoto
            {
                ID = ownerId,
                Name = "Renamed",
                PhotoId = legitFileId, // unchanged from the row's own persisted value
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict, "#824 Finding 4 probe 1: must not surface as a concurrency conflict");
            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#824 Finding 4 probe 1: an UNCHANGED cross-tenant PhotoId on a non-ITenant row " +
                "must not reject the edit — the row already had this reference before this " +
                "request; nothing new is being introduced by TENANT_B's rename");

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == ownerId);
            Assert.AreEqual("Renamed", reloaded.Name, "#824 Finding 4 probe 1: the legitimate rename must persist");
            Assert.AreEqual(legitFileId, reloaded.PhotoId, "#824 Finding 4 probe 1: the pre-existing PhotoId must be untouched");
        }

        [TestMethod]
        [Description("#824 Finding 4 probe 2 (must-not-reject): a row whose PhotoId points at a NULL-tenant FileAttachment (a documented, supported pattern — mainhost/pre-multi-tenancy uploads, see the #859 entry in production-readiness.md) must still be editable by a real-tenant caller when the FK is UNCHANGED")]
        public void DoEdit_NullTenantFileFK_UnchangedByRealTenantCaller_RenameSucceeds()
        {
            Guid ownerId, nullTenantFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                var file = new FileAttachment
                {
                    ID = Guid.NewGuid(),
                    FileName = "mainhost.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = null, // e.g. uploaded via a mainhost/pre-multi-tenancy context
                    UploadTime = DateTime.UtcNow,
                    Length = 4,
                };
                seedCtx.Set<FileAttachment>().Add(file);
                seedCtx.SaveChanges();
                nullTenantFileId = file.ID;

                var owner = new ProductWithOptionalPhoto { Name = "Orig", PhotoId = nullTenantFileId };
                seedCtx.Set<ProductWithOptionalPhoto>().Add(owner);
                seedCtx.SaveChanges();
                ownerId = owner.ID;
            }

            var editorDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            editorDc.SetTenantCode("TENANT_A"); // a real (non-null) tenant -- cannot resolve a NULL-tenant file per the #815/#859 null-safe-equality precedent
            var vm = new BaseCRUDVM<ProductWithOptionalPhoto>
            {
                Wtm = MockWtmContext.CreateWtmContext(editorDc, "editor")
            };
            vm.Entity = new ProductWithOptionalPhoto
            {
                ID = ownerId,
                Name = "Renamed",
                PhotoId = nullTenantFileId, // unchanged from the row's own persisted value
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict, "#824 Finding 4 probe 2: must not surface as a concurrency conflict");
            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#824 Finding 4 probe 2: an UNCHANGED null-tenant PhotoId must not reject an edit " +
                "by a real-tenant caller — this is a documented, supported pattern (#859), and the " +
                "row already had this exact reference before this request");

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == ownerId);
            Assert.AreEqual("Renamed", reloaded.Name, "#824 Finding 4 probe 2: the legitimate rename must persist");
            Assert.AreEqual(nullTenantFileId, reloaded.PhotoId, "#824 Finding 4 probe 2: the pre-existing null-tenant PhotoId must be untouched");
        }

        [TestMethod]
        [Description("#824 Finding 4 non-regression: a Modified entity whose posted FK DIFFERS from what is persisted (here: from an EXISTING legitimate value, not merely from null) must still be fully validated and rejected — the Finding 4 narrowing must not defeat the guard for a genuine forgery")]
        public void UpdateProperty_ChangedFromExistingLegitimatePhotoFK_StillRejectedByGuard_NotPersisted()
        {
            // Deliberately does NOT use BaseCRUDVM.DoEdit: that path runs #815's own pre-existing
            // RejectUnresolvableFileAttachmentReferences gate FIRST (inside DoEditPrepare), which
            // — for an OPTIONAL FK with no legitimate prior value — SILENTLY CLEARS the posted
            // value to null and lets the edit succeed, rather than rejecting. That is #815's own,
            // pre-existing, correct behaviour, but it means DoEdit never lets a changed forged
            // value reach THIS guard at all when the row's prior value was null — it would prove
            // #815 still works, not that Finding 4's narrowing specifically doesn't defeat this
            // guard. The raw UpdateProperty primitive (bypass path #5) reaches the guard directly.
            //
            // Uses an EXISTING legitimate value (not null) as the row's prior state specifically
            // so this test cannot be satisfied by accident: the persisted PhotoId is a REAL,
            // resolvable-for-this-caller id, and the posted one is a DIFFERENT, unresolvable one —
            // proving Finding 4's "already persisted, same value" comparison correctly treats a
            // CHANGED value as needing full validation, not just "was there something before".
            Guid ownerId, legitFileId, victimFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_ATTACKER");
                legitFileId = SeedFile(seedCtx, "TENANT_ATTACKER").ID;
                seedCtx.SetTenantCode("TENANT_VICTIM");
                victimFileId = SeedFile(seedCtx, "TENANT_VICTIM").ID;

                // Reset to TENANT_ATTACKER before seeding the owner row -- its PhotoId
                // (legitFileId) belongs to TENANT_ATTACKER, and seedCtx's own tenant would
                // otherwise still be TENANT_VICTIM (the last SetTenantCode call above), which
                // would trip the guard on THIS seed write itself.
                seedCtx.SetTenantCode("TENANT_ATTACKER");
                var owner = new ProductWithOptionalPhoto { Name = "Orig", PhotoId = legitFileId };
                seedCtx.Set<ProductWithOptionalPhoto>().Add(owner);
                seedCtx.SaveChanges();
                ownerId = owner.ID;
            }

            var attackerDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var entity = new ProductWithOptionalPhoto { ID = ownerId, PhotoId = victimFileId };
            attackerDc.UpdateProperty(entity, nameof(ProductWithOptionalPhoto.PhotoId));

            Assert.ThrowsException<UnresolvableFileAttachmentReferenceException>(() => attackerDc.SaveChanges());

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == ownerId);
            Assert.AreEqual(legitFileId, reloaded.PhotoId,
                "#824 Finding 4 non-regression: a genuinely CHANGED cross-tenant FK must still be " +
                "rejected and the row's EXISTING legitimate PhotoId must be untouched — Finding " +
                "4's narrowing only exempts a posted value that already matches what's persisted, " +
                "and this posted value (the victim's) does not match what was persisted (the " +
                "attacker's own legitimate file)");
        }
    }
}
