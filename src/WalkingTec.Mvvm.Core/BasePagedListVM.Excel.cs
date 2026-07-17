#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using NPOI.HSSF.Util;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.Streaming;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core
{
    public partial class BasePagedListVM<TModel, TSearcher> : BaseVM, IBasePagedListVM<TModel, TSearcher>
        where TModel : TopBasePoco
        where TSearcher : BaseSearcher
    {
        #region GenerateExcel

        /// <summary>
        /// 生成Excel
        /// </summary>
        /// <returns>生成的Excel文件</returns>
        public virtual byte[] GenerateExcel()
        {
            NeedPage = false;

            //获取导出的表头
            if (GridHeaders == null)
            {
                GetHeaders();
            }

            //去掉ID列和Action列
            RemoveActionAndIdColumn();

            var query = SearcherMode== ListVMSearchModeEnum.CheckExport? GetCheckedExportQuery() : GetExportQuery();
            int listcount = query.Count();
            ExportRowCount = listcount;

            //获取分成Excel的个数
            ExportMaxCount = ExportMaxCount == 0 ? 1000000 : (ExportMaxCount > 1000000 ? 1000000 : ExportMaxCount);
            ExportExcelCount = listcount < ExportMaxCount ? 1 : ((listcount % ExportMaxCount) == 0 ? (listcount / ExportMaxCount) : (listcount / ExportMaxCount + 1));

            //如果是1，直接下载Excel，如果是多个，下载ZIP包
            if (ExportExcelCount == 1)
            {
                List<TModel> data = [.. query];
                return DownLoadExcel(data);
            }
            else
            {
                Guid g = Guid.NewGuid();
                var FileName = typeof(TModel).Name + "_" + Wtm!.TimeProvider.GetLocalNow().DateTime.ToString("yyyyMMddHHmmssffff");
                //文件根目录 — use Path.Combine for cross-platform compatibility
                string RootPath = Path.Combine(Wtm!.ConfigInfo.HostRoot, $"export{g}");

                //文件夹目录
                string FilePath = Path.Combine(RootPath, "FileFolder");

                //压缩包目录
                string ZipPath = Path.Combine(RootPath, $"{g}.zip");

                byte[] bt;
                try
                {
                    //打开文件夹
                    DirectoryInfo FileFolder = new DirectoryInfo(FilePath);
                    if (!FileFolder.Exists)
                    {
                        //创建文件夹
                        FileFolder.Create();
                    }
                    else
                    {
                        //清空文件夹
                        FileSystemInfo[] Files = FileFolder.GetFileSystemInfos();
                        foreach (var item in Files)
                        {
                            if (item is DirectoryInfo)
                            {
                                DirectoryInfo Directory = new DirectoryInfo(item.FullName);
                                Directory.Delete(true);
                            }
                            else
                            {
                                File.Delete(item.FullName);
                            }
                        }
                    }
                    for (int i = 0; i < ExportExcelCount; i++)
                    {
                        List<TModel> data = [.. query.Skip(i * ExportMaxCount).Take(ExportMaxCount)];
                        var WorkBook = GenerateWorkBook(data);
                        string SavePath = Path.Combine(FilePath, $"{FileName}_{i + 1}.xlsx");
                        using (FileStream FS = new FileStream(SavePath, FileMode.CreateNew))
                        {
                            WorkBook.Write(FS);
                        }
                    }

                    //生成压缩包
                    ZipFile.CreateFromDirectory(FilePath, ZipPath);

                    //读取压缩包
                    using (FileStream ZipFS = new FileStream(ZipPath, FileMode.Open, FileAccess.Read))
                    {
                        bt = new byte[ZipFS.Length];
                        ZipFS.Read(bt, 0, bt.Length);
                    }
                }
                finally
                {
                    // Best-effort cleanup: remove the temp directory regardless of success or failure.
                    try { if (Directory.Exists(RootPath)) Directory.Delete(RootPath, true); }
                    catch { /* best-effort cleanup */ }
                }
                return bt;
            }
        }

        /// <summary>
        /// 根据集合生成单个Excel
        /// </summary>
        /// <param name="List"></param>
        /// <returns></returns>
        private IWorkbook GenerateWorkBook(List<TModel> List)
        {
            IWorkbook book = new XSSFWorkbook();
            ISheet sheet = book.CreateSheet();
            IRow row = sheet.CreateRow(0);

            //创建表头样式
            ICellStyle headerStyle = book.CreateCellStyle();
            headerStyle.FillBackgroundColor = ExportTitleBackColor == null ? HSSFColor.LightBlue.Index : ExportTitleBackColor.Value;
            headerStyle.FillPattern = FillPattern.SolidForeground;
            headerStyle.FillForegroundColor = ExportTitleBackColor == null ? HSSFColor.LightBlue.Index : ExportTitleBackColor.Value;
            headerStyle.BorderBottom = BorderStyle.Thin;
            headerStyle.BorderTop = BorderStyle.Thin;
            headerStyle.BorderLeft = BorderStyle.Thin;
            headerStyle.BorderRight = BorderStyle.Thin;
            IFont font = book.CreateFont();
            font.FontName = "Calibri";
            font.FontHeightInPoints = 12;
            font.Color = ExportTitleFontColor == null ? HSSFColor.Black.Index : ExportTitleFontColor.Value;
            headerStyle.SetFont(font);

            ICellStyle cellStyle = book.CreateCellStyle();
            cellStyle.BorderBottom = BorderStyle.Thin;
            cellStyle.BorderTop = BorderStyle.Thin;
            cellStyle.BorderLeft = BorderStyle.Thin;
            cellStyle.BorderRight = BorderStyle.Thin;

            //生成表头
            int max = MakeExcelHeader(sheet, GridHeaders!, 0, 0, headerStyle);

            //放入数据
            // Perf(#674): flatten the header tree into its bottom columns ONCE for this
            // chunk's row loop instead of re-walking BottomChildren (which rebuilds a new
            // List) for every row. GridHeaders is unchanged across the loop
            // (RemoveActionAndIdColumn already ran before GenerateWorkBook is invoked), so
            // the flattened set is identical for every row — behaviour-preserving.
            var flatCols = GridHeaders!.SelectMany(h => h.BottomChildren).ToList();
            var ColIndex = 0;
            for (int i = 0; i < List.Count; i++)
            {
                ColIndex = 0;
                var DR = sheet.CreateRow(i + max);
                foreach (var col in flatCols)
                {
                    //处理枚举变量的多语言
                    bool IsEmunBoolParp = false;
                    var proType = col.FieldType;
                    if (proType != null && proType.IsEnumOrNullableEnum())
                    {
                        IsEmunBoolParp = true;
                    }                       //获取数据，并过滤特殊字符
                    string text = Helper.CoreRegexes.HtmlTagStripRegex().Replace(col.GetText(List[i]).ToString() ?? "", String.Empty);

                    //处理枚举变量的多语言
                    if (IsEmunBoolParp)
                    {
                        string enumdisplay = PropertyHelper.GetEnumDisplayName(proType, text);
                        if (string.IsNullOrEmpty(enumdisplay) == false)
                        {
                            text = enumdisplay;
                        }

                        else
                        {
                            if (int.TryParse(text, out int enumvalue))
                            {
                                text = PropertyHelper.GetEnumDisplayName(proType, enumvalue);
                            }
                        }
                    }

                    //建立excel单元格
                    ICell cell;
                    if (col.FieldType?.IsNumber() == true)
                    {
                        double trydouble = 0;
                        cell = DR.CreateCell(ColIndex, CellType.Numeric);
                        if (double.TryParse(text, out trydouble))
                        {
                            cell.SetCellValue(trydouble);
                        }

                    }
                    else
                    {
                        cell = DR.CreateCell(ColIndex);
                        cell.SetCellValue(text);
                    }
                    cell.CellStyle = cellStyle;
                    ColIndex++;
                }
            }
            return book;
        }

        private byte[] DownLoadExcel(List<TModel> data)
        {
            var book = GenerateWorkBook(data);
            byte[] rv = Array.Empty<byte>();
            using (MemoryStream ms = new MemoryStream())
            {
                book.Write(ms);
                rv = ms.ToArray();
            }
            return rv;
        }

        /// <summary>
        /// 生成Excel的表头
        /// </summary>
        /// <param name="sheet"></param>
        /// <param name="cols"></param>
        /// <param name="rowIndex"></param>
        /// <param name="colIndex"></param>
        /// <param name="style"></param>
        /// <returns></returns>
        private int MakeExcelHeader(ISheet sheet, IEnumerable<IGridColumn<TModel>> cols, int rowIndex, int colIndex, ICellStyle style)
        {
            var row = sheet.GetRow(rowIndex);
            if (row == null)
            {
                row = sheet.CreateRow(rowIndex);
            }
            int maxLevel = cols.Select(x => x.MaxLevel).Max();
            //循环所有列
            foreach (var col in cols)
            {
                //添加新单元格
                var cell = row.CreateCell(colIndex);
                cell.CellStyle = style;
                cell.SetCellValue(col.Title);
                var bcount = col.BottomChildren.Count();
                var rowspan = 0;
                if (rowIndex >= 0)
                {
                    rowspan = maxLevel - col.MaxLevel;
                }
                var cellRangeAddress = new CellRangeAddress(rowIndex, rowIndex + rowspan, colIndex, colIndex + bcount - 1);
                if (rowspan > 0 || bcount > 1)
                {
                    sheet.AddMergedRegion(cellRangeAddress); // NPOI 2.6+: must span 2+ cells
                    cell.CellStyle.Alignment = HorizontalAlignment.Center;
                    cell.CellStyle.VerticalAlignment = VerticalAlignment.Center;
                }
                for (int i = cellRangeAddress.FirstRow; i <= cellRangeAddress.LastRow; i++)
                {
                    IRow r = CellUtil.GetRow(i, sheet);
                    for (int j = cellRangeAddress.FirstColumn; j <= cellRangeAddress.LastColumn; j++)
                    {
                        ICell c = CellUtil.GetCell(r, (short)j);
                        c.CellStyle = style;
                    }
                }
                if (col.Children != null && col.Children.Count() > 0)
                {
                    MakeExcelHeader(sheet, col.Children, rowIndex + rowspan + 1, colIndex, style);
                }
                colIndex += bcount;
            }
            return maxLevel;
        }

        #endregion

        #region Streaming Export (opt-in)

        /// <summary>
        /// When <see langword="true"/>, the controller's streaming export action
        /// (<c>GetExportExcelStream</c>) is available and uses SXSSF to minimise
        /// peak heap allocations for large grids.  Default is <see langword="false"/>
        /// (existing byte[] path remains the default).
        /// </summary>
        [JsonIgnore]
        public bool UseStreamingExport { get; set; }

        /// <summary>
        /// Writes the export data directly to <paramref name="output"/> using NPOI
        /// <see cref="SXSSFWorkbook"/> (the streaming windowed workbook).  Only a
        /// configurable sliding window of rows is kept in memory; the rest are flushed
        /// to SXSSF temporary files.
        /// <para>
        /// Column layout, headers, title colours, and enum localisation are identical
        /// to the existing <see cref="GenerateExcel"/> path so the produced XLSX is
        /// equivalent.
        /// </para>
        /// <para>
        /// <b>Temp-file cleanup</b>: <see cref="SXSSFWorkbook.Dispose"/> is called in
        /// a <c>finally</c> block, which deletes the SXSSF backing files regardless of
        /// success or failure.
        /// </para>
        /// </summary>
        /// <param name="output">The stream to write the XLSX bytes into.  Must be
        /// writable.  The caller owns the stream lifetime.</param>
        /// <param name="rowWindowSize">
        /// Number of rows kept in memory at a time before flushing.  Defaults to 100.
        /// </param>
        public virtual void GenerateExcelToStream(Stream output, int rowWindowSize = 100)
        {
            NeedPage = false;

            if (GridHeaders == null)
            {
                GetHeaders();
            }

            RemoveActionAndIdColumn();

            var query = SearcherMode == ListVMSearchModeEnum.CheckExport
                ? GetCheckedExportQuery()
                : GetExportQuery();

            int listcount = query.Count();
            ExportRowCount = listcount;

            var sxssf = new SXSSFWorkbook(rowWindowSize);
            try
            {
                ISheet sheet = sxssf.CreateSheet();
                IRow row = sheet.CreateRow(0);

                // Header style — mirrors GenerateWorkBook()
                ICellStyle headerStyle = sxssf.CreateCellStyle();
                headerStyle.FillBackgroundColor = ExportTitleBackColor ?? HSSFColor.LightBlue.Index;
                headerStyle.FillPattern = FillPattern.SolidForeground;
                headerStyle.FillForegroundColor = ExportTitleBackColor ?? HSSFColor.LightBlue.Index;
                headerStyle.BorderBottom = BorderStyle.Thin;
                headerStyle.BorderTop = BorderStyle.Thin;
                headerStyle.BorderLeft = BorderStyle.Thin;
                headerStyle.BorderRight = BorderStyle.Thin;
                IFont font = sxssf.CreateFont();
                font.FontName = "Calibri";
                font.FontHeightInPoints = 12;
                font.Color = ExportTitleFontColor ?? HSSFColor.Black.Index;
                headerStyle.SetFont(font);

                ICellStyle cellStyle = sxssf.CreateCellStyle();
                cellStyle.BorderBottom = BorderStyle.Thin;
                cellStyle.BorderTop = BorderStyle.Thin;
                cellStyle.BorderLeft = BorderStyle.Thin;
                cellStyle.BorderRight = BorderStyle.Thin;

                // Build header rows
                int dataStartRow = MakeExcelHeader(sheet, GridHeaders!, 0, 0, headerStyle);

                // Write data rows in a streaming fashion
                // Perf(#674): flatten the header tree ONCE before the row loop instead of
                // re-walking BottomChildren per row. GridHeaders is fixed for the duration
                // of this export (RemoveActionAndIdColumn already ran above), so the
                // flattened set is identical for every row.
                var flatCols = GridHeaders!.SelectMany(h => h.BottomChildren).ToList();
                int rowIdx = 0;
                foreach (var item in query)
                {
                    int colIndex = 0;
                    IRow dr = sheet.CreateRow(rowIdx + dataStartRow);
                    foreach (var col in flatCols)
                    {
                        bool isEnumBoolProp = col.FieldType != null && col.FieldType.IsEnumOrNullableEnum();
                        string text = Helper.CoreRegexes.HtmlTagStripRegex().Replace(col.GetText(item).ToString() ?? "", string.Empty);

                        if (isEnumBoolProp)
                        {
                            string enumDisplay = PropertyHelper.GetEnumDisplayName(col.FieldType, text);
                            if (!string.IsNullOrEmpty(enumDisplay))
                            {
                                text = enumDisplay;
                            }
                            else if (int.TryParse(text, out int enumValue))
                            {
                                text = PropertyHelper.GetEnumDisplayName(col.FieldType, enumValue);
                            }
                        }

                        ICell cell;
                        if (col.FieldType?.IsNumber() == true && double.TryParse(text, out double numVal))
                        {
                            cell = dr.CreateCell(colIndex, CellType.Numeric);
                            cell.SetCellValue(numVal);
                        }
                        else
                        {
                            cell = dr.CreateCell(colIndex);
                            cell.SetCellValue(text);
                        }
                        cell.CellStyle = cellStyle;
                        colIndex++;
                    }
                    rowIdx++;
                }

                // NPOI IWorkbook.Write(Stream) closes the stream it receives (Apache POI
                // legacy behaviour).  Wrap output in a NonClosingWrapper so the caller's
                // stream remains open and usable after this call returns.
                using var wrapper = new NonClosingStreamWrapper(output);
                sxssf.Write(wrapper);
            }
            finally
            {
                // Deletes SXSSF backing temp files — must be called even on exception.
                sxssf.Dispose();
            }
        }

        /// <summary>
        /// Writes the export data as UTF-8 CSV directly to <paramref name="output"/>.
        /// No NPOI workbook is created, so memory usage is minimal.  Column selection
        /// and enum localisation match the existing Excel export.
        /// </summary>
        /// <param name="output">The stream to write CSV bytes into.  The caller owns
        /// the stream lifetime.</param>
        public virtual void GenerateCsvToStream(Stream output)
        {
            NeedPage = false;

            if (GridHeaders == null)
            {
                GetHeaders();
            }

            RemoveActionAndIdColumn();

            var query = SearcherMode == ListVMSearchModeEnum.CheckExport
                ? GetCheckedExportQuery()
                : GetExportQuery();

            int listcount = query.Count();
            ExportRowCount = listcount;

            using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 65536, leaveOpen: true);

            // Perf(#674): flatten the header tree ONCE — reused for both the header line
            // and every data row below — instead of re-walking BottomChildren per row.
            // GridHeaders is fixed for the duration of this export (RemoveActionAndIdColumn
            // already ran above), so the flattened set is identical for every row.
            var flatCols = GridHeaders!.SelectMany(h => h.BottomChildren).ToList();

            // Write header line
            var headerParts = new List<string>();
            foreach (var col in flatCols)
            {
                headerParts.Add(CsvEscape(col.Title ?? string.Empty));
            }
            writer.WriteLine(string.Join(",", headerParts));

            // Write data rows
            foreach (var item in query)
            {
                var parts = new List<string>();
                foreach (var col in flatCols)
                {
                    bool isEnumBoolProp = col.FieldType != null && col.FieldType.IsEnumOrNullableEnum();
                    string text = Helper.CoreRegexes.HtmlTagStripRegex().Replace(col.GetText(item).ToString() ?? "", string.Empty);

                    if (isEnumBoolProp)
                    {
                        string enumDisplay = PropertyHelper.GetEnumDisplayName(col.FieldType, text);
                        if (!string.IsNullOrEmpty(enumDisplay))
                        {
                            text = enumDisplay;
                        }
                        else if (int.TryParse(text, out int enumValue))
                        {
                            text = PropertyHelper.GetEnumDisplayName(col.FieldType, enumValue);
                        }
                    }

                    parts.Add(CsvEscape(text));
                }
                writer.WriteLine(string.Join(",", parts));
            }
        }

        /// <summary>RFC-4180 CSV field escaping.</summary>
        private static string CsvEscape(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        /// <summary>
        /// Delegates all <see cref="Stream"/> members to an inner stream but suppresses
        /// <see cref="Dispose"/> and <see cref="Close"/> so that NPOI's
        /// <c>IWorkbook.Write(Stream)</c> — which closes its argument — cannot close the
        /// caller-owned stream.
        /// </summary>
        private sealed class NonClosingStreamWrapper(Stream inner) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
            // Suppress Dispose/Close — the caller owns the inner stream.
            protected override void Dispose(bool disposing) { /* intentionally no-op */ }
            public override void Close() { /* intentionally no-op */ }
        }

        #endregion
    }
}
