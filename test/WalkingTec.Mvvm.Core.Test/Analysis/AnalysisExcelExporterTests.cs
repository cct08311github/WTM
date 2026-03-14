#nullable enable
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// AnalysisExcelExporter 完整覆蓋測試。
    /// 涵蓋：Header 行、decimal 值（Numeric cell）、字串值（String cell）、
    /// null 值（空字串）、空資料列（僅 Header）。
    /// </summary>
    [TestClass]
    public class AnalysisExcelExporterTests
    {
        // ─── Helpers ───────────────────────────────────────────────────────────

        private static AnalysisQueryResponse MakeResponse(
            List<string> columns,
            List<Dictionary<string, object?>> rows)
        {
            return new AnalysisQueryResponse
            {
                Columns    = columns,
                Rows       = rows,
                TotalCount = rows.Count,
                Truncated  = false,
                QueryHash  = "0000000000000000"
            };
        }

        /// <summary>從 Export 回傳的 bytes 建立 NPOI workbook（用於驗證）。</summary>
        private static IWorkbook OpenWorkbook(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            return new XSSFWorkbook(ms);
        }

        // ─── 基本輸出 ──────────────────────────────────────────────────────────

        [TestMethod]
        public void Export_returns_non_empty_bytes()
        {
            var resp = MakeResponse(
                new List<string> { "Region", "Amount_Sum" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "華東", ["Amount_Sum"] = 300m }
                });

            var bytes = AnalysisExcelExporter.Export(resp);

            Assert.IsNotNull(bytes);
            Assert.IsTrue(bytes.Length > 0);
        }

        // ─── Header 行 ────────────────────────────────────────────────────────

        [TestMethod]
        public void Export_produces_correct_column_headers()
        {
            var resp = MakeResponse(
                new List<string> { "Region", "Category", "Amount_Sum" },
                new List<Dictionary<string, object?>>());

            var wb     = OpenWorkbook(AnalysisExcelExporter.Export(resp));
            var sheet  = wb.GetSheetAt(0);
            var header = sheet.GetRow(0);

            Assert.AreEqual(3,            header.LastCellNum);
            Assert.AreEqual("Region",     header.GetCell(0).StringCellValue);
            Assert.AreEqual("Category",   header.GetCell(1).StringCellValue);
            Assert.AreEqual("Amount_Sum", header.GetCell(2).StringCellValue);
        }

        // ─── 資料列數 ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Export_produces_correct_row_count()
        {
            var rows = new List<Dictionary<string, object?>>
            {
                new() { ["Region"] = "華東", ["Amount_Sum"] = 100m },
                new() { ["Region"] = "華南", ["Amount_Sum"] = 300m },
            };
            var resp = MakeResponse(new List<string> { "Region", "Amount_Sum" }, rows);

            var wb    = OpenWorkbook(AnalysisExcelExporter.Export(resp));
            var sheet = wb.GetSheetAt(0);

            // NPOI LastRowNum 是 0-based；header=0，data=1,2 → LastRowNum=2
            Assert.AreEqual(2, sheet.LastRowNum);
        }

        // ─── Cell type：decimal → Numeric ─────────────────────────────────────

        [TestMethod]
        public void Export_stores_decimal_as_numeric_cell()
        {
            var resp = MakeResponse(
                new List<string> { "Amount_Sum" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Amount_Sum"] = 123.45m }
                });

            var wb   = OpenWorkbook(AnalysisExcelExporter.Export(resp));
            var cell = wb.GetSheetAt(0).GetRow(1).GetCell(0);

            Assert.AreEqual(CellType.Numeric, cell.CellType);
            Assert.AreEqual(123.45, cell.NumericCellValue, delta: 0.001);
        }

        // ─── Cell type：string → String ───────────────────────────────────────

        [TestMethod]
        public void Export_stores_string_value_as_string_cell()
        {
            var resp = MakeResponse(
                new List<string> { "Region" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "華東" }
                });

            var wb   = OpenWorkbook(AnalysisExcelExporter.Export(resp));
            var cell = wb.GetSheetAt(0).GetRow(1).GetCell(0);

            Assert.AreEqual(CellType.String, cell.CellType);
            Assert.AreEqual("華東", cell.StringCellValue);
        }

        // ─── Cell type：null → 空字串 ─────────────────────────────────────────

        [TestMethod]
        public void Export_handles_null_value_as_empty_string()
        {
            // TryGetValue 找不到 key 時 val=null → SetCellValue("")
            var resp = MakeResponse(
                new List<string> { "Region" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = null }
                });

            var wb   = OpenWorkbook(AnalysisExcelExporter.Export(resp));
            var cell = wb.GetSheetAt(0).GetRow(1).GetCell(0);

            Assert.AreEqual(CellType.String, cell.CellType);
            Assert.AreEqual("", cell.StringCellValue);
        }

        // ─── 空資料列：只有 Header ─────────────────────────────────────────────

        [TestMethod]
        public void Export_with_empty_rows_produces_header_only()
        {
            var resp = MakeResponse(
                new List<string> { "Region", "Amount_Sum" },
                new List<Dictionary<string, object?>>());

            var wb    = OpenWorkbook(AnalysisExcelExporter.Export(resp));
            var sheet = wb.GetSheetAt(0);

            Assert.AreEqual(0, sheet.LastRowNum); // 只有 row 0（header）
            Assert.IsNull(sheet.GetRow(1));       // 無資料列
        }

        // ─── includeChart=false：不含圖表（預設行為）────────────────────────────

        [TestMethod]
        public void Export_without_chart_has_no_drawing()
        {
            var resp = MakeResponse(
                new List<string> { "Region", "Amount_Sum" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "華東", ["Amount_Sum"] = 300m }
                });

            var wb    = OpenWorkbook(AnalysisExcelExporter.Export(resp, includeChart: false));
            var sheet = wb.GetSheetAt(0) as XSSFSheet;

            Assert.IsNotNull(sheet);
            // 無圖表時不應有 Drawing
            var drawing = sheet.GetDrawingPatriarch();
            Assert.IsTrue(drawing == null || drawing.GetCharts().Count == 0);
        }

        // ─── includeChart=true：嵌入 bar chart ──────────────────────────────────

        [TestMethod]
        public void Export_with_chart_has_drawing_with_chart()
        {
            var resp = MakeResponse(
                new List<string> { "Region", "Amount_Sum" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "華東", ["Amount_Sum"] = 100m },
                    new() { ["Region"] = "華南", ["Amount_Sum"] = 300m },
                });

            var wb    = OpenWorkbook(AnalysisExcelExporter.Export(resp, includeChart: true));
            var sheet = wb.GetSheetAt(0) as XSSFSheet;

            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch() as XSSFDrawing;
            Assert.IsNotNull(drawing, "includeChart=true 時應有 Drawing");
            Assert.IsTrue(drawing.GetCharts().Count >= 1, "至少應有 1 個 chart");
        }

        // ─── includeChart 預設值為 false ─────────────────────────────────────────

        [TestMethod]
        public void Export_default_includeChart_is_false()
        {
            var resp = MakeResponse(
                new List<string> { "Region", "Amount_Sum" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "華東", ["Amount_Sum"] = 300m }
                });

            // 不帶 includeChart 參數 → 預設不含圖表（向後相容）
            var wb    = OpenWorkbook(AnalysisExcelExporter.Export(resp));
            var sheet = wb.GetSheetAt(0) as XSSFSheet;

            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch();
            Assert.IsTrue(drawing == null || drawing.GetCharts().Count == 0);
        }

        // ─── chartType 參數測試 (#275) ──────────────────────────────────────

        private static AnalysisQueryResponse MakeTwoRowResponse()
        {
            return MakeResponse(
                new List<string> { "Region", "Amount_Sum" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "North", ["Amount_Sum"] = 100m },
                    new() { ["Region"] = "South", ["Amount_Sum"] = 200m },
                });
        }

        [TestMethod]
        public void Export_chartType_pie_produces_chart()
        {
            var wb = OpenWorkbook(AnalysisExcelExporter.Export(MakeTwoRowResponse(), includeChart: true, chartType: "pie"));
            var sheet = wb.GetSheetAt(0) as XSSFSheet;
            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch() as XSSFDrawing;
            Assert.IsNotNull(drawing, "pie chart 應有 Drawing");
            Assert.IsTrue(drawing.GetCharts().Count >= 1);
        }

        [TestMethod]
        public void Export_chartType_line_produces_chart()
        {
            var wb = OpenWorkbook(AnalysisExcelExporter.Export(MakeTwoRowResponse(), includeChart: true, chartType: "line"));
            var sheet = wb.GetSheetAt(0) as XSSFSheet;
            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch() as XSSFDrawing;
            Assert.IsNotNull(drawing, "line chart 應有 Drawing");
            Assert.IsTrue(drawing.GetCharts().Count >= 1);
        }

        [TestMethod]
        public void Export_chartType_bar_stacked_produces_chart()
        {
            var wb = OpenWorkbook(AnalysisExcelExporter.Export(MakeTwoRowResponse(), includeChart: true, chartType: "bar-stacked"));
            var sheet = wb.GetSheetAt(0) as XSSFSheet;
            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch() as XSSFDrawing;
            Assert.IsNotNull(drawing, "bar-stacked chart 應有 Drawing");
            Assert.IsTrue(drawing.GetCharts().Count >= 1);
        }

        [TestMethod]
        public void Export_chartType_null_defaults_to_bar()
        {
            var wb = OpenWorkbook(AnalysisExcelExporter.Export(MakeTwoRowResponse(), includeChart: true, chartType: null));
            var sheet = wb.GetSheetAt(0) as XSSFSheet;
            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch() as XSSFDrawing;
            Assert.IsNotNull(drawing);
            Assert.IsTrue(drawing.GetCharts().Count >= 1);
        }

        // ─── #281: ComputeScale ─────────────────────────────────────────────

        [TestMethod]
        public void ComputeScale_small_value_no_unit()
        {
            var (divisor, unit) = AnalysisExcelExporter.ComputeScale(9999);
            Assert.AreEqual(1, divisor);
            Assert.AreEqual("", unit);
        }

        [TestMethod]
        public void ComputeScale_wan_threshold()
        {
            var (divisor, unit) = AnalysisExcelExporter.ComputeScale(50000);
            Assert.AreEqual(10_000, divisor);
            Assert.AreEqual("萬", unit);
        }

        [TestMethod]
        public void ComputeScale_yi_threshold()
        {
            var (divisor, unit) = AnalysisExcelExporter.ComputeScale(200_000_000);
            Assert.AreEqual(100_000_000, divisor);
            Assert.AreEqual("億", unit);
        }

        // ─── #281: Column header scaling ────────────────────────────────────

        [TestMethod]
        public void Export_header_appends_unit_for_large_values()
        {
            var resp = MakeResponse(
                new List<string> { "Region", "Amount_Sum" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "North", ["Amount_Sum"] = 5_000_000m },
                });

            var wb = OpenWorkbook(AnalysisExcelExporter.Export(resp));
            var header = wb.GetSheetAt(0).GetRow(0);
            Assert.AreEqual("Amount_Sum（百萬）", header.GetCell(1).StringCellValue);
        }

        [TestMethod]
        public void Export_header_no_unit_for_small_values()
        {
            var resp = MakeResponse(
                new List<string> { "Region", "Amount_Sum" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "North", ["Amount_Sum"] = 500m },
                });

            var wb = OpenWorkbook(AnalysisExcelExporter.Export(resp));
            var header = wb.GetSheetAt(0).GetRow(0);
            Assert.AreEqual("Amount_Sum", header.GetCell(1).StringCellValue);
        }

        // ─── #281: Dual axis charts ─────────────────────────────────────────

        private static AnalysisQueryResponse MakeDualMeasureResponse()
        {
            return MakeResponse(
                new List<string> { "Region", "Amount_Sum", "Qty_Count" },
                new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "North", ["Amount_Sum"] = 1_000_000m, ["Qty_Count"] = 5m },
                    new() { ["Region"] = "South", ["Amount_Sum"] = 2_000_000m, ["Qty_Count"] = 8m },
                });
        }

        [TestMethod]
        public void Export_dual_axis_bar_chart_does_not_throw()
        {
            var resp = MakeDualMeasureResponse();
            var bytes = AnalysisExcelExporter.Export(resp, includeChart: true, chartType: "bar");
            var wb = OpenWorkbook(bytes);
            var sheet = wb.GetSheetAt(0) as XSSFSheet;
            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch() as XSSFDrawing;
            Assert.IsNotNull(drawing);
            Assert.IsTrue(drawing.GetCharts().Count >= 1);
        }

        [TestMethod]
        public void Export_dual_axis_line_chart_does_not_throw()
        {
            var resp = MakeDualMeasureResponse();
            var bytes = AnalysisExcelExporter.Export(resp, includeChart: true, chartType: "line");
            var wb = OpenWorkbook(bytes);
            var sheet = wb.GetSheetAt(0) as XSSFSheet;
            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch() as XSSFDrawing;
            Assert.IsNotNull(drawing);
            Assert.IsTrue(drawing.GetCharts().Count >= 1);
        }

        [TestMethod]
        public void Export_single_measure_backward_compat()
        {
            // Single measure should still work exactly as before
            var resp = MakeTwoRowResponse();
            var bytes = AnalysisExcelExporter.Export(resp, includeChart: true, chartType: "bar");
            Assert.IsNotNull(bytes);
            Assert.IsTrue(bytes.Length > 0);
        }
    }
}
