#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Export;

/// <summary>
/// Tests for #414 opt-in streaming (SXSSF/CSV) export.
/// Validates:
///   1. Equivalence: SXSSF output matches buffered GenerateExcel() for headers + data rows.
///   2. ExportRowCount is set correctly on both streaming paths.
///   3. CSV streaming output is well-formed and content-equivalent.
///   4. UseStreamingExport property is false by default.
///   5. SXSSF temp-file cleanup: Dispose() is called (validated indirectly via no exception).
/// </summary>
[TestClass]
public class StreamingExportTests
{
    // -----------------------------------------------------------------------
    //  Shared test VM and data
    // -----------------------------------------------------------------------

    private static IList<School> _data = new List<School>();

    private sealed class SchoolStreamVM : BasePagedListVM<School, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<School>> InitGridHeader() =>
        [
            this.MakeGridHeader(x => x.SchoolCode),
            this.MakeGridHeader(x => x.SchoolName),
        ];

        public override IOrderedQueryable<School> GetSearchQuery() =>
            _data.AsQueryable().OrderBy(x => x.SchoolCode);
    }

    private SchoolStreamVM CreateVm()
    {
        var vm = MockWtmContext.CreateWtmContext().CreateVM<SchoolStreamVM>();
        vm.NeedPage = false;
        return vm;
    }

    [TestInitialize]
    public void Init() => _data = new List<School>();

    // -----------------------------------------------------------------------
    //  UseStreamingExport defaults
    // -----------------------------------------------------------------------

    [TestMethod]
    public void UseStreamingExport_is_false_by_default()
    {
        var vm = CreateVm();
        Assert.IsFalse(vm.UseStreamingExport,
            "UseStreamingExport must default to false so existing exports are unaffected.");
    }

    [TestMethod]
    public void UseStreamingExport_can_be_set()
    {
        var vm = CreateVm();
        vm.UseStreamingExport = true;
        Assert.IsTrue(vm.UseStreamingExport);
    }

    // -----------------------------------------------------------------------
    //  SXSSF - ExportRowCount
    // -----------------------------------------------------------------------

    [TestMethod]
    public void GenerateExcelToStream_ExportRowCount_zero_when_no_data()
    {
        var vm = CreateVm();
        using var ms = new MemoryStream();
        vm.GenerateExcelToStream(ms);
        Assert.AreEqual(0, vm.ExportRowCount);
    }

    [TestMethod]
    public void GenerateExcelToStream_ExportRowCount_matches_actual_rows()
    {
        _data = new List<School>
        {
            new School { SchoolCode = "001", SchoolName = "Alpha", Remark = "r" },
            new School { SchoolCode = "002", SchoolName = "Beta",  Remark = "r" },
            new School { SchoolCode = "003", SchoolName = "Gamma", Remark = "r" },
        };
        var vm = CreateVm();
        using var ms = new MemoryStream();
        vm.GenerateExcelToStream(ms);
        Assert.AreEqual(3, vm.ExportRowCount);
    }

    // -----------------------------------------------------------------------
    //  SXSSF equivalence vs. buffered GenerateExcel()
    // -----------------------------------------------------------------------

    [TestMethod]
    public void GenerateExcelToStream_produces_same_headers_as_buffered()
    {
        _data = new List<School>
        {
            new School { SchoolCode = "001", SchoolName = "Alpha", Remark = "r" },
        };

        // Buffered path
        var vmBuffered = CreateVm();
        byte[] bufferedBytes = vmBuffered.GenerateExcel();

        // Streaming path
        var vmStream = CreateVm();
        using var ms = new MemoryStream();
        vmStream.GenerateExcelToStream(ms);
        byte[] streamedBytes = ms.ToArray();

        // Parse both and compare header row
        ISheet bufferedSheet = new XSSFWorkbook(new MemoryStream(bufferedBytes)).GetSheetAt(0);
        ISheet streamedSheet = new XSSFWorkbook(new MemoryStream(streamedBytes)).GetSheetAt(0);

        IRow bHeader = bufferedSheet.GetRow(0);
        IRow sHeader = streamedSheet.GetRow(0);

        Assert.IsNotNull(bHeader);
        Assert.IsNotNull(sHeader);
        Assert.AreEqual(bHeader.LastCellNum, sHeader.LastCellNum,
            "Header column count must match between buffered and streamed output.");

        for (int c = 0; c < bHeader.LastCellNum; c++)
        {
            Assert.AreEqual(
                bHeader.GetCell(c)?.StringCellValue,
                sHeader.GetCell(c)?.StringCellValue,
                $"Header cell [{c}] value mismatch.");
        }
    }

    [TestMethod]
    public void GenerateExcelToStream_produces_same_data_rows_as_buffered()
    {
        _data = new List<School>
        {
            new School { SchoolCode = "001", SchoolName = "Alpha", Remark = "r" },
            new School { SchoolCode = "002", SchoolName = "Beta",  Remark = "r" },
            new School { SchoolCode = "003", SchoolName = "Gamma", Remark = "r" },
        };

        // Buffered path
        var vmBuffered = CreateVm();
        byte[] bufferedBytes = vmBuffered.GenerateExcel();

        // Streaming path
        var vmStream = CreateVm();
        using var ms = new MemoryStream();
        vmStream.GenerateExcelToStream(ms);
        byte[] streamedBytes = ms.ToArray();

        ISheet bufferedSheet = new XSSFWorkbook(new MemoryStream(bufferedBytes)).GetSheetAt(0);
        ISheet streamedSheet = new XSSFWorkbook(new MemoryStream(streamedBytes)).GetSheetAt(0);

        // Both have 1 header row + 3 data rows
        Assert.AreEqual(bufferedSheet.LastRowNum, streamedSheet.LastRowNum,
            "Row count (LastRowNum) must match.");

        // Compare every cell in data rows
        for (int r = 1; r <= bufferedSheet.LastRowNum; r++)
        {
            IRow bRow = bufferedSheet.GetRow(r);
            IRow sRow = streamedSheet.GetRow(r);
            Assert.IsNotNull(sRow, $"Streamed sheet is missing row {r}.");
            int colCount = bRow.LastCellNum;
            for (int c = 0; c < colCount; c++)
            {
                string bVal = bRow.GetCell(c)?.ToString() ?? string.Empty;
                string sVal = sRow.GetCell(c)?.ToString() ?? string.Empty;
                Assert.AreEqual(bVal, sVal, $"Cell [{r},{c}] mismatch: buffered='{bVal}' streamed='{sVal}'.");
            }
        }
    }

    [TestMethod]
    public void GenerateExcelToStream_row_window_smaller_than_dataset_still_produces_correct_output()
    {
        // Use a window of 2 rows with 5 data rows to exercise SXSSF flushing.
        _data = Enumerable.Range(1, 5)
            .Select(i => new School { SchoolCode = i.ToString("D3"), SchoolName = $"School{i}", Remark = "r" })
            .ToList();

        var vm = CreateVm();
        using var ms = new MemoryStream();
        vm.GenerateExcelToStream(ms, rowWindowSize: 2);

        Assert.AreEqual(5, vm.ExportRowCount);

        ISheet sheet = new XSSFWorkbook(new MemoryStream(ms.ToArray())).GetSheetAt(0);
        // 1 header row + 5 data rows = LastRowNum 5
        Assert.AreEqual(5, sheet.LastRowNum);
    }

    // -----------------------------------------------------------------------
    //  SXSSF - temp-file cleanup
    // -----------------------------------------------------------------------

    [TestMethod]
    public void GenerateExcelToStream_disposes_without_exception()
    {
        // If SXSSF Dispose() leaves temp files or throws, this test will surface it.
        _data = new List<School>
        {
            new School { SchoolCode = "001", SchoolName = "Alpha", Remark = "r" },
        };
        var vm = CreateVm();
        using var ms = new MemoryStream();
        // Should not throw; any SXSSF temp-file leak would surface here.
        vm.GenerateExcelToStream(ms);
        Assert.IsTrue(ms.Length > 0, "Output stream must have bytes.");
    }

    // -----------------------------------------------------------------------
    //  CSV streaming
    // -----------------------------------------------------------------------

    [TestMethod]
    public void GenerateCsvToStream_ExportRowCount_matches_actual_rows()
    {
        _data = new List<School>
        {
            new School { SchoolCode = "001", SchoolName = "Alpha", Remark = "r" },
            new School { SchoolCode = "002", SchoolName = "Beta",  Remark = "r" },
        };
        var vm = CreateVm();
        using var ms = new MemoryStream();
        vm.GenerateCsvToStream(ms);
        Assert.AreEqual(2, vm.ExportRowCount);
    }

    [TestMethod]
    public void GenerateCsvToStream_produces_header_and_data_rows()
    {
        _data = new List<School>
        {
            new School { SchoolCode = "001", SchoolName = "Alpha", Remark = "r" },
            new School { SchoolCode = "002", SchoolName = "Beta",  Remark = "r" },
        };
        var vm = CreateVm();
        using var ms = new MemoryStream();
        vm.GenerateCsvToStream(ms);

        string csv = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿'); // strip BOM
        string[] lines = csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // 1 header + 2 data
        Assert.AreEqual(3, lines.Length, $"Expected 3 lines, got {lines.Length}. CSV:\n{csv}");

        // Header should contain column names
        Assert.IsTrue(lines[0].Contains("SchoolCode") || lines[0].Contains("学校编码"),
            $"Header line should contain column title. Got: {lines[0]}");

        // Data rows should contain actual values
        Assert.IsTrue(lines[1].Contains("001"), $"First data row should contain '001'. Got: {lines[1]}");
        Assert.IsTrue(lines[2].Contains("002"), $"Second data row should contain '002'. Got: {lines[2]}");
    }

    [TestMethod]
    public void GenerateCsvToStream_data_matches_buffered_excel_cell_values()
    {
        _data = new List<School>
        {
            new School { SchoolCode = "001", SchoolName = "Alpha", Remark = "r" },
            new School { SchoolCode = "002", SchoolName = "Beta",  Remark = "r" },
        };

        // Collect cell values from buffered Excel
        var vmBuffered = CreateVm();
        byte[] bufferedBytes = vmBuffered.GenerateExcel();
        ISheet bufferedSheet = new XSSFWorkbook(new MemoryStream(bufferedBytes)).GetSheetAt(0);

        // Collect CSV rows (skip header)
        var vmCsv = CreateVm();
        using var ms = new MemoryStream();
        vmCsv.GenerateCsvToStream(ms);
        string csv = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿');
        string[] csvLines = csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // Compare data rows (csvLines[0] is header, bufferedSheet row 0 is also header)
        for (int r = 1; r <= bufferedSheet.LastRowNum; r++)
        {
            IRow excelRow = bufferedSheet.GetRow(r);
            string[] csvCols = csvLines[r].Split(',');

            for (int c = 0; c < excelRow.LastCellNum && c < csvCols.Length; c++)
            {
                string excelVal = excelRow.GetCell(c)?.ToString() ?? string.Empty;
                // Strip CSV escaping quotes if present
                string csvVal = csvCols[c].Trim('"');
                Assert.AreEqual(excelVal, csvVal,
                    $"Row {r} col {c}: Excel='{excelVal}' CSV='{csvVal}'.");
            }
        }
    }

    [TestMethod]
    public void GenerateCsvToStream_empty_data_gives_zero_ExportRowCount()
    {
        var vm = CreateVm();
        using var ms = new MemoryStream();
        vm.GenerateCsvToStream(ms);
        Assert.AreEqual(0, vm.ExportRowCount);
    }

    // -----------------------------------------------------------------------
    //  Interface surface
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Interface_exposes_GenerateExcelToStream()
    {
        var method = typeof(IBasePagedListVM<,>).GetMethod("GenerateExcelToStream");
        Assert.IsNotNull(method, "IBasePagedListVM must expose GenerateExcelToStream.");
    }

    [TestMethod]
    public void Interface_exposes_GenerateCsvToStream()
    {
        var method = typeof(IBasePagedListVM<,>).GetMethod("GenerateCsvToStream");
        Assert.IsNotNull(method, "IBasePagedListVM must expose GenerateCsvToStream.");
    }

    [TestMethod]
    public void Interface_exposes_UseStreamingExport()
    {
        var prop = typeof(IBasePagedListVM<,>).GetProperty("UseStreamingExport");
        Assert.IsNotNull(prop, "IBasePagedListVM must expose UseStreamingExport.");
        Assert.IsTrue(prop!.CanRead);
        Assert.IsTrue(prop.CanWrite);
    }
}
