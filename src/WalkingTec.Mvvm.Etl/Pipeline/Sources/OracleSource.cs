#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;

namespace WalkingTec.Mvvm.Etl.Pipeline.Sources;

/// <summary>
/// Oracle 資料來源 — 使用 OracleDataReader 串流讀取，分批 yield DataTable
/// </summary>
public class OracleSource : IEtlSource, IAsyncDisposable
{
    private OracleConnection? _connection;

    /// <summary>
    /// ODP.NET FetchSize 調整（opt-in，10.6+）。
    /// 設為 &gt; 0 時，於 reader 開啟後設定
    /// <c>reader.FetchSize = FetchRowCount * reader.RowSize</c>，
    /// 以減少到 Oracle 的網路往返次數。
    /// 預設 <c>0</c> = 使用 ODP.NET 預設值（行為與 10.5.x 完全一致）。
    /// 既有的 <c>new OracleSource()</c> 呼叫端不受影響。
    /// </summary>
    public int FetchRowCount { get; init; } = 0;

    public async IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString,
        string queryTemplate,
        object? watermarkValue,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var connection = new OracleConnection(connectionString);
        _connection = connection;
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = queryTemplate;
            cmd.CommandTimeout = 300; // 5-minute hard timeout; CancellationToken provides additional control

            if (watermarkValue != null)
            {
                cmd.Parameters.Add(new OracleParameter("watermark", watermarkValue));
            }

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

            // Opt-in ODP.NET fetch-size tuning: when FetchRowCount > 0, set FetchSize
            // to reduce network round-trips at the cost of higher per-batch memory.
            // FetchRowCount == 0 (default) → ODP.NET built-in default, identical to 10.5.x.
            if (FetchRowCount > 0 && reader is OracleDataReader odr && odr.RowSize > 0)
            {
                odr.FetchSize = (long)FetchRowCount * odr.RowSize;
            }

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
                if (count < batchSize) break;
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
