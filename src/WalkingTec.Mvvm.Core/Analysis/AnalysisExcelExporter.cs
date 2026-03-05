#nullable disable
using System.Collections.Generic;
using System.IO;
using NPOI.SS.UserModel;
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
        public static byte[] Export(AnalysisQueryResponse result)
        {
            var workbook = new XSSFWorkbook();
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
                    else
                        cell.SetCellValue(val?.ToString() ?? "");
                }
            }

            using var ms = new MemoryStream();
            workbook.Write(ms, leaveOpen: true);
            return ms.ToArray();
        }
    }
}
