#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 將 GroupBy 聚合結果做行列轉置（Pivot），將指定維度展開為動態欄位。
    /// 純記憶體操作，不涉及 DB 查詢。
    /// </summary>
    public static class AnalysisPivotEngine
    {
        /// <summary>Pivot 維度允許的最大唯一值數量。</summary>
        public const int MaxPivotValues = 50;

        /// <summary>
        /// 執行 Pivot 轉置。
        /// </summary>
        /// <param name="groupByResult">標準 GroupBy 查詢結果。</param>
        /// <param name="pivotDimension">要展開為欄位的維度名稱。</param>
        /// <param name="allDimensions">原始查詢的所有維度名稱。</param>
        /// <param name="measureNames">度量欄位名（Field_Func 格式）。</param>
        /// <returns>Pivot 結果。</returns>
        public static AnalysisPivotResponse Pivot(
            AnalysisQueryResponse groupByResult,
            string pivotDimension,
            List<string> allDimensions,
            List<string> measureNames)
        {
            if (string.IsNullOrEmpty(pivotDimension))
                throw new InvalidOperationException("PivotDimension 不可為空。");

            if (!allDimensions.Contains(pivotDimension))
                throw new InvalidOperationException(
                    $"PivotDimension '{pivotDimension}' 必須是選取維度之一。");

            // Row dimensions = all dimensions except pivot dimension
            List<string> rowDims = [.. allDimensions.Where(d => d != pivotDimension)];

            // Collect unique pivot values
            List<string> pivotValues = [.. groupByResult.Rows
                .Select(r => r.GetValueOrDefault(pivotDimension)?.ToString() ?? "")
                .Distinct()
                .OrderBy(v => v)];

            if (pivotValues.Count > MaxPivotValues)
                throw new InvalidOperationException(
                    $"PivotDimension '{pivotDimension}' 有 {pivotValues.Count} 個唯一值，超過上限 {MaxPivotValues}。");

            // Build column names
            List<string> columns = [.. rowDims];
            foreach (var pv in pivotValues)
            {
                foreach (var mn in measureNames)
                {
                    columns.Add($"{pv}_{mn}");
                }
            }

            // Group input rows by row dimensions to build pivot rows
            int pivotRowCapacity = rowDims.Count + pivotValues.Count * measureNames.Count;
            List<Dictionary<string, object?>> pivotRows = [];
            var groups = groupByResult.Rows
                .GroupBy(r => BuildRowKey(r, rowDims));

            foreach (var group in groups)
            {
                var pivotRow = new Dictionary<string, object?>(pivotRowCapacity);

                // Set row dimension values from first row in group
                var firstRow = group.First();
                foreach (var rd in rowDims)
                {
                    pivotRow[rd] = firstRow.GetValueOrDefault(rd);
                }

                // Initialize all pivot cells to 0
                foreach (var pv in pivotValues)
                {
                    foreach (var mn in measureNames)
                    {
                        pivotRow[$"{pv}_{mn}"] = 0m;
                    }
                }

                // Fill in actual values
                foreach (var row in group)
                {
                    var pvKey = row.GetValueOrDefault(pivotDimension)?.ToString() ?? "";
                    foreach (var mn in measureNames)
                    {
                        pivotRow[$"{pvKey}_{mn}"] = row.GetValueOrDefault(mn);
                    }
                }

                pivotRows.Add(pivotRow);
            }

            return new AnalysisPivotResponse
            {
                RowDimensions = rowDims,
                PivotValues = pivotValues,
                MeasureNames = measureNames,
                Rows = pivotRows,
                Columns = columns
            };
        }

        private static string BuildRowKey(Dictionary<string, object?> row, List<string> rowDims)
        {
            // Fast-path for common single/double-dim cases; fall through to Join for 3+.
            return rowDims.Count switch
            {
                0 => "",
                1 => row.GetValueOrDefault(rowDims[0])?.ToString() ?? "",
                2 => string.Concat(
                        row.GetValueOrDefault(rowDims[0])?.ToString() ?? "",
                        "\0",
                        row.GetValueOrDefault(rowDims[1])?.ToString() ?? ""),
                _ => string.Join("\0", rowDims.Select(d => row.GetValueOrDefault(d)?.ToString() ?? ""))
            };
        }
    }
}
