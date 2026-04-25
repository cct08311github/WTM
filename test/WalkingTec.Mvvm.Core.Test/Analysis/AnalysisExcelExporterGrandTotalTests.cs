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
    /// Tests for grand-total footer rendering in
    /// <see cref="AnalysisExcelExporter"/> — the Excel half of the
    /// <see cref="AnalysisQueryResponse.GrandTotalRow"/> contract.
    /// </summary>
    [TestClass]
    public class AnalysisExcelExporterGrandTotalTests
    {
        private static AnalysisQueryResponse MakeResponseWithTotal(
            List<string> columns,
            List<Dictionary<string, object?>> rows,
            Dictionary<string, object?>? grandTotal)
        {
            return new AnalysisQueryResponse
            {
                Columns        = columns,
                Rows           = rows,
                TotalCount     = rows.Count,
                Truncated      = false,
                QueryHash      = "0000000000000000",
                GrandTotalRow  = grandTotal,
            };
        }

        private static IWorkbook OpenWorkbook(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            return new XSSFWorkbook(ms);
        }

        // ── Default (no GrandTotalRow) ──────────────────────────────────

        [TestMethod]
        public void Null_grand_total_does_not_emit_extra_row()
        {
            var resp = MakeResponseWithTotal(
                new() { "Region", "Amount_Sum" },
                new()
                {
                    new() { ["Region"] = "北", ["Amount_Sum"] = 100m },
                    new() { ["Region"] = "南", ["Amount_Sum"] = 200m },
                },
                grandTotal: null);

            var bytes = AnalysisExcelExporter.Export(resp);
            using var wb = OpenWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);

            // Header (row 0) + 2 data rows (rows 1-2). No row 3.
            Assert.AreEqual(2, sheet.LastRowNum,
                "Without GrandTotalRow, sheet must end at the last data row.");
        }

        // ── Grand-total row appended after data ─────────────────────────

        [TestMethod]
        public void Grand_total_row_is_appended_after_data()
        {
            var resp = MakeResponseWithTotal(
                new() { "Region", "Amount_Sum" },
                new()
                {
                    new() { ["Region"] = "北", ["Amount_Sum"] = 100m },
                    new() { ["Region"] = "南", ["Amount_Sum"] = 200m },
                },
                grandTotal: new()
                {
                    ["Region"] = null,
                    ["Amount_Sum"] = 300m,
                });

            var bytes = AnalysisExcelExporter.Export(resp);
            using var wb = OpenWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);

            Assert.AreEqual(3, sheet.LastRowNum,
                "With GrandTotalRow, sheet must have one extra row.");

            var totalRow = sheet.GetRow(3);
            Assert.IsNotNull(totalRow);

            // First null dimension cell carries the "總計" label.
            Assert.AreEqual("總計", totalRow.GetCell(0).StringCellValue);

            // Numeric measure cell carries the sum.
            Assert.AreEqual(CellType.Numeric, totalRow.GetCell(1).CellType);
            Assert.AreEqual(300d, totalRow.GetCell(1).NumericCellValue);
        }

        [TestMethod]
        public void Grand_total_row_label_only_appears_in_first_null_dimension()
        {
            var resp = MakeResponseWithTotal(
                new() { "Region", "Channel", "Amount_Sum" },
                new()
                {
                    new() { ["Region"] = "北", ["Channel"] = "A", ["Amount_Sum"] = 100m },
                },
                grandTotal: new()
                {
                    ["Region"] = null,
                    ["Channel"] = null,
                    ["Amount_Sum"] = 100m,
                });

            var bytes = AnalysisExcelExporter.Export(resp);
            using var wb = OpenWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var totalRow = sheet.GetRow(2);

            Assert.AreEqual("總計", totalRow.GetCell(0).StringCellValue,
                "Label appears in the first null dimension cell.");
            Assert.AreEqual("", totalRow.GetCell(1).StringCellValue,
                "Subsequent null dimension cells stay blank to mirror typical hand-written Excel totals.");
        }

        [TestMethod]
        public void Grand_total_row_uses_bold_font_with_yellow_fill()
        {
            var resp = MakeResponseWithTotal(
                new() { "Region", "Amount_Sum" },
                new()
                {
                    new() { ["Region"] = "北", ["Amount_Sum"] = 100m },
                },
                grandTotal: new()
                {
                    ["Region"] = null,
                    ["Amount_Sum"] = 100m,
                });

            var bytes = AnalysisExcelExporter.Export(resp);
            using var wb = OpenWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var totalRow = sheet.GetRow(2);
            var labelCell = totalRow.GetCell(0);
            var sumCell = totalRow.GetCell(1);

            var labelFont = wb.GetFontAt(labelCell.CellStyle.FontIndex);
            var sumFont = wb.GetFontAt(sumCell.CellStyle.FontIndex);

            Assert.IsTrue(labelFont.IsBold);
            Assert.IsTrue(sumFont.IsBold);
            Assert.AreEqual(IndexedColors.LightYellow.Index, labelCell.CellStyle.FillForegroundColor);
            Assert.AreEqual(IndexedColors.LightYellow.Index, sumCell.CellStyle.FillForegroundColor);
        }

        [TestMethod]
        public void Grand_total_null_measure_cell_renders_as_empty_string()
        {
            // Avg / DistinctCount intentionally emit null in the request →
            // exporter must render a clean empty cell, not "null".
            var resp = MakeResponseWithTotal(
                new() { "Region", "Amount_Avg" },
                new()
                {
                    new() { ["Region"] = "北", ["Amount_Avg"] = 100m },
                },
                grandTotal: new()
                {
                    ["Region"] = null,
                    ["Amount_Avg"] = null,
                });

            var bytes = AnalysisExcelExporter.Export(resp);
            using var wb = OpenWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var totalRow = sheet.GetRow(2);
            var avgCell = totalRow.GetCell(1);

            // Cells were created but null measure rendered as empty string,
            // not numeric 0 (which would falsely suggest "average is zero").
            Assert.AreEqual(CellType.String, avgCell.CellType);
            Assert.AreEqual("", avgCell.StringCellValue);
        }

        [TestMethod]
        public void Grand_total_decimal_value_renders_as_numeric_cell()
        {
            var resp = MakeResponseWithTotal(
                new() { "Region", "Amount_Sum" },
                new()
                {
                    new() { ["Region"] = "北", ["Amount_Sum"] = 1234.56m },
                },
                grandTotal: new()
                {
                    ["Region"] = null,
                    ["Amount_Sum"] = 1234.56m,
                });

            var bytes = AnalysisExcelExporter.Export(resp);
            using var wb = OpenWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var totalRow = sheet.GetRow(2);
            var sumCell = totalRow.GetCell(1);

            Assert.AreEqual(CellType.Numeric, sumCell.CellType,
                "Decimal grand total must use a numeric cell so Excel can SUM/format it like the body rows.");
            Assert.AreEqual(1234.56d, sumCell.NumericCellValue, 0.0001d);
        }
    }
}
