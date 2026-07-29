#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;
using DUWENINK.Captcha;

namespace WalkingTec.Mvvm.Admin.Test
{
    // ─── ListVM used by GetExportExcel / GetExportExcelStream — counts constructor,
    // InitListVM(), and GetSearchQuery (the DB query) invocations. GetExportExcelStream's own
    // UseStreamingExport opt-in check runs strictly AFTER CanExportVm, so a single type covers
    // the deny path for both endpoints — the deny happens before that check is even reached.
    // GetSearchQuery is counted rather than made to fail outright, because the SAME fixture is
    // reused by this file's positive-control tests, where GenerateExcel legitimately calls it on
    // the (correctly) allowed path. ───
    internal class Vm829OrderingListVM : BasePagedListVM<ImportEndpointItem, BaseSearcher>
    {
        public static int ConstructorCallCount;
        public static int InitListVMCallCount;
        public static int GetSearchQueryCallCount;

        public Vm829OrderingListVM()
        {
            ConstructorCallCount++;
        }

        protected override IEnumerable<IGridColumn<ImportEndpointItem>> InitGridHeader() =>
        [
            this.MakeGridHeader(x => x.Code),
        ];

        protected override void InitListVM()
        {
            InitListVMCallCount++;
            base.InitListVM();
        }

        public override IOrderedQueryable<ImportEndpointItem> GetSearchQuery()
        {
            GetSearchQueryCallCount++;
            return Enumerable.Empty<ImportEndpointItem>().AsQueryable().OrderBy(x => x.Code);
        }
    }

    // ─── Template VM used by GetExcelTemplate — counts InitVM() calls, which is exactly what
    // BaseVM.DoInit() invokes (see BaseVM.cs: DoInit() { InitVM(); ... }), so this is a direct
    // proxy for "was template.DoInit() called". ───
    internal class Vm829OrderingTemplateVM : BaseTemplateVM
    {
        public static int InitVMCallCount;

        protected override void InitVM()
        {
            InitVMCallCount++;
        }
    }

    // ─── ImportVM used by GetExcelTemplate — counts constructor invocations. ───
    internal class Vm829OrderingImportVM : BaseImportVM<Vm829OrderingTemplateVM, ImportEndpointItem>
    {
        public static int ConstructorCallCount;

        public Vm829OrderingImportVM()
        {
            ConstructorCallCount++;
        }

        public override void SetEntityList()
        {
        }
    }

    // ─── CRUD VM used by GetDeletePreview — counts constructor invocations. ───
    internal class Vm829OrderingCRUDVM : BaseCRUDVM<ImportEndpointItem>
    {
        public static int ConstructorCallCount;

        public Vm829OrderingCRUDVM()
        {
            ConstructorCallCount++;
        }
    }

    /// <summary>
    /// Issue #829 — <c>_FrameworkController</c>'s VM-name-driven endpoints used to construct a
    /// full instance of the caller-named VM (constructor, <c>SetSubVm</c>, and — for a
    /// <c>IBasePagedListVM</c> — <c>DoInitListVM()</c>, or — for a <c>IBaseImport&lt;BaseTemplateVM&gt;</c>
    /// — <c>template.DoInit()</c>) BEFORE the matching authorization hook ever ran, via a
    /// <c>Wtm.CreateVM(name, null, null, true)</c> "probe": <c>passInit: true</c> only gates
    /// <c>DoInit()</c>/<c>InitVM()</c>/<c>searcher.DoInit()</c> — it never gated the constructor
    /// call itself, <c>DoInitListVM()</c>, or (for import VMs) <c>template.DoInit()</c> — so an
    /// authenticated caller could trigger a caller-selected VM's own initialization side effects
    /// and DB queries even on a request that was ultimately denied.
    ///
    /// <para>
    /// The fix (this PR) replaces every such probe with
    /// <see cref="WTMContext.TryResolveVmType"/> — the same Type-only resolution
    /// <c>DoImport</c> already used for the #818 fix — so nothing is constructed until AFTER the
    /// authorization decision. This class proves the deny path never constructs, never calls
    /// <c>InitListVM()</c>, and never calls the import template's <c>InitVM()</c> (a direct proxy
    /// for <c>DoInit()</c> — see <c>BaseVM.DoInit()</c>), using counting VM fixtures. Each
    /// negative assertion is paired with a positive control on the ALLOWED path, proving the
    /// counters are wired at all and the negative assertion is not vacuously true.
    /// </para>
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class FrameworkControllerVmConstructionOrderingTest
    {
        private string _seed = null!;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
            Vm829OrderingListVM.ConstructorCallCount = 0;
            Vm829OrderingListVM.InitListVMCallCount = 0;
            Vm829OrderingListVM.GetSearchQueryCallCount = 0;
            Vm829OrderingTemplateVM.InitVMCallCount = 0;
            Vm829OrderingImportVM.ConstructorCallCount = 0;
            Vm829OrderingCRUDVM.ConstructorCallCount = 0;
        }

