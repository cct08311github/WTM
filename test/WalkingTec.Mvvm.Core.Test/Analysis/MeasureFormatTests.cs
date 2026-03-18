#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis;

/// <summary>
/// Tests for issue #614 — Excel export dynamic number formatting via MeasureFormat.
/// Covers: attribute → scanner → response → exporter pipeline.
/// </summary>
[TestClass]
public class MeasureFormatTests
{
    // ── Attribute & Scanner ───────────────────────────────────────────────────

    private class SalesModel
    {
        [Dimension] public string? Region { get; set; }

        [Measure(AllowedFuncs = AggregateFunc.Sum)]
        public decimal Revenue { get; set; }

        [Measure(AllowedFuncs = AggregateFunc.Sum, Format = MeasureFormat.Currency)]
        public decimal Cost { get; set; }

        [Measure(AllowedFuncs = AggregateFunc.Sum, Format = MeasureFormat.Integer)]
        public int Quantity { get; set; }

        [Measure(AllowedFuncs = AggregateFunc.Avg, Format = MeasureFormat.Percent)]
        public double Margin { get; set; }
    }

    [TestMethod]
    public void Scanner_defaults_to_Auto_when_Format_not_specified()
    {
        var metas = AnalysisFieldScanner.ScanModel(typeof(SalesModel)).ToList();
        var revenue = metas.Single(m => m.FieldName == "Revenue");
        Assert.AreEqual(MeasureFormat.Auto, revenue.Format);
    }

    [TestMethod]
    public void Scanner_propagates_Currency_format()
    {
        var metas = AnalysisFieldScanner.ScanModel(typeof(SalesModel)).ToList();
        var cost = metas.Single(m => m.FieldName == "Cost");
        Assert.AreEqual(MeasureFormat.Currency, cost.Format);
    }

    [TestMethod]
    public void Scanner_propagates_Integer_format()
    {
        var metas = AnalysisFieldScanner.ScanModel(typeof(SalesModel)).ToList();
        var qty = metas.Single(m => m.FieldName == "Quantity");
        Assert.AreEqual(MeasureFormat.Integer, qty.Format);
    }

    [TestMethod]
    public void Scanner_propagates_Percent_format()
    {
        var metas = AnalysisFieldScanner.ScanModel(typeof(SalesModel)).ToList();
        var margin = metas.Single(m => m.FieldName == "Margin");
        Assert.AreEqual(MeasureFormat.Percent, margin.Format);
    }

    // ── AnalysisQueryResponse.ColumnFormats ───────────────────────────────────

    [TestMethod]
    public void ColumnFormats_defaults_to_empty_dict()
    {
        var r = new AnalysisQueryResponse();
        Assert.IsNotNull(r.ColumnFormats);
        Assert.AreEqual(0, r.ColumnFormats.Count);
    }

    // ── AnalysisExcelExporter — per-column number format ─────────────────────

    private static AnalysisQueryResponse BuildResponse(params (string col, MeasureFormat fmt, object val)[] cols)
    {
        var resp = new AnalysisQueryResponse();
        foreach (var (col, fmt, _) in cols)
        {
            resp.Columns.Add(col);
            resp.ColumnFormats[col] = fmt;
        }
        var row = new Dictionary<string, object?>();
        foreach (var (col, _, val) in cols) row[col] = val;
        resp.Rows.Add(row);
        return resp;
    }

    private static string GetDataFormatString(IWorkbook wb, ICell cell)
    {
        var styleIdx = cell.CellStyle.DataFormat;
        return wb.CreateDataFormat().GetFormat(styleIdx);
    }

    private static (IWorkbook wb, ISheet sheet) LoadExcel(byte[] bytes)
    {
        var ms = new MemoryStream(bytes);
        var wb = new XSSFWorkbook(ms);
        return (wb, wb.GetSheetAt(0));
    }

