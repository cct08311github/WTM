#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace WalkingTec.Mvvm.Etl.Pipeline.Sources;

/// <summary>
/// Excel (.xlsx) 資料來源 — 使用 NPOI 讀取工作表，分批 yield DataTable。
///
/// <para>
/// <b>connectionString</b> 視為 Excel 檔案路徑（可含環境變數，例如
/// <c>%TEMP%\import.xlsx</c>）；<b>queryTemplate</b> 在 Excel source
/// 中用來指定工作表名稱（空白時使用第一張工作表），
/// <b>watermarkValue</b> 被忽略。
/// </para>
///
/// <para>支援特性：</para>
/// <list type="bullet">
/// <item>僅支援 .xlsx（OOXML）格式（NPOI XSSFWorkbook）</item>
/// <item>可設定是否有 Header Row（預設有；無 header 時自動命名 Col0、Col1…）</item>
/// <item>可設定工作表索引（<see cref="SheetIndex"/>，預設 0）或工作表名稱（由 queryTemplate 傳入）</item>
/// <item>批次大小 / CancellationToken 語義與 Oracle/Mssql source 一致</item>
/// <item>數值/日期/布林欄位轉為對應的 .NET 型別；其餘一律取字串</item>
/// </list>
/// </summary>
public sealed class ExcelEtlSource : IEtlSource
{
    /// <summary>第一行是否為欄位名稱（Header Row）。預設 <c>true</c>。</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>
    /// 工作表索引（從 0 起算）。當 <paramref name="queryTemplate"/> 指定了工作表名稱時，
    /// 此屬性被忽略，以名稱優先。預設 <c>0</c>（第一張）。
    /// </summary>
    public int SheetIndex { get; init; } = 0;

    /// <inheritdoc />
    public async IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString,
        string queryTemplate,
        object? watermarkValue,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filePath = Environment.ExpandEnvironmentVariables(connectionString);

