#nullable enable
// Tests for three LOW bugs fixed in Issue #150.
//
// L3: BaseImportVM workbook memory leak — implements IDisposable, disposes XSSFWorkbook,
//     no double-dispose, GetErrorJson uses local workbook instead of overwriting field.
// L4: SetEntityData hardcoded rowIndex=2 — now uses item.ExcelIndex for correct row in errors.
// L12: ExcelPropety culture-sensitive decimal/DateTime TryParse — fixed to InvariantCulture.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ═══════════════════════════════════════════════════════════════════════════
    // L3 — IDisposable
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Minimal concrete subclass used to access the protected xssfworkbook field.
    /// </summary>
    internal class DisposableTestImportVM : BaseImportVM<ImportTestTemplateVM, ImportTestItem>
    {
        protected override void InitVM() { }

        /// <summary>Exposes the protected xssfworkbook field for assertions.</summary>
        public NPOI.XSSF.UserModel.XSSFWorkbook? PublicWorkbook
        {
            get
            {
                var fi = typeof(BaseImportVM<ImportTestTemplateVM, ImportTestItem>)
                    .GetField("xssfworkbook",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                return fi?.GetValue(this) as NPOI.XSSF.UserModel.XSSFWorkbook;
            }
            set
            {
                var fi = typeof(BaseImportVM<ImportTestTemplateVM, ImportTestItem>)
                    .GetField("xssfworkbook",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                fi?.SetValue(this, value);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // L4 — rowIndex from ExcelIndex
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Import VM whose TemplateData is pre-seeded so SetEntityData runs with
    /// known ExcelIndex values (bypasses file-loading machinery).
    /// </summary>
    internal class RowIndexImportVM : BaseImportVM<ImportTestTemplateVM, ImportTestItem>
    {
        private readonly List<ImportTestTemplateVM> _rows;

        public RowIndexImportVM(List<ImportTestTemplateVM> rows)
        {
            _rows = rows;
        }

        protected override void InitVM() { }

        public override void SetTemplateData()
        {
            TemplateData = _rows;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Test class
    // ═══════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class ImportLowBugFixTests
    {
        // ─── L3: IDisposable ────────────────────────────────────────────────────

        [TestMethod]
        [Description("L3: BaseImportVM must implement IDisposable")]
        public void L3_BaseImportVM_ImplementsIDisposable()
        {
            var vm = new DisposableTestImportVM();
            Assert.IsInstanceOfType(vm, typeof(IDisposable),
                "BaseImportVM<T,P> must implement IDisposable.");
        }

        [TestMethod]
        [Description("L3: Dispose() on a VM with no workbook must not throw")]
        public void L3_Dispose_WithNullWorkbook_DoesNotThrow()
        {
            var vm = new DisposableTestImportVM();
            // xssfworkbook is null — Dispose must be a no-op
            vm.Dispose(); // must not throw
        }

        [TestMethod]
        [Description("L3: Dispose() when workbook is set must set the field to null")]
        public void L3_Dispose_WithWorkbook_SetsFieldNull()
        {
            var vm = new DisposableTestImportVM();
            // Assign a real (empty) workbook so we have something to dispose
            vm.PublicWorkbook = new NPOI.XSSF.UserModel.XSSFWorkbook();

            vm.Dispose();

            Assert.IsNull(vm.PublicWorkbook,
                "xssfworkbook field must be null after Dispose().");
        }

        [TestMethod]
        [Description("L3: Calling Dispose() twice must not throw (double-dispose guard)")]
        public void L3_Dispose_CalledTwice_DoesNotThrow()
        {
            var vm = new DisposableTestImportVM();
            vm.PublicWorkbook = new NPOI.XSSF.UserModel.XSSFWorkbook();

            vm.Dispose();
            vm.Dispose(); // second call must not throw
        }

        // ─── L4: correct Excel row number in SetEntityData errors ──────────────

        [TestMethod]
        [Description("L4: FormatData error on a row other than 2 must report the real row number in ErrorListVM")]
        public void L4_SetEntityData_ErrorRowIndex_ReflectsActualExcelIndex()
        {
            // Build a template row at ExcelIndex 7 with a FormatData delegate that always errors.
            var row7 = new ImportTestTemplateVM();
            row7.ExcelIndex = 7;

            var nameEp = new ExcelPropety
            {
                FieldName = "Name",
                FieldDisplayName = "Name",
                DataType = ColumnDataType.Text,
                IsNullAble = false,
                Value = "Alice",
                // FormatData that always produces an error so SetEntityFieldValue
                // adds an ErrorMessage with the rowIndex it received.
                FormatData = (excelValue, template) =>
                {
                    var pr = new ProcessResult();
                    pr.EntityValues.Add(new EntityValue
                    {
                        FieldName = "Name",
                        FieldValue = "Alice",
                        ErrorMsg = "forced-error"
                    });
                    return pr;
                }
            };

            var valEp = new ExcelPropety
            {
                FieldName = "Value",
                FieldDisplayName = "Value",
                DataType = ColumnDataType.Number,
                IsNullAble = false,
                Value = 0
            };

            // Use reflection to inject ExcelPropety instances into the template row.
            var nameField = typeof(ImportTestTemplateVM).GetField("Name_Excel")!;
            var valField  = typeof(ImportTestTemplateVM).GetField("Value_Excel")!;
            nameField.SetValue(row7, nameEp);
            valField.SetValue(row7, valEp);

            var vm = new RowIndexImportVM(new List<ImportTestTemplateVM> { row7 });
            vm.SetTemplateData();  // pre-seed TemplateData
            vm.SetEntityData();

            // The error emitted by SetEntityFieldValue must carry ExcelIndex = 7 (not 2).
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0,
                "SetEntityData must have produced at least one error (forced by FormatData).");

            var firstError = vm.ErrorListVM.EntityList[0];
            Assert.AreEqual(7L, firstError.Index,
                "Error Index must be 7 (item.ExcelIndex), not the old hard-coded 2.");
        }

        // ─── L12: InvariantCulture in ExcelPropety.ValueValidity ───────────────

        [TestMethod]
        [Description("L12: decimal '1.5' must parse correctly under de-DE culture")]
        public void L12_DecimalParse_DotSeparator_WorksUnderDeDeCulture()
        {
            var originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                var prop = ExcelPropety.CreateProperty<ImportTestItem>(x => x.Value);
                // Force DataType to Float (decimal)
                prop.DataType = ColumnDataType.Float;
                prop.IsNullAble = false;

                var errors = new List<ErrorMessage>();
                prop.ValueValidity("1.5", errors, rowIndex: 3);

                Assert.AreEqual(0, errors.Count,
                    "Decimal '1.5' (dot separator) must parse without error under de-DE culture.");
                Assert.AreEqual(1.5m, Convert.ToDecimal(prop.Value, CultureInfo.InvariantCulture),
                    "Parsed value must be 1.5.");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
            }
        }

        [TestMethod]
        [Description("L12: ISO date '2024-01-15' must parse correctly under de-DE culture")]
        public void L12_DateParse_IsoFormat_WorksUnderDeDeCulture()
        {
            var originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.CreatedDate);
                prop.IsNullAble = false;

                var errors = new List<ErrorMessage>();
                prop.ValueValidity("2024-01-15", errors, rowIndex: 4);

                Assert.AreEqual(0, errors.Count,
                    "ISO date '2024-01-15' must parse without error under de-DE culture.");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
            }
        }
    }
}
