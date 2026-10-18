using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// OpenXML Excel 辅助类
    /// </summary>
    public static class OpenXmlHelper
    {
        /// <summary>
        /// 获取或创建 WorkbookPart
        /// </summary>
        public static WorkbookPart GetOrCreateWorkbookPart(SpreadsheetDocument doc)
        {
            if (doc.WorkbookPart == null)
            {
                doc.AddWorkbookPart();
                doc.WorkbookPart.Workbook = new Workbook();
                Sheets sheets = new Sheets();
                doc.WorkbookPart.Workbook.AppendChild(sheets);
                doc.WorkbookPart.Workbook.Save();
            }
            return doc.WorkbookPart;
        }

        /// <summary>
        /// 获取或创建 WorksheetPart
        /// </summary>
        public static WorksheetPart GetOrCreateWorksheetPart(SpreadsheetDocument doc, uint sheetIndex)
        {
            var workbookPart = GetOrCreateWorkbookPart(doc);
            Sheets sheets = workbookPart.Workbook.GetFirstChild<Sheets>();

            Sheet sheet = sheets.Elements<Sheet>().FirstOrDefault(s => s.SheetId == sheetIndex);
            if (sheet != null)
            {
                return (WorksheetPart)workbookPart.GetPartById(sheet.Id);
            }

            WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(new SheetData());

            uint maxSheetId = sheets.Elements<Sheet>().Any()
                ? sheets.Elements<Sheet>().Max(s => s.SheetId.Value)
                : 0u;

            Sheet newSheet = new Sheet()
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = maxSheetId + 1,
                Name = $"Sheet{maxSheetId + 1}"
            };
            sheets.AppendChild(newSheet);
            workbookPart.Workbook.Save();

            return worksheetPart;
        }

        /// <summary>
        /// 获取 SheetData
        /// </summary>
        public static SheetData GetSheetData(WorksheetPart worksheetPart)
        {
            return worksheetPart.Worksheet.GetFirstChild<SheetData>()
                ?? worksheetPart.Worksheet.AppendChild(new SheetData());
        }

        /// <summary>
        /// 获取或创建行
        /// </summary>
        public static Row GetOrCreateRow(SheetData sheetData, int rowIndex)
        {
            Row row = sheetData.Elements<Row>().FirstOrDefault(r => r.RowIndex == (uint)rowIndex);
            if (row == null)
            {
                row = new Row() { RowIndex = (uint)rowIndex };
                // 插入到正确位置
                var rows = sheetData.Elements<Row>().ToList();
                int insertIndex = rows.FindIndex(r => r.RowIndex > (uint)rowIndex);
                if (insertIndex >= 0)
                    sheetData.InsertAt(row, insertIndex);
                else
                    sheetData.AppendChild(row);
            }
            return row;
        }

        /// <summary>
        /// 创建单元格
        /// </summary>
        public static Cell CreateCell(string reference, string value, CellValues dataType, uint? styleIndex = null)
        {
            var cell = new Cell() { CellReference = reference };
            if (styleIndex.HasValue)
                cell.StyleIndex = styleIndex.Value;

            if (dataType == CellValues.String)
            {
                cell.DataType = CellValues.InlineString;
                cell.AppendChild(new InlineString(new Text(value ?? "")));
            }
            else if (dataType == CellValues.Number)
            {
                cell.DataType = CellValues.Number;
                cell.CellValue = new CellValue(value ?? "0");
            }
            else if (dataType == CellValues.Date)
            {
                cell.DataType = CellValues.Date;
                cell.CellValue = new CellValue(value ?? "");
            }
            else
            {
                cell.DataType = CellValues.String;
                cell.CellValue = new CellValue(value ?? "");
            }

            return cell;
        }

        /// <summary>
        /// 获取列字母（0=A, 1=B, ..., 25=Z, 26=AA, ...）
        /// </summary>
        public static string GetColumnLetter(int columnIndex)
        {
            string letter = "";
            while (columnIndex >= 0)
            {
                letter = (char)('A' + columnIndex % 26) + letter;
                columnIndex = columnIndex / 26 - 1;
            }
            return letter;
        }

        /// <summary>
        /// 获取单元格引用字符串
        /// </summary>
        public static string GetCellReference(int columnIndex, int rowIndex)
        {
            return GetColumnLetter(columnIndex) + (rowIndex + 1);
        }

        /// <summary>
        /// 从单元格引用（如 "C2"、"AA10"、"$B$3"）解析出从 0 开始的列索引（A=0, B=1, ..., AA=26）。
        /// 引用为空或不含列字母时返回 -1。
        /// </summary>
        public static int GetColumnIndex(string cellReference)
        {
            if (string.IsNullOrEmpty(cellReference)) return -1;
            int index = 0;
            bool hasLetter = false;
            foreach (char ch in cellReference)
            {
                if (ch == '$' && !hasLetter) continue;
                char upper = char.ToUpperInvariant(ch);
                if (upper < 'A' || upper > 'Z') break;
                hasLetter = true;
                index = index * 26 + (upper - 'A' + 1);
            }
            return hasLetter ? index - 1 : -1;
        }

        /// <summary>
        /// 将颜色转换为 OOXML 要求的 8 位 ARGB 十六进制字符串。
        /// 6 位 RGB (可带 #) 补上不透明的 FF alpha，8 位原样保留。
        /// </summary>
        public static string ToArgb(string color)
        {
            var c = (color ?? string.Empty).Trim().TrimStart('#').ToUpperInvariant();
            return c.Length == 6 ? "FF" + c : c;
        }

        /// <summary>
        /// 计算 SheetProtection 的 password 属性值 (ECMA-376 legacy 16 位哈希，4 位十六进制)。
        /// 该属性必须是 hexBinary，不能直接写明文。
        /// </summary>
        public static string LegacyPasswordHash(string password)
        {
            password ??= string.Empty;
            int hash = 0;
            for (int i = password.Length - 1; i >= 0; i--)
            {
                hash = ((hash >> 14) & 0x01) | ((hash << 1) & 0x7fff);
                hash ^= password[i];
            }
            hash = ((hash >> 14) & 0x01) | ((hash << 1) & 0x7fff);
            hash ^= password.Length;
            hash ^= 0xCE4B;
            return hash.ToString("X4");
        }

        /// <summary>
        /// 创建标题样式
        /// </summary>
        public static Stylesheet CreateStylesheet(string headerBackColor = "B0C4DE", string headerFontColor = "000000")
        {
            var stylesheet = new Stylesheet();

            // 字体: 0=默认, 1=标题
            var fonts = new Fonts(
                new Font(), // 0 - 默认
                new Font(  // 1 - 标题
                    new FontSize() { Val = 12 },
                    new Color() { Rgb = ToArgb(headerFontColor) },
                    new FontName() { Val = "Calibri" }
                )
            );

            // 填充: 0=无, 1=灰色背景
            var fills = new Fills(
                new Fill(new PatternFill() { PatternType = PatternValues.None }),     // 0
                new Fill(new PatternFill() { PatternType = PatternValues.Gray125 }),   // 1
                new Fill(new PatternFill(                                             // 2 - 标题背景色
                    new BackgroundColor() { Rgb = ToArgb(headerBackColor) },
                    new ForegroundColor() { Rgb = ToArgb(headerBackColor) }
                ) { PatternType = PatternValues.Solid })
            );

            // 边框: 0=无, 1=细边框
            var borders = new Borders(
                new Border(), // 0 - 无边框
                new Border(   // 1 - 四面细边框
                    new LeftBorder(new Color() { Auto = true }) { Style = BorderStyleValues.Thin },
                    new RightBorder(new Color() { Auto = true }) { Style = BorderStyleValues.Thin },
                    new TopBorder(new Color() { Auto = true }) { Style = BorderStyleValues.Thin },
                    new BottomBorder(new Color() { Auto = true }) { Style = BorderStyleValues.Thin },
                    new DiagonalBorder()
                )
            );

            // 单元格格式: 0=默认, 1=标题样式, 2=数据样式(带边框)
            var cellFormats = new CellFormats(
                new CellFormat(),                                                     // 0 - 默认
                new CellFormat(                                                        // 1 - 标题
                    new Alignment() { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center }
                ) { FontId = 1, FillId = 2, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true },
                new CellFormat(                                                        // 2 - 数据单元格(带边框)
                    new Alignment() { Vertical = VerticalAlignmentValues.Center, WrapText = true }
                ) { FontId = 0, FillId = 0, BorderId = 1, ApplyFont = true, ApplyBorder = true, ApplyAlignment = true }
            );

            stylesheet.AppendChild(fonts);
            stylesheet.AppendChild(fills);
            stylesheet.AppendChild(borders);
            stylesheet.AppendChild(cellFormats);

            return stylesheet;
        }

        /// <summary>
        /// 从内存流创建 SpreadsheetDocument 用于读取
        /// </summary>
        public static SpreadsheetDocument OpenForReading(Stream stream)
        {
            return SpreadsheetDocument.Open(stream, false);
        }

        /// <summary>
        /// 创建新的 SpreadsheetDocument 用于写入
        /// </summary>
        public static SpreadsheetDocument CreateForWriting(MemoryStream ms)
        {
            return SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook);
        }
    }
}
