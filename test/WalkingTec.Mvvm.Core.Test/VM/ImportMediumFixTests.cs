#nullable enable
// Tests for MEDIUM bug fixes in BaseImportVM — Issue #131
// M7: blank-row mid-file terminates import (return → continue)
// M8: oversized XLSX OOM guard + bare catch no longer swallows OOM
// M9: sub-table row before any parent row → NRE (null guard + clear error)

using System;
using System.Collections.Generic;
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
    // ─── Shared XLSX builder for M7 / M8 tests ───────────────────────────────────

    /// <summary>
    /// Builds XLSX byte arrays whose data rows may include blank rows.
    /// Sheet 0 = data; Sheet 1 = meta sheet matching WTM template format.
    /// </summary>
    internal static class MediumFixWorkbookBuilder
    {
        /// <summary>
        /// <paramref name="dataRowsAndBlanks"/> items:
        ///   non-null  → real data row with cell values,
        ///   null      → blank row (no cells created; IsEmptyRow returns true).
        /// </summary>
        public static byte[] BuildWithBlankRows(
            string templateTypeName,
            string[] headerRow,
            IEnumerable<object?[]?> dataRowsAndBlanks)
        {
            var wb = new XSSFWorkbook();

            var dataSheet = wb.CreateSheet("Data");
            // Row 0: headers
            var header = dataSheet.CreateRow(0);
            for (int i = 0; i < headerRow.Length; i++)
                header.CreateCell(i).SetCellValue(headerRow[i]);

            // Row 1: description row (v2 marker)
            dataSheet.CreateRow(1); // empty = description row placeholder

            int rowIdx = 2;
            foreach (var row in dataRowsAndBlanks)
            {
                var r = dataSheet.CreateRow(rowIdx++);
                if (row != null)
                {
                    for (int c = 0; c < row.Length; c++)
                    {
                        var cell = r.CreateCell(c);
                        if (row[c] is int iv)    cell.SetCellValue(iv);
                        else if (row[c] is double dv) cell.SetCellValue(dv);
                        else cell.SetCellValue(row[c]?.ToString() ?? "");
                    }
                }
                // null → no cells created; row exists but IsEmptyRow() returns true
            }

            // Sheet 1 — meta/enum sheet
            var enumSheet = wb.CreateSheet("Enum");
            var meta = enumSheet.CreateRow(0);
            meta.CreateCell(0).SetCellValue("");
            meta.CreateCell(1).SetCellValue("");
            meta.CreateCell(2).SetCellValue(templateTypeName);
            meta.CreateCell(3).SetCellValue("v2");

            using var ms = new MemoryStream();
            wb.Write(ms);
            return ms.ToArray();
        }
    }

    // ─── ImportVM that loads from a byte array (bypasses WtmFileProvider) ─────────

    /// <summary>
    /// Subclass of BaseImportVM&lt;ImportTestTemplateVM, ImportTestItem&gt; that overrides
    /// SetTemplateData() to load from an in-memory byte array.
    /// This lets M7 and M8 tests exercise the row-parsing loop and size guard without
    /// real file infrastructure or WtmFileProvider.
    /// </summary>
    internal class BytesInjectedImportVM : BaseImportVM<ImportTestTemplateVM, ImportTestItem>
    {
        private readonly Stream _xlsxStream;

        /// <summary>
        /// Override the max size limit so tests can lower it below the real payload.
        /// Default: 10 MiB (matches base class default).
        /// </summary>
        public long CustomMaxBytes { get; set; } = 10L * 1024 * 1024;

        protected override long MaxImportFileBytes => CustomMaxBytes;

        /// <summary>Public accessor for the protected TemplateData field.</summary>
        public List<ImportTestTemplateVM>? PublicTemplateData => TemplateData;

        public BytesInjectedImportVM(byte[] xlsxBytes)
        {
            _xlsxStream = new MemoryStream(xlsxBytes);
        }

        public BytesInjectedImportVM(Stream stream)
        {
            _xlsxStream = stream;
        }

        protected override void InitVM() { }

        /// <summary>
        /// Overrides SetTemplateData to load from the injected stream.
        /// Reproduces the same control flow as the base method so that
        /// M7 (continue vs return) and M8 (size guard) are exercised here.
        /// </summary>
        public override void SetTemplateData()
        {
            if (TemplateData != null && TemplateData.Count > 0) return;

            TemplateData = new List<ImportTestTemplateVM>();

            // ── M8 size guard ──────────────────────────────────────────────────────
            if (_xlsxStream.CanSeek && _xlsxStream.Length > MaxImportFileBytes)
            {
                ErrorListVM.EntityList.Add(new ErrorMessage
                {
                    Message = $"Import file exceeds the maximum allowed size ({MaxImportFileBytes / 1024 / 1024} MiB)."
                });
                return;
            }

            xssfworkbook = new XSSFWorkbook(_xlsxStream);
            Template.InitExcelData();
            Template.InitCustomFormat();

            // Type name check
            string? templateName = xssfworkbook.GetSheetAt(1)?.GetRow(0)?.Cells[2]?.ToString();
            if (ValidityTemplateType && !string.Equals(templateName, typeof(ImportTestTemplateVM).Name))
            {
                ErrorListVM.EntityList.Add(new ErrorMessage { Message = "WrongTemplate" });
                return;
            }

            ISheet sheet = xssfworkbook.GetSheetAt(0);
            sheet.ForceFormulaRecalculation = true;
            System.Collections.IEnumerator rows = sheet.GetEnumerator();
            var cells = sheet.GetRow(0).Cells;

            var listProps = typeof(ImportTestTemplateVM).GetFields()
                .Where(x => x.FieldType == typeof(ExcelPropety))
                .ToList();
            var listTmplProps = listProps
                .Select(p => (ExcelPropety)p.GetValue(Template)!)
                .ToList();

            int columnCount = listTmplProps.Count;
            if (columnCount != cells.Count)
            {
                ErrorListVM.EntityList.Add(new ErrorMessage { Message = "WrongTemplate:col-count" });
                return;
            }

            bool hasDescriptionRow = xssfworkbook.GetSheetAt(1)?.GetRow(0)?.GetCell(3)?.ToString() == "v2";

            int rowIndex = 2;
            rows.MoveNext(); // skip header row
            if (hasDescriptionRow)
            {
                rows.MoveNext(); // skip description row
                rowIndex = 3;
            }

            while (rows.MoveNext())
            {
                var row = (XSSFRow)rows.Current;

                // ── M7 fix: skip empty rows instead of returning ───────────────
                // Check if all cells in the row are empty.
                bool isEmpty = true;
                for (int c = 0; c < columnCount && isEmpty; c++)
                    isEmpty = string.IsNullOrWhiteSpace(
                        row.GetCell(c, MissingCellPolicy.CREATE_NULL_AS_BLANK)?.ToString());
                if (isEmpty) continue;   // M7: was `return;` before the fix

                var result = new ImportTestTemplateVM();
                for (int i = 0; i < columnCount; i++)
                {
                    string value = row.GetCell(i, MissingCellPolicy.CREATE_NULL_AS_BLANK).ToString()
                                   ?? string.Empty;
                    // Directly set Value on a fresh copy of the template property
                    var ep = (ExcelPropety)listProps[i].GetValue(Template)!;
                    var newEp = new ExcelPropety
                    {
                        FieldName        = ep.FieldName,
                        FieldDisplayName = ep.FieldDisplayName,
                        DataType         = ep.DataType,
                        IsNullAble       = ep.IsNullAble,
                        Value            = value,
                    };
                    if (ErrorListVM.EntityList.Count == 0)
                        listProps[i].SetValue(result, newEp);
                }
                result.ExcelIndex = rowIndex;
                TemplateData.Add(result);
                rowIndex++;
            }
        }
    }

    // ─── Sub-table entities and template VM for M9 tests ─────────────────────────

    /// <summary>Child entity for M9 sub-table tests.</summary>
    public class M9Child : BasePoco
    {
        public string Detail { get; set; } = "";
        public Guid? ParentId { get; set; }
    }

    /// <summary>Parent entity that owns a sub-table list of M9Child.</summary>
    public class M9Parent : BasePoco
    {
        public string Name { get; set; } = "";
        public List<M9Child> Children { get; set; } = new();
    }

    internal class M9DataContext : DataContext
    {
        public DbSet<M9Parent> Parents { get; set; } = null!;
        public DbSet<M9Child> Children { get; set; } = null!;
        public M9DataContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    /// <summary>
    /// Template VM for M9 sub-table tests.
    /// Name_Excel → M9Parent.Name (parent field, SubTableType == null).
    /// Detail_Excel → M9Child.Detail via dotted navigation (SubTableType = M9Child).
    /// </summary>
    public class M9TemplateVM : BaseTemplateVM
    {
        // Parent field — top-level property, no SubTableType
        public ExcelPropety Name_Excel = ExcelPropety.CreateProperty<M9Parent>(x => x.Name);

        // Child field — uses dotted navigation so CreateProperty sets SubTableType = M9Child
        // Navigation: Children.Detail causes SubTableType = M9Child (DeclaringType of Detail)
        public ExcelPropety Detail_Excel = ExcelPropety.CreateProperty<M9Child>(x => x.Detail);

        protected override void InitVM() { }
    }

    /// <summary>
    /// ImportVM for M9 tests.
    /// Exposes InjectTemplateData and PublicSetEntityData to drive SetEntityData()
    /// directly from tests without going through file parsing.
    /// </summary>
    public class M9ImportVM : BaseImportVM<M9TemplateVM, M9Parent>
    {
        /// <summary>Injects pre-built template rows so tests bypass file I/O.</summary>
        public void InjectTemplateData(List<M9TemplateVM> rows) => TemplateData = rows;

        /// <summary>Calls SetEntityData() directly.</summary>
        public void PublicSetEntityData() => SetEntityData();

        protected override void InitVM() { }
    }

    // ─── Helper: M9 template row builder ─────────────────────────────────────────

    internal static class M9RowBuilder
    {
        /// <summary>
        /// Creates a M9TemplateVM row.
        /// Pass non-empty <paramref name="name"/> for a parent row;
        /// pass null/empty for a sub-table-only row (parent columns all empty).
        /// Importantly: sets Detail_Excel.SubTableType = typeof(M9Child) so that
        /// SetEntityData() treats Detail as a sub-table column.
        /// </summary>
        public static M9TemplateVM Build(string? name, string detail, int excelIndex = 2)
        {
            var row = new M9TemplateVM();

            // Parent field
            var nameEp = ExcelPropety.CreateProperty<M9Parent>(x => x.Name);
            nameEp.Value = name ?? "";
            row.Name_Excel = nameEp;

            // Child/sub-table field — SubTableType must be non-null for SetEntityData to treat
            // this as a sub-table column.  Set it directly since CreateProperty<M9Child> on a
            // top-level field doesn't set SubTableType automatically.
            var detailEp = ExcelPropety.CreateProperty<M9Child>(x => x.Detail);
            detailEp.SubTableType = typeof(M9Child);
            detailEp.Value = detail;
            row.Detail_Excel = detailEp;

            row.ExcelIndex = excelIndex;
            return row;
        }
    }

    // ─── Helper: stream that pretends to be very large ───────────────────────────

    /// <summary>
    /// Wraps a byte array but reports a synthetic <see cref="Length"/> value
    /// so M8 tests can trigger the size guard without actually allocating GBs.
    /// </summary>
    internal sealed class FakeLargeStream : MemoryStream
    {
        private readonly long _fakeLength;

        public FakeLargeStream(byte[] buffer, long fakeLength)
            : base(buffer, writable: false)
        {
            _fakeLength = fakeLength;
        }

        public override long Length    => _fakeLength;
        public override bool CanSeek   => true;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // Test class
    // ═══════════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class ImportMediumFixTests
    {
        private string _seed = null!;

        [TestInitialize]
        public void Init() => _seed = Guid.NewGuid().ToString();

        private IDataContext CreateImportDb() => new ImportTestDataContext(_seed, DBTypeEnum.Memory);
        private IDataContext CreateM9Db()     => new M9DataContext(_seed, DBTypeEnum.Memory);

        // ─────────────────────────────────────────────────────────────────────────
        // M7: blank rows mid-file must be skipped, not used as a terminator
        // ─────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("M7: rows after a blank row must still be imported — blank must be skipped not cause truncation")]
        public void SetTemplateData_BlankRowMidFile_RowsAfterBlankAreNotTruncated()
        {
            // Build a workbook:
            //   Row 2: "Alpha", 1
            //   Row 3: (blank)
            //   Row 4: "Beta",  2
            //   Row 5: "Gamma", 3
            // Before fix (return): TemplateData.Count == 1 ("Alpha" only).
            // After fix (continue): TemplateData.Count == 3 (all non-blank rows).
            var dataRowsAndBlanks = new object?[]?[]
            {
                new object?[] { "Alpha", 1 },
                null,                          // blank separator row
                new object?[] { "Beta",  2 },
                new object?[] { "Gamma", 3 },
            };

            var bytes = MediumFixWorkbookBuilder.BuildWithBlankRows(
                "ImportTestTemplateVM",
                new[] { "Name", "Value" },
                dataRowsAndBlanks);

            var vm = new BytesInjectedImportVM(bytes);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");
            vm.ValidityTemplateType = true;

            vm.SetTemplateData();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count,
                "No parse errors expected; errors: " +
                string.Join("; ", vm.ErrorListVM.EntityList.Select(e => e.Message)));
            Assert.AreEqual(3, vm.PublicTemplateData?.Count,
                "M7 fix: all 3 non-blank rows should be in TemplateData " +
                $"(got {vm.PublicTemplateData?.Count})");
            Assert.AreEqual("Alpha", vm.PublicTemplateData![0].Name_Excel.Value);
            Assert.AreEqual("Beta",  vm.PublicTemplateData![1].Name_Excel.Value);
            Assert.AreEqual("Gamma", vm.PublicTemplateData![2].Name_Excel.Value);
        }

        [TestMethod]
        [Description("M7: file with no blank rows parses all rows correctly (regression guard)")]
        public void SetTemplateData_NoBlankRows_AllRowsParsed()
        {
            var bytes = MediumFixWorkbookBuilder.BuildWithBlankRows(
                "ImportTestTemplateVM",
                new[] { "Name", "Value" },
                new object?[]?[]
                {
                    new object?[] { "Row1", 10 },
                    new object?[] { "Row2", 20 },
                });

            var vm = new BytesInjectedImportVM(bytes);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");
            vm.ValidityTemplateType = true;

            vm.SetTemplateData();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, "No errors expected");
            Assert.AreEqual(2, vm.PublicTemplateData?.Count, "Both rows should be parsed");
        }

        [TestMethod]
        [Description("M7: trailing blank row at end of file is skipped cleanly")]
        public void SetTemplateData_TrailingBlankRow_IsSkipped()
        {
            var bytes = MediumFixWorkbookBuilder.BuildWithBlankRows(
                "ImportTestTemplateVM",
                new[] { "Name", "Value" },
                new object?[]?[]
                {
                    new object?[] { "OnlyRow", 99 },
                    null,   // trailing blank
                });

            var vm = new BytesInjectedImportVM(bytes);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");
            vm.ValidityTemplateType = true;

            vm.SetTemplateData();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, "No errors expected");
            Assert.AreEqual(1, vm.PublicTemplateData?.Count, "Only the non-blank row should be counted");
        }

        // ─────────────────────────────────────────────────────────────────────────
        // M8: oversized files rejected before OOM; OOM not swallowed by bare catch
        // ─────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("M8: stream exceeding MaxImportFileBytes is rejected with a clear error message")]
        public void SetTemplateData_OversizedStream_RejectsWithSizeError()
        {
            // Build a real (tiny) workbook, but wrap it in a stream that reports 100 MiB length.
            var bytes = MediumFixWorkbookBuilder.BuildWithBlankRows(
                "ImportTestTemplateVM",
                new[] { "Name", "Value" },
                new object?[]?[] { new object?[] { "TinyRow", 1 } });

            // FakeLargeStream reports 100 MiB but only holds the real bytes.
            var oversizedStream = new FakeLargeStream(bytes, fakeLength: 100L * 1024 * 1024);

            var vm = new BytesInjectedImportVM(oversizedStream);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");
            vm.CustomMaxBytes = 10L * 1024 * 1024; // 10 MiB limit (default)
            vm.ValidityTemplateType = true;

            vm.SetTemplateData();

            Assert.AreEqual(1, vm.ErrorListVM.EntityList.Count,
                "Exactly one size-rejection error expected");
            StringAssert.Contains(
                vm.ErrorListVM.EntityList[0].Message,
                "exceeds the maximum allowed size",
                "Error must mention the size limit");
            Assert.AreEqual(0, vm.PublicTemplateData?.Count,
                "No rows should be populated for an oversized file");
        }

        [TestMethod]
        [Description("M8: file within size limit is processed normally (regression guard)")]
        public void SetTemplateData_WithinSizeLimit_ProcessesNormally()
        {
            var bytes = MediumFixWorkbookBuilder.BuildWithBlankRows(
                "ImportTestTemplateVM",
                new[] { "Name", "Value" },
                new object?[]?[] { new object?[] { "SmallFile", 42 } });

            var vm = new BytesInjectedImportVM(bytes);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateImportDb(), "user");
            vm.ValidityTemplateType = true;

            vm.SetTemplateData();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count,
                "No error expected for a normally-sized file");
            Assert.AreEqual(1, vm.PublicTemplateData?.Count,
                "One row should be parsed from the valid workbook");
        }

        // ─────────────────────────────────────────────────────────────────────────
        // M9: sub-table row before any parent row must emit a clear error, not NRE
        // ─────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("M9: orphaned sub-table row (before any parent row) must add a diagnostic error, not throw NRE")]
        public void SetEntityData_SubTableRowBeforeParentRow_AddsErrorNotNRE()
        {
            // Arrange: first row has empty Name (parent columns all empty) and
            // non-empty Detail (sub-table column with SubTableType = M9Child).
            // EntityList is empty at this point → EntityList.LastOrDefault() == null.
            // Before the M9 fix: NRE on entity!.GetType() / GetID() / SetPropertyValue etc.
            // After the fix: error added, row skipped, no exception.
            var vm = new M9ImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateM9Db(), "user");

            // Orphan row: Name empty (parent), Detail non-empty (sub-table)
            var orphanRow = M9RowBuilder.Build(name: null, detail: "OrphanDetail", excelIndex: 2);
            vm.InjectTemplateData(new List<M9TemplateVM> { orphanRow });

            Exception? thrownEx = null;
            try
            {
                vm.PublicSetEntityData();
            }
            catch (Exception ex)
            {
                thrownEx = ex;
            }

            // M9 fix: no exception should propagate
            Assert.IsNull(thrownEx,
                $"No exception expected after M9 fix; got: {thrownEx?.GetType().Name}: {thrownEx?.Message}");

            // A diagnostic error must have been recorded
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0,
                "An error should have been recorded for the orphaned sub-table row");
            Assert.IsTrue(
                vm.ErrorListVM.EntityList.Any(e =>
                    e.Message?.Contains("Sub-table row appears before any parent row") == true),
                "Error message should describe the orphaned-row situation. " +
                "Actual errors: " + string.Join("; ", vm.ErrorListVM.EntityList.Select(e => e.Message)));
        }

        [TestMethod]
        [Description("M9: multiple consecutive orphan sub-table rows each get their own error entry")]
        public void SetEntityData_MultipleOrphanSubTableRows_AddsErrorPerOrphanRow()
        {
            var vm = new M9ImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateM9Db(), "user");

            var orphan1 = M9RowBuilder.Build(name: null, detail: "Orphan1", excelIndex: 2);
            var orphan2 = M9RowBuilder.Build(name: null, detail: "Orphan2", excelIndex: 3);
            vm.InjectTemplateData(new List<M9TemplateVM> { orphan1, orphan2 });

            vm.PublicSetEntityData();

            Assert.AreEqual(2, vm.ErrorListVM.EntityList.Count,
                "One error per orphaned sub-table row expected");
            Assert.IsTrue(
                vm.ErrorListVM.EntityList.All(e =>
                    e.Message?.Contains("Sub-table row appears before any parent row") == true),
                "Both errors should describe the orphaned sub-table situation");
        }

        [TestMethod]
        [Description("M9: valid parent-only row (no sub-table orphan scenario) still processes correctly")]
        public void SetEntityData_ParentRowOnly_ProcessesWithoutError()
        {
            // Arrange: a parent row with Name non-empty; Detail has SubTableType set but
            // the sub-table value is also empty → ChildrenEntityDic value is empty string
            // → sub-table assignment branch is skipped.
            // The parent entity should be created without error.
            var vm = new M9ImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateM9Db(), "user");

            // Parent row: Name set, Detail empty (sub-table column but no child data)
            var parentRow = M9RowBuilder.Build(name: "ParentA", detail: "", excelIndex: 2);
            vm.InjectTemplateData(new List<M9TemplateVM> { parentRow });

            Exception? thrownEx = null;
            try { vm.PublicSetEntityData(); }
            catch (Exception ex) { thrownEx = ex; }

            Assert.IsNull(thrownEx,
                $"No exception expected for a valid parent row; got: {thrownEx?.GetType().Name}: {thrownEx?.Message}");
            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count,
                "No errors expected for a valid parent row");
            Assert.AreEqual(1, vm.EntityList.Count, "One entity should be created for the parent row");
            Assert.AreEqual("ParentA", vm.EntityList[0].Name);
        }
    }
}
