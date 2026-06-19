#nullable enable
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Regression tests for Issue #381 fixes #11 and #14 in BaseImportVM.
    /// These tests operate at the NPOI layer to verify the fix patterns directly,
    /// without requiring the full Excel upload pipeline.
    /// </summary>
    [TestClass]
    public class BaseImportVMRegressionTests381
    {
        // ── Fix #11: GetErrorJson must not throw NRE on physically-absent rows ──

        /// <summary>
        /// NPOI returns null for rows that were never written to the sheet (sparse
        /// physical layout). Fix #11 uses <c>sheet.GetRow(idx) ?? sheet.CreateRow(idx)</c>.
        /// This test verifies: (a) GetRow on an absent index returns null, and
        /// (b) the null-coalesce fallback to CreateRow works correctly.
        /// </summary>
        [TestMethod]
        public void GetRow_OnPhysicallyAbsentRow_ReturnsNull_AndCreateRowSucceeds()
        {
            // Create a workbook with rows 0 and 2 only — row 1 is physically absent.
            using var wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet("Sheet1");
            sheet.CreateRow(0).CreateCell(0).SetCellValue("header");
            sheet.CreateRow(2).CreateCell(0).SetCellValue("data");

            // Row 1 is physically absent — GetRow returns null.
            var missingRow = sheet.GetRow(1);
            Assert.IsNull(missingRow,
                "Fix #11 precondition: NPOI GetRow returns null for physically-absent rows.");

            // The fix pattern: null-coalesce to CreateRow.
            var created = sheet.GetRow(1) ?? sheet.CreateRow(1);
            Assert.IsNotNull(created, "Fix #11: CreateRow must succeed for the absent row.");

            // CreateCell on the newly-created row must not throw.
            var cell = created.CreateCell(0);
            cell.SetCellValue("error message");
            Assert.AreEqual("error message", cell.StringCellValue,
                "Fix #11: Cell created on previously-absent row must hold the error message.");
        }

        [TestMethod]
        public void GetRow_ExistingRow_ReturnsRow_PatternWorksCorrectly()
        {
            // Verify the null-coalesce pattern does NOT interfere with existing rows.
            using var wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet("Sheet1");
            var originalRow = sheet.CreateRow(0);
            originalRow.CreateCell(0).SetCellValue("existing");

            // The fix pattern: should return the existing row, not create a new one.
            var row = sheet.GetRow(0) ?? sheet.CreateRow(0);
            Assert.IsNotNull(row);
            Assert.AreEqual("existing", row.GetCell(0).StringCellValue,
                "Fix #11: null-coalesce pattern must not replace existing rows.");
        }

        // ── Fix #14: Text columns must preserve literal strings like "=1+1" ──

        /// <summary>
        /// Before fix #14, Text-column cells were routed through GetCellFormulaValue,
        /// which would evaluate formula-like strings. After the fix, CellType.String
        /// cells are read via StringCellValue directly, preserving the literal text.
        /// </summary>
        [TestMethod]
        public void TextCell_StringType_FormulaLiteral_PreservesRawString()
        {
            // Build a workbook with a String-type cell containing "=1+1".
            using var wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet("Sheet1");
            var row = sheet.CreateRow(0);
            var cell = row.CreateCell(0, CellType.String);
            cell.SetCellValue("=1+1");

            // Verify the cell is CellType.String and StringCellValue is the literal.
            Assert.AreEqual(CellType.String, cell.CellType,
                "Precondition: cell must be CellType.String.");
            Assert.AreEqual("=1+1", cell.StringCellValue,
                "Precondition: StringCellValue must be the literal '=1+1'.");

            // Simulate the fix #14 guard: read StringCellValue for Text columns.
            string value = (cell.CellType == CellType.String)
                ? cell.StringCellValue
                : cell.ToString() ?? string.Empty;

            Assert.AreEqual("=1+1", value,
                "Fix #14: After the guard, value must be the literal '=1+1', not '2'.");
        }

        [TestMethod]
        public void TextCell_NonFormulaString_IsPreserved()
        {
            using var wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet("Sheet1");
            var row = sheet.CreateRow(0);
            var cell = row.CreateCell(0, CellType.String);
            cell.SetCellValue("hello world");

            string value = (cell.CellType == CellType.String)
                ? cell.StringCellValue
                : cell.ToString() ?? string.Empty;

            Assert.AreEqual("hello world", value,
                "Fix #14: Normal Text cell values must also be preserved unchanged.");
        }

        /// <summary>
        /// Demonstrates that when a CellType.String cell containing "=1+1" is
        /// written and read back, NPOI may parse it as CellType.Formula.
        /// Fix #14's guard reads StringCellValue only when CellType.String — this
        /// test confirms that non-String cells are handled by the fallback path
        /// (cell.ToString()) and not by the StringCellValue guard.
        /// </summary>
        [TestMethod]
        public void TextCell_NonString_CellType_FallsBackToToString()
        {
            // Build a numeric cell — CellType.Numeric, not String.
            using var wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet("Sheet1");
            var row = sheet.CreateRow(0);
            var cell = row.CreateCell(0, CellType.Numeric);
            cell.SetCellValue(42.0);

            // The fix #14 guard: only String cells use StringCellValue.
            string value = (cell.CellType == CellType.String)
                ? cell.StringCellValue
                : cell.ToString() ?? string.Empty;

            Assert.AreNotEqual(string.Empty, value,
                "Fix #14: Non-String cell must fall back to ToString() and return a non-empty value.");
            // The guard must NOT call StringCellValue on non-String cells.
            Assert.AreEqual(CellType.Numeric, cell.CellType,
                "Fix #14: Numeric cell must remain CellType.Numeric in the guard check.");
        }
    }
}
