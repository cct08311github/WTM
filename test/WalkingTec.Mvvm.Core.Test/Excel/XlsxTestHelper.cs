using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;

namespace WalkingTec.Mvvm.Core.Test.Excel
{
    /// <summary>
    /// Reads generated .xlsx bytes back with DocumentFormat.OpenXml so tests can assert on what a user would see in Excel.
    /// </summary>
    internal static class XlsxTestHelper
    {
        /// <summary>The zip magic number every xlsx starts with.</summary>
        public static bool LooksLikeZip(byte[] bytes) => bytes != null && bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B;

        public static IReadOnlyList<(string Name, bool Hidden)> SheetNames(byte[] bytes)
        {
            using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
            return doc.WorkbookPart.Workbook.GetFirstChild<Sheets>().Elements<Sheet>()
                .OrderBy(s => s.SheetId.Value)
                .Select(s => (s.Name.Value, s.State?.Value == SheetStateValues.Hidden))
                .ToList();
        }

        /// <summary>
        /// Returns the sheet as rows of cell texts, placing every cell by its A1 reference
        /// (so omitted blank cells do not shift the following ones). Missing cells are "".
        /// </summary>
        public static List<string[]> ReadSheet(byte[] bytes, int sheetIndex = 0)
        {
            using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
            var workbook = doc.WorkbookPart;
            var sheet = workbook.Workbook.GetFirstChild<Sheets>().Elements<Sheet>().OrderBy(s => s.SheetId.Value).ElementAt(sheetIndex);
            var part = (WorksheetPart)workbook.GetPartById(sheet.Id);
            var rows = new List<string[]>();
            foreach (var row in part.Worksheet.GetFirstChild<SheetData>().Elements<Row>())
            {
                var cells = row.Elements<Cell>().Select(c => (Col: ColumnIndex(c.CellReference.Value), Text: CellText(c, workbook))).ToList();
                var width = cells.Count == 0 ? 0 : cells.Max(c => c.Col) + 1;
                var values = Enumerable.Repeat(string.Empty, width).ToArray();
                foreach (var cell in cells)
                {
                    values[cell.Col] = cell.Text;
                }
                rows.Add(values);
            }
            return rows;
        }

        public static string CellText(Cell cell, WorkbookPart workbook)
        {
            if (cell.DataType?.Value == CellValues.InlineString)
            {
                return cell.InlineString?.InnerText ?? string.Empty;
            }
            if (cell.DataType?.Value == CellValues.SharedString)
            {
                var table = workbook.SharedStringTablePart?.SharedStringTable;
                return table?.Elements<SharedStringItem>().ElementAt(int.Parse(cell.CellValue.Text)).InnerText ?? string.Empty;
            }
            return cell.CellValue?.Text ?? string.Empty;
        }

        /// <summary>A1 column letters to zero based index (A=0, B=1, AA=26).</summary>
        public static int ColumnIndex(string cellReference)
        {
            var index = 0;
            foreach (var ch in cellReference.TakeWhile(char.IsLetter))
            {
                index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            return index - 1;
        }

        /// <summary>OOXML schema violations (description + location), materialised before the package is closed.</summary>
        public static List<string> SchemaErrors(byte[] bytes)
        {
            using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
            return new OpenXmlValidator(FileFormatVersions.Office2010).Validate(doc)
                .Select(e => e.Description + " @ " + e.Path?.XPath)
                .ToList();
        }
    }
}
