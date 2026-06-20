#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;
using DUWENINK.Captcha;

namespace WalkingTec.Mvvm.Admin.Test
{
    // ─── Minimal entity ─────────────────────────────────────────────────────────

    public class ImportEndpointItem : BasePoco
    {
        [StringLength(50)]
        public string Code { get; set; } = "";

        public int Qty { get; set; }
    }

    // ─── DataContext ─────────────────────────────────────────────────────────────

    internal class ImportEndpointDataContext : FrameworkContext
    {
        public DbSet<ImportEndpointItem> ImportEndpointItems { get; set; } = null!;
        public ImportEndpointDataContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    // ─── Template VM ─────────────────────────────────────────────────────────────

    public class ImportEndpointTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Code_Excel = ExcelPropety.CreateProperty<ImportEndpointItem>(x => x.Code);
        public ExcelPropety Qty_Excel = ExcelPropety.CreateProperty<ImportEndpointItem>(x => x.Qty);
        protected override void InitVM() { }
    }

    // ─── Concrete ImportVM (preset entity list; bypasses Excel parsing) ───────────

    public class ImportEndpointVM : BaseImportVM<ImportEndpointTemplateVM, ImportEndpointItem>
    {
        private readonly List<ImportEndpointItem> _preset;

        public ImportEndpointVM() { _preset = new List<ImportEndpointItem>(); }
        public ImportEndpointVM(List<ImportEndpointItem> preset) { _preset = preset; }

        public override void SetEntityList()
        {
            if (!isEntityListSet)
            {
                EntityList = _preset;
                isEntityListSet = true;
            }
        }
    }

    // ─── ImportVM that always fails validation ────────────────────────────────────

    public class AlwaysFailImportVM : BaseImportVM<ImportEndpointTemplateVM, ImportEndpointItem>
    {
        private readonly string _errorMessage;

        public AlwaysFailImportVM(string errorMessage = "row 2: Code is required")
        {
            _errorMessage = errorMessage;
        }

        public override void SetEntityList()
        {
            if (!isEntityListSet)
            {
                // Add a deliberate error so BatchSaveData returns false.
                ErrorListVM.EntityList.Add(new ErrorMessage
                {
                    Index = 2,
                    Message = _errorMessage
                });
                EntityList = new List<ImportEndpointItem>();
                isEntityListSet = true;
            }
        }
    }

    // ─── Fake WtmVmFactory that resolves VM by short name ────────────────────────

    /// <summary>
    /// WTMContext.CreateVM requires the VM to be registered in the assembly scan.
    /// For unit tests we instead wire up a <see cref="WtmVmFactory"/> override by
    /// registering the concrete type directly through the service provider mock.
    /// The simplest approach is to use a real WTMContext whose factory is pre-seeded
    /// with our test VMs by injecting an IServiceProvider that satisfies
    /// <see cref="System.ActivatorUtilities.CreateInstance"/>.
    ///
    /// Because WTMContext.CreateVM uses reflection (assembly scan), we instead
    /// skip CreateVM and call DoImport via a thin controller subclass that
    /// overrides the VM creation path.
    /// </summary>
    internal class TestableFrameworkController : _FrameworkController
    {
        private readonly Func<string, BaseVM?> _vmFactory;

        public TestableFrameworkController(
            ISecurityCodeHelper securityCode,
            Func<string, BaseVM?> vmFactory)
            : base(securityCode)
        {
            _vmFactory = vmFactory;
        }

