#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace WalkingTec.Mvvm.Etl.Pipeline.Sources;

/// <summary>
/// MySQL 資料來源 — 使用 MySqlDataReader 串流讀取，分批 yield DataTable。
/// <para>
/// 使用 <see cref="CommandBehavior.SequentialAccess"/> 減少記憶體壓力。
/// 連線字串支援 MySqlConnector 的所有連接選項（包含 UseCompression、AllowBatch 等）。
/// </para>
/// </summary>
public class MySqlEtlSource : IEtlSource, IAsyncDisposable
{
    private MySqlConnection? _connection;

    public async IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString,
        string queryTemplate,
        object? watermarkValue,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(connectionString);
        _connection = connection;
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = queryTemplate;
            cmd.CommandTimeout = 300; // 5-minute hard timeout; CancellationToken provides additional control

            if (watermarkValue != null)
            {
                cmd.Parameters.AddWithValue("@watermark", watermarkValue);
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
