#nullable enable
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
        /// <summary>
        /// 匯出分析結果為 Excel（xlsx），回傳位元組陣列。
        /// </summary>
        /// <param name="result">查詢結果</param>
        /// <param name="includeChart">是否嵌入圖表（預設 false）</param>
        public static byte[] Export(AnalysisQueryResponse result, bool includeChart = false)
        {
            using var workbook = new XSSFWorkbook();
            var sheet = workbook.CreateSheet("Analysis");

            // Header row
            var header = sheet.CreateRow(0);
            for (int i = 0; i < result.Columns.Count; i++)
                header.CreateCell(i).SetCellValue(result.Columns[i]);

            // Data rows
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
                    else if (val is float f)
                        cell.SetCellValue(f);
                    else if (val is int i)
                        cell.SetCellValue(i);
                    else if (val is long l)
                        cell.SetCellValue(l);
                    else
                        cell.SetCellValue(val?.ToString() ?? "");
                }
            }

            if (includeChart && result.Rows.Count > 0 && result.Columns.Count >= 2)
            {
                EmbedBarChart(sheet, result);
            }

            using var ms = new MemoryStream();
            workbook.Write(ms, leaveOpen: true);
            return ms.ToArray();
        }

        /// <summary>
        /// 在數據表下方嵌入 bar chart。
        /// 假設第一個 column 為維度（category），其餘為度量（values）。
        /// </summary>
        private static void EmbedBarChart(ISheet sheet, AnalysisQueryResponse result)
        {
            var drawing = sheet.CreateDrawingPatriarch();
            // 圖表放在數據列下方，留 2 行空白
            int chartTopRow = result.Rows.Count + 3;
            var anchor = drawing.CreateAnchor(0, 0, 0, 0,
                0, chartTopRow, result.Columns.Count + 2, chartTopRow + 15);
            var chart = drawing.CreateChart(anchor);

            // 找出度量 columns（跳過第一個維度 column）
            var measureIndices = new List<int>();
            for (int c = 1; c < result.Columns.Count; c++)
            {
                // 如果 column 對應的值在第一行是 numeric，視為度量
                if (result.Rows[0].TryGetValue(result.Columns[c], out var v)
                    && (v is decimal or double or float or int or long))
                {
                    measureIndices.Add(c);
                }
            }

            if (measureIndices.Count == 0) return;

            var data = chart.ChartDataFactory.CreateBarChartData<string, double>();

            // Category axis: first column, rows 1..N (0-based: row 1 = first data row)
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
            valAxis.SetCrosses(AxisCrosses.AutoZero);

            chart.Plot(data, catAxis, valAxis);
            chart.GetOrCreateLegend().Position = LegendPosition.Bottom;
        }
    }
}
