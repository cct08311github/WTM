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
    // ─── ImportVM with a static preset entity list ────────────────────────────────
    // The real DoImport endpoint resolves the VM through WTMContext.CreateVM, which
    // constructs it via a bare parameterless constructor (reflection) — it cannot see
    // FrameworkControllerImportTest's ImportEndpointVM(List<...>) constructor overload.
    // A static field mirrors the pattern FrameworkControllerRbacHooksTest already uses
    // for RbacExportListVM.Data to get data into a reflection-constructed VM.
    internal class ImportAuthVM : BaseImportVM<ImportEndpointTemplateVM, ImportEndpointItem>
    {
        public static List<ImportEndpointItem> NextPreset = new();

        public override void SetEntityList()
        {
            if (!isEntityListSet)
            {
                EntityList = NextPreset;
                isEntityListSet = true;
            }
        }
    }

    // ─── Test double: exposes the protected #818 CanImportVm hook so tests can assert
    // its default answer directly and simulate a deployment-supplied override, mirroring
    // RbacHookProbeController's shape for the #796/#814 hooks. ───
    internal class ImportAuthProbeController : _FrameworkController
    {
        public Func<Type, bool>? ImportOverride;

        public ImportAuthProbeController(ISecurityCodeHelper securityCode) : base(securityCode) { }

        protected override bool CanImportVm(Type vmType) =>
            ImportOverride != null ? ImportOverride(vmType) : base.CanImportVm(vmType);

        // Direct hook-boundary assertion (bypasses any override wired above).
        public bool BaseCanImportVm(Type vmType) => base.CanImportVm(vmType);
    }

    /// <summary>
    /// Issue #818 — per-caller VM-level authorization gate on the shared <c>DoImport</c>
    /// endpoint (#433), mirroring the #796/#814 hook shape (<c>CanExportVm</c>,
    /// <c>CanPreviewDelete</c>, <c>CanAccessFile</c>).
    ///
    /// Uses the real <c>DoImport</c> action (not a test-only bypass like
    /// <c>FrameworkControllerImportTest</c>'s <c>DoImportWithTestVm</c>) so the actual
    /// deny-before-construct wiring is exercised end to end, reusing
    /// <see cref="ImportAuthVM"/> / <see cref="ImportEndpointDataContext"/> /
    /// <see cref="ImportEndpointItem"/> from that file (same assembly, resolvable via
    /// <c>Type.GetType(AssemblyQualifiedName)</c> — same mechanism already proven by
    /// <c>FrameworkControllerRbacHooksTest</c>'s <c>GetExportExcel</c> calls).
    ///
    /// Covers:
    ///   - the unchanged default (config flag off, no override) → ALLOWED, proving #818
    ///     did not silently flip default behaviour (Compatibility Red Line);
    ///   - the new fail-closed opt-in (config flag on, no override) → DENIED, and no
    ///     rows written;
    ///   - a deployment-supplied override taking precedence over the flag in both
    ///     directions.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class FrameworkControllerImportAuthTest
    {
        private string _seed = null!;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
            // #818 nit: ImportAuthVM.NextPreset is mutable static state shared by every test
            // in this class (SetEntityList reads it via reflection-constructed VMs, so it
            // can't be passed as an instance value). Reset it here so a test that forgets to
            // set its own preset fails loudly on an empty/zero row-count assertion instead of
            // silently inheriting whatever the previous test left behind — the same TCONC
            // shared-static-state shape behind this repo's earlier flakes. [DoNotParallelize]
            // on the class is the other half: MSTest could otherwise run these methods on
            // different threads and race writes to this same static field.
            ImportAuthVM.NextPreset = new List<ImportEndpointItem>();
        }

        private IDataContext CreateDb() => new ImportEndpointDataContext(_seed, DBTypeEnum.Memory);

        private static ImportAuthProbeController CreateController(IDataContext dataContext, string usercode = "testuser")
        {
            var mockSecurityCode = new Mock<ISecurityCodeHelper>();
            var controller = new ImportAuthProbeController(mockSecurityCode.Object);
            controller.Wtm = MockWtmContext.CreateWtmContext(dataContext, usercode);

            var httpContext = new DefaultHttpContext();
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.Body = System.IO.Stream.Null;

            // Wire a permissive RequestServices mock so the ILogger resolution on the
            // deny path (and anything else GetService<T>() is called on) doesn't NRE on
            // a null provider — mirrors FrameworkControllerRbacHooksTest's setup.
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

        private int CountRows()
        {
            using var ctx = (DbContext)CreateDb();
            return ctx.Set<ImportEndpointItem>().Count();
        }

        // ══════════════════════ CanImportVm — hook boundary ══════════════════════

        [TestMethod]
        public void CanImportVm_DefaultConfig_ReturnsTrue()
        {
            var controller = CreateController(CreateDb());
            Assert.IsTrue(controller.BaseCanImportVm(typeof(ImportAuthVM)),
                "Default (unconfigured) CanImportVm must allow — unchanged pre-#818 behaviour.");
        }

        [TestMethod]
        public void CanImportVm_EnforceFlagEnabled_ReturnsFalse()
        {
            var controller = CreateController(CreateDb());
            controller.Wtm.ConfigInfo!.EnforceVmImportAuthorization = true;
            Assert.IsFalse(controller.BaseCanImportVm(typeof(ImportAuthVM)),
                "Enabling EnforceVmImportAuthorization must flip the un-overridden default to deny.");
        }

        // ══════════════════════ DoImport — happy path (flag off) ══════════════════════

        [TestMethod]
        public void DoImport_DefaultConfig_AllowsImport_ReturnsOkWithImportedCount()
        {
            ImportAuthVM.NextPreset = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "A001", Qty = 10 },
                new ImportEndpointItem { Code = "A002", Qty = 20 },
            };

            var controller = CreateController(CreateDb());
            var vmName = typeof(ImportAuthVM).AssemblyQualifiedName!;

            var result = controller.DoImport(vmName, null!, validateOnly: false);

            Assert.IsInstanceOfType(result, typeof(OkObjectResult),
                "Default config (flag off, no override) must allow the import — unchanged pre-#818 behaviour.");
            Assert.AreEqual(2, CountRows(), "Rows must be persisted on the allowed default path.");
        }

        // ══════════════════════ DoImport — denied path (flag on, no override) ═════════

        [TestMethod]
        public void DoImport_EnforceFlagEnabledNoOverride_ReturnsForbid_NoRowsWritten()
        {
            ImportAuthVM.NextPreset = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "B001", Qty = 1 },
            };

            var controller = CreateController(CreateDb());
            controller.Wtm.ConfigInfo!.EnforceVmImportAuthorization = true;
            var vmName = typeof(ImportAuthVM).AssemblyQualifiedName!;

            var result = controller.DoImport(vmName, null!, validateOnly: false);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "With EnforceVmImportAuthorization enabled and no override, import must be denied " +
                "(fail-closed kill switch — no code change required to enable).");
            Assert.AreEqual(0, CountRows(),
                "No rows may be written on the denied path — the deny must happen strictly before " +
                "BatchSaveData(), not merely surface as a Forbid status alongside a completed write.");
        }

        // ══════════════════════ DoImport — override precedence ═══════════════════════

        [TestMethod]
        public void DoImport_OverrideDenies_ReturnsForbid_NoRowsWritten()
        {
            ImportAuthVM.NextPreset = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "C001", Qty = 1 },
            };

            var controller = CreateController(CreateDb());
            controller.ImportOverride = _ => false;
            var vmName = typeof(ImportAuthVM).AssemblyQualifiedName!;

            var result = controller.DoImport(vmName, null!, validateOnly: false);

            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "A deployment-supplied CanImportVm override must be able to deny the import regardless of config.");
            Assert.AreEqual(0, CountRows(), "No rows may be written when the override denies.");
        }

        [TestMethod]
        public void DoImport_OverrideAllows_EvenWithFlagEnabled_ReturnsOk()
        {
            ImportAuthVM.NextPreset = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "D001", Qty = 1 },
            };

            var controller = CreateController(CreateDb());
            controller.Wtm.ConfigInfo!.EnforceVmImportAuthorization = true; // fail-closed baseline
            controller.ImportOverride = _ => true;                          // deployment explicitly allows this VM
            var vmName = typeof(ImportAuthVM).AssemblyQualifiedName!;

            var result = controller.DoImport(vmName, null!, validateOnly: false);

            Assert.IsInstanceOfType(result, typeof(OkObjectResult),
                "An override returning true must allow the import even when the fail-closed flag is enabled.");
            Assert.AreEqual(1, CountRows(), "Rows must be persisted when the override allows.");
        }

        // ══════════════════════ Non-import VM ═════════════════════════════════════════

        [TestMethod]
        public void DoImport_NonImportVm_ReturnsBadRequest_RegardlessOfFlag()
        {
            var controller = CreateController(CreateDb());
            controller.Wtm.ConfigInfo!.EnforceVmImportAuthorization = true;
            // typeof(BaseCRUDVM<ImportEndpointItem>) IS a BaseVM (it resolves fine), so this
            // proves the IWtmImportable half of the type-check ahead of CanImportVm rejects a
            // non-import BaseVM with 400, not a 403 from the authorization hook.
            var vmName = typeof(BaseCRUDVM<ImportEndpointItem>).AssemblyQualifiedName!;

            var result = controller.DoImport(vmName, null!, validateOnly: false);

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "A VM type that does not implement IWtmImportable must return 400, not 403.");
        }

        [TestMethod]
        public void DoImport_NonBaseVmType_ReturnsBadRequest_RegardlessOfFlag()
        {
            var controller = CreateController(CreateDb());
            controller.Wtm.ConfigInfo!.EnforceVmImportAuthorization = true;
            // ImportEndpointItem is a plain entity (BasePoco), not a BaseVM at all, so
            // TryResolveVmType itself returns null — this covers the BaseVM half of the
            // guard (as opposed to the IWtmImportable half exercised by the case above).
            var vmName = typeof(ImportEndpointItem).AssemblyQualifiedName!;

            var result = controller.DoImport(vmName, null!, validateOnly: false);

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "A type that does not derive from BaseVM must return 400, not 403.");
        }

        // ══════════════════════ Authorized type == constructed type (#818 nit) ═════════

        [TestMethod]
        public void TryResolveVmType_AgreesWithCreateVmConstructedType_ForImportVm()
        {
            // #818 review: ResolveVmType (authorization) used to be a SEPARATE
            // implementation from WTMContext.CreateVM's inline resolution (construction),
            // living in a different assembly (WalkingTec.Mvvm.Mvc vs .Core) — Type.GetType
            // resolves a non-assembly-qualified name relative to the CALLING assembly, so
            // the two copies could in principle resolve a given name to different Types.
            // WTMContext.TryResolveVmType is now the ONE implementation both DoImport's
            // authorization check (Wtm.TryResolveVmType) and CreateVM(string, ...) call
            // internally, so the type CanImportVm authorizes and the type CreateVM
            // constructs are structurally guaranteed to be the same resolution. This test
            // pins that invariant against both call sites directly.
            var controller = CreateController(CreateDb());
            var vmName = typeof(ImportAuthVM).AssemblyQualifiedName!;

            var authorizedType = controller.Wtm.TryResolveVmType(vmName);
            var constructedType = controller.Wtm.CreateVM(vmName, null, null, true).GetType();

            Assert.IsNotNull(authorizedType);
            Assert.AreEqual(authorizedType, constructedType,
                "The type CanImportVm would authorize (TryResolveVmType) and the type " +
                "CreateVM actually constructs must be the same Type for a given VM name.");
        }

        [TestMethod]
        public void DoImport_TypeMatchGuard_DoesNotBlockNormalImport()
        {
            // Companion to the invariant test above: with authorization and construction now
            // sharing one resolver, the rawVm.GetType() != vmType guard added to DoImport
            // never trips on a real request — confirms the defense-in-depth guard is a no-op
            // on the ordinary allowed path, not an accidental new way to reject valid imports.
            ImportAuthVM.NextPreset = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "E001", Qty = 5 },
            };

            var controller = CreateController(CreateDb());
            var vmName = typeof(ImportAuthVM).AssemblyQualifiedName!;

            var result = controller.DoImport(vmName, null!, validateOnly: false);

            Assert.IsInstanceOfType(result, typeof(OkObjectResult));
            Assert.AreEqual(1, CountRows());
        }
    }
}
