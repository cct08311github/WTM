#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;
using DUWENINK.Captcha;

namespace WalkingTec.Mvvm.Admin.Test
{
    // ─── Test double: exposes the protected #796 RBAC hooks so tests can assert their
    // default answer directly, and lets a test simulate a deployment-supplied override.
    // (The CanAccessFile hook and its GetFile/GetFileName/ViewFile guards were carved out
    // of #796 to issue #814 — see FrameworkControllerRbacHooksTest's summary below.) ───
    internal class RbacHookProbeController : _FrameworkController
    {
        public Func<Type, bool>? ExportOverride;
        public Func<Type, bool>? PreviewOverride;

        public RbacHookProbeController(ISecurityCodeHelper securityCode) : base(securityCode) { }

        protected override bool CanExportVm(Type vmType) =>
            ExportOverride != null ? ExportOverride(vmType) : base.CanExportVm(vmType);

        protected override bool CanPreviewDelete(Type vmType) =>
            PreviewOverride != null ? PreviewOverride(vmType) : base.CanPreviewDelete(vmType);

        // Direct hook-boundary assertions (bypass any override wired above).
        public bool BaseCanExportVm(Type vmType) => base.CanExportVm(vmType);
        public bool BaseCanPreviewDelete(Type vmType) => base.CanPreviewDelete(vmType);
    }

    // ─── Minimal ListVM for GetExportExcel / GetExportExcelStream — an in-memory
    // static list keeps this independent of any DataContext/EF wiring (mirrors the
    // SchoolStreamVM pattern used by StreamingExportTests). ───
    internal class RbacExportListVM : BasePagedListVM<ImportEndpointItem, BaseSearcher>
    {
        public static List<ImportEndpointItem> Data = new();

        protected override IEnumerable<IGridColumn<ImportEndpointItem>> InitGridHeader() =>
        [
            this.MakeGridHeader(x => x.Code),
            this.MakeGridHeader(x => x.Qty),
        ];

        public override IOrderedQueryable<ImportEndpointItem> GetSearchQuery() =>
            Data.AsQueryable().OrderBy(x => x.Code);
    }

    internal class RbacExportStreamListVM : RbacExportListVM
    {
        public RbacExportStreamListVM()
        {
            UseStreamingExport = true;
        }
    }

    // ─── Round 3 (LOW): counts InitVM() calls so GetExportExcel's up-front, passInit:true
    // probe VM can be proven NOT to trigger a full VM init before the CanExportVm deny —
    // the exact defect round 2 fixed for GetDeletePreview. ───
    internal class RbacExportInitCountingVM : RbacExportListVM
    {
        public static int InitVMCallCount;

        protected override void InitVM()
        {
            InitVMCallCount++;
            base.InitVM();
        }
    }

    // ─── Review round 2 (MEDIUM): counts InitVM() calls so GetDeletePreview's up-front
    // Type-resolution call can be proven NOT to trigger a full VM init on the default path
    // (a standard generated CRUD VM's InitVM() commonly issues its own DB queries, e.g.
    // GetSelectListItems — this VM stands in for that cost without needing a real lookup). ───
    internal class RbacPreviewInitCountingVM : BaseCRUDVM<ImportEndpointItem>
    {
        public static int InitVMCallCount;

        protected override void InitVM()
        {
            InitVMCallCount++;
            base.InitVM();
        }
    }

