#nullable enable
// Issue #815: BaseVM.DeletedFileIds is model-bound (arrives straight from the posted form —
// UploadTagHelper.cs ~247 emits the hidden input the browser posts) so, before this fix, a
// caller could name ANY FileAttachment GUID and have it deleted, regardless of whether the
// entity being saved ever referenced it — including a file belonging to a different tenant
// (WtmFileProvider resolves by id with IgnoreQueryFilters() when
// FileUploadOptions.EnforceTenantFileScope is false, the default).
//
// These tests assert on the SURVIVING FileAttachment row in the database, not on a status
// code or an exception, per the issue's instructions.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    [TestClass]
    public class DeletedFileIdsAuthorizationTests815
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

        private static Student SeedStudent(DataContext dc, Guid? photoId)
        {
            var student = new Student
            {
                ID = Guid.NewGuid(),
                LoginName = "u" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Password = "pw",
                Name = "n",
                PhotoId = photoId,
                IsValid = true
            };
            dc.Set<Student>().Add(student);
            dc.SaveChanges();
            return student;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Core scenario: an id that does NOT belong to the entity must survive.
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#815: DoEdit must NOT delete a FileAttachment the entity never referenced, even when named in DeletedFileIds (cross-tenant)")]
        public void DoEdit_DeletedFileIds_UnrelatedTenantFile_IsNotDeleted()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, fileAId, fileBId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var fileA = SeedFile(seedCtx, "TENANT_A");
                var fileB = SeedFile(seedCtx, "TENANT_B");
                fileAId = fileA.ID;
                fileBId = fileB.ID;
                var student = SeedStudent(seedCtx, fileA.ID);
                studentId = student.ID;
            }

            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "attacker")
            };
            vm.Entity = new Student
            {
                ID = studentId,
                LoginName = "u2",
                Password = "pw",
                Name = "n2",
                PhotoId = fileAId, // unchanged — still points at its own file
                IsValid = true
            };
            // Attacker names TENANT_B's file, which this Student never referenced before or
            // after this edit.
            vm.DeletedFileIds.Add(fileBId.ToString());

            vm.DoEdit(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict, "edit should succeed");

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == fileBId),
                "#815: a FileAttachment the entity never referenced must survive DeletedFileIds " +
                "— it belongs to a different tenant and was never this Student's Photo");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == fileAId),
                "the entity's own (re-affirmed, not deleted) file must also still exist");
        }

        [TestMethod]
        [Description("#815 async: DoEditAsync must NOT delete a FileAttachment the entity never referenced (cross-tenant)")]
        public async Task DoEditAsync_DeletedFileIds_UnrelatedTenantFile_IsNotDeleted()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, fileAId, fileBId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var fileA = SeedFile(seedCtx, "TENANT_A");
                var fileB = SeedFile(seedCtx, "TENANT_B");
                fileAId = fileA.ID;
                fileBId = fileB.ID;
                var student = SeedStudent(seedCtx, fileA.ID);
                studentId = student.ID;
            }

            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "attacker")
            };
            vm.Entity = new Student
            {
                ID = studentId,
                LoginName = "u2",
                Password = "pw",
                Name = "n2",
                PhotoId = fileAId,
                IsValid = true
            };
            vm.DeletedFileIds.Add(fileBId.ToString());

            await vm.DoEditAsync(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict, "edit should succeed");

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == fileBId),
                "#815: DoEditAsync must not delete a file the entity never referenced");
        }

        // ─────────────────────────────────────────────────────────────────────
        // The fix must not break the legitimate "detach my own file" flow.
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#815: DoEdit still deletes the entity's own PRE-EDIT file when it is genuinely detached")]
        public void DoEdit_DeletedFileIds_OwnPreEditFile_IsDeleted()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, fileAId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var fileA = SeedFile(seedCtx, "TENANT_A");
                fileAId = fileA.ID;
                var student = SeedStudent(seedCtx, fileA.ID);
                studentId = student.ID;
            }

            // #815 rework: DeleteFileTenantScoped (the new primary control) resolves the file
            // through the caller's DC with the ITenant query filter kept ON, so this DC must be
            // scoped to the SAME tenant as the seeded file — exactly what Wtm.CreateDC() would do
            // in production for an authenticated user whose LoginUserInfo.CurrentTenant matches
            // the tenant that originally uploaded the file. Without this, the legitimate
            // same-tenant deletion below would be wrongly blocked by the tenant filter, same as
            // the deliberately-blocked cross-tenant case.
            var editDc = new DataContext(seed, DBTypeEnum.Memory);
            editDc.SetTenantCode("TENANT_A");
            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(editDc, "user")
            };
            vm.Entity = new Student
            {
                ID = studentId,
                LoginName = "u2",
                Password = "pw",
                Name = "n2",
                PhotoId = null, // legitimately detaching the photo (UploadTagHelper's DoDelete
                                 // on the pre-edit Field.Model clears the hidden field to '')
                IsValid = true
            };
            vm.DeletedFileIds.Add(fileAId.ToString());

            vm.DoEdit(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict, "edit should succeed");

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsFalse(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == fileAId),
                "#815 fix must not break the legitimate flow: the entity's OWN pre-edit file, " +
                "named in DeletedFileIds, is still deleted");
        }

        // ─────────────────────────────────────────────────────────────────────
        // The fix must close the self-referential bypass on Add (posting the SAME id as
        // both the entity's own FK and DeletedFileIds must not be sufficient authorization).
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#815: DoAdd must NOT delete a file just because the new entity's own posted FK also names it")]
        public void DoAdd_DeletedFileIds_SelfReferencedInSameRequest_IsNotDeleted()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_B");
                victimFileId = victim.ID;
            }

            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "attacker")
            };
            vm.Entity = new Student
            {
                LoginName = "newuser" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Password = "pw",
                Name = "n",
                PhotoId = victimFileId, // attacker points their OWN new entity's FK at the victim's file
                IsValid = true
            };
            // ...and in the SAME request, also asks for that same id to be deleted. If the
            // check trusted Entity's own posted properties this would succeed.
            vm.DeletedFileIds.Add(victimFileId.ToString());

            vm.DoAdd();

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815: DoAdd must not delete a file just because the newly-inserted entity's own " +
                "posted FK also names it — a brand-new row has no pre-existing reference to " +
                "validate DeletedFileIds against, so nothing is deletable through this path");
            var reloaded = checkCtx.Set<Student>().IgnoreQueryFilters().First(x => x.LoginName == vm.Entity.LoginName);
            Assert.IsNull(reloaded.PhotoId,
                "#815 SECOND REWORK: the posted FK itself must also be rejected at write time — " +
                "an unresolvable-for-this-caller's-tenant PhotoId must never persist in the first " +
                "place, regardless of what DeletedFileIds does or doesn't request");
        }

        [TestMethod]
        [Description("#815 async: DoAddAsync must NOT delete a file just because the new entity's own posted FK also names it")]
        public async Task DoAddAsync_DeletedFileIds_SelfReferencedInSameRequest_IsNotDeleted()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_B");
                victimFileId = victim.ID;
            }

            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "attacker")
            };
            vm.Entity = new Student
            {
                LoginName = "newuser" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Password = "pw",
                Name = "n",
                PhotoId = victimFileId,
                IsValid = true
            };
            vm.DeletedFileIds.Add(victimFileId.ToString());

            await vm.DoAddAsync();

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815: DoAddAsync must not delete a file just because the newly-inserted entity's " +
                "own posted FK also names it");
            var reloaded = checkCtx.Set<Student>().IgnoreQueryFilters().First(x => x.LoginName == vm.Entity.LoginName);
            Assert.IsNull(reloaded.PhotoId,
                "#815 SECOND REWORK async: the posted FK itself must also be rejected at write time");
        }

        // ─────────────────────────────────────────────────────────────────────
        // #815 SECOND REWORK — kill the primitive at its source (Option A). Rounds 1/2 patched
        // DELETE sinks; a reviewer proved both bypassed by a primitive that never touches
        // DeletedFileIds at all:
        //   POST 1 — edit your own row, set PhotoId = the victim's (cross-tenant) file GUID.
        //            Round 1/2 saved this unvalidated: DoEditPrepare only ever nulled the
        //            NAVIGATION property, never the posted FK scalar.
        //   POST 2 — ANY path that derives file ids from the entity: DeletedFileIds,
        //            DoRealDelete(Async), DoBatchDelete(Async), or a plain read (GetById
        //            resolves the navigation via WtmFileProvider.GetFile, which uses
        //            IgnoreQueryFilters() by default) — all reach the victim's file this way.
        // RejectUnresolvableFileAttachmentReferences (BaseCRUDVM.DoAddPrepare / DoEditPrepare)
        // now rejects POST 1 itself: a posted FileAttachment FK that does not resolve under the
        // caller's own tenant scope (ITenant query filter kept ON unconditionally) is reverted
        // to the entity's own pre-edit value BEFORE anything is written. There is nothing left
        // for POST 2 to reach through any sink once POST 1 no longer lands.
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#815 SECOND REWORK: forging PhotoId to a cross-tenant victim file must be rejected at write time — the FK never lands")]
        public void DoEdit_ForgedCrossTenantPhotoFK_RejectedAtWriteTime_VictimFileSurvives()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                // Victim's file lives in a DIFFERENT tenant than the attacker.
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                var student = SeedStudent(seedCtx, null); // attacker's own row, no photo yet
                studentId = student.ID;
            }

            // POST 1 (the primitive rounds 1/2 never closed): attacker edits their OWN row and
            // sets PhotoId to the victim's cross-tenant file.
            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.Entity = new Student
            {
                ID = studentId,
                LoginName = "u2",
                Password = "pw",
                Name = "n2",
                PhotoId = victimFileId, // forged cross-tenant FK
                IsValid = true
            };
            vm.DoEdit(updateAllFields: true);
            Assert.IsFalse(vm.IsConcurrencyConflict, "editing the attacker's own row must still succeed");

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            var reloaded = checkCtx.Set<Student>().IgnoreQueryFilters().First(x => x.ID == studentId);
            Assert.IsNull(reloaded.PhotoId,
                "#815 SECOND REWORK: RejectUnresolvableFileAttachmentReferences must reject a " +
                "posted FK the caller cannot resolve for their own tenant BEFORE it is written — " +
                "the forged reference must never land, reverted to the entity's pre-edit value " +
                "(null, here) instead");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist — nothing ever touched it");
        }

        [TestMethod]
        [Description("#815 SECOND REWORK async: forging PhotoId to a cross-tenant victim file must be rejected at write time — the FK never lands")]
        public async Task DoEditAsync_ForgedCrossTenantPhotoFK_RejectedAtWriteTime_VictimFileSurvives()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                var student = SeedStudent(seedCtx, null);
                studentId = student.ID;
            }

            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.Entity = new Student
            {
                ID = studentId,
                LoginName = "u2",
                Password = "pw",
                Name = "n2",
                PhotoId = victimFileId,
                IsValid = true
            };
            await vm.DoEditAsync(updateAllFields: true);
            Assert.IsFalse(vm.IsConcurrencyConflict, "editing the attacker's own row must still succeed");

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            var reloaded = checkCtx.Set<Student>().IgnoreQueryFilters().First(x => x.ID == studentId);
            Assert.IsNull(reloaded.PhotoId,
                "#815 SECOND REWORK async: the forged reference must never land, reverted to null");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must obviously still exist");
        }

        [TestMethod]
        [Description("#815: DoAdd must also reject a forged cross-tenant PhotoId FK at write time, not merely refuse to delete through it")]
        public void DoAdd_ForgedCrossTenantPhotoFK_RejectedAtWriteTime()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
            }

            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            var newId = Guid.NewGuid();
            vm.Entity = new Student
            {
                ID = newId,
                LoginName = "newuser" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Password = "pw",
                Name = "n",
                PhotoId = victimFileId, // forged cross-tenant FK on a brand-new row
                IsValid = true
            };

            vm.DoAdd();

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            var reloaded = checkCtx.Set<Student>().IgnoreQueryFilters().FirstOrDefault(x => x.ID == newId);
            Assert.IsNotNull(reloaded, "the new row itself must still be created");
            Assert.IsNull(reloaded!.PhotoId,
                "#815: a forged cross-tenant FK on Add must be rejected and reverted to null (no " +
                "pre-existing value to fall back to) — never persisted");
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file must still exist");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Defence in depth: DeleteFileTenantScoped must still hold even for data that already
        // has a cross-tenant FK — e.g. a row written before this fix existed, or by any future
        // path RejectUnresolvableFileAttachmentReferences does not (yet) cover. Seeded directly
        // via EF, bypassing the VM write path entirely, to isolate this layer's own protection
        // from the write-time gate above.
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#815 defence in depth: DeletedFileIds still cannot delete a victim's file even when a cross-tenant FK already exists (e.g. legacy pre-fix data)")]
        public void DoEdit_PreExistingForgedCrossTenantPhotoFK_DeletedFileIds_VictimFileSurvives()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                // Bypasses the VM layer entirely — simulates data that already carries a
                // cross-tenant FK (e.g. written before this fix shipped).
                var student = SeedStudent(seedCtx, victim.ID);
                studentId = student.ID;
            }

            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.Entity = new Student
            {
                ID = studentId,
                LoginName = "u2",
                Password = "pw",
                Name = "n2",
                PhotoId = victimFileId, // re-affirmed, unchanged from the pre-existing row
                IsValid = true
            };
            vm.DeletedFileIds.Add(victimFileId.ToString());

            vm.DoEdit(updateAllFields: true);
            Assert.IsFalse(vm.IsConcurrencyConflict, "the edit itself should succeed");

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815 defence in depth: DeleteFileTenantScoped must refuse to resolve the victim's " +
                "file outside the caller's own tenant even when the entity-reference layer alone " +
                "(a pre-existing cross-tenant FK) would have allowed it");
        }

        [TestMethod]
        [Description("#815 defence in depth async: same as the sync version above")]
        public async Task DoEditAsync_PreExistingForgedCrossTenantPhotoFK_DeletedFileIds_VictimFileSurvives()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                var student = SeedStudent(seedCtx, victim.ID);
                studentId = student.ID;
            }

            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.Entity = new Student
            {
                ID = studentId,
                LoginName = "u2",
                Password = "pw",
                Name = "n2",
                PhotoId = victimFileId,
                IsValid = true
            };
            vm.DeletedFileIds.Add(victimFileId.ToString());

            await vm.DoEditAsync(updateAllFields: true);
            Assert.IsFalse(vm.IsConcurrencyConflict, "the edit itself should succeed");

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815 defence in depth async: the victim's file must survive");
        }

        [TestMethod]
        [Description("#815 defence in depth: DoRealDelete must not delete a victim's cross-tenant file even when a pre-existing forged FK resolves it via GetById's navigation load")]
        public void DoRealDelete_PreExistingForgedCrossTenantPhotoFK_VictimFileSurvives()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                var student = SeedStudent(seedCtx, victim.ID);
                studentId = student.ID;
            }

            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            // GetById resolves the Photo navigation via WtmFileProvider.GetFile, which uses
            // IgnoreQueryFilters() by default (FileUploadOptions.EnforceTenantFileScope=false),
            // so the forged cross-tenant reference DOES load into Entity.Photo here — this is
            // the "read primitive" half of #815; DoRealDelete then reads fileids off that
            // navigation property.
            vm.SetEntityById(studentId);
            Assert.IsNotNull(vm.Entity, "the entity itself must load");

            vm.DoRealDelete();

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815 defence in depth: DoRealDelete's fp.DeleteFileTenantScoped call must refuse " +
                "to resolve the victim's cross-tenant file even though it loaded into Entity.Photo");
            Assert.IsFalse(checkCtx.Set<Student>().IgnoreQueryFilters().Any(x => x.ID == studentId),
                "sanity check: the Student row itself must actually have been deleted");
        }

        [TestMethod]
        [Description("#815 defence in depth async: same as the sync DoRealDelete version above")]
        public async Task DoRealDeleteAsync_PreExistingForgedCrossTenantPhotoFK_VictimFileSurvives()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                var student = SeedStudent(seedCtx, victim.ID);
                studentId = student.ID;
            }

            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            vm.SetEntityById(studentId);
            Assert.IsNotNull(vm.Entity, "the entity itself must load");

            await vm.DoRealDeleteAsync();

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815 defence in depth async: the victim's cross-tenant file must survive");
            Assert.IsFalse(checkCtx.Set<Student>().IgnoreQueryFilters().Any(x => x.ID == studentId),
                "sanity check: the Student row itself must actually have been deleted");
        }

        [TestMethod]
        [Description("#815 defence in depth: DoBatchDelete must not delete a victim's cross-tenant file even when a pre-existing forged FK is read straight off the DB row")]
        public void DoBatchDelete_PreExistingForgedCrossTenantPhotoFK_VictimFileSurvives()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                // StudentTop (plain TopBasePoco, not IPersistPoco) so DoBatchDelete takes the
                // physical-delete branch that reads the FileAttachment FK straight off the row —
                // Student (IPersistPoco) would instead take the soft-delete branch, which never
                // touches file ids at all.
                var student = new StudentTop
                {
                    ID = Guid.NewGuid(),
                    LoginName = "u" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Password = "pw",
                    Name = "n",
                    PhotoId = victim.ID
                };
                seedCtx.Set<StudentTop>().Add(student);
                seedCtx.SaveChanges();
                studentId = student.ID;
            }

            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseBatchVM<StudentTop, BaseVM>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker"),
                Ids = [studentId.ToString()]
            };

            var result = vm.DoBatchDelete();

            Assert.IsTrue(result, "the batch delete itself should succeed");
            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815 defence in depth: DoBatchDelete's fp.DeleteFileTenantScoped call must refuse " +
                "to resolve the victim's cross-tenant file");
            Assert.IsFalse(checkCtx.Set<StudentTop>().IgnoreQueryFilters().Any(x => x.ID == studentId),
                "sanity check: the StudentTop row itself must actually have been deleted");
        }

        [TestMethod]
        [Description("#815 defence in depth async: same as the sync DoBatchDelete version above")]
        public async Task DoBatchDeleteAsync_PreExistingForgedCrossTenantPhotoFK_VictimFileSurvives()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                var student = new StudentTop
                {
                    ID = Guid.NewGuid(),
                    LoginName = "u" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Password = "pw",
                    Name = "n",
                    PhotoId = victim.ID
                };
                seedCtx.Set<StudentTop>().Add(student);
                seedCtx.SaveChanges();
                studentId = student.ID;
            }

            var attackerDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseBatchVM<StudentTop, BaseVM>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker"),
                Ids = [studentId.ToString()]
            };

            var result = await vm.DoBatchDeleteAsync();

            Assert.IsTrue(result, "the batch delete itself should succeed");
            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "#815 defence in depth async: the victim's cross-tenant file must survive");
            Assert.IsFalse(checkCtx.Set<StudentTop>().IgnoreQueryFilters().Any(x => x.ID == studentId),
                "sanity check: the StudentTop row itself must actually have been deleted");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Read variant (#815 rework's "check that and report it"): a forged FK is not only a
        // delete primitive — GetById resolves the FileAttachment navigation via
        // WtmFileProvider.GetFile, which is tenant-agnostic by default. Once write-time
        // rejection prevents the forge, GetById has nothing forged left to resolve.
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#815 read variant: after a rejected forge attempt, GetById/SetEntityById does not resolve the victim's cross-tenant file")]
        public void SetEntityById_AfterRejectedForgeAttempt_DoesNotResolveVictimFile()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid studentId, victimFileId;

            using (var seedCtx = new DataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var victim = SeedFile(seedCtx, "TENANT_VICTIM");
                victimFileId = victim.ID;
                var student = SeedStudent(seedCtx, null);
                studentId = student.ID;
            }

            // Attempted forge — rejected at write time (see
            // DoEdit_ForgedCrossTenantPhotoFK_RejectedAtWriteTime_VictimFileSurvives above).
            var attackerEditDc = new DataContext(seed, DBTypeEnum.Memory);
            attackerEditDc.SetTenantCode("TENANT_ATTACKER");
            var editVm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerEditDc, "attacker")
            };
            editVm.Entity = new Student
            {
                ID = studentId,
                LoginName = "u2",
                Password = "pw",
                Name = "n2",
                PhotoId = victimFileId,
                IsValid = true
            };
            editVm.DoEdit(updateAllFields: true);

            // Now read it back — GetById must not resolve the victim's file, because the forge
            // never actually landed.
            var readDc = new DataContext(seed, DBTypeEnum.Memory);
            readDc.SetTenantCode("TENANT_ATTACKER");
            var readVm = new BaseCRUDVM<Student>
            {
                Wtm = MockWtmContext.CreateWtmContext(readDc, "attacker")
            };
            readVm.SetEntityById(studentId);

            Assert.IsNull(readVm.Entity.Photo,
                "#815 read variant: GetById must not resolve the victim's cross-tenant file into " +
                "Entity.Photo — the forged FK that would have made it resolvable was rejected at " +
                "write time");

            using var checkCtx = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == victimFileId),
                "the victim's file itself must still exist — nothing deleted it, this is a read test");
        }

        // ─────────────────────────────────────────────────────────────────────
        // BaseImportVM.BatchSaveData sink — no legitimate producer at all (see the
        // production comment): bulk import creates new rows, so DeletedFileIds is no
        // longer processed there. Reuses the TestImportVM/ImportTestItem/
        // ImportTestDataContext fixtures declared in BaseImportVMTest.cs (same assembly,
        // same namespace).
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#815: BaseImportVM.BatchSaveData must NOT delete a FileAttachment named in posted DeletedFileIds")]
        public void BatchSaveData_DeletedFileIds_NamedFile_IsNotDeleted()
        {
            var seed = Guid.NewGuid().ToString("N");
            Guid fileId;

            using (var seedCtx = new ImportTestDataContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                var file = new FileAttachment
                {
                    ID = Guid.NewGuid(),
                    FileName = "f.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = "TENANT_A",
                    UploadTime = DateTime.UtcNow,
                    Length = 4
                };
                fileId = file.ID;
                seedCtx.Set<FileAttachment>().Add(file);
                seedCtx.SaveChanges();
            }

            var vm = new TestImportVM(new List<ImportTestItem>
            {
                new ImportTestItem { Name = "Item1", Value = 1 }
            })
            {
                Wtm = MockWtmContext.CreateWtmContext(new ImportTestDataContext(seed, DBTypeEnum.Memory), "importuser")
            };
            // Attacker names a real FileAttachment id that this bulk import never created,
            // updated, or otherwise referenced.
            vm.DeletedFileIds.Add(fileId.ToString());

            bool result;
            try
            {
                result = vm.BatchSaveData();
            }
            catch (Exception ex)
            {
                // Load-bearing, not incidental: the pre-#815 BatchSaveData iterated
                // DeletedFileIds and called fp.DeleteFile(item, Wtm.CreateDC(false)) for each —
                // Wtm.CreateDC() cannot be made to resolve a working DataContext through this
                // test harness's mock (no "default" connection is configured; see this repo's
                // test/.../testing.md note on Wtm.CreateDC('default') being unmockable), so
                // reverting this fix throws here instead of actually reaching the DB. That
                // thrown exception IS the regression signal — the fixed code below never
                // attempts this call at all, so it never throws. Fail explicitly rather than
                // let an unhandled exception read as an unrelated test-harness crash.
                Assert.Fail(
                    "#815: BatchSaveData must not throw while (correctly) skipping DeletedFileIds. " +
                    $"Got {ex.GetType().Name}: {ex.Message}. If this fires after reverting the #815 " +
                    "production hunks, it means the reverted code attempted fp.DeleteFile via " +
                    "Wtm.CreateDC() for the posted DeletedFileIds — i.e. the pre-fix vulnerable path " +
                    "was reached.");
                return; // unreachable — Assert.Fail always throws
            }

            Assert.IsTrue(result, "import should still succeed");

            using var checkCtx = new ImportTestDataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Any(x => x.ID == fileId),
                "#815: BatchSaveData must not delete a file named in posted DeletedFileIds — bulk " +
                "import creates new rows and has no pre-existing entity reference to validate against");
        }
    }
}