        // NPOI is synchronous; offload to thread pool to stay non-blocking.
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);

        // ReadAsync loads all bytes first — NPOI does not support streaming opens.
        var bytes = await ReadAllBytesAsync(fs, cancellationToken);
        using var ms = new MemoryStream(bytes, writable: false);

        IWorkbook workbook;
        try
        {
            workbook = new XSSFWorkbook(ms); // .xlsx
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ExcelEtlSource: could not open '{filePath}' as an .xlsx workbook. " +
                $"Only OOXML (.xlsx) format is supported. Inner: {ex.Message}", ex);
        }

        using (workbook)
        {
            ISheet sheet = ResolveSheet(workbook, queryTemplate);
            int lastRowIndex = sheet.LastRowNum;
            int startRow = 0;

            // --- Derive column names ---
            string[]? headers = null;
            if (HasHeader)
            {
                var headerRow = sheet.GetRow(startRow);
                if (headerRow is null)
                    yield break; // empty sheet

                headers = ExtractHeaders(headerRow);
                startRow = 1;
            }

            if (startRow > lastRowIndex)
                yield break; // no data rows

            // Derive column types from first data row
            string[]? colNames = null;
            Type[]? colTypes = null;
            DataTable? batch = null;
            int count = 0;

            for (int rowIdx = startRow; rowIdx <= lastRowIndex; rowIdx++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var xlRow = sheet.GetRow(rowIdx);
                if (xlRow is null)
                    continue; // blank row — skip

                // Build schema from the first data row encountered
                if (colNames is null)
                {
                    int cellCount = xlRow.LastCellNum;
                    if (cellCount <= 0)
                        continue;

                    colNames = headers ?? GenerateColumnNames(cellCount);
                    colTypes = InferColumnTypes(xlRow, cellCount);

                    batch = new DataTable();
                    for (int ci = 0; ci < colNames.Length; ci++)
                        batch.Columns.Add(colNames[ci], colTypes[ci]);
                }

                if (batch is null) continue; // safety

                var row = batch.NewRow();
                int colCount = Math.Min(xlRow.LastCellNum, batch.Columns.Count);
                for (int ci = 0; ci < colCount; ci++)
                {
                    var cell = xlRow.GetCell(ci);
                    row[ci] = cell is null ? DBNull.Value : ReadCellValue(cell, batch.Columns[ci].DataType);
                }

                batch.Rows.Add(row);
                count++;

                if (count >= batchSize)
                {
                    yield return batch;
                    batch = batch.Clone();
                    count = 0;
                }
            }

            if (batch is not null && count > 0)
                yield return batch;
        }
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private ISheet ResolveSheet(IWorkbook workbook, string queryTemplate)
    {
        // queryTemplate used as optional sheet name
        if (!string.IsNullOrWhiteSpace(queryTemplate))
        {
            var named = workbook.GetSheet(queryTemplate.Trim());
            if (named is not null)
                return named;
        }

        int idx = Math.Max(0, SheetIndex);
        if (idx >= workbook.NumberOfSheets)
            idx = 0;
        return workbook.GetSheetAt(idx) ??
               throw new InvalidOperationException(
                   $"ExcelEtlSource: no sheet found at index {idx}.");
    }

    private static string[] ExtractHeaders(IRow headerRow)
    {
        int count = headerRow.LastCellNum;
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            var cell = headerRow.GetCell(i);
            var name = cell?.StringCellValue?.Trim();
            names[i] = string.IsNullOrWhiteSpace(name) ? $"Col{i}" : name;
        }
        return names;
    }

    private static string[] GenerateColumnNames(int count)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = $"Col{i}";
        return names;
    }

    /// <summary>
    /// Infer .NET column types from the first data row.
    /// Numeric cells → double, Boolean → bool, DateTime/Formula(date) → DateTime,
    /// everything else → string.
    /// </summary>
    private static Type[] InferColumnTypes(IRow row, int cellCount)
    {
        var types = new Type[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            var cell = row.GetCell(i);
            if (cell is null)
            {
                types[i] = typeof(string);
                continue;
            }

            types[i] = cell.CellType switch
            {
                CellType.Numeric when DateUtil.IsCellDateFormatted(cell) => typeof(DateTime),
                CellType.Numeric => typeof(double),
                CellType.Boolean => typeof(bool),
                _ => typeof(string)
            };
        }
        return types;
    }

    private static object ReadCellValue(ICell cell, Type targetType)
    {
        try
        {
            return cell.CellType switch
            {
                CellType.Blank => DBNull.Value,
                CellType.Boolean => targetType == typeof(bool)
                    ? (object)cell.BooleanCellValue
                    : cell.BooleanCellValue.ToString()!,
                CellType.Numeric when targetType == typeof(DateTime)
                    => DateUtil.IsCellDateFormatted(cell)
                        ? (object)(cell.DateCellValue ?? DateTime.MinValue)
                        : cell.NumericCellValue.ToString()!,
                CellType.Numeric => targetType == typeof(double)
                    ? (object)cell.NumericCellValue
                    : cell.NumericCellValue.ToString()!,
                CellType.Formula => EvaluateFormulaCellValue(cell, targetType),
                _ => (object?)cell.StringCellValue ?? DBNull.Value
            };
        }
        catch
        {
            // Fallback: return string representation for any unexpected cell type
            return (object?)cell.ToString() ?? DBNull.Value;
        }
    }

    private static object EvaluateFormulaCellValue(ICell cell, Type targetType)
    {
        return cell.CachedFormulaResultType switch
        {
            CellType.Numeric when targetType == typeof(DateTime) && DateUtil.IsCellDateFormatted(cell)
                => (object)(cell.DateCellValue ?? DateTime.MinValue),
            CellType.Numeric => targetType == typeof(double)
                ? (object)cell.NumericCellValue
                : cell.NumericCellValue.ToString()!,
            CellType.Boolean => targetType == typeof(bool)
                ? (object)cell.BooleanCellValue
                : cell.BooleanCellValue.ToString()!,
            CellType.String => (object?)cell.StringCellValue ?? DBNull.Value,
            _ => (object?)cell.ToString() ?? DBNull.Value
        };
    }

    private static async Task<byte[]> ReadAllBytesAsync(
        FileStream fs, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream((int)Math.Min(fs.Length, int.MaxValue));
        await fs.CopyToAsync(ms, bufferSize: 81920, cancellationToken);
        return ms.ToArray();
    }

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
