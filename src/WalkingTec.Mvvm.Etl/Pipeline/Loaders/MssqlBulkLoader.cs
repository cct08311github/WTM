#nullable enable
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace WalkingTec.Mvvm.Etl.Pipeline.Loaders;

/// <summary>
/// MSSQL 批量載入器 — SqlBulkCopy + MERGE INTO
/// </summary>
public class MssqlBulkLoader : IBulkLoader
{
    public async Task BulkLoadAsync(
        string connectionString, string stagingTableName,
        DataTable batch, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        using var bulkCopy = new SqlBulkCopy(conn)
        {
            DestinationTableName = stagingTableName,
            BatchSize = batch.Rows.Count,
            BulkCopyTimeout = 0
        };

        foreach (DataColumn col in batch.Columns)
        {
            bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(batch, cancellationToken);
    }

    public async Task MergeAsync(
        string connectionString, string stagingTableName,
        string targetTableName, string mergeKeyColumn,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(conn, stagingTableName, cancellationToken);
        var updateCols = columns.Where(c => c != mergeKeyColumn).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"MERGE [{targetTableName}] AS target");
        sb.AppendLine($"USING [{stagingTableName}] AS source");
        sb.AppendLine($"ON target.[{mergeKeyColumn}] = source.[{mergeKeyColumn}]");

        if (updateCols.Count > 0)
        {
            sb.AppendLine("WHEN MATCHED THEN UPDATE SET");
            sb.AppendLine(string.Join(",\n",
                updateCols.Select(c => $"  target.[{c}] = source.[{c}]")));
        }

        sb.AppendLine("WHEN NOT MATCHED THEN INSERT (");
        sb.AppendLine(string.Join(", ", columns.Select(c => $"[{c}]")));
        sb.AppendLine(") VALUES (");
        sb.AppendLine(string.Join(", ", columns.Select(c => $"source.[{c}]")));
        sb.AppendLine(");");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task TruncateStagingAsync(
        string connectionString, string stagingTableName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE TABLE [{stagingTableName}]";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnsureStagingTableAsync(
        string connectionString, string stagingTableName,
        StagingTableSpec spec, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME = @tableName";
        checkCmd.Parameters.AddWithValue("@tableName", stagingTableName);
        var exists = (int)(await checkCmd.ExecuteScalarAsync(cancellationToken))! > 0;

        if (!exists)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE [{stagingTableName}] (");
            sb.AppendLine(string.Join(",\n",
                spec.Columns.Select(c => $"  [{c.Name}] {c.SqlType}")));
            sb.AppendLine(")");

            await using var createCmd = conn.CreateCommand();
            createCmd.CommandText = sb.ToString();
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<List<string>> GetColumnsAsync(
        SqlConnection conn, string tableName, CancellationToken ct)
    {
        var columns = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @tableName
            ORDER BY ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@tableName", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(reader.GetString(0));
        }
        return columns;
    }

    public async Task<bool> IsUniqueColumnAsync(
        string connectionString, string tableName, string columnName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        // Check PK or Unique constraints in MSSQL
        cmd.CommandText = @"
            SELECT COUNT(*) 
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
            JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS t ON k.CONSTRAINT_NAME = t.CONSTRAINT_NAME
            WHERE k.TABLE_NAME = @tableName AND k.COLUMN_NAME = @colName
              AND t.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE')";
        cmd.Parameters.AddWithValue("@tableName", tableName);
        cmd.Parameters.AddWithValue("@colName", columnName);

        var count = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        return count > 0;
    }
}
