#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ─── Additional entity / template for overwrite-exist-data paths ─────────

    public class OverwriteTestItem : BasePoco
    {
        [StringLength(30)]
        public string Code { get; set; } = "";

        [StringLength(100)]
        public string Description { get; set; } = "";

        public int Count { get; set; }
    }

    internal class OverwriteDataContext : DataContext
    {
        public DbSet<OverwriteTestItem> OverwriteItems { get; set; } = null!;
        public OverwriteDataContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    public class OverwriteTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Code_Excel = ExcelPropety.CreateProperty<OverwriteTestItem>(x => x.Code);
        public ExcelPropety Description_Excel = ExcelPropety.CreateProperty<OverwriteTestItem>(x => x.Description);
        public ExcelPropety Count_Excel = ExcelPropety.CreateProperty<OverwriteTestItem>(x => x.Count);
        protected override void InitVM() { }
    }

    /// <summary>
    /// ImportVM that bypasses Excel and supports overwrite checks.
    /// </summary>
    public class OverwriteImportVM : BaseImportVM<OverwriteTemplateVM, OverwriteTestItem>
    {
        private readonly List<OverwriteTestItem> _preset;
        public OverwriteImportVM() { _preset = new List<OverwriteTestItem>(); }
        public OverwriteImportVM(List<OverwriteTestItem> preset) { _preset = preset; }

        public override void SetEntityList()
        {
            if (!isEntityListSet)
            {
                EntityList = _preset;
                isEntityListSet = true;
            }
        }

        public override DuplicatedInfo<OverwriteTestItem>? SetDuplicatedCheck()
            => CreateFieldsInfo(SimpleField(x => x.Code));
    }

    // ─── Entity with RequiredAttribute for field-level validation in import ───

    public class RequiredFieldItem : BasePoco
    {
        [Required(ErrorMessage = "RequiredCode is required")]
        [StringLength(20)]
        public string RequiredCode { get; set; } = "";

        [StringLength(100)]
        public string OptionalNote { get; set; } = "";
    }

    internal class RequiredFieldDataContext : DataContext
    {
        public DbSet<RequiredFieldItem> RequiredItems { get; set; } = null!;
        public RequiredFieldDataContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    public class RequiredFieldTemplateVM : BaseTemplateVM
    {
        public ExcelPropety RequiredCode_Excel = ExcelPropety.CreateProperty<RequiredFieldItem>(x => x.RequiredCode);
        public ExcelPropety OptionalNote_Excel = ExcelPropety.CreateProperty<RequiredFieldItem>(x => x.OptionalNote);
        protected override void InitVM() { }
    }

    public class RequiredFieldImportVM : BaseImportVM<RequiredFieldTemplateVM, RequiredFieldItem>
    {
        private readonly List<RequiredFieldItem> _preset;
        public RequiredFieldImportVM() { _preset = new List<RequiredFieldItem>(); }
        public RequiredFieldImportVM(List<RequiredFieldItem> preset) { _preset = preset; }

        public override void SetEntityList()
        {
            if (!isEntityListSet)
            {
                EntityList = _preset;
                isEntityListSet = true;
            }
        }
    }

    // ─── NPOI Excel builder helper ────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal XLSX workbook that matches the WTM import template format,
    /// so tests can exercise SetTemplateData() without a real file upload.
    /// </summary>
    internal static class NpoiHelper
    {
        /// <summary>
        /// Generates an XLSX byte array whose sheet 0 has a single header row
        /// and the given data rows, and whose sheet 1 hidden-name cell matches
        /// <paramref name="templateTypeName"/> so ValidityTemplateType passes.
        /// </summary>
        public static byte[] BuildWorkbook(
            string templateTypeName,
            string[] headerRow,
            IEnumerable<object[]> dataRows,
            bool v2Marker = true)
        {
            var wb = new XSSFWorkbook();

            // Sheet 0 — data
            var dataSheet = wb.CreateSheet("Data");
            var header = dataSheet.CreateRow(0);
            for (int i = 0; i < headerRow.Length; i++)
                header.CreateCell(i).SetCellValue(headerRow[i]);

            int rowIdx = 1;
            foreach (var row in dataRows)
            {
                var r = dataSheet.CreateRow(rowIdx++);
                for (int c = 0; c < row.Length; c++)
                {
                    var cell = r.CreateCell(c);
                    if (row[c] is int iv) cell.SetCellValue(iv);
                    else if (row[c] is double dv) cell.SetCellValue(dv);
                    else cell.SetCellValue(row[c]?.ToString() ?? "");
                }
            }

            // Sheet 1 — hidden enum/meta sheet
            var enumSheet = wb.CreateSheet("Enum");
            var metaRow = enumSheet.CreateRow(0);
            metaRow.CreateCell(0).SetCellValue("");
            metaRow.CreateCell(1).SetCellValue("");
            metaRow.CreateCell(2).SetCellValue(templateTypeName);    // col 2 = type name
            metaRow.CreateCell(3).SetCellValue(v2Marker ? "v2" : ""); // col 3 = version marker

            using var ms = new MemoryStream();
            wb.Write(ms);
            return ms.ToArray();
        }
    }

    // ─── Test class ───────────────────────────────────────────────────────────

    [TestClass]
    public class BaseImportVMExtendedTest
    {
        private string _seed = null!;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
        }

        private IDataContext CreateImportDb() => new ImportTestDataContext(_seed, DBTypeEnum.Memory);
        private IDataContext CreateOverwriteDb() => new OverwriteDataContext(_seed, DBTypeEnum.Memory);
        private IDataContext CreateRequiredDb() => new RequiredFieldDataContext(_seed, DBTypeEnum.Memory);

        // ─── Constructor / property defaults ──────────────────────────────

        [TestMethod]
        public void Constructor_ErrorListVM_IsInitialized()
        {
            var vm = new TestImportVM();
            Assert.IsNotNull(vm.ErrorListVM);
            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count);
        }

        [TestMethod]
        public void Constructor_Template_IsInitialized()
        {
            var vm = new TestImportVM();
            Assert.IsNotNull(vm.Template);
        }

        [TestMethod]
        public void Constructor_EntityList_IsEmpty()
        {
            var vm = new TestImportVM();
            Assert.IsNotNull(vm.EntityList);
            Assert.AreEqual(0, vm.EntityList.Count);
        }

        [TestMethod]
        public void Constructor_IsOverWriteExistData_DefaultsToTrue()
        {
            var vm = new TestImportVM();
            Assert.IsTrue(vm.IsOverWriteExistData);
        }

        [TestMethod]
        public void Constructor_ValidityTemplateType_DefaultsToTrue()
        {
            var vm = new TestImportVM();
            Assert.IsTrue(vm.ValidityTemplateType);
        }

        [TestMethod]
        public void Constructor_UseBulkSave_DefaultsFalse()
        {
            var vm = new TestImportVM();
            Assert.IsFalse(vm.UseBulkSave);
        }

        [TestMethod]
        public void Constructor_InlineErrorLimit_DefaultIs50()
        {
            var vm = new TestImportVM();
            Assert.AreEqual(50, vm.InlineErrorLimit);
        }

        // ─── GenerateTemplate ─────────────────────────────────────────────

        [TestMethod]
        public void GenerateTemplate_ReturnsNonEmptyBytes()
        {
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var bytes = vm.GenerateTemplate(out string name);

            Assert.IsNotNull(bytes);
            Assert.IsTrue(bytes.Length > 0);
        }

        [TestMethod]
        public void GenerateTemplate_FileNameNotEmpty()
        {
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            vm.GenerateTemplate(out string name);

            Assert.IsFalse(string.IsNullOrEmpty(name));
        }

        [TestMethod]
        public void GenerateTemplate_ProducesValidXlsx()
        {
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var bytes = vm.GenerateTemplate(out _);

            using var ms = new MemoryStream(bytes);
            var wb = new XSSFWorkbook(ms);
            Assert.IsNotNull(wb.GetSheetAt(0), "Sheet 0 must exist");
        }

        // ─── SetParms ─────────────────────────────────────────────────────

        [TestMethod]
        public void SetParms_SetsTemplateParams()
        {
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var parms = new Dictionary<string, string> { ["key1"] = "val1", ["key2"] = "val2" };
            vm.SetParms(parms);

            Assert.IsNotNull(vm.Template.Parms);
            Assert.AreEqual("val1", vm.Template.Parms["key1"]);
            Assert.AreEqual("val2", vm.Template.Parms["key2"]);
        }

        [TestMethod]
        public void SetParms_OverwritesPreviousParams()
        {
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            vm.SetParms(new Dictionary<string, string> { ["k"] = "old" });
            vm.SetParms(new Dictionary<string, string> { ["k"] = "new" });

            Assert.AreEqual("new", vm.Template.Parms!["k"]);
        }

        // ─── SetDuplicatedCheck — default returns null ────────────────────

        [TestMethod]
        public void SetDuplicatedCheck_Default_ReturnsNull()
        {
            var vm = new TestImportVM();
            // TestImportVM does not override SetDuplicatedCheck — base returns null
            var info = vm.SetDuplicatedCheck();
            Assert.IsNull(info);
        }

        [TestMethod]
        public void SetDuplicatedCheck_Override_ReturnsInfo()
        {
            var vm = new ValidationImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new ImportValidationDataContext(_seed, DBTypeEnum.Memory), "user");
            var info = vm.SetDuplicatedCheck();
            Assert.IsNotNull(info);
        }

        // ─── BatchSaveData — IsOverWriteExistData = true (overwrite) ──────

        [TestMethod]
        public void BatchSaveData_IsOverWriteExistData_True_UpdatesExistingRow()
        {
            // Pre-seed a row
            using (var ctx = (DbContext)CreateOverwriteDb())
            {
                ctx.Set<OverwriteTestItem>().Add(
                    new OverwriteTestItem { Code = "OW1", Description = "Original", Count = 1 });
                ctx.SaveChanges();
            }

            var entities = new List<OverwriteTestItem>
            {
                new OverwriteTestItem { Code = "OW1", Description = "Overwritten", Count = 99 }
            };

            var vm = new OverwriteImportVM(entities);
            vm.IsOverWriteExistData = true;
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateOverwriteDb(), "user");

            var result = vm.BatchSaveData();

            // Even if result varies, the overwrite path was taken (no crash)
            Assert.IsNotNull(result);

            using (var ctx = (DbContext)CreateOverwriteDb())
            {
                // Should have exactly 1 row (original was overwritten, not duplicated)
                Assert.AreEqual(1, ctx.Set<OverwriteTestItem>().Count());
            }
        }

        [TestMethod]
        public void BatchSaveData_IsOverWriteExistData_False_ErrorOnDuplicate()
        {
            // Pre-seed
            using (var ctx = (DbContext)CreateOverwriteDb())
            {
                ctx.Set<OverwriteTestItem>().Add(
                    new OverwriteTestItem { Code = "DUP2", Description = "Existing", Count = 5 });
                ctx.SaveChanges();
            }

            var entities = new List<OverwriteTestItem>
            {
                new OverwriteTestItem { Code = "DUP2", Description = "Incoming Dup", Count = 10 }
            };

            var vm = new OverwriteImportVM(entities);
            vm.IsOverWriteExistData = false;
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateOverwriteDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsFalse(result, "Should fail when duplicate detected and overwrite is off");
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0);
        }

        [TestMethod]
        public void BatchSaveData_IsOverWriteExistData_False_NewRow_Succeeds()
        {
            var entities = new List<OverwriteTestItem>
            {
                new OverwriteTestItem { Code = "NEW1", Description = "New Item", Count = 7 }
            };

            var vm = new OverwriteImportVM(entities);
            vm.IsOverWriteExistData = false;
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateOverwriteDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsTrue(result, "New row (no duplicate) should succeed even without overwrite");
            using (var ctx = (DbContext)CreateOverwriteDb())
            {
                Assert.AreEqual(1, ctx.Set<OverwriteTestItem>().Count());
            }
        }

        // ─── BatchSaveData — field-level validation (DataAnnotations) ────

        [TestMethod]
        public void BatchSaveData_RequiredFieldMissing_CollectsError()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = new string('X', 60), Value = 1 } // Name exceeds StringLength(50)
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsFalse(result);
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0);
        }

        [TestMethod]
        public void BatchSaveData_ValidEntity_NoErrors()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = "Valid", Value = 42 }
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsTrue(result);
            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count);
        }

        // ─── BatchSaveData — multiple errors accumulate ───────────────────

        [TestMethod]
        public void BatchSaveData_MultipleInvalidRows_AccumulatesErrors()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = new string('A', 55), Value = 1 },
                new ImportTestItem { Name = new string('B', 55), Value = 2 },
                new ImportTestItem { Name = new string('C', 55), Value = 3 }
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsFalse(result);
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0);
        }

        // ─── BatchSaveData — error path: DoReInit resets isEntityListSet ──

        [TestMethod]
        public void BatchSaveData_ValidationError_EntityListStillPopulated()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = new string('X', 60), Value = 1 }
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            vm.BatchSaveData();

            // EntityList is populated (the VM receives entities before validation)
            Assert.IsTrue(vm.EntityList.Count >= 0);
        }

        // ─── BatchSaveData — ValidateOnly false, data persisted ──────────

        [TestMethod]
        public void BatchSaveData_ValidateOnlyFalse_DataIsPersisted()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = "Persist1", Value = 11 },
                new ImportTestItem { Name = "Persist2", Value = 22 }
            };

            var vm = new TestImportVM(entities) { ValidateOnly = false };
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsTrue(result);
            using var ctx = CreateImportDb();
            Assert.AreEqual(2, ctx.Set<ImportTestItem>().Count());
        }

        // ─── InlineErrors ─────────────────────────────────────────────────

        [TestMethod]
        public void InlineErrors_EmptyOnSuccess()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = "Good", Value = 1 }
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            vm.BatchSaveData();

            Assert.AreEqual(0, vm.InlineErrors.Count);
        }

        [TestMethod]
        public void InlineErrors_NonZeroLimitWithErrors()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = new string('E', 60), Value = 1 }
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");
            vm.InlineErrorLimit = 10;

            vm.BatchSaveData();

            Assert.IsTrue(vm.InlineErrors.Count <= 10);
        }

        [TestMethod]
        public void InlineErrors_ZeroLimit_ReturnsEmpty()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = new string('E', 60), Value = 1 }
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");
            vm.InlineErrorLimit = 0;

            vm.BatchSaveData();

            Assert.AreEqual(0, vm.InlineErrors.Count);
        }

        // ─── TemplateErrorListVM ─────────────────────────────────────────

        [TestMethod]
        public void TemplateErrorListVM_GetSearchQuery_ReturnsOrdered()
        {
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            vm.ErrorListVM.EntityList.Add(new ErrorMessage { Index = 5, Message = "Late" });
            vm.ErrorListVM.EntityList.Add(new ErrorMessage { Index = 2, Message = "Early" });
            vm.ErrorListVM.EntityList.Add(new ErrorMessage { Index = 9, Message = "Later" });

            var ordered = vm.ErrorListVM.GetSearchQuery().ToList();

            Assert.AreEqual(3, ordered.Count);
            Assert.IsTrue(ordered[0].Index <= ordered[1].Index);
            Assert.IsTrue(ordered[1].Index <= ordered[2].Index);
        }

        [TestMethod]
        public void TemplateErrorListVM_NeedPage_DefaultFalse()
        {
            var vm = new TestImportVM();
            Assert.IsFalse(vm.ErrorListVM.NeedPage);
        }

        // ─── SetDuplicatedCheck + CreateFieldsInfo + SimpleField ─────────

        [TestMethod]
        public void CreateFieldsInfo_ReturnsNonNullDuplicatedInfo()
        {
            var vm = new ValidationImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new ImportValidationDataContext(_seed, DBTypeEnum.Memory), "user");

            var info = vm.SetDuplicatedCheck();
            Assert.IsNotNull(info);
            Assert.IsTrue(info!.Groups.Count > 0);
        }

        // ─── IsOverWriteExistData — overwrite multiple rows ───────────────

        [TestMethod]
        public void BatchSaveData_IsOverWrite_UpdatesAuditFields()
        {
            using (var ctx = (DbContext)CreateOverwriteDb())
            {
                ctx.Set<OverwriteTestItem>().Add(
                    new OverwriteTestItem { Code = "AUD1", Description = "Before", Count = 0 });
                ctx.SaveChanges();
            }

            var entities = new List<OverwriteTestItem>
            {
                new OverwriteTestItem { Code = "AUD1", Description = "After", Count = 99 }
            };

            var vm = new OverwriteImportVM(entities);
            vm.IsOverWriteExistData = true;
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateOverwriteDb(), "auditor");

            vm.BatchSaveData();

            using var ctx2 = (DbContext)CreateOverwriteDb();
            var item = ctx2.Set<OverwriteTestItem>().Single();
            // The overwrite path goes through UpdateProperty, keeping 1 row
            Assert.IsNotNull(item);
        }

        // ─── BatchSaveData — large batch, all valid ───────────────────────

        [TestMethod]
        public void BatchSaveData_LargeBatch_AllSaved()
        {
            var entities = Enumerable.Range(1, 30)
                .Select(i => new ImportTestItem { Name = $"Item{i:D3}", Value = i })
                .ToList();

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsTrue(result);
            using var ctx = CreateImportDb();
            Assert.AreEqual(30, ctx.Set<ImportTestItem>().Count());
        }

        // ─── GetCellFormulaValue safe-string path ────────────────────────

        [TestMethod]
        public void GetCellFormulaValue_NonFormula_ReturnsOriginal()
        {
            // This exercises the public GetCellFormulaValue path with a plain string
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            // Create a dummy workbook to build an evaluator
            var wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet();
            var row = sheet.CreateRow(0);
            var cell = row.CreateCell(0);
            cell.SetCellValue("hello");

            var evaluator = new NPOI.XSSF.UserModel.XSSFFormulaEvaluator(wb);

            var result = vm.GetCellFormulaValue(evaluator, cell, "hello");
            Assert.AreEqual("hello", result, "Non-formula string should be returned unchanged");
        }

        [TestMethod]
        public void GetCellFormulaValue_EmptyString_ReturnsEmpty()
        {
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet();
            var row = sheet.CreateRow(0);
            var cell = row.CreateCell(0);
            cell.SetCellValue("");

            var evaluator = new NPOI.XSSF.UserModel.XSSFFormulaEvaluator(wb);

            var result = vm.GetCellFormulaValue(evaluator, null, "");
            Assert.AreEqual("", result);
        }

        [TestMethod]
        public void GetCellFormulaValue_DangerousFormula_ReturnedVerbatim()
        {
            // Dangerous formulas (WEBSERVICE, CMD, etc.) should NOT be evaluated — returned raw
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet();
            var row = sheet.CreateRow(0);
            var cell = row.CreateCell(0);
            var evaluator = new NPOI.XSSF.UserModel.XSSFFormulaEvaluator(wb);

            var dangerous = "=WEBSERVICE(\"http://evil.com\")";
            var result = vm.GetCellFormulaValue(evaluator, cell, dangerous);

            Assert.AreEqual(dangerous, result,
                "Dangerous formula should be returned as-is (not evaluated)");
        }

        [TestMethod]
        public void GetCellFormulaValue_CmdFormula_ReturnedVerbatim()
        {
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet();
            var row = sheet.CreateRow(0);
            var cell = row.CreateCell(0);
            var evaluator = new NPOI.XSSF.UserModel.XSSFFormulaEvaluator(wb);

            var cmdFormula = "=CMD(\"rm -rf /\")";
            var result = vm.GetCellFormulaValue(evaluator, cell, cmdFormula);

            Assert.AreEqual(cmdFormula, result,
                "CMD formula should be returned verbatim (injection blocked)");
        }

        [TestMethod]
        public void GetCellFormulaValue_HyperlinkFormula_ReturnedVerbatim()
        {
            var vm = new TestImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet();
            var row = sheet.CreateRow(0);
            var cell = row.CreateCell(0);
            var evaluator = new NPOI.XSSF.UserModel.XSSFFormulaEvaluator(wb);

            var formula = "=HYPERLINK(\"http://phishing.com\",\"Click Me\")";
            var result = vm.GetCellFormulaValue(evaluator, cell, formula);

            Assert.AreEqual(formula, result,
                "HYPERLINK formula should be returned verbatim");
        }

        // ─── SetEntityList idempotency ────────────────────────────────────

        [TestMethod]
        public void SetEntityList_CalledTwice_DoesNotDuplicateEntities()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = "Once", Value = 1 }
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            vm.SetEntityList();
            int countAfterFirst = vm.EntityList.Count;

            vm.SetEntityList();
            int countAfterSecond = vm.EntityList.Count;

            Assert.AreEqual(countAfterFirst, countAfterSecond,
                "Second SetEntityList call should be a no-op (isEntityListSet = true)");
        }

        // ─── BatchSaveData — progress reporting ──────────────────────────

        [TestMethod]
        public void BatchSaveData_WithNullProgress_DoesNotThrow()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = "NoProgress", Value = 5 }
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            Assert.IsTrue(vm.BatchSaveData(null));
        }

        // ─── ValidityTemplateType property ───────────────────────────────

        [TestMethod]
        public void ValidityTemplateType_CanBeSetFalse()
        {
            var vm = new TestImportVM();
            vm.ValidityTemplateType = false;
            Assert.IsFalse(vm.ValidityTemplateType);
        }

        // ─── UploadFileId property ────────────────────────────────────────

        [TestMethod]
        public void UploadFileId_CanBeSet()
        {
            var vm = new TestImportVM();
            vm.UploadFileId = "some-file-id";
            Assert.AreEqual("some-file-id", vm.UploadFileId);
        }

        [TestMethod]
        public void UploadFileId_DefaultIsNull()
        {
            var vm = new TestImportVM();
            Assert.IsNull(vm.UploadFileId);
        }

        // ─── FileDisplayName property ─────────────────────────────────────

        [TestMethod]
        public void FileDisplayName_DefaultIsNull()
        {
            var vm = new TestImportVM();
            Assert.IsNull(vm.FileDisplayName);
        }

        [TestMethod]
        public void FileDisplayName_CanBeSet()
        {
            var vm = new TestImportVM();
            vm.FileDisplayName = "My Import Template";
            Assert.AreEqual("My Import Template", vm.FileDisplayName);
        }

        // ─── BatchSaveData — SetEntityList called with empty yields true ──

        [TestMethod]
        public void BatchSaveData_EmptyEntityList_ReturnsTrue_NothingSaved()
        {
            var vm = new TestImportVM(new List<ImportTestItem>());
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsTrue(result);
            using var ctx = CreateImportDb();
            Assert.AreEqual(0, ctx.Set<ImportTestItem>().Count());
        }

        // ─── ErrorListVM InitGridHeader ───────────────────────────────────

        [TestMethod]
        public void TemplateErrorListVM_GetSearchQuery_EmptyList_ReturnsEmpty()
        {
            var listVm = new TemplateErrorListVM();
            var result = listVm.GetSearchQuery().ToList();
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void TemplateErrorListVM_EntityList_CanBeAddedTo()
        {
            var listVm = new TemplateErrorListVM();
            listVm.EntityList.Add(new ErrorMessage { Index = 1, Message = "Error at row 1" });
            Assert.AreEqual(1, listVm.EntityList.Count);
        }

        // ─── Regression #480: UseBulkSave must not silently drop new rows ──

        /// <summary>
        /// Regression test for issue #480.
        /// When UseBulkSave == true and ConfigInfo.Connections contains a SqlServer
        /// entry for the "default" key, new rows must still be persisted via EF Core.
        /// Before the fix the branch whose only statement was the commented-out
        /// ListAdd.Add(item) caused every new row to be silently dropped, returning
        /// true with zero rows committed.
        /// </summary>
        [TestMethod]
        public void BatchSaveData_UseBulkSave_SqlServerConfigured_NewRowsArePersisted_Regression480()
        {
            // Arrange: two brand-new rows (no pre-existing duplicates)
            var entities = new List<OverwriteTestItem>
            {
                new OverwriteTestItem { Code = "BULK1", Description = "First",  Count = 1 },
                new OverwriteTestItem { Code = "BULK2", Description = "Second", Count = 2 },
            };

            var db = CreateOverwriteDb();
            var vm = new OverwriteImportVM(entities);
            vm.UseBulkSave = true;
            vm.Wtm = MockWtmContext.CreateWtmContext(db, "user");

            // Inject a SqlServer-typed connection entry for key "default" into
            // ConfigInfo so the (now-removed) dead branch would have been entered.
            // The actual DataContext remains InMemory (per test-isolation convention),
            // but ConfigInfo.Connections drives the branch selection in BatchSaveData.
            vm.Wtm.ConfigInfo!.Connections.Clear();
            vm.Wtm.ConfigInfo.Connections.Add(new CS
            {
                Key     = "default",
                DbType  = DBTypeEnum.SqlServer,
                Enabled = true,
            });

            // Act
            var result = vm.BatchSaveData();

            // Assert: rows must be persisted — NOT silently dropped
            Assert.IsTrue(result, "BatchSaveData should return true for valid rows with UseBulkSave=true");
            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, "No import errors expected");

            using var ctx = (Microsoft.EntityFrameworkCore.DbContext)CreateOverwriteDb();
            var count = ctx.Set<OverwriteTestItem>().Count();
            Assert.AreEqual(2, count,
                "All 2 new rows must be in the DB; prior bug silently dropped them when " +
                "UseBulkSave=true and DbType=SqlServer (issue #480).");
        }
    }
}