        // Mirrors FrameworkControllerRbacHooksTest.CreateController: a bare _FrameworkController
        // (no hook overrides needed — these tests exercise the un-overridden, flag-driven
        // default), with a Mock<IServiceProvider> that only stubs the SINGULAR Type-taking
        // GetService(Type) overload and returns null for it -- ResolveEndpointAuthorizer's DI
        // lookup therefore resolves to null (Inherit), exactly the "no policy registered" shape
        // this fix must change nothing for.
        private static _FrameworkController CreateController(IDataContext? dc = null, string usercode = "testuser")
        {
            var mockSecurityCode = new Mock<ISecurityCodeHelper>();
            var controller = new _FrameworkController(mockSecurityCode.Object);
            controller.Wtm = MockWtmContext.CreateWtmContext(dc, usercode);

            var httpContext = new DefaultHttpContext();
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.Body = System.IO.Stream.Null;

            var mockAuth = new Mock<IAuthenticationService>();
            mockAuth
                .Setup(a => a.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()))
                .Returns(Task.CompletedTask);

            var mockServiceProvider = new Mock<IServiceProvider>();
            mockServiceProvider.Setup(sp => sp.GetService(It.IsAny<Type>())).Returns((object?)null);
            mockServiceProvider.Setup(sp => sp.GetService(typeof(IAuthenticationService))).Returns(mockAuth.Object);
            httpContext.RequestServices = mockServiceProvider.Object;

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        // ══════════════════════ GetExportExcel ══════════════════════

