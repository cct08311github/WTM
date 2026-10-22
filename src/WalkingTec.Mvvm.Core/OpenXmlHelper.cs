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
        /// 判断单元格套用的数字格式是否为日期／时间格式。
        /// 内置格式按 ID 判断（14–22、27–36、45–47、50–58，含东亚语系的内置日期格式），
        /// 自定义格式（ID &gt;= 164）按 FormatCode 判断。找不到样式时返回 false。
        /// </summary>
        public static bool IsDateFormattedCell(Cell cell, WorkbookPart workbookPart)
        {
            if (cell == null || workbookPart == null) return false;
            var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
            var cellFormats = stylesheet?.CellFormats;
            if (cellFormats == null) return false;

            int styleIndex = (int)(cell.StyleIndex?.Value ?? 0u);
            var format = cellFormats.Elements<CellFormat>().ElementAtOrDefault(styleIndex);
            if (format == null) return false;

            uint formatId = format.NumberFormatId?.Value ?? 0u;
            if (IsBuiltInDateFormatId(formatId)) return true;
            if (formatId < 164) return false;

            var numberingFormat = stylesheet.NumberingFormats?.Elements<NumberingFormat>()
                .FirstOrDefault(n => n.NumberFormatId?.Value == formatId);
            return IsDateFormatCode(numberingFormat?.FormatCode?.Value);
        }

        private static bool IsBuiltInDateFormatId(uint id)
        {
            return (id >= 14 && id <= 22) || (id >= 27 && id <= 36) || (id >= 45 && id <= 47) || (id >= 50 && id <= 58);
        }

        /// <summary>
/// 自定义格式代码是否表示日期／时间：先去掉 "..." 文字、[...] 条件与颜色（[h]、[mm]、[ss] 时间经过格式保留）
        /// 以及反斜杠转义字符，剩余部分含 y、d、h、s、m 之一即视为日期／时间（数字格式如 0.00 不含这些字母）。
        /// </summary>
        private static bool IsDateFormatCode(string formatCode)
        {
            if (string.IsNullOrEmpty(formatCode)) return false;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < formatCode.Length; i++)
            {
                char c = formatCode[i];
                if (c == '"')
                {
                    int end = formatCode.IndexOf('"', i + 1);
                    i = end < 0 ? formatCode.Length : end;
                }
                else if (c == '[')
                {
                    int end = formatCode.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        i = formatCode.Length;
                    }
                    else
                    {
                        // [h]、[mm]、[ss] 是时间经过格式，属于时间；[Red]、[$-409]、[>100] 等不是
                        string inner = formatCode.Substring(i + 1, end - i - 1).ToLowerInvariant();
                        if (inner.Length > 0 && inner.All(ch => ch == 'h' || ch == 'm' || ch == 's'))
                        {
                            sb.Append(inner);
                        }
                        i = end;
                    }
                }
                else if (c == '\\')
                {
                    i++;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().IndexOfAny(new[] { 'y', 'Y', 'd', 'D', 'h', 'H', 's', 'S', 'm', 'M' }) >= 0;
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
