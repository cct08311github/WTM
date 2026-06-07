#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace WalkingTec.Mvvm.Etl.Pipeline.Sources;

/// <summary>
/// MSSQL 資料來源 — 使用 DbDataReader 串流讀取，分批 yield DataTable
/// </summary>
public class MssqlSource : IEtlSource, IAsyncDisposable
{
    private SqlConnection? _connection;

    /// <summary>
    /// Watermark 參數的 SQL 型別（opt-in，10.6+）。
    /// 設為非 <c>null</c> 時，改用明確型別的 <c>SqlParameter</c>，
    /// 避免 <c>AddWithValue</c> 的隱式型別推斷（例如 <c>datetime2</c>
    /// watermark 被推斷為 <c>nvarchar</c>，導致索引無法使用）。
    /// <c>null</c>（預設）= 維持 <c>AddWithValue</c> 行為，
    /// 與 10.5.x 完全一致。
    /// 既有的 <c>new MssqlSource()</c> 呼叫端不受影響。
    /// </summary>
    public SqlDbType? WatermarkSqlType { get; set; } = null;

    public async IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString,
        string queryTemplate,
        object? watermarkValue,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(connectionString);
        _connection = connection;
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = queryTemplate;
            cmd.CommandTimeout = 300; // 5-minute hard timeout; CancellationToken provides additional control

            if (watermarkValue != null)
            {
                if (WatermarkSqlType.HasValue)
                {
                    // Opt-in explicit typed parameter: avoids AddWithValue implicit type inference
                    // (e.g. a datetime2 watermark being inferred as nvarchar, killing index seeks).
                    // WatermarkSqlType == null (default) → AddWithValue path, identical to 10.5.x.
                    var p = new SqlParameter("@watermark", WatermarkSqlType.Value) { Value = watermarkValue };
                    cmd.Parameters.Add(p);
                }
                else
                {
                    cmd.Parameters.AddWithValue("@watermark", watermarkValue);
                }
            }

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

            // Hoist GetName/GetFieldType out of the per-batch loop — the reader schema
            // is fixed for the lifetime of one query execution.
            int fieldCount = reader.FieldCount;
            var colNames = new string[fieldCount];
            var colTypes = new Type[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                colNames[i] = reader.GetName(i);
                colTypes[i] = reader.GetFieldType(i) ?? typeof(object);
            }

            while (true)
            {
                var batch = new DataTable();
                for (int i = 0; i < fieldCount; i++)
                {
                    batch.Columns.Add(colNames[i], colTypes[i]);
                }

                int count = 0;
                while (count < batchSize && await reader.ReadAsync(cancellationToken))
                {
                    var row = batch.NewRow();
                    for (int i = 0; i < fieldCount; i++)
                    {
                        row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                    }
                    batch.Rows.Add(row);
                    count++;
                }

                if (count == 0) break;
                yield return batch;
                if (count < batchSize) break; // 最後一批
            }
        }
        finally
        {
            // Ensure connection is released even if the iterator is abandoned mid-enumeration
            await connection.DisposeAsync();
            _connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
