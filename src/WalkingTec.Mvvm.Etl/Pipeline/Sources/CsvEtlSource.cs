#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Etl.Pipeline.Sources;

/// <summary>
/// CSV 資料來源 — 從 CSV 檔案讀取資料，分批 yield DataTable。
///
/// <para>
/// <b>connectionString</b> 視為 CSV 檔案路徑（可含環境變數，例如
/// <c>%TEMP%\import.csv</c>）；<b>queryTemplate</b> 在 CSV source
/// 中被忽略（CSV 沒有 SQL 查詢語義），<b>watermarkValue</b> 同樣被忽略。
/// </para>
///
/// <para>支援的 CSV 特性：</para>
/// <list type="bullet">
/// <item>可設定分隔符（預設逗號）</item>
/// <item>RFC 4180 引號欄位（欄位含逗號/換行時以雙引號包覆）</item>
/// <item>欄位內嵌 <c>""</c> 跳脫為單一 <c>"</c></item>
/// <item>可設定是否有 Header Row（預設有；無 header 時自動命名 Col0、Col1…）</item>
/// <item>批次大小 / CancellationToken 語義與 Oracle/Mssql source 一致</item>
/// </list>
/// </summary>
public sealed class CsvEtlSource : IEtlSource
{
    /// <summary>欄位分隔字元。預設 <c>,</c>。</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>第一行是否為欄位名稱（Header Row）。預設 <c>true</c>。</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>
    /// 檔案編碼。預設 <see cref="Encoding.UTF8"/>（StreamReader 會自動偵測 BOM）。
    /// </summary>
    public Encoding Encoding { get; init; } = Encoding.UTF8;

    /// <inheritdoc />
    public async IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString,
        string queryTemplate,
        object? watermarkValue,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // connectionString is used as the file path for CSV sources.
        var filePath = Environment.ExpandEnvironmentVariables(connectionString);

        using var reader = new StreamReader(filePath, Encoding);

        // --- Read / derive header ---
        string[]? headers = null;
        if (HasHeader)
        {
            var headerLine = await reader.ReadLineAsync(cancellationToken);
            if (headerLine is null)
                yield break; // empty file

            headers = ParseCsvLine(headerLine, Delimiter);
        }

        // --- Stream rows ---
        string?[] rowBuffer = Array.Empty<string?>();
        bool firstRow = true;

        var batch = new DataTable();
        int count = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Read next logical line (may span multiple physical lines for quoted fields)
            var logicalLine = await ReadLogicalLineAsync(reader, cancellationToken);
            if (logicalLine is null)
                break; // EOF

            var fields = ParseCsvLine(logicalLine, Delimiter);

            // Build schema from first data row when HasHeader == false
            if (firstRow)
            {
                firstRow = false;

                if (headers is null)
                {
                    // No header — generate column names Col0, Col1, …
                    headers = new string[fields.Length];
                    for (int i = 0; i < fields.Length; i++)
                        headers[i] = $"Col{i}";
                }

                // All columns are string (CSV has no type info).
                foreach (var h in headers)
                    batch.Columns.Add(h, typeof(string));
            }

            var row = batch.NewRow();
            int colCount = Math.Min(fields.Length, batch.Columns.Count);
            for (int i = 0; i < colCount; i++)
                row[i] = (object?)fields[i] ?? DBNull.Value;

            batch.Rows.Add(row);
            count++;

            if (count >= batchSize)
            {
                yield return batch;
                batch = batch.Clone(); // same schema, no rows
                count = 0;
            }
        }

        if (count > 0)
            yield return batch;
    }

    // -----------------------------------------------------------------
    // RFC 4180 CSV parser — handles quoted fields, embedded commas,
    // and embedded newlines (\r\n or \n inside double-quoted fields).
    // -----------------------------------------------------------------

    /// <summary>
    /// Reads a "logical" CSV line from the reader.
    /// A quoted field may contain embedded newlines; we keep reading until
    /// we are not inside a quoted field.
    /// Returns null at EOF.
    /// </summary>
    private static async Task<string?> ReadLogicalLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
            return null;

        // Count unescaped quotes to decide whether we are still inside a field.
        // An even number of quotes → we are outside.
        while (CountUnescapedQuotes(line) % 2 != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = await reader.ReadLineAsync(cancellationToken);
            if (next is null)
                break; // truncated quoted field — tolerate it
            line = line + "\n" + next;
        }

        return line;
    }

    private static int CountUnescapedQuotes(string s)
    {
        int count = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"')
            {
                // "" is an escaped quote — skip both characters
                if (i + 1 < s.Length && s[i + 1] == '"')
                {
                    i++; // skip the second "
                    continue;
                }
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Parses a single (already-reassembled) logical CSV line into fields.
    /// </summary>
    internal static string[] ParseCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        int i = 0;

        while (i < line.Length)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Peek: is the next char also a quote? → escaped literal "
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }
                    // Closing quote
                    inQuotes = false;
                    i++;
                    continue;
                }

                sb.Append(c);
                i++;
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                    i++;
                    continue;
                }

                if (c == delimiter)
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                    i++;
                    continue;
                }

                sb.Append(c);
                i++;
            }
        }

        // Last field (no trailing delimiter needed)
        fields.Add(sb.ToString());

        return fields.ToArray();
    }

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