    /// <summary>
    /// Issue #796 — per-caller RBAC gates on the VM-name-driven
    /// <c>_FrameworkController</c> endpoints (<c>GetExportExcel</c>,
    /// <c>GetExportExcelStream</c>, <c>GetDeletePreview</c>, <c>GetExcelTemplate</c>)
    /// plus the <c>SetTenant</c> rejected-switch fix.
    ///
    /// (The file-id-driven half of #796 — <c>CanAccessFile</c> and the
    /// <c>GetFile</c>/<c>GetFileName</c>/<c>ViewFile</c> guards — was carved out to issue
    /// #814 and is covered there, not in this class.)
    ///
    /// Every gated endpoint is covered for:
    ///   - the unchanged default (config flag off, no override) → ALLOWED, proving #796 did
    ///     not silently flip default behaviour (Compatibility Red Line);
    ///   - the new fail-closed opt-in (config flag on, no override) → DENIED;
    ///   - a deployment-supplied override taking precedence over the flag.
    /// </summary>
    [TestClass]
    public class FrameworkControllerRbacHooksTest
    {
        private string _seed = null!;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
            RbacExportListVM.Data = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "A001", Qty = 1 },
            };
            RbacPreviewInitCountingVM.InitVMCallCount = 0;
            RbacExportInitCountingVM.InitVMCallCount = 0;
        }

        private static RbacHookProbeController CreateController(IDataContext? dc = null, string usercode = "testuser")
        {
            var mockSecurityCode = new Mock<ISecurityCodeHelper>();
            var controller = new RbacHookProbeController(mockSecurityCode.Object);
            controller.Wtm = MockWtmContext.CreateWtmContext(dc, usercode);

            var httpContext = new DefaultHttpContext();
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.Body = System.IO.Stream.Null;

            // Wire a permissive RequestServices mock so SetTenant's ILogger resolution
            // (and anything else GetService<T>() is called on) doesn't NRE on a null provider.
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

        // ══════════════════════ CanExportVm / GetExportExcel ══════════════════════

        [TestMethod]
        public void CanExportVm_DefaultConfig_ReturnsTrue()
        {
            var controller = CreateController();
            Assert.IsTrue(controller.BaseCanExportVm(typeof(RbacExportListVM)),
                "Default (unconfigured) CanExportVm must allow — unchanged pre-#796 behaviour.");
        }

        [TestMethod]
        public void CanExportVm_EnforceFlagEnabled_ReturnsFalse()
        {
            var controller = CreateController();
            controller.Wtm.ConfigInfo!.EnforceVmExportAuthorization = true;
            Assert.IsFalse(controller.BaseCanExportVm(typeof(RbacExportListVM)),
                "Enabling EnforceVmExportAuthorization must flip the un-overridden default to deny.");
        }

        [TestMethod]
        public void GetExportExcel_DefaultConfig_AllowsExport()
        {
            var controller = CreateController();
            var result = controller.GetExportExcel(typeof(RbacExportListVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(FileContentResult),
                "Default config (flag off, no override) must allow export — unchanged pre-#796 behaviour.");
        }

        [TestMethod]
        public void GetExportExcel_EnforceFlagEnabledNoOverride_ReturnsForbid()
        {
            var controller = CreateController();
            controller.Wtm.ConfigInfo!.EnforceVmExportAuthorization = true;
            var result = controller.GetExportExcel(typeof(RbacExportListVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "With EnforceVmExportAuthorization enabled and no override, export must be denied " +
                "(fail-closed kill switch — no code change required to enable).");
        }

        [TestMethod]
        public void GetExportExcel_OverrideDenies_ReturnsForbid()
        {
            var controller = CreateController();
            controller.ExportOverride = _ => false;
            var result = controller.GetExportExcel(typeof(RbacExportListVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "A deployment-supplied CanExportVm override must be able to deny the export regardless of config.");
        }

        [TestMethod]
        public void GetExportExcel_OverrideAllows_EvenWithFlagEnabled_ReturnsFileResult()
        {
            var controller = CreateController();
            controller.Wtm.ConfigInfo!.EnforceVmExportAuthorization = true; // fail-closed baseline
            controller.ExportOverride = _ => true;                          // deployment explicitly allows this VM
            var result = controller.GetExportExcel(typeof(RbacExportListVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(FileContentResult),
                "An override returning true must allow the export even when the fail-closed flag is enabled.");
        }

        // ══════════════════════ GetExportExcelStream ══════════════════════

        [TestMethod]
        public void GetExportExcelStream_DefaultConfig_AllowsExport()
        {
            var controller = CreateController();
            var result = controller.GetExportExcelStream(typeof(RbacExportStreamListVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(FileContentResult),
                "Default config must allow the streaming export — unchanged pre-#796 behaviour.");
        }

        [TestMethod]
        public void GetExportExcelStream_EnforceFlagEnabledNoOverride_ReturnsForbid()
        {
            var controller = CreateController();
            controller.Wtm.ConfigInfo!.EnforceVmExportAuthorization = true;
            var result = controller.GetExportExcelStream(typeof(RbacExportStreamListVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "GetExportExcelStream shares CanExportVm with GetExportExcel — the flag must gate it too.");
        }

        [TestMethod]
        public void GetExportExcelStream_OverrideDenies_ReturnsForbid()
        {
            var controller = CreateController();
            controller.ExportOverride = _ => false;
            var result = controller.GetExportExcelStream(typeof(RbacExportStreamListVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "A deployment-supplied CanExportVm override must be able to deny the streaming export " +
                "regardless of config — GetExportExcelStream shares the same hook as GetExportExcel, " +
                "and override precedence must hold for both.");
        }

        /// <summary>
        /// Round 3 (LOW): before the fix, GetExportExcel's up-front <c>Wtm.CreateVM(_DONOT_USE_VMNAME)</c>
        /// call defaulted to <c>passInit: false</c>, which ran the full <c>DoInit()</c>/<c>InitVM()</c>
        /// on the caller-named VM on every request — including when the flag is on and the
        /// request is about to be denied. Proves InitVM() is never invoked on the deny path now.
        /// </summary>
        [TestMethod]
        public void GetExportExcel_EnforceFlagEnabled_DeniesBeforeTriggeringInitVM()
        {
            var controller = CreateController();
            controller.Wtm.ConfigInfo!.EnforceVmExportAuthorization = true;
            var result = controller.GetExportExcel(typeof(RbacExportInitCountingVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "Sanity check: the deny itself must still happen on the fail-closed path.");
            Assert.AreEqual(0, RbacExportInitCountingVM.InitVMCallCount,
                "GetExportExcel must not call InitVM() on the caller-named VM before the " +
                "CanExportVm deny decision — the up-front resolution must use passInit: true.");
        }

        // ══════════════════════ CanPreviewDelete / GetDeletePreview ══════════════════════

        [TestMethod]
        public void CanPreviewDelete_DefaultConfig_ReturnsTrue()
        {
            var controller = CreateController();
            Assert.IsTrue(controller.BaseCanPreviewDelete(typeof(BaseCRUDVM<ImportEndpointItem>)),
                "Default (unconfigured) CanPreviewDelete must allow — unchanged pre-#796 behaviour.");
        }

        [TestMethod]
        public void CanPreviewDelete_EnforceFlagEnabled_ReturnsFalse()
        {
            var controller = CreateController();
            controller.Wtm.ConfigInfo!.EnforceDeletePreviewAuthorization = true;
            Assert.IsFalse(controller.BaseCanPreviewDelete(typeof(BaseCRUDVM<ImportEndpointItem>)),
                "Enabling EnforceDeletePreviewAuthorization must flip the un-overridden default to deny.");
        }

        [TestMethod]
        public void GetDeletePreview_DefaultConfig_AllowsPreview_ReturnsLabel()
        {
            var seededId = Guid.NewGuid();
            using (var seedDc = new ImportEndpointDataContext(_seed, DBTypeEnum.Memory))
            {
                seedDc.Database.EnsureCreated();
                seedDc.Set<ImportEndpointItem>().Add(new ImportEndpointItem { ID = seededId, Code = "P001", Qty = 3 });
                seedDc.SaveChanges();
            }

            var controller = CreateController(new ImportEndpointDataContext(_seed, DBTypeEnum.Memory));
            var vmName = typeof(BaseCRUDVM<ImportEndpointItem>).AssemblyQualifiedName!;

            var result = controller.GetDeletePreview(vmName, new[] { seededId.ToString() });

            Assert.IsInstanceOfType(result, typeof(OkObjectResult),
                "Default config must allow the delete preview — unchanged pre-#796 behaviour.");
        }

        [TestMethod]
        public void GetDeletePreview_EnforceFlagEnabledNoOverride_ReturnsForbid()
        {
            var controller = CreateController(new ImportEndpointDataContext(_seed, DBTypeEnum.Memory));
            controller.Wtm.ConfigInfo!.EnforceDeletePreviewAuthorization = true;
            var vmName = typeof(BaseCRUDVM<ImportEndpointItem>).AssemblyQualifiedName!;

            var result = controller.GetDeletePreview(vmName, new[] { Guid.NewGuid().ToString() });

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "With EnforceDeletePreviewAuthorization enabled and no override, preview must be denied.");
        }

        [TestMethod]
        public void GetDeletePreview_OverrideDenies_ReturnsForbid()
        {
            var controller = CreateController(new ImportEndpointDataContext(_seed, DBTypeEnum.Memory));
            controller.PreviewOverride = _ => false;
            var vmName = typeof(BaseCRUDVM<ImportEndpointItem>).AssemblyQualifiedName!;

            var result = controller.GetDeletePreview(vmName, new[] { Guid.NewGuid().ToString() });

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "A deployment-supplied CanPreviewDelete override must be able to deny the preview.");
        }

        /// <summary>
        /// #881 review (P2): base behaviour returns <c>BadRequest</c> for an unresolvable/
        /// unregistered VM name (<c>Wtm.CreateVM(string, ...)</c>'s <c>ArgumentException</c>,
        /// caught in the pre-#829 code). The #829 refactor to
        /// <see cref="WTMContext.TryResolveVmType"/> initially merged that case into the same
        /// <c>Forbid()</c> branch as an actual <c>CanPreviewDelete</c> denial — a real wire-
        /// contract change (400 → 302 under cookie auth) that a comment on the same lines
        /// incorrectly claimed was "unchanged". This pins the restored, original contract: an
        /// unresolvable name must still be <c>BadRequest</c>, not <c>Forbid()</c>.
        /// </summary>
        [TestMethod]
        public void GetDeletePreview_UnknownVmName_ReturnsBadRequestNotForbid()
        {
            var controller = CreateController(new ImportEndpointDataContext(_seed, DBTypeEnum.Memory));

            var result = controller.GetDeletePreview("Totally.Unregistered.Nonexistent.VmType, NoSuchAssembly", new[] { Guid.NewGuid().ToString() });

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "#881: an unresolvable/unregistered VM name must return BadRequest — the same " +
                "contract GetDeletePreview had before the #829 TryResolveVmType refactor — not " +
                "Forbid(), which would be indistinguishable from an actual CanPreviewDelete denial.");
        }

        [TestMethod]
        public void GetDeletePreview_EmptyIds_UnaffectedByFlag_ReturnsEmptyOk()
        {
            // Documents pre-existing (unchanged) behaviour: an empty ids[] short-circuits
            // before the RBAC gate ever runs. It returns no data either way, so this is not
            // a new hole introduced by #796 — just recorded so a future change to the
            // short-circuit ordering trips a test.
            var controller = CreateController(new ImportEndpointDataContext(_seed, DBTypeEnum.Memory));
            controller.Wtm.ConfigInfo!.EnforceDeletePreviewAuthorization = true;
            var vmName = typeof(BaseCRUDVM<ImportEndpointItem>).AssemblyQualifiedName!;

            var result = controller.GetDeletePreview(vmName, Array.Empty<string>());

            Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        }

        /// <summary>
        /// Review round 2 (MEDIUM): before the fix, GetDeletePreview's up-front
        /// <c>Wtm.CreateVM(_DONOT_USE_VMNAME)</c> call defaulted to <c>passInit: false</c>,
        /// which ran the full <c>DoInit()</c>/<c>InitVM()</c> on the caller-named VM on every
        /// request — including with the flag off, and before the deny decision — even though
        /// the per-row VMs created in the loop below already used <c>passInit: true</c> and
        /// never needed it. Proves InitVM() is never invoked by GetDeletePreview at all now.
        /// </summary>
        [TestMethod]
        public void GetDeletePreview_DefaultConfig_DoesNotTriggerInitVM()
        {
            var seededId = Guid.NewGuid();
            using (var seedDc = new ImportEndpointDataContext(_seed, DBTypeEnum.Memory))
            {
                seedDc.Database.EnsureCreated();
                seedDc.Set<ImportEndpointItem>().Add(new ImportEndpointItem { ID = seededId, Code = "P002", Qty = 1 });
                seedDc.SaveChanges();
            }

            var controller = CreateController(new ImportEndpointDataContext(_seed, DBTypeEnum.Memory));
            var vmName = typeof(RbacPreviewInitCountingVM).AssemblyQualifiedName!;

            var result = controller.GetDeletePreview(vmName, new[] { seededId.ToString() });

            Assert.IsInstanceOfType(result, typeof(OkObjectResult),
                "Sanity check: the preview itself must still succeed on the default path.");
            Assert.AreEqual(0, RbacPreviewInitCountingVM.InitVMCallCount,
                "GetDeletePreview must not call InitVM() at all: neither the up-front " +
                "Type-resolution call (now passInit: true) nor the per-row loop (already " +
                "passInit: true before this fix) should trigger it.");
        }

        // ══════════════════════ CanExportVm / GetExcelTemplate ══════════════════════
        // Residual-risk follow-up (round 3): GetExcelTemplate is a caller-VM-named
        // file-generation endpoint with the same shape of exposure as GetExportExcel /
        // GetExportExcelStream (discloses an arbitrary registered VM's column layout to any
        // authenticated caller) — gated with the same CanExportVm hook.

        [TestMethod]
        public void GetExcelTemplate_DefaultConfig_AllowsGeneration()
        {
            var controller = CreateController();
            var result = controller.GetExcelTemplate(typeof(ImportEndpointVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(FileContentResult),
                "Default config (flag off, no override) must allow template generation — unchanged pre-#796 behaviour.");
        }

        [TestMethod]
        public void GetExcelTemplate_EnforceFlagEnabledNoOverride_ReturnsForbid()
        {
            var controller = CreateController();
            controller.Wtm.ConfigInfo!.EnforceVmExportAuthorization = true;
            var result = controller.GetExcelTemplate(typeof(ImportEndpointVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "With EnforceVmExportAuthorization enabled and no override, template generation must be denied.");
        }

        [TestMethod]
        public void GetExcelTemplate_OverrideDenies_ReturnsForbid()
        {
            var controller = CreateController();
            controller.ExportOverride = _ => false;
            var result = controller.GetExcelTemplate(typeof(ImportEndpointVM).AssemblyQualifiedName!, null!);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "A deployment-supplied CanExportVm override must be able to deny GetExcelTemplate too — it shares the hook.");
        }

        // ══════════════════════ SetTenant — rejected switch must not report success (#796) ══════

        /// <summary>
        /// Before #796, <c>SetTenant</c> discarded <c>SetCurrentTenant</c>'s bool result: a
        /// caller who is not entitled to switch into the requested tenant still got the
        /// success response (page reload), silently staying on their original tenant with no
        /// error surfaced and nothing logged. This proves the rejected switch is now surfaced
        /// as 403 and does not silently claim success.
        /// </summary>
        [TestMethod]
        public void SetTenant_RejectedSwitch_ReturnsForbidNotSuccess()
        {
            var controller = CreateController();
            controller.Wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "user1",
                TenantCode = "tenantA",
            };
            // GlobaInfo.AllTenant defaults to [] (no TenantGetFunc registered), so "tenantB" is
            // not found as a child tenant of "tenantA" — SetCurrentTenant must reject the switch.

            var result = controller.SetTenant("tenantB");

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "A tenant switch SetCurrentTenant rejects must return 403, not the success/reload response.");
        }

        /// <summary>
        /// A caller switching to their own (current) tenant is always accepted by
        /// <c>SetCurrentTenant</c> — this must keep returning the success/reload response,
        /// proving the #796 fix does not regress the legitimate case.
        /// <para>
        /// #1007 design round 6 §8 row 16 ("first strengthen this existing test"): before this
        /// change, the assertion only checked <c>result</c> was NOT a <see cref="ForbidResult"/>
        /// — a controller bug that returned <c>Unauthorized()</c> (or anything else that isn't
        /// literally <c>ForbidResult</c>) for this legitimate, allowed switch would have passed
        /// silently. Asserting the PRECISE success type (<see cref="WtmActionResult"/>, what
        /// <c>FFResultJson()</c> actually returns — <c>BaseController.cs:461</c>) closes that
        /// gap and serves as mutant M1's positive/green control
        /// (<c>test/mutants/entries/1007-setcurrenttenant-hostbypass-reintroduce.json</c>).
        /// </para>
        /// </summary>
        [TestMethod]
        public void SetTenant_SwitchToOwnTenant_DoesNotReturnForbid()
        {
            var controller = CreateController();
            controller.Wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "user1",
                TenantCode = "tenantA",
            };

            var result = controller.SetTenant("tenantA");

            Assert.IsNotInstanceOfType(result, typeof(ForbidResult),
                "Switching to the caller's own current tenant must still succeed.");
            Assert.IsInstanceOfType(result, typeof(WtmActionResult),
                "#1007 row 16: switching to the caller's own current tenant must return the " +
                "precise success/reload response FFResultJson().Reload() produces — not merely " +
                "'anything that isn't Forbid()' (e.g. an Unauthorized() bug would have slipped " +
                "past the weaker assertion this replaces).");
        }

        // ══════════════════════ #1007 — controller-level admission narrowing ══════

        /// <summary>
        /// #1007 design round 6 §8 row 15 ("host x ghost tenant"). This is a
        /// CONTROLLER-LEVEL test — it constructs <see cref="RbacHookProbeController"/> directly
        /// and calls its <c>SetTenant</c> action method in-process, the same convention every
        /// other test in this file uses (see the class-level doc comment); it is NOT a real,
        /// routed HTTP request through a test server. A genuinely wire-level (real HTTP, real
        /// request-scoped DI) proof that <see cref="WalkingTec.Mvvm.Core.Services.IWtmTenantSwitchPolicy"/>
        /// is reachable through <c>WTMContext</c>'s real <c>ServiceProvider</c> fallback lives in
        /// <c>WalkingTec.Mvvm.Api.Test.TenantSwitchPolicySeamTests1007</c> — see
        /// production-readiness.md's #1007 section for why both exist.
        /// <para>
        /// A host caller (<c>TenantCode == null</c>) requesting a tenant code that does not
        /// resolve in <c>GlobaInfo.AllTenant</c> at all (a "ghost" tenant) must now be refused —
        /// before #1007 this was unconditionally admitted for any host caller (design round 6
        /// §7 table, row H3, "the core hole" this issue closes).
        /// </para>
        /// </summary>
        [TestMethod]
        public void SetTenant_HostGhostTenant_ReturnsForbidResult()
        {
            var controller = CreateController();
            controller.Wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "hostuser1",
                TenantCode = null,
            };
            // GlobaInfo.AllTenant defaults to [] (no TenantGetFunc registered), so "ghost-co" is
            // unresolvable — IsTenantSwitchPermitted's L-notfound branch must refuse it even for
            // a host caller.

            var result = controller.SetTenant("ghost-co");

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "#1007 row 15: a host caller switching into an unresolvable ('ghost') tenant " +
                "code must now be refused (403), not silently admitted as it was pre-#1007.");
        }
    }
}
