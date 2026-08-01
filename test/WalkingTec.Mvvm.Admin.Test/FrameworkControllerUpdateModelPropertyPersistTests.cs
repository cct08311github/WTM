using System;
using System.Linq;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Demo;
using WalkingTec.Mvvm.Demo.Models;
using WalkingTec.Mvvm.Demo.ViewModels.MajorVMs;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkUserVms;
using WalkingTec.Mvvm.Test.Mock;
using DUWENINK.Captcha;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// #797 (round 3): a real generated-shape CRUD VM with a <see cref="BaseCRUDVM{TModel}.SetDuplicatedCheck"/>
    /// override, so <see cref="BaseCRUDVM{TModel}.ValidateDuplicateData"/>'s
    /// IPersistPoco-branch (force <c>Entity.IsValid = true</c> to build its uniqueness query)
    /// actually runs against a demo <see cref="WxReportData"/> entity.
    /// </summary>
    public class WxReportDataDupCheckVM : BaseCRUDVM<WxReportData>
    {
        public override DuplicatedInfo<WxReportData>? SetDuplicatedCheck()
            => CreateFieldsInfo(SimpleField(x => x.ToWxUser));
    }

    /// <summary>
    /// Regression tests for Issue #532: UpdateModelProperty silently discarded the edited
    /// value while still returning Success.
    ///
    /// Root cause: the entity is loaded AsNoTracking (detached) via
    /// BaseCRUDVM&lt;TModel&gt;.GetById(), and DoEdit(false) only marks a property modified
    /// when the form collection carries an "entity.&lt;field&gt;" prefixed key. This endpoint's
    /// own field/value/id/_DONOT_USE_VMNAME keys never match that prefix, so the reflected
    /// property value was never change-tracked or marked modified — SaveChanges persisted only
    /// UpdateTime/UpdateBy while the endpoint still returned Success.
    ///
    /// These tests use two separate <see cref="DataContext"/> instances sharing the same
    /// in-memory seed name (mirrors the real per-request DbContext lifecycle: the row is
    /// seeded in one "request", edited in a fresh detached-load "request", then verified via
    /// a third fresh context) so the fix is proven against real persistence, not merely
    /// against the original tracked in-memory object.
    /// </summary>
    [TestClass]
    public class FrameworkControllerUpdateModelPropertyPersistTests
    {
        private string _seed = null!;

        [TestInitialize]
        public void Setup()
        {
            _seed = Guid.NewGuid().ToString();
        }

        private static _FrameworkController CreateController(IDataContext dataContext)
        {
            var mockSecurityCode = new Mock<ISecurityCodeHelper>();
            var controller = new _FrameworkController(mockSecurityCode.Object);
            controller.Wtm = MockWtmContext.CreateWtmContext(dataContext, "testuser");
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        private static readonly string MajorVmFullName = typeof(BaseCRUDVM<Major>).AssemblyQualifiedName!;

        // ─── #532: valid inline edit must persist ─────────────────────────────

        /// <summary>
        /// A valid inline edit (unblocked, writable field) must actually be persisted to the
        /// database — re-querying the entity afterwards via a fresh DataContext must show the
        /// new value, not just a 200/Success response.
        /// </summary>
        [TestMethod]
        public void UpdateModelProperty_ValidField_ActuallyPersists()
        {
            Guid majorId;
            using (var seedDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var major = new Major
                {
                    MajorCode = "001",
                    MajorName = "OldName",
                    MajorType = MajorTypeEnum.Required,
                    SchoolId = 1,
                };
                seedDc.Set<Major>().Add(major);
                seedDc.SaveChanges();
                majorId = major.ID;
            }

            using (var editDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var controller = CreateController(editDc);

                var result = controller.UpdateModelProperty(MajorVmFullName, majorId, "MajorName", "NewName");

                Assert.IsNotInstanceOfType(
                    result,
                    typeof(BadRequestObjectResult),
                    $"Expected UpdateModelProperty to succeed, but got: {(result as BadRequestObjectResult)?.Value}");
            }

            using (var verifyDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var reloaded = verifyDc.Set<Major>().Single(m => m.ID == majorId);
                Assert.AreEqual(
                    "NewName",
                    reloaded.MajorName,
                    "#532: the inline-edited field must actually be persisted, not silently discarded");
            }
        }

        // ─── #532: sensitive/blocklisted field must still be rejected ─────────

        /// <summary>
        /// MVC-004 regression guard: the sensitive-field blocklist behaviour must be unchanged
        /// by the #532 persistence fix — a blocklisted field must still return 400 and the
        /// underlying value must remain untouched in the database.
        /// </summary>
        [TestMethod]
        public void UpdateModelProperty_BlockedField_StillRejectedAndNotPersisted()
        {
            Guid majorId;
            const string originalCreateBy = "original-author";
            using (var seedDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var major = new Major
                {
                    MajorCode = "002",
                    MajorName = "Name2",
                    MajorType = MajorTypeEnum.Required,
                    SchoolId = 1,
                    CreateBy = originalCreateBy,
                };
                seedDc.Set<Major>().Add(major);
                seedDc.SaveChanges();
                majorId = major.ID;
            }

            using (var editDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var controller = CreateController(editDc);

                var result = controller.UpdateModelProperty(MajorVmFullName, majorId, "CreateBy", "hacked");

                Assert.IsInstanceOfType(
                    result,
                    typeof(BadRequestObjectResult),
                    "MVC-004: CreateBy is on the sensitive-field blocklist and must be rejected");
            }

            using (var verifyDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var reloaded = verifyDc.Set<Major>().Single(m => m.ID == majorId);
                Assert.AreEqual(
                    originalCreateBy,
                    reloaded.CreateBy,
                    "A rejected blocklisted-field edit must not be persisted");
            }
        }

        // ─── #797: a STANDARD generated CRUD VM must not write collateral changes ───

        /// <summary>
        /// Regression test for Issue #797: the #532 fix moved <c>Attach()</c> ahead of
        /// <c>DoEditPrepare</c>'s mutation pass. Because <c>SetInclude()</c> lives in the
        /// VM constructor for every generated/demo CRUD VM (see
        /// <see cref="MajorVM"/>, which includes <c>School</c> and <c>StudentMajors</c>),
        /// routing the save through <c>vm.DoEdit(false)</c> attached the whole loaded graph
        /// and then let <c>DoEditPrepare</c> null out navigations / resync-or-delete child
        /// rows that this single-field inline edit never intended to touch.
        ///
        /// Both halves must hold: the targeted scalar field must persist (the #532 promise)
        /// AND no sibling FK / navigation / related child row may be written (the #797 fix).
        /// Uses <see cref="MajorVM"/> — a real generated-shape CRUD VM whose constructor
        /// calls <c>SetInclude(x => x.School)</c> / <c>SetInclude(x => x.StudentMajors)</c> —
        /// not the degenerate <c>BaseCRUDVM&lt;Major&gt;</c> shape the #532 tests use.
        /// </summary>
        [TestMethod]
        public void UpdateModelProperty_StandardCrudVM_DoesNotWriteCollateralChanges()
        {
            var majorVmFullName = typeof(MajorVM).AssemblyQualifiedName!;
            Guid majorId;
            const string studentId = "student-797";
            DateTime originalStudentMajorUpdateTime = new DateTime(2020, 1, 1);
            const string originalStudentMajorUpdateBy = "original-editor";

            using (var seedDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var school = new School
                {
                    ID = 1,
                    SchoolCode = "100",
                    SchoolName = "OldSchool",
                    SchoolType = SchoolTypeEnum.PUB,
                    Remark = "seed",
                };
                seedDc.Set<School>().Add(school);

                var student = new Student
                {
                    ID = studentId,
                    Password = "pwd12345678901234567890123456789",
                    Name = "Student797",
                    IsValid = true,
                    EnRollDate = new DateTime(2021, 9, 1),
                };
                seedDc.Set<Student>().Add(student);

                var major = new Major
                {
                    MajorCode = "797",
                    MajorName = "OldName",
                    MajorType = MajorTypeEnum.Required,
                    SchoolId = 1,
                };
                seedDc.Set<Major>().Add(major);
                seedDc.SaveChanges();
                majorId = major.ID;

                var studentMajor = new StudentMajor
                {
                    ID = Guid.NewGuid(),
                    MajorId = majorId,
                    StudentId = studentId,
                    UpdateTime = originalStudentMajorUpdateTime,
                    UpdateBy = originalStudentMajorUpdateBy,
                };
                seedDc.Set<StudentMajor>().Add(studentMajor);
                seedDc.SaveChanges();
            }

            using (var editDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var controller = CreateController(editDc);

                var result = controller.UpdateModelProperty(majorVmFullName, majorId, "MajorName", "NewName797");

                Assert.IsNotInstanceOfType(
                    result,
                    typeof(BadRequestObjectResult),
                    $"Expected UpdateModelProperty to succeed, but got: {(result as BadRequestObjectResult)?.Value}");
            }

            using (var verifyDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                // #797 load-bearing assertion FIRST: pre-fix, routing the save through
                // vm.DoEdit(false) resynced MajorVM's SetInclude(x => x.StudentMajors)
                // collection from a "SelectedStudentMajorIDs"-shaped property InitVM() never
                // populated (this endpoint runs with passInit: true) — which DoEditPrepare
                // reads as "caller wants zero children" and physically deletes every DB-loaded
                // StudentMajor row. Reverting the #797 production fix makes this .Single(...)
                // throw InvalidOperationException ("no matching element") because the row
                // count is 0, not 1 — that is the actual regression this test guards against.
                var reloadedStudentMajor = verifyDc.Set<StudentMajor>().Single(sm => sm.MajorId == majorId);
                Assert.AreEqual(
                    studentId,
                    reloadedStudentMajor.StudentId,
                    "#797: the untouched sibling StudentMajor row must still exist with its original FK, not be deleted/orphaned");
                Assert.AreEqual(
                    originalStudentMajorUpdateTime,
                    reloadedStudentMajor.UpdateTime,
                    "#797: a sibling child row's UpdateTime must not be collaterally rewritten by an unrelated single-field inline edit");
                Assert.AreEqual(
                    originalStudentMajorUpdateBy,
                    reloadedStudentMajor.UpdateBy,
                    "#797: a sibling child row's UpdateBy must not be collaterally rewritten by an unrelated single-field inline edit");

                var reloadedMajor = verifyDc.Set<Major>().Single(m => m.ID == majorId);
                Assert.AreEqual(
                    "NewName797",
                    reloadedMajor.MajorName,
                    "#532: the targeted field must still actually persist for a standard generated CRUD VM");
                // Secondary/non-load-bearing under the EF InMemory provider used here: reverting
                // the #797 fix does NOT flip this value in this test (SchoolId stays 1 either
                // way), because InMemory does not enforce/null FK columns the way a relational
                // provider's Attach()-then-null-navigation pass would. Kept only as a
                // documented illustration of the DoEditPrepare navigation-nulling mechanism
                // described in the class-level remarks; do not rely on it to catch a #797
                // regression on this provider.
                Assert.AreEqual(
                    1,
                    reloadedMajor.SchoolId,
                    "#797 (secondary, provider-dependent): the untouched SchoolId FK must not be nulled out as a side effect of attaching the School navigation");
            }
        }

        // ─── #809: bypassing DoEdit() must not drop the [AuditChanges] ChangeLog row ───

        /// <summary>
        /// Regression test for the #809 review round: #797 stopped routing
        /// <c>UpdateModelProperty</c> through <c>vm.DoEdit(false)</c> to avoid its collateral
        /// writes, but <c>DoEdit()</c> was also the only place that called
        /// <c>AppendChangeLog("Edit", ...)</c>. Without restoring that call explicitly, inline
        /// edits of an <see cref="AuditChangesAttribute"/>-annotated RBAC entity (here
        /// <see cref="FrameworkUser"/>, via the standard generated <see cref="FrameworkUserVM"/>)
        /// through this <c>[AllRights]</c> endpoint would silently stop writing an audit trail.
        /// </summary>
        [TestMethod]
        public void UpdateModelProperty_AuditedEntity_WritesEditChangeLog()
        {
            var frameworkUserVmFullName = typeof(FrameworkUserVM).AssemblyQualifiedName!;
            const string itCode = "u809";
            const string originalName = "OldUserName";

            using (var seedDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var user = new FrameworkUser
                {
                    ITCode = itCode,
                    Password = "pwd12345678901234567890123456789",
                    Name = originalName,
                    IsValid = true,
                };
                seedDc.Set<FrameworkUser>().Add(user);
                seedDc.SaveChanges();
            }

            Guid userId;
            using (var idDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                userId = idDc.Set<FrameworkUser>().Single(u => u.ITCode == itCode).ID;
            }

            using (var editDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var controller = CreateController(editDc);

                var result = controller.UpdateModelProperty(frameworkUserVmFullName, userId, "Name", "NewUserName");

                Assert.IsNotInstanceOfType(
                    result,
                    typeof(BadRequestObjectResult),
                    $"Expected UpdateModelProperty to succeed, but got: {(result as BadRequestObjectResult)?.Value}");
            }

            using (var verifyDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var reloadedUser = verifyDc.Set<FrameworkUser>().Single(u => u.ID == userId);
                Assert.AreEqual(
                    "NewUserName",
                    reloadedUser.Name,
                    "The targeted field must still actually persist");

                var logs = verifyDc.Set<ChangeLog>().Where(l => l.EntityId == userId.ToString()).ToList();
                Assert.AreEqual(
                    1,
                    logs.Count,
                    "#809: inline-editing an [AuditChanges] entity through UpdateModelProperty must write exactly one ChangeLog row, matching DoEdit()'s own AppendChangeLog behaviour");

                var log = logs[0];
                Assert.AreEqual("Edit", log.Action);
                StringAssert.Contains(log.OldValues, originalName, "OldValues must capture the pre-edit Name");
                StringAssert.Contains(log.NewValues, "NewUserName", "NewValues must capture the post-edit Name");
            }
        }

        // ─── #797 (round 3): ValidateDuplicateData() must not overwrite the client's edit ───

        /// <summary>
        /// Regression test for the round-3 fix: <c>BaseCRUDVM.ValidateDuplicateData()</c>
        /// unconditionally forces <c>Entity.IsValid = true</c> (to build its uniqueness
        /// query) whenever <typeparamref name="TModel"/> is <see cref="IPersistPoco"/> and
        /// the duplicate-check group does not already cover "IsValid" — see
        /// <see cref="WxReportDataDupCheckVM"/>. Before the fix, an inline edit that set
        /// IsValid to <c>false</c> ran <c>SetPropertyValue</c> BEFORE
        /// <c>ValidateDuplicateDataOnly()</c>, which then clobbered it back to <c>true</c>
        /// before the property was marked Modified — the endpoint returned "Success" while
        /// silently persisting the OPPOSITE of what the client asked for (the #532 class of
        /// bug, recreated for the IsValid/TenantCode fields by the round-2 fix).
        /// </summary>
        [TestMethod]
        public void UpdateModelProperty_IsValidField_DuplicateCheckDoesNotOverwriteClientValue()
        {
            var vmFullName = typeof(WxReportDataDupCheckVM).AssemblyQualifiedName!;
            Guid reportId;

            using (var seedDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var report = new WxReportData
                {
                    ToWxUser = "user-797-round3",
                    Date = new DateTime(2024, 1, 1),
                    IsValid = true,
                };
                seedDc.Set<WxReportData>().Add(report);
                seedDc.SaveChanges();
                reportId = report.ID;
            }

            using (var editDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var controller = CreateController(editDc);

                var result = controller.UpdateModelProperty(vmFullName, reportId, "IsValid", "false");

                Assert.IsNotInstanceOfType(
                    result,
                    typeof(BadRequestObjectResult),
                    $"Expected UpdateModelProperty to succeed, but got: {(result as BadRequestObjectResult)?.Value}");
            }

            using (var verifyDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                // Load-bearing assertion: pre-fix, ValidateDuplicateData()'s IPersistPoco
                // branch forces Entity.IsValid = true AFTER SetPropertyValue(field, "false")
                // already ran, and that forced "true" is what gets marked Modified and
                // persisted — silently discarding the client's requested "false".
                var reloaded = verifyDc.Set<WxReportData>().IgnoreQueryFilters().Single(r => r.ID == reportId);
                Assert.IsFalse(
                    reloaded.IsValid,
                    "round-3: an inline edit that sets IsValid=false must persist as false, " +
                    "not be silently overwritten to true by ValidateDuplicateData()'s internal " +
                    "uniqueness-query scratch value");
            }
        }

        /// <summary>
        /// Regression test for the round-3 fix's second half: even when the edited field is
        /// NOT one of the fields <c>ValidateDuplicateData()</c> mutates, its
        /// ITenant-branch scratch write to <c>Entity.TenantCode</c> must not bleed into the
        /// <c>[AuditChanges]</c> ChangeLog row that <c>AppendEditChangeLog()</c> writes.
        /// Before the fix, editing "Name" on a <see cref="FrameworkUser"/> (an
        /// <see cref="ITenant"/>, <c>[AuditChanges]</c> entity whose
        /// <see cref="FrameworkUserVM.SetDuplicatedCheck"/> checks ITCode only) with
        /// <c>EnableTenant = true</c> would make <c>ValidateDuplicateData()</c> force
        /// <c>Entity.TenantCode</c> to the caller's current tenant before
        /// <c>AppendEditChangeLog()</c> serializes <see cref="Entity"/> for <c>NewValues</c>
        /// — recording a TenantCode transition that was never written to the database (the
        /// DB row's real TenantCode is untouched, because this endpoint only marks "Name" /
        /// UpdateTime / UpdateBy Modified). An audit row that lies about what changed is
        /// worse than no audit row at all.
        /// </summary>
        [TestMethod]
        public void UpdateModelProperty_UnrelatedField_DoesNotProduceLyingTenantCodeAuditEntry()
        {
            var frameworkUserVmFullName = typeof(FrameworkUserVM).AssemblyQualifiedName!;
            const string itCode = "u797r3";
            const string originalTenantCode = "TENANT_ORIGINAL";
            const string currentUserTenantCode = "TENANT_CURRENT_USER";

            using (var seedDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var user = new FrameworkUser
                {
                    ITCode = itCode,
                    Password = "pwd12345678901234567890123456789",
                    Name = "OldUserName797R3",
                    IsValid = true,
                    TenantCode = originalTenantCode,
                };
                seedDc.Set<FrameworkUser>().Add(user);
                seedDc.SaveChanges();
            }

            Guid userId;
            using (var idDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                // DataContext applies the global ITenant filter (x.TenantCode == dc.TenantCode);
                // scope this lookup context to the entity's own tenant so it is visible.
                idDc.SetTenantCode(originalTenantCode);
                userId = idDc.Set<FrameworkUser>().Single(u => u.ITCode == itCode).ID;
            }

            using (var editDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                editDc.SetTenantCode(originalTenantCode);
                var controller = CreateController(editDc);
                // Enable tenancy and set the caller's current tenant to a value DIFFERENT
                // from the entity's own TenantCode, so a leaked ValidateDuplicateData()
                // scratch write is observably distinct from the entity's real value.
                controller.Wtm.ConfigInfo!.EnableTenant = true;
                controller.Wtm.LoginUserInfo!.CurrentTenant = currentUserTenantCode;

                var result = controller.UpdateModelProperty(frameworkUserVmFullName, userId, "Name", "NewUserName797R3");

                Assert.IsNotInstanceOfType(
                    result,
                    typeof(BadRequestObjectResult),
                    $"Expected UpdateModelProperty to succeed, but got: {(result as BadRequestObjectResult)?.Value}");
            }

            using (var verifyDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                verifyDc.SetTenantCode(originalTenantCode);
                var reloadedUser = verifyDc.Set<FrameworkUser>().Single(u => u.ID == userId);
                Assert.AreEqual(
                    "NewUserName797R3",
                    reloadedUser.Name,
                    "The targeted field must still actually persist");
                Assert.AreEqual(
                    originalTenantCode,
                    reloadedUser.TenantCode,
                    "TenantCode was never marked Modified for this edit and must be unchanged in the database");

                var log = verifyDc.Set<ChangeLog>().Single(l => l.EntityId == userId.ToString());

                // Load-bearing assertion: pre-fix, ValidateDuplicateData()'s ITenant branch
                // forces Entity.TenantCode = currentUserTenantCode before AppendEditChangeLog()
                // serializes Entity for NewValues, so NewValues would (wrongly) show
                // currentUserTenantCode instead of the DB's real, unchanged originalTenantCode.
                StringAssert.Contains(
                    log.NewValues,
                    $"\"TenantCode\":\"{originalTenantCode}\"",
                    "round-3: the audit ChangeLog's NewValues must reflect the entity's real " +
                    "(unchanged) TenantCode, not ValidateDuplicateData()'s internal scratch value");
                Assert.IsFalse(
                    log.NewValues.Contains(currentUserTenantCode),
                    "round-3: the audit ChangeLog must never record a TenantCode transition " +
                    "that was never actually persisted to the database");
            }
        }

        // ─── #824 Part 1 / B1.1: FileAttachment FK must be rejected unconditionally ───

        /// <summary>
        /// Regression test for Issue #824 Part 1 / B1.1 — the live exploit chain: without a
        /// dedicated gate, <see cref="_FrameworkController.UpdateModelProperty"/> would let any
        /// caller set <see cref="FrameworkUser.PhotoId"/> (a scalar FK whose principal is
        /// <see cref="FileAttachment"/>) to ANY <see cref="FileAttachment"/> id — including one
        /// belonging to a different tenant — because #815's FK gate
        /// (<c>BaseCRUDVM.RejectUnresolvableFileAttachmentReferences</c>) only hangs off
        /// <c>DoAddPrepare</c>/<c>DoEditPrepare</c>, and #797 deliberately routes this endpoint
        /// around both. The posted id here is a REAL, persisted <see cref="FileAttachment"/> row
        /// (not a garbage GUID) specifically so that reverting the new guard would let the
        /// request through to a genuine 200 — a garbage/non-existent id could instead be caught
        /// by an unrelated DB-level FK-constraint failure and produce a DIFFERENT 400 ("Edit
        /// failed"), which would make this test SURVIVE the guard's removal instead of detecting
        /// it. Asserting the guard's own message text (not just the status code) is what actually
        /// pins this guard, same convention as the sibling MVC-004 tests above.
        ///
        /// Both halves live in the SAME test method by design: the negative assertion (PhotoId
        /// rejected) proves the deny-list catches an attachment FK; the positive control (Name
        /// still succeeds) proves the guard is scoped to attachment FKs and does not over-block.
        /// Per Issue #824's explicit "unconditional deny" decision, the positive control is
        /// deliberately a NON-attachment field — a same-tenant (or any-tenant) attachment FK edit
        /// is expected to fail too, so it cannot serve as a positive control here.
        /// </summary>
        [TestMethod]
        public void UpdateModelProperty_FileAttachmentForeignKey_RejectedButNonAttachmentFieldStillSucceeds()
        {
            var frameworkUserVmFullName = typeof(FrameworkUserVM).AssemblyQualifiedName!;
            const string itCode = "u824";
            Guid userId;
            Guid fileId;

            using (var seedDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var user = new FrameworkUser
                {
                    ITCode = itCode,
                    Password = "pwd12345678901234567890123456789",
                    Name = "Issue824User",
                    IsValid = true,
                };
                seedDc.Set<FrameworkUser>().Add(user);

                var file = new FileAttachment
                {
                    FileName = "victim.png",
                    FileExt = ".png",
                    Length = 42,
                    UploadTime = DateTime.UtcNow,
                    TenantCode = "TENANT_VICTIM_824",
                };
                seedDc.Set<FileAttachment>().Add(file);
                seedDc.SaveChanges();
                userId = user.ID;
                fileId = file.ID;
            }

            // Negative: PhotoId is a FileAttachment FK and must be rejected unconditionally,
            // regardless of whether the posted id resolves to a real (even cross-tenant) row.
            using (var editDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var controller = CreateController(editDc);

                var result = controller.UpdateModelProperty(frameworkUserVmFullName, userId, "PhotoId", fileId.ToString());

                Assert.IsInstanceOfType(
                    result,
                    typeof(BadRequestObjectResult),
                    "#824: PhotoId is a FileAttachment FK and must be rejected");
                var body = (result as BadRequestObjectResult)?.Value?.ToString() ?? string.Empty;
                StringAssert.Contains(
                    body,
                    "FileAttachment foreign key",
                    "#824: the 400 must come from the FileAttachment-FK guard specifically (its " +
                    "message), not from an unrelated 400 (e.g. a DB-level FK-constraint failure) " +
                    $"that a garbage id could ALSO produce. Got body: {body}");
            }

            using (var verifyDc = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var reloaded = verifyDc.Set<FrameworkUser>().Single(u => u.ID == userId);
                Assert.IsNull(reloaded.PhotoId, "#824: the rejected PhotoId edit must not be persisted");
            }

            // Positive control: a non-attachment field on the SAME entity must still succeed —
            // proves the new guard is scoped to attachment FKs, not a blanket denial.
            using (var editDc2 = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var controller = CreateController(editDc2);

                var result = controller.UpdateModelProperty(frameworkUserVmFullName, userId, "Name", "Updated824");

                Assert.IsNotInstanceOfType(
                    result,
                    typeof(BadRequestObjectResult),
                    $"#824: a non-attachment field must still succeed. Got: {(result as BadRequestObjectResult)?.Value}");
            }

            using (var verifyDc2 = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var reloaded = verifyDc2.Set<FrameworkUser>().Single(u => u.ID == userId);
                Assert.AreEqual("Updated824", reloaded.Name,
                    "#824: the positive control's edit must actually persist");
            }
        }

        /// <summary>
        /// #824 Part 2 review (PR #978 CI): the test above can no longer isolate Part 1's
        /// UpdateModelProperty field-level gate as a mutation target — Part 2's
        /// FileAttachmentSaveChangesGuard boundary now catches the same forged write
        /// independently, with an equivalent message, so neutralizing Part 1 alone no longer
        /// changes that test's observable outcome. This test disables Part 2 via its own kill
        /// switch (<see cref="FileAttachmentSaveChangesGuard.Enabled"/> = false) for its
        /// duration, isolating Part 1's own gate: a mutant that neutralizes it has nothing left
        /// to catch the forged write (the edit would succeed instead of 400), while the real,
        /// unmutated code still rejects it on Part 1's own merits.
        /// </summary>
        [TestMethod]
        public void UpdateModelProperty_FileAttachmentForeignKey_Part1GateAlone_RejectedWithBoundaryGuardDisabled()
        {
            FileAttachmentSaveChangesGuard.Enabled = false;
            try
            {
                var frameworkUserVmFullName = typeof(FrameworkUserVM).AssemblyQualifiedName!;
                const string itCode = "u824p1";
                Guid userId;
                Guid fileId;

                using (var seedDc = new DataContext(_seed, DBTypeEnum.Memory))
                {
                    var user = new FrameworkUser
                    {
                        ITCode = itCode,
                        Password = "pwd12345678901234567890123456789",
                        Name = "Issue824Part1User",
                        IsValid = true,
                    };
                    seedDc.Set<FrameworkUser>().Add(user);

                    var file = new FileAttachment
                    {
                        FileName = "victim-part1.png",
                        FileExt = ".png",
                        Length = 42,
                        UploadTime = DateTime.UtcNow,
                        TenantCode = "TENANT_VICTIM_824_PART1",
                    };
                    seedDc.Set<FileAttachment>().Add(file);
                    seedDc.SaveChanges();
                    userId = user.ID;
                    fileId = file.ID;
                }

                using (var editDc = new DataContext(_seed, DBTypeEnum.Memory))
                {
                    var controller = CreateController(editDc);

                    var result = controller.UpdateModelProperty(frameworkUserVmFullName, userId, "PhotoId", fileId.ToString());

                    Assert.IsInstanceOfType(
                        result,
                        typeof(BadRequestObjectResult),
                        "#824 Part 1 alone (boundary guard disabled): PhotoId is a FileAttachment FK and must still be rejected");
                    var body = (result as BadRequestObjectResult)?.Value?.ToString() ?? string.Empty;
                    StringAssert.Contains(
                        body,
                        "FileAttachment foreign key",
                        "#824 Part 1 alone: the 400 must come from Part 1's own field-level gate — " +
                        "this test uses a REAL, persisted (just cross-tenant) FileAttachment id " +
                        "specifically so a neutralized Part 1 gate, with the boundary guard ALSO " +
                        $"disabled, would let the write through to a genuine success, never a " +
                        $"differently-worded 400. Got body: {body}");
                }

                using (var verifyDc = new DataContext(_seed, DBTypeEnum.Memory))
                {
                    var reloaded = verifyDc.Set<FrameworkUser>().Single(u => u.ID == userId);
                    Assert.IsNull(reloaded.PhotoId, "#824 Part 1 alone: the rejected PhotoId edit must not be persisted");
                }
            }
            finally
            {
                FileAttachmentSaveChangesGuard.Enabled = true;
            }
        }
    }
}