        [TestMethod]
        public void GetExportExcel_EnforceFlagEnabled_DeniesBeforeConstructingVm()
        {
            var controller = CreateController();
            controller.Wtm.ConfigInfo!.EnforceVmExportAuthorization = true;

            var result = controller.GetExportExcel(typeof(Vm829OrderingListVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "Sanity check: the deny itself must still happen on the fail-closed path.");
            Assert.AreEqual(0, Vm829OrderingListVM.ConstructorCallCount,
                "#829: GetExportExcel must not construct the caller-named VM at all before the " +
                "CanExportVm deny decision.");
            Assert.AreEqual(0, Vm829OrderingListVM.InitListVMCallCount,
                "#829: GetExportExcel must not call InitListVM() before the CanExportVm deny " +
                "decision -- WTMContext.CreateVM's private overload calls DoInitListVM() " +
                "unconditionally, regardless of passInit, which is exactly why the prior " +
                "passInit:true probe still had this gap.");
            Assert.AreEqual(0, Vm829OrderingListVM.GetSearchQueryCallCount,
                "#829: GetExportExcel must not run the VM's own DB query before the CanExportVm " +
                "deny decision.");
        }

        [TestMethod]
        public void GetExportExcel_DefaultConfig_AllowedPath_StillConstructsVm()
        {
            // Positive control: proves the counters above are actually wired (not simply always
            // zero because the fixture never runs) by exercising the ALLOWED path.
            var controller = CreateController();
            var result = controller.GetExportExcel(typeof(Vm829OrderingListVM).AssemblyQualifiedName!, null!);

            Assert.IsNotInstanceOfType(result, typeof(ForbidResult),
                "Sanity check: default config (flag off) must allow the export.");
            Assert.IsTrue(Vm829OrderingListVM.ConstructorCallCount > 0,
                "Positive control: the allowed path must actually construct the VM -- otherwise " +
                "the deny-path assertion above would be vacuously true.");
            Assert.IsTrue(Vm829OrderingListVM.GetSearchQueryCallCount > 0,
                "Positive control: the allowed path must actually run the DB query.");
        }

        // ══════════════════════ GetExportExcelStream ══════════════════════

        [TestMethod]
        public void GetExportExcelStream_EnforceFlagEnabled_DeniesBeforeConstructingVm()
        {
            var controller = CreateController();
            controller.Wtm.ConfigInfo!.EnforceVmExportAuthorization = true;

            var result = controller.GetExportExcelStream(typeof(Vm829OrderingListVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "Sanity check: the deny itself must still happen on the fail-closed path.");
            Assert.AreEqual(0, Vm829OrderingListVM.ConstructorCallCount,
                "#829: GetExportExcelStream must not construct the caller-named VM at all before " +
                "the CanExportVm deny decision.");
            Assert.AreEqual(0, Vm829OrderingListVM.InitListVMCallCount,
                "#829: GetExportExcelStream must not call InitListVM() before the CanExportVm " +
                "deny decision.");
            Assert.AreEqual(0, Vm829OrderingListVM.GetSearchQueryCallCount,
                "#829: GetExportExcelStream must not run the VM's own DB query before the " +
                "CanExportVm deny decision.");
        }

        // ══════════════════════ GetExcelTemplate ══════════════════════

        [TestMethod]
        public void GetExcelTemplate_EnforceFlagEnabled_DeniesBeforeConstructingImportVmOrTemplate()
        {
            var controller = CreateController();
            controller.Wtm.ConfigInfo!.EnforceVmExportAuthorization = true;

            var result = controller.GetExcelTemplate(typeof(Vm829OrderingImportVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "Sanity check: the deny itself must still happen on the fail-closed path.");
            Assert.AreEqual(0, Vm829OrderingImportVM.ConstructorCallCount,
                "#829: GetExcelTemplate must not construct the caller-named import VM at all " +
                "before the CanExportVm deny decision.");
            Assert.AreEqual(0, Vm829OrderingTemplateVM.InitVMCallCount,
                "#829: GetExcelTemplate must not call the import template's InitVM() (i.e. " +
                "DoInit()) before the CanExportVm deny decision -- this is the exact gap the " +
                "issue called out: WTMContext.CreateVM's IBaseImport<BaseTemplateVM> branch " +
                "called tvm.Template.DoInit() unconditionally, not gated behind passInit the way " +
                "the plain-VM DoInit()/ListVM searcher.DoInit() were.");
        }

        [TestMethod]
        public void GetExcelTemplate_DefaultConfig_AllowedPath_StillConstructsImportVmAndTemplate()
        {
            // Positive control: proves the counters above are actually wired.
            var controller = CreateController();
            var result = controller.GetExcelTemplate(typeof(Vm829OrderingImportVM).AssemblyQualifiedName!, null!);

            Assert.IsNotInstanceOfType(result, typeof(ForbidResult),
                "Sanity check: default config (flag off) must allow template generation.");
            Assert.IsTrue(Vm829OrderingImportVM.ConstructorCallCount > 0,
                "Positive control: the allowed path must actually construct the import VM.");
            Assert.IsTrue(Vm829OrderingTemplateVM.InitVMCallCount > 0,
                "Positive control: the allowed path must actually initialize the template " +
                "(GenerateTemplate needs it) -- otherwise the deny-path assertion above would be " +
                "vacuously true.");
        }

        // ══════════════════════ GetDeletePreview ══════════════════════

        [TestMethod]
        public void GetDeletePreview_EnforceFlagEnabled_DeniesBeforeConstructingVm()
        {
            var controller = CreateController(new ImportEndpointDataContext(_seed, DBTypeEnum.Memory));
            controller.Wtm.ConfigInfo!.EnforceDeletePreviewAuthorization = true;
            var vmName = typeof(Vm829OrderingCRUDVM).AssemblyQualifiedName!;

            var result = controller.GetDeletePreview(vmName, new[] { Guid.NewGuid().ToString() });

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "Sanity check: the deny itself must still happen on the fail-closed path.");
            Assert.AreEqual(0, Vm829OrderingCRUDVM.ConstructorCallCount,
                "#829: GetDeletePreview must not construct the caller-named VM at all -- neither " +
                "for the up-front Type-resolution step nor the per-row loop -- before the " +
                "CanPreviewDelete deny decision.");
        }

        [TestMethod]
        public void GetDeletePreview_DefaultConfig_AllowedPath_StillConstructsVm()
        {
            // Positive control: proves the counter above is actually wired. Uses a seeded row so
            // the per-row loop's CreateVM call (which the deny-path test above proves never runs
            // when denied) actually executes on the allowed path.
            var seededId = Guid.NewGuid();
            using (var seedDc = new ImportEndpointDataContext(_seed, DBTypeEnum.Memory))
            {
                seedDc.Database.EnsureCreated();
                seedDc.Set<ImportEndpointItem>().Add(new ImportEndpointItem { ID = seededId, Code = "O829", Qty = 1 });
                seedDc.SaveChanges();
            }

            var controller = CreateController(new ImportEndpointDataContext(_seed, DBTypeEnum.Memory));
            var vmName = typeof(Vm829OrderingCRUDVM).AssemblyQualifiedName!;

            var result = controller.GetDeletePreview(vmName, new[] { seededId.ToString() });

            Assert.IsNotInstanceOfType(result, typeof(ForbidResult),
                "Sanity check: default config (flag off) must allow the preview.");
            Assert.IsTrue(Vm829OrderingCRUDVM.ConstructorCallCount > 0,
                "Positive control: the allowed path must actually construct the per-row VM -- " +
                "otherwise the deny-path assertion above would be vacuously true.");
        }
    }
}
