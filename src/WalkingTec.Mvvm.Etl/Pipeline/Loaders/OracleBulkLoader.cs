#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;

namespace WalkingTec.Mvvm.Etl.Pipeline.Loaders;

/// <summary>
/// Oracle 批量載入器 — Array Binding 批次插入 + MERGE INTO
/// 使用 Array Binding 而非 OracleBulkCopy，因為更穩定且不依賴 Oracle client 版本。
/// </summary>
public class OracleBulkLoader : IBulkLoader
{
    public async Task BulkLoadAsync(
        string connectionString, string stagingTableName,
        DataTable batch, CancellationToken cancellationToken = default)
    {
        await using var conn = new OracleConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var columns = batch.Columns.Cast<DataColumn>().ToList();
        var insertSql = new StringBuilder();
        insertSql.Append($"INSERT INTO {stagingTableName} (");
        insertSql.Append(string.Join(", ", columns.Select(c => c.ColumnName)));
        insertSql.Append(") VALUES (");
        insertSql.Append(string.Join(", ", columns.Select(c => $":{c.ColumnName}")));
        insertSql.Append(')');

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = insertSql.ToString();
        cmd.ArrayBindCount = batch.Rows.Count;

        foreach (var col in columns)
        {
            var values = new object[batch.Rows.Count];
            for (int i = 0; i < batch.Rows.Count; i++)
                values[i] = batch.Rows[i][col.ColumnName];

            cmd.Parameters.Add(new OracleParameter(col.ColumnName, values));
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MergeAsync(
        string connectionString, string stagingTableName,
        string targetTableName, string mergeKeyColumn,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new OracleConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(conn, stagingTableName, cancellationToken);
        var updateCols = columns.Where(c => c != mergeKeyColumn).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"MERGE INTO {targetTableName} t");
        sb.AppendLine($"USING {stagingTableName} s");
        sb.AppendLine($"ON (t.{mergeKeyColumn} = s.{mergeKeyColumn})");

        if (updateCols.Count > 0)
        {
            sb.AppendLine("WHEN MATCHED THEN UPDATE SET");
            sb.AppendLine(string.Join(",\n",
                updateCols.Select(c => $"  t.{c} = s.{c}")));
        }

        sb.AppendLine("WHEN NOT MATCHED THEN INSERT (");
        sb.AppendLine(string.Join(", ", columns));
        sb.AppendLine(") VALUES (");
        sb.AppendLine(string.Join(", ", columns.Select(c => $"s.{c}")));
        sb.Append(')');

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task TruncateStagingAsync(
        string connectionString, string stagingTableName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new OracleConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE TABLE {stagingTableName}";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceAsync(
        string connectionString, string stagingTableName,
        string targetTableName, string? whereClause,
        CancellationToken cancellationToken = default)
    {
        // Reuse the same conservative whitelist as MSSQL — Oracle is
        // arguably more restrictive about ;-terminators inside command
        // text, but the operator-vetted clause posture is identical.
        if (!Loaders.MssqlBulkLoader.IsSafeWhereClause(whereClause))
        {
            throw new System.ArgumentException(
                $"Replace whereClause contains disallowed characters or token: '{whereClause}'.",
                nameof(whereClause));
        }

        await using var conn = new OracleConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(conn, stagingTableName, cancellationToken);
        if (columns.Count == 0)
        {
            throw new System.InvalidOperationException(
                $"Replace mode aborted: staging table '{stagingTableName}' has no columns.");
        }

        await using var tran = (Oracle.ManagedDataAccess.Client.OracleTransaction)await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            var deleteSql = string.IsNullOrWhiteSpace(whereClause)
                ? $"DELETE FROM {targetTableName}"
                : $"DELETE FROM {targetTableName} WHERE {whereClause}";
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tran;
                del.CommandText = deleteSql;
                del.CommandTimeout = 0;
                await del.ExecuteNonQueryAsync(cancellationToken);
            }

            var colList = string.Join(", ", columns);
            var insertSql =
                $"INSERT INTO {targetTableName} ({colList}) " +
                $"SELECT {colList} FROM {stagingTableName}";
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tran;
                ins.CommandText = insertSql;
                ins.CommandTimeout = 0;
                await ins.ExecuteNonQueryAsync(cancellationToken);
            }

            await tran.CommitAsync(cancellationToken);
        }
        catch
        {
            await tran.RollbackAsync(System.Threading.CancellationToken.None);
            throw;
        }
    }

    public async Task EnsureStagingTableAsync(
        string connectionString, string stagingTableName,
        StagingTableSpec spec, CancellationToken cancellationToken = default)
    {
        await using var conn = new OracleConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"
            SELECT COUNT(*) FROM USER_TABLES
            WHERE TABLE_NAME = :tableName";
        checkCmd.Parameters.Add(new OracleParameter("tableName", stagingTableName.ToUpperInvariant()));
        var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(cancellationToken));

        if (count == 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE {stagingTableName} (");
            sb.AppendLine(string.Join(",\n",
                spec.Columns.Select(c => $"  {c.Name} {c.SqlType}")));
            sb.Append(')');

            await using var createCmd = conn.CreateCommand();
            createCmd.CommandText = sb.ToString();
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<List<string>> GetColumnsAsync(
        OracleConnection conn, string tableName, CancellationToken ct)
    {
        List<string> columns = [];
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COLUMN_NAME FROM USER_TAB_COLUMNS
            WHERE TABLE_NAME = :tableName
            ORDER BY COLUMN_ID";
        cmd.Parameters.Add(new OracleParameter("tableName", tableName.ToUpperInvariant()));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));
        return columns;
    }

    public async Task<bool> IsUniqueColumnAsync(
        string connectionString, string tableName, string columnName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new OracleConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        // Check PK or Unique constraints in Oracle
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM all_cons_columns a
            JOIN all_constraints c ON a.constraint_name = c.constraint_name
            WHERE a.table_name = :tableName AND a.column_name = :colName
              AND c.constraint_type IN ('P', 'U')";
        cmd.Parameters.Add(new OracleParameter("tableName", tableName.ToUpperInvariant()));
        cmd.Parameters.Add(new OracleParameter("colName", columnName.ToUpperInvariant()));

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }
}
