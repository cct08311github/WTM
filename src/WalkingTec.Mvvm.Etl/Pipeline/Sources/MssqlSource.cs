#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Data.SqlClient;

namespace WalkingTec.Mvvm.Etl.Pipeline.Sources;

/// <summary>
/// MSSQL 資料來源 — 使用 DbDataReader 串流讀取，分批 yield DataTable
/// </summary>
public class MssqlSource : IEtlSource
{
    private SqlConnection? _connection;

    public async IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString,
        string queryTemplate,
        object? watermarkValue,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _connection = new SqlConnection(connectionString);
        await _connection.OpenAsync(cancellationToken);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = queryTemplate;
        cmd.CommandTimeout = 0; // Pipeline 層的 CancellationToken 控制超時

        if (watermarkValue != null)
        {
            cmd.Parameters.AddWithValue("@watermark", watermarkValue);
        }

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

        while (true)
        {
            var batch = new DataTable();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                batch.Columns.Add(reader.GetName(i), reader.GetFieldType(i) ?? typeof(object));
            }

            int count = 0;
            while (count < batchSize && await reader.ReadAsync(cancellationToken))
            {
                var row = batch.NewRow();
                for (int i = 0; i < reader.FieldCount; i++)
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

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
