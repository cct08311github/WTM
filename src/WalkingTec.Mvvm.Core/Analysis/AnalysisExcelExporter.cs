#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NPOI.SS.UserModel;
using NPOI.SS.UserModel.Charts;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 將 AnalysisQueryResponse 匯出為 xlsx 位元組陣列（使用 NPOI）。
    /// </summary>
    public static class AnalysisExcelExporter
    {
        // Maximum column width in NPOI units (1/256 of a character). Caps at 50 characters.
        internal const int MaxColumnWidth = 50 * 256;

        /// <summary>
        /// 匯出分析結果為 Excel（xlsx），回傳位元組陣列。
        /// </summary>
        /// <param name="result">查詢結果</param>
        /// <param name="includeChart">是否嵌入圖表（預設 false）</param>
        /// <param name="chartType">圖表類型：bar, pie, line, bar-stacked（預設 bar）</param>
        /// <param name="includeMetadata">是否輸出 Metadata 工作表（預設 false）</param>
        public static byte[] Export(AnalysisQueryResponse result, bool includeChart = false, string? chartType = null, bool includeMetadata = false)
        {
            using var workbook = new XSSFWorkbook();
            var sheet = workbook.CreateSheet("Analysis");

            // ── Styles ────────────────────────────────────────────────────────────
            // Header: bold font + light-gray background
            var headerFont = workbook.CreateFont();
            headerFont.IsBold = true;
            var headerStyle = workbook.CreateCellStyle();
            headerStyle.SetFont(headerFont);
            headerStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index;
            headerStyle.FillPattern = FillPattern.SolidForeground;

            // Numeric data: one ICellStyle per MeasureFormat value
            var numericFormat = workbook.CreateDataFormat();
            ICellStyle MakeNumericStyle(string fmt)
            {
                var s = workbook.CreateCellStyle();
                s.DataFormat = numericFormat.GetFormat(fmt);
                return s;
            }
            var formatStyles = new System.Collections.Generic.Dictionary<MeasureFormat, ICellStyle>
            {
                [MeasureFormat.Auto]     = MakeNumericStyle("#,##0.00"),
                [MeasureFormat.Integer]  = MakeNumericStyle("#,##0"),
                [MeasureFormat.Currency] = MakeNumericStyle("¥#,##0.00"),
                [MeasureFormat.Percent]  = MakeNumericStyle("0.00%"),
            };

            // ── Header row — use display names when available ─────────────────────
            var header = sheet.CreateRow(0);
            for (int i = 0; i < result.Columns.Count; i++)
            {
                var col = result.Columns[i];
                var headerText = result.ColumnDisplayNames.TryGetValue(col, out var dn) ? dn : col;
                var cell = header.CreateCell(i);
                cell.SetCellValue(headerText);
                cell.CellStyle = headerStyle;
            }

            // ── Resolve per-column ICellStyle ────────────────────────────────────
            // ColumnFormats carries explicit format from [Measure(Format=...)] attribute.
            // Fall back to numeric auto-detection (Auto style) for legacy responses.
            var colStyles = new ICellStyle?[result.Columns.Count];
            for (int i = 0; i < result.Columns.Count; i++)
            {
                var colKey = result.Columns[i];
                if (result.ColumnFormats.TryGetValue(colKey, out var fmt))
                    colStyles[i] = formatStyles[fmt];
            }
            if (result.ColumnFormats.Count == 0 && result.Rows.Count > 0)
            {
                // Legacy path: no format metadata — detect numeric columns by CLR type.
                for (int c = 0; c < result.Columns.Count; c++)
                {
                    result.Rows[0].TryGetValue(result.Columns[c], out var firstVal);
                    if (firstVal is decimal or double or float or int or long)
                        colStyles[c] = formatStyles[MeasureFormat.Auto];
                }
            }

            // ── Data rows ─────────────────────────────────────────────────────────
            for (int r = 0; r < result.Rows.Count; r++)
            {
                var row = sheet.CreateRow(r + 1);
                for (int c = 0; c < result.Columns.Count; c++)
                {
                    result.Rows[r].TryGetValue(result.Columns[c], out var val);
                    var cell = row.CreateCell(c);
                    if (val is decimal d)
                        cell.SetCellValue((double)d);
                    else if (val is double db)
                        cell.SetCellValue(db);
                    else if (val is float fv)
                        cell.SetCellValue(fv);
                    else if (val is int iv)
                        cell.SetCellValue(iv);
                    else if (val is long lv)
                        cell.SetCellValue(lv);
                    else
                        cell.SetCellValue(val?.ToString() ?? "");

                    if (colStyles[c] != null)
                        cell.CellStyle = colStyles[c];
                }
            }

            // ── Grand-total footer row (when AnalysisQueryRequest.IncludeGrandTotal = true) ──
            // Bold + light-yellow fill so the consumer's eye lands on the
            // rollup without the row blending into ordinary data. Numeric
            // formats inherit from the column so currency / percent / etc.
            // remain consistent with the body.
            if (result.GrandTotalRow != null)
            {
                var totalFont = workbook.CreateFont();
                totalFont.IsBold = true;

                // Pre-build one ICellStyle per distinct DataFormat key to avoid
                // creating a new workbook style object for every grand-total cell
                // (NPOI has a workbook-level style limit; sharing reduces pressure).
                var totalStyleByFormat = new Dictionary<short, ICellStyle>();
                ICellStyle GetOrCreateTotalStyle(ICellStyle? baseStyle)
                {
                    short fmt = baseStyle?.DataFormat ?? 0;
                    if (!totalStyleByFormat.TryGetValue(fmt, out var cached))
                    {
                        var s = workbook.CreateCellStyle();
                        s.DataFormat = fmt;
                        s.SetFont(totalFont);
                        s.FillForegroundColor = IndexedColors.LightYellow.Index;
                        s.FillPattern = FillPattern.SolidForeground;
                        totalStyleByFormat[fmt] = s;
                        cached = s;
                    }
                    return cached;
                }

                var totalRow = sheet.CreateRow(result.Rows.Count + 1);
                bool labelEmitted = false;
                for (int c = 0; c < result.Columns.Count; c++)
                {
                    var colKey = result.Columns[c];
                    result.GrandTotalRow.TryGetValue(colKey, out var val);
                    var cell = totalRow.CreateCell(c);

                    if (val == null)
                    {
                        // First null dimension cell carries the "總計" label
                        // so users see WHY the row exists; subsequent null
                        // cells stay blank to match how Excel users typically
                        // build manual grand-total rows.
                        if (!labelEmitted)
                        {
                            cell.SetCellValue("總計");
                            labelEmitted = true;
                        }
                        else
                        {
                            cell.SetCellValue("");
                        }
                    }
                    else if (val is decimal d)
                        cell.SetCellValue((double)d);
                    else if (val is double db)
                        cell.SetCellValue(db);
                    else if (val is float fv)
                        cell.SetCellValue(fv);
                    else if (val is int iv)
                        cell.SetCellValue(iv);
                    else if (val is long lv)
                        cell.SetCellValue(lv);
                    else
                        cell.SetCellValue(val.ToString() ?? "");

                    cell.CellStyle = GetOrCreateTotalStyle(colStyles[c]);
                }
            }

            // ── Auto-size all columns, cap at MaxColumnWidth ──────────────────────
            for (int i = 0; i < result.Columns.Count; i++)
            {
                // AutoSizeColumn requires system fonts (SixLabors.Fonts). On CI Linux runners
                // without fonts installed, it throws. Fall back to a header-length heuristic.
                try
                {
                    sheet.AutoSizeColumn(i);
                }
                catch
                {
                    var colKey = result.Columns[i];
                    var headerLabel = result.ColumnDisplayNames.TryGetValue(colKey, out var dn2) ? dn2 : colKey;
                    int headerLen = headerLabel.Length;
                    sheet.SetColumnWidth(i, Math.Min(Math.Max(headerLen * 512, 3000), MaxColumnWidth));
                }
                if (sheet.GetColumnWidth(i) > MaxColumnWidth)
                    sheet.SetColumnWidth(i, MaxColumnWidth);
            }

            if (includeChart && result.Rows.Count > 0 && result.Columns.Count >= 2)
            {
                var type = (chartType ?? "bar").ToLowerInvariant();
                switch (type)
                {
                    case "pie":
                        EmbedPieChart(sheet, result);
                        break;
                    case "line":
                        EmbedLineChart(sheet, result);
                        break;
                    default: // bar, bar-stacked, and any unknown type
                        EmbedBarChart(sheet, result);
                        break;
                }
            }






            if (includeMetadata)
            {
                var meta = workbook.CreateSheet("Metadata");
                var rows = new[]
                {
                    ("匯出時間", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"),
                    ("QueryHash",  result.QueryHash  ?? ""),
                    ("資料筆數",       result.TotalCount.ToString()),
                    ("已截斷",         result.Truncated ? "是" : "否"),
                    ("來源資料截斷",   result.DataTruncated ? "是" : "否"),
                    ("來源截斷說明",   result.DataTruncatedMessage ?? ""),
                };
                for (int i = 0; i < rows.Length; i++)
                {
                    var metaRow = meta.CreateRow(i);
                    metaRow.CreateCell(0).SetCellValue(rows[i].Item1);
                    metaRow.CreateCell(1).SetCellValue(rows[i].Item2);
                }
            }
            var ms = new MemoryStream();
            workbook.Write(ms);
            return ms.ToArray();
        }

        internal static (double Divisor, string Unit) ComputeScale(double maxAbsValue)
        {
            var abs = Math.Abs(maxAbsValue);
            if (abs >= 100_000_000) return (100_000_000, "億");
            if (abs >= 1_000_000) return (1_000_000, "百萬");
            if (abs >= 10_000) return (10_000, "萬");
            return (1, "");
        }

        internal static bool DetectDualAxis(AnalysisQueryResponse result, List<int> measureIndices)
        {
            if (measureIndices.Count != 2 || result.Rows.Count == 0) return false;

            double max0 = 0, max1 = 0;
            var col0 = result.Columns[measureIndices[0]];
            var col1 = result.Columns[measureIndices[1]];

            foreach (var row in result.Rows)
            {
                if (row.TryGetValue(col0, out var v0) && v0 is decimal or double or float or int or long)
                {
                    var a0 = Math.Abs(Convert.ToDouble(v0));
                    if (a0 > max0) max0 = a0;
                }
                if (row.TryGetValue(col1, out var v1) && v1 is decimal or double or float or int or long)
                {
                    var a1 = Math.Abs(Convert.ToDouble(v1));
                    if (a1 > max1) max1 = a1;
                }
            }

            if (max0 == 0 || max1 == 0) return false;
            var ratio = max0 > max1 ? max0 / max1 : max1 / max0;
            return ratio >= 10;
        }

        private static (IChart chart, List<int> measureIndices) CreateChartBase(ISheet sheet, AnalysisQueryResponse result)
        {
            var drawing = sheet.CreateDrawingPatriarch();
            // Shift chart down one row when a grand-total footer is
            // present so the chart anchor sits below it cleanly.
            int totalRowOffset = result.GrandTotalRow != null ? 1 : 0;
            int chartTopRow = result.Rows.Count + 3 + totalRowOffset;
            var anchor = drawing.CreateAnchor(0, 0, 0, 0,
                0, chartTopRow, result.Columns.Count + 2, chartTopRow + 15);
            var chart = drawing.CreateChart(anchor);

            List<int> measureIndices = [];
            for (int c = 1; c < result.Columns.Count; c++)
            {
                if (result.Rows[0].TryGetValue(result.Columns[c], out var v)
                    && (v is decimal or double or float or int or long))
                {
                    measureIndices.Add(c);
                }
            }

            return (chart, measureIndices);
        }

        private static void EmbedBarChart(ISheet sheet, AnalysisQueryResponse result)
        {
            var (chart, measureIndices) = CreateChartBase(sheet, result);
            if (measureIndices.Count == 0) return;

            var data = chart.ChartDataFactory.CreateBarChartData<string, double>();
            var cats = DataSources.FromStringCellRange(sheet,
                new CellRangeAddress(1, result.Rows.Count, 0, 0));

            foreach (var colIdx in measureIndices)
            {
                var vals = DataSources.FromNumericCellRange(sheet,
                    new CellRangeAddress(1, result.Rows.Count, colIdx, colIdx));
                data.AddSeries(cats, vals).SetTitle(result.Columns[colIdx]);
            }

            var catAxis = chart.ChartAxisFactory.CreateCategoryAxis(AxisPosition.Bottom);
            var valAxis = chart.ChartAxisFactory.CreateValueAxis(AxisPosition.Left);
            valAxis.Crosses = AxisCrosses.AutoZero;

            if (DetectDualAxis(result, measureIndices))
            {
                var valAxisRight = chart.ChartAxisFactory.CreateValueAxis(AxisPosition.Right);
                valAxisRight.Crosses = AxisCrosses.AutoZero;
                chart.Plot(data, catAxis, valAxis, valAxisRight);
            }
            else
            {
                chart.Plot(data, catAxis, valAxis);
            }

            chart.GetOrCreateLegend().Position = LegendPosition.Bottom;
        }

        private static void EmbedLineChart(ISheet sheet, AnalysisQueryResponse result)
        {
            var (chart, measureIndices) = CreateChartBase(sheet, result);
            if (measureIndices.Count == 0) return;

            var data = chart.ChartDataFactory.CreateLineChartData<string, double>();
            var cats = DataSources.FromStringCellRange(sheet,
                new CellRangeAddress(1, result.Rows.Count, 0, 0));

            foreach (var colIdx in measureIndices)
            {
                var vals = DataSources.FromNumericCellRange(sheet,
                    new CellRangeAddress(1, result.Rows.Count, colIdx, colIdx));
                data.AddSeries(cats, vals).SetTitle(result.Columns[colIdx]);
            }

            var catAxis = chart.ChartAxisFactory.CreateCategoryAxis(AxisPosition.Bottom);
            var valAxis = chart.ChartAxisFactory.CreateValueAxis(AxisPosition.Left);
            valAxis.Crosses = AxisCrosses.AutoZero;

            if (DetectDualAxis(result, measureIndices))
            {
                var valAxisRight = chart.ChartAxisFactory.CreateValueAxis(AxisPosition.Right);
                valAxisRight.Crosses = AxisCrosses.AutoZero;
                chart.Plot(data, catAxis, valAxis, valAxisRight);
            }
            else
            {
                chart.Plot(data, catAxis, valAxis);
            }

            chart.GetOrCreateLegend().Position = LegendPosition.Bottom;
        }

        private static void EmbedPieChart(ISheet sheet, AnalysisQueryResponse result)
        {
            var (chart, measureIndices) = CreateChartBase(sheet, result);
            if (measureIndices.Count == 0) return;

            // Pie chart uses only the first measure
            var firstMeasureIdx = measureIndices[0];
            var data = chart.ChartDataFactory.CreatePieChartData<string, double>();
            var cats = DataSources.FromStringCellRange(sheet,
                new CellRangeAddress(1, result.Rows.Count, 0, 0));
            var vals = DataSources.FromNumericCellRange(sheet,
                new CellRangeAddress(1, result.Rows.Count, firstMeasureIdx, firstMeasureIdx));
            data.AddSeries(cats, vals).SetTitle(result.Columns[firstMeasureIdx]);

            chart.Plot(data);
            chart.GetOrCreateLegend().Position = LegendPosition.Bottom;
        }
    }
}
