using System;
using System.Linq;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Demo;
using WalkingTec.Mvvm.Demo.Models;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;
using DUWENINK.Captcha;

namespace WalkingTec.Mvvm.Admin.Test
{
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
    }
}