    [TestMethod]
    public void Export_Auto_format_applies_comma_decimal()
    {
        var resp = BuildResponse(("Amount_Sum", MeasureFormat.Auto, 1234.56m));
        var bytes = AnalysisExcelExporter.Export(resp);
        var (_, sheet) = LoadExcel(bytes);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.IsNotNull(cell.CellStyle, "Data cell should have a style");
        // Auto = #,##0.00; format index 4 in built-in OR a custom format string
        var fmtStr = cell.CellStyle.GetDataFormatString();
        Assert.IsTrue(fmtStr.Contains("0.00"), $"Expected '0.00' in format, got: {fmtStr}");
        Assert.IsTrue(fmtStr.Contains(",") || fmtStr.Contains("#,##"), $"Expected thousand separator, got: {fmtStr}");
    }

    [TestMethod]
    public void Export_Integer_format_has_no_decimal()
    {
        var resp = BuildResponse(("Qty_Sum", MeasureFormat.Integer, 42));
        var bytes = AnalysisExcelExporter.Export(resp);
        var (_, sheet) = LoadExcel(bytes);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.IsNotNull(cell.CellStyle);
        var fmtStr = cell.CellStyle.GetDataFormatString();
        StringAssert.Contains(fmtStr, "#,##0");
        Assert.IsFalse(fmtStr.Contains(".00"), $"Integer format must not contain '.00', got: {fmtStr}");
    }

    [TestMethod]
    public void Export_Currency_format_contains_yen_symbol()
    {
        var resp = BuildResponse(("Cost_Sum", MeasureFormat.Currency, 9999.99m));
        var bytes = AnalysisExcelExporter.Export(resp);
        var (_, sheet) = LoadExcel(bytes);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.IsNotNull(cell.CellStyle);
        var fmtStr = cell.CellStyle.GetDataFormatString();
        StringAssert.Contains(fmtStr, "¥");
        StringAssert.Contains(fmtStr, "0.00");
    }

    [TestMethod]
    public void Export_Percent_format_contains_percent_sign()
    {
        var resp = BuildResponse(("Margin_Avg", MeasureFormat.Percent, 0.75));
        var bytes = AnalysisExcelExporter.Export(resp);
        var (_, sheet) = LoadExcel(bytes);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.IsNotNull(cell.CellStyle);
        var fmtStr = cell.CellStyle.GetDataFormatString();
        StringAssert.Contains(fmtStr, "%");
    }

    [TestMethod]
    public void Export_mixed_formats_apply_independently_per_column()
    {
        var resp = new AnalysisQueryResponse();
        resp.Columns.AddRange(new[] { "Revenue_Sum", "Qty_Sum", "Margin_Avg" });
        resp.ColumnFormats["Revenue_Sum"] = MeasureFormat.Currency;
        resp.ColumnFormats["Qty_Sum"] = MeasureFormat.Integer;
        resp.ColumnFormats["Margin_Avg"] = MeasureFormat.Percent;
        resp.Rows.Add(new Dictionary<string, object?>
        {
            ["Revenue_Sum"] = 12345.67m,
            ["Qty_Sum"] = 100,
            ["Margin_Avg"] = 0.42,
        });

        var bytes = AnalysisExcelExporter.Export(resp);
        var (_, sheet) = LoadExcel(bytes);
        var dataRow = sheet.GetRow(1);

        StringAssert.Contains(dataRow.GetCell(0).CellStyle.GetDataFormatString(), "¥");
        Assert.IsFalse(dataRow.GetCell(1).CellStyle.GetDataFormatString().Contains(".00"),
            "Integer column must not have .00");
        StringAssert.Contains(dataRow.GetCell(2).CellStyle.GetDataFormatString(), "%");
    }

    [TestMethod]
    public void Export_legacy_response_without_ColumnFormats_still_applies_numeric_style()
    {
        // Simulate old response with empty ColumnFormats (backward compat)
        var resp = new AnalysisQueryResponse();
        resp.Columns.Add("Value_Sum");
        // ColumnFormats is empty — legacy path
        resp.Rows.Add(new Dictionary<string, object?> { ["Value_Sum"] = 100m });

        var bytes = AnalysisExcelExporter.Export(resp);
        var (_, sheet) = LoadExcel(bytes);
        var cell = sheet.GetRow(1).GetCell(0);
        // Legacy auto-detection should still apply numeric style
        Assert.IsNotNull(cell.CellStyle);
        var fmt = cell.CellStyle.GetDataFormatString();
        Assert.IsFalse(string.IsNullOrEmpty(fmt), "Legacy numeric cell should have a format string");
    }
}
