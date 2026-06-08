#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

[TestClass]
public class ExcelEtlSourceTests
{
    // -----------------------------------------------------------------
    // Helper — build an .xlsx file in memory and save to a temp path
    // -----------------------------------------------------------------

    private static string BuildTempXlsx(Action<ISheet> populate, string sheetName = "Sheet1")
    {
        var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet(sheetName);
        populate(sheet);

        var path = Path.GetTempFileName() + ".xlsx";
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        wb.Write(fs);
        return path;
    }

    private static IRow AddRow(ISheet sheet, params object?[] values)
    {
        var row = sheet.CreateRow(sheet.PhysicalNumberOfRows);
        for (int i = 0; i < values.Length; i++)
        {
            var cell = row.CreateCell(i);
            switch (values[i])
            {
                case null:
                    cell.SetCellType(CellType.Blank);
                    break;
                case double d:
                    cell.SetCellValue(d);
                    break;
                case int n:
                    cell.SetCellValue((double)n);
                    break;
                case bool b:
                    cell.SetCellValue(b);
                    break;
                case DateTime dt:
                    cell.SetCellValue(dt);
                    // Apply a built-in date format so NPOI marks it as date-formatted
                    var wb2 = sheet.Workbook;
                    var style = wb2.CreateCellStyle();
                    style.DataFormat = wb2.CreateDataFormat().GetFormat("yyyy-MM-dd");
                    cell.CellStyle = style;
                    break;
                default:
                    cell.SetCellValue(values[i]?.ToString());
                    break;
            }
        }
        return row;
    }

    // -----------------------------------------------------------------
    // Basic: header + data rows
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Simple_xlsx_with_header_parses_correctly()
    {
        var path = BuildTempXlsx(sheet =>
        {
            AddRow(sheet, "Name", "Score");
            AddRow(sheet, "Alice", 95.0);
            AddRow(sheet, "Bob", 80.0);
        });

        try
        {
            using var src = new ExcelEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows.Should().HaveCount(2);
            rows[0]["Name"].Should().Be("Alice");
            rows[0]["Score"].Should().Be(95.0);
            rows[1]["Name"].Should().Be("Bob");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Column_names_come_from_header_row()
    {
        var path = BuildTempXlsx(sheet =>
        {
            AddRow(sheet, "FirstName", "LastName");
            AddRow(sheet, "John", "Doe");
        });

        try
        {
            using var src = new ExcelEtlSource();
            DataTable? schema = null;
            await foreach (var batch in src.ExtractBatchesAsync(path, "", null, 1000))
                schema = batch;

            schema!.Columns["FirstName"].Should().NotBeNull();
            schema.Columns["LastName"].Should().NotBeNull();
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Numeric and bool types
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Numeric_cells_are_read_as_double()
    {
        var path = BuildTempXlsx(sheet =>
        {
            AddRow(sheet, "Value");
            AddRow(sheet, 3.14);
        });

        try
        {
            using var src = new ExcelEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows[0]["Value"].Should().Be(3.14);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Boolean_cells_are_read_as_bool()
    {
        var path = BuildTempXlsx(sheet =>
        {
            AddRow(sheet, "Flag");
            AddRow(sheet, true);
        });

        try
        {
            using var src = new ExcelEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows[0]["Flag"].Should().Be(true);
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // No header
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task No_header_generates_ColN_column_names()
    {
        var path = BuildTempXlsx(sheet =>
        {
            AddRow(sheet, "alpha", "beta", "gamma");
        });

        try
        {
            using var src = new ExcelEtlSource { HasHeader = false };
            DataTable? schema = null;
            var allRows = new List<DataRow>();
            await foreach (var batch in src.ExtractBatchesAsync(path, "", null, 1000))
            {
                schema = batch;
                foreach (DataRow r in batch.Rows) allRows.Add(r);
            }

            schema!.Columns["Col0"].Should().NotBeNull();
            schema.Columns["Col1"].Should().NotBeNull();
            schema.Columns["Col2"].Should().NotBeNull();
            allRows.Should().HaveCount(1);
            allRows[0]["Col0"].Should().Be("alpha");
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Sheet selection by name via queryTemplate
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Sheet_name_in_queryTemplate_selects_correct_sheet()
    {
        // Build a workbook with two sheets from scratch.
        var wb = new XSSFWorkbook();

        var defaultSheet = wb.CreateSheet("Default");
        var r0 = defaultSheet.CreateRow(0); r0.CreateCell(0).SetCellValue("X");
        var r1 = defaultSheet.CreateRow(1); r1.CreateCell(0).SetCellValue("WrongSheet");

        var targetSheet = wb.CreateSheet("Target");
        var h = targetSheet.CreateRow(0); h.CreateCell(0).SetCellValue("Header");
        var d = targetSheet.CreateRow(1); d.CreateCell(0).SetCellValue("TargetData");

        var path = Path.GetTempFileName() + ".xlsx";
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            wb.Write(fs);

        try
        {
            using var src = new ExcelEtlSource();
            // queryTemplate = sheet name
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000, queryTemplate: "Target");

            rows.Should().HaveCount(1);
            rows[0]["Header"].Should().Be("TargetData");
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Batching
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Batching_splits_rows_at_batchSize_boundary()
    {
        var path = BuildTempXlsx(sheet =>
        {
            AddRow(sheet, "Id");
            for (int i = 1; i <= 5; i++) AddRow(sheet, (double)i);
        });

        try
        {
            using var src = new ExcelEtlSource();
            var batches = new List<DataTable>();
            await foreach (var batch in src.ExtractBatchesAsync(path, "", null, batchSize: 2))
                batches.Add(batch);

            batches.Should().HaveCount(3, "5 rows / batchSize 2 → 3 batches");
            batches[0].Rows.Count.Should().Be(2);
            batches[1].Rows.Count.Should().Be(2);
            batches[2].Rows.Count.Should().Be(1);
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Empty sheet
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Empty_sheet_with_header_only_yields_no_rows()
    {
        var path = BuildTempXlsx(sheet =>
        {
            AddRow(sheet, "Name", "Age");
            // No data rows
        });

        try
        {
            using var src = new ExcelEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows.Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Completely_empty_sheet_yields_nothing()
    {
        var path = BuildTempXlsx(sheet => { /* no rows */ });

        try
        {
            using var src = new ExcelEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows.Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Invalid file format
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Non_xlsx_file_throws_InvalidOperationException()
    {
        // Write a file with .xlsx extension but containing CSV content
        var path = Path.GetTempFileName() + ".xlsx";
        File.WriteAllText(path, "Name,Age\r\nAlice,30\r\n");

        try
        {
            using var src = new ExcelEtlSource();
            var act = async () =>
            {
                await foreach (var _ in src.ExtractBatchesAsync(path, "", null, 1000)) { }
            };

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*xlsx*");
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static async Task<List<DataRow>> CollectAllRowsAsync(
        ExcelEtlSource src, string path, int batchSize, string queryTemplate = "")
    {
        var rows = new List<DataRow>();
        await foreach (var batch in src.ExtractBatchesAsync(path, queryTemplate, null, batchSize))
            foreach (DataRow r in batch.Rows)
                rows.Add(r);
        return rows;
    }
}
