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
    }
}