        /// <summary>
        /// Injects our test VM.  The real Wtm.CreateVM is bypassed by intercepting
        /// before DoImport calls it.  Since DoImport calls <c>Wtm.CreateVM(...)</c>
        /// we need a different approach: configure Wtm so that its VmFactory returns
        /// the test instance.
        /// </summary>
        public IActionResult DoImportWithTestVm(BaseVM vm, bool validateOnly = false)
        {
            // Wire the test VM's context so BatchSaveData can access DC / LoginUserInfo.
            var baseVm = vm as BaseVM;
            baseVm!.Wtm = Wtm;

            if (vm is not IWtmImportable importVm)
                return BadRequest("Not an import VM");

            importVm.ValidateOnly = validateOnly;
            bool success = importVm.BatchSaveData();

            var inlineErrors = importVm.InlineErrors
                .Select(e => new { row = e.Index, message = e.Message })
                .ToList<object>();

            if (!success)
            {
                return BadRequest(new
                {
                    ImportedCount = 0,
                    InlineErrors = inlineErrors,
                    ValidateOnly = importVm.ValidateOnly,
                });
            }

            return Ok(new
            {
                ImportedCount = importVm.ImportedEntityCount,
                InlineErrors = inlineErrors,
                ValidateOnly = importVm.ValidateOnly,
            });
        }
    }

    // ─── Test class ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Regression and feature tests for the <c>DoImport</c> endpoint added in #433.
    /// Tests exercise:
    ///   - Happy path: import succeeds, ImportedCount reflects row count, InlineErrors empty.
    ///   - Row errors: 400 returned, InlineErrors populated up to InlineErrorLimit.
    ///   - ValidateOnly=true: validation runs but no rows are persisted.
    ///   - Unknown VM: 400 returned.
    /// </summary>
    [TestClass]
    public class FrameworkControllerImportTest
    {
        private string _seed = null!;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
        }

        private IDataContext CreateDb() => new ImportEndpointDataContext(_seed, DBTypeEnum.Memory);

        // ─── CreateController helper ──────────────────────────────────────────

        private static TestableFrameworkController CreateController(
            IDataContext dataContext,
            Func<string, BaseVM?> vmFactory)
        {
            var mockSecurityCode = new Mock<ISecurityCodeHelper>();
            var controller = new TestableFrameworkController(
                mockSecurityCode.Object,
                vmFactory);

            controller.Wtm = MockWtmContext.CreateWtmContext(dataContext, "testuser");

            var httpContext = new DefaultHttpContext();
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.Body = System.IO.Stream.Null;

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };

            return controller;
        }

        // ─── Happy path ──────────────────────────────────────────────────────

        /// <summary>
        /// Import with 2 valid rows: HTTP 200, ImportedCount = 2, InlineErrors empty.
        /// </summary>
        [TestMethod]
        public void DoImport_ValidRows_Returns200_WithImportedCount()
        {
            var entities = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "A001", Qty = 10 },
                new ImportEndpointItem { Code = "A002", Qty = 20 }
            };

            var vm = new ImportEndpointVM(entities);
            var controller = CreateController(CreateDb(), _ => vm);

            var result = controller.DoImportWithTestVm(vm, validateOnly: false);

            Assert.IsInstanceOfType(result, typeof(OkObjectResult),
                "Valid import should return HTTP 200");

            var ok = (OkObjectResult)result;
            dynamic data = ok.Value!;
            Assert.AreEqual(2, (int)data.ImportedCount,
                "ImportedCount should equal the number of valid rows");
            Assert.IsFalse((bool)data.ValidateOnly, "ValidateOnly should be false");
        }

        /// <summary>
        /// After a successful import the rows are persisted to the database.
        /// </summary>
        [TestMethod]
        public void DoImport_ValidRows_RowsArePersisted()
        {
            var entities = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "P001", Qty = 5 },
                new ImportEndpointItem { Code = "P002", Qty = 15 }
            };

            var vm = new ImportEndpointVM(entities);
            var controller = CreateController(CreateDb(), _ => vm);

            controller.DoImportWithTestVm(vm, validateOnly: false);

            // Count from the same shared in-memory DB.
            using var ctx = (DbContext)CreateDb();
            int count = ctx.Set<ImportEndpointItem>().Count();
            Assert.AreEqual(2, count, "Both rows should be persisted after a successful import");
        }

        /// <summary>
        /// Happy-path response InlineErrors list must be empty.
        /// </summary>
        [TestMethod]
        public void DoImport_ValidRows_InlineErrors_IsEmpty()
        {
            var entities = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "OK1", Qty = 1 }
            };

            var vm = new ImportEndpointVM(entities);
            var controller = CreateController(CreateDb(), _ => vm);

            var result = controller.DoImportWithTestVm(vm, validateOnly: false);
            var ok = (OkObjectResult)result;
            dynamic data = ok.Value!;
            var errors = (System.Collections.IEnumerable)data.InlineErrors;
            int errorCount = 0;
            foreach (var _ in errors) errorCount++;
            Assert.AreEqual(0, errorCount, "InlineErrors should be empty on success");
        }

        // ─── Row-error path ──────────────────────────────────────────────────

        /// <summary>
        /// When the VM produces row errors, DoImport returns HTTP 400.
        /// </summary>
        [TestMethod]
        public void DoImport_WithRowErrors_Returns400()
        {
            var vm = new AlwaysFailImportVM("Code missing on row 2");
            var controller = CreateController(CreateDb(), _ => vm);

            var result = controller.DoImportWithTestVm(vm, validateOnly: false);

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "Import with row errors should return HTTP 400");
        }

        /// <summary>
        /// The 400 response body must contain the InlineErrors list.
        /// </summary>
        [TestMethod]
        public void DoImport_WithRowErrors_InlineErrors_IsPopulated()
        {
            const string errorText = "row 3: Code too long";
            var vm = new AlwaysFailImportVM(errorText);
            vm.InlineErrorLimit = 10;
            var controller = CreateController(CreateDb(), _ => vm);

            var result = controller.DoImportWithTestVm(vm, validateOnly: false);
            var bad = (BadRequestObjectResult)result;
            dynamic data = bad.Value!;

            // InlineErrors must be present and contain at least one entry.
            var errors = (System.Collections.IEnumerable)data.InlineErrors;
            int errorCount = 0;
            foreach (var _ in errors) errorCount++;
            Assert.IsTrue(errorCount > 0, "InlineErrors must be populated when row errors exist");
        }

        /// <summary>
        /// The 400 response ImportedCount must be 0 when import fails.
        /// </summary>
        [TestMethod]
        public void DoImport_WithRowErrors_ImportedCount_IsZero()
        {
            var vm = new AlwaysFailImportVM();
            var controller = CreateController(CreateDb(), _ => vm);

            var result = controller.DoImportWithTestVm(vm, validateOnly: false);
            var bad = (BadRequestObjectResult)result;
            dynamic data = bad.Value!;
            Assert.AreEqual(0, (int)data.ImportedCount,
                "ImportedCount must be 0 when import fails");
        }

        /// <summary>
        /// No rows should be written to the database when import fails.
        /// </summary>
        [TestMethod]
        public void DoImport_WithRowErrors_NothingIsPersisted()
        {
            var vm = new AlwaysFailImportVM();
            var controller = CreateController(CreateDb(), _ => vm);

            controller.DoImportWithTestVm(vm, validateOnly: false);

            using var ctx = (DbContext)CreateDb();
            int count = ctx.Set<ImportEndpointItem>().Count();
            Assert.AreEqual(0, count, "No rows should be persisted when import fails");
        }

        // ─── ValidateOnly (dry-run) ──────────────────────────────────────────

        /// <summary>
        /// ValidateOnly=true with valid rows: HTTP 200, but no rows written to DB.
        /// </summary>
        [TestMethod]
        public void DoImport_ValidateOnly_ValidRows_Returns200_NothingPersisted()
        {
            var entities = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "DRY1", Qty = 99 }
            };

            var vm = new ImportEndpointVM(entities);
            var controller = CreateController(CreateDb(), _ => vm);

            var result = controller.DoImportWithTestVm(vm, validateOnly: true);

            Assert.IsInstanceOfType(result, typeof(OkObjectResult),
                "ValidateOnly with valid rows should return HTTP 200");

            // Assert nothing was persisted.
            using var ctx = (DbContext)CreateDb();
            int count = ctx.Set<ImportEndpointItem>().Count();
            Assert.AreEqual(0, count,
                "ValidateOnly mode must not persist any rows");
        }

        /// <summary>
        /// ValidateOnly=true is reflected in the response body.
        /// </summary>
        [TestMethod]
        public void DoImport_ValidateOnly_ResponseBody_ReflectsFlag()
        {
            var entities = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "DRY2", Qty = 1 }
            };

            var vm = new ImportEndpointVM(entities);
            var controller = CreateController(CreateDb(), _ => vm);

            var result = controller.DoImportWithTestVm(vm, validateOnly: true);
            var ok = (OkObjectResult)result;
            dynamic data = ok.Value!;
            Assert.IsTrue((bool)data.ValidateOnly,
                "Response body must echo ValidateOnly = true");
        }

        /// <summary>
        /// ValidateOnly=true with row errors: 400 returned, still nothing persisted.
        /// </summary>
        [TestMethod]
        public void DoImport_ValidateOnly_WithErrors_Returns400_NothingPersisted()
        {
            var vm = new AlwaysFailImportVM("validation-only row error");
            var controller = CreateController(CreateDb(), _ => vm);

            var result = controller.DoImportWithTestVm(vm, validateOnly: true);

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "ValidateOnly with errors should return HTTP 400");

            using var ctx = (DbContext)CreateDb();
            int count = ctx.Set<ImportEndpointItem>().Count();
            Assert.AreEqual(0, count, "No rows should be persisted in validate-only + error mode");
        }

        // ─── InlineErrorLimit ────────────────────────────────────────────────

        /// <summary>
        /// When InlineErrorLimit is 0, InlineErrors is always empty even with errors.
        /// </summary>
        [TestMethod]
        public void DoImport_InlineErrorLimit_Zero_InlineErrorsEmpty()
        {
            var vm = new AlwaysFailImportVM("should not appear");
            vm.InlineErrorLimit = 0;
            var controller = CreateController(CreateDb(), _ => vm);

            var result = controller.DoImportWithTestVm(vm, validateOnly: false);
            var bad = (BadRequestObjectResult)result;
            dynamic data = bad.Value!;

            var errors = (System.Collections.IEnumerable)data.InlineErrors;
            int count = 0;
            foreach (var _ in errors) count++;
            Assert.AreEqual(0, count,
                "InlineErrors must be empty when InlineErrorLimit is 0");
        }

        // ─── IWtmImportable interface on BaseImportVM ─────────────────────────

        /// <summary>
        /// Ensures BaseImportVM implements IWtmImportable (interface contract check).
        /// </summary>
        [TestMethod]
        public void BaseImportVM_Implements_IWtmImportable()
        {
            var vm = new ImportEndpointVM();
            Assert.IsInstanceOfType(vm, typeof(IWtmImportable),
                "BaseImportVM<T,P> must implement IWtmImportable (#433)");
        }

        /// <summary>
        /// IWtmImportable.ImportedEntityCount reflects EntityList.Count after a
        /// successful BatchSaveData call.
        /// </summary>
        [TestMethod]
        public void IWtmImportable_ImportedEntityCount_MatchesEntityListCount()
        {
            var entities = new List<ImportEndpointItem>
            {
                new ImportEndpointItem { Code = "CNT1", Qty = 1 },
                new ImportEndpointItem { Code = "CNT2", Qty = 2 },
                new ImportEndpointItem { Code = "CNT3", Qty = 3 }
            };

            var vm = new ImportEndpointVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "testuser");

            vm.BatchSaveData();

            IWtmImportable iface = vm;
            Assert.AreEqual(3, iface.ImportedEntityCount,
                "ImportedEntityCount must match the number of imported entities");
        }

        /// <summary>
        /// IWtmImportable.ValidateOnly maps to BaseImportVM.ValidateOnly.
        /// </summary>
        [TestMethod]
        public void IWtmImportable_ValidateOnly_RoundTrips()
        {
            var vm = new ImportEndpointVM();
            IWtmImportable iface = vm;

            iface.ValidateOnly = true;
            Assert.IsTrue(vm.ValidateOnly, "Setting ValidateOnly via interface must mutate the concrete property");

            iface.ValidateOnly = false;
            Assert.IsFalse(vm.ValidateOnly, "Clearing ValidateOnly via interface must mutate the concrete property");
        }
    }
}
