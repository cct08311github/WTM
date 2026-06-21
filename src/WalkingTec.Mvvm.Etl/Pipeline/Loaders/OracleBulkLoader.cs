#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

namespace WalkingTec.Mvvm.Etl.Pipeline.Loaders;

/// <summary>
/// Oracle 批量載入器 — Array Binding 批次插入 + MERGE INTO
/// 使用 Array Binding 而非 OracleBulkCopy，因為更穩定且不依賴 Oracle client 版本。
/// </summary>
public class OracleBulkLoader : IBulkLoader
{
    /// <summary>
    /// Timeout in seconds applied to all <c>OracleCommand.CommandTimeout</c> calls
    /// inside this loader (Merge, Replace, Truncate, EnsureStaging, and BulkLoad).
    /// <para>
    /// Defaults to 0 = Oracle's "no limit" (infinite-wait), which is the exact
    /// pre-10.6 behaviour. This default is intentional: Oracle DDL/DML operations can
    /// be legitimately long-running and users should consciously opt in to a timeout
    /// rather than have it silently imposed. Pass a positive value to enable a hard
    /// timeout per command.
    /// </para>
    /// </summary>
    public int TimeoutSeconds { get; }

    /// <summary>
    /// Initialises the loader with a configurable per-command timeout.
    /// </summary>
    /// <param name="timeoutSeconds">
    /// Seconds before each Oracle command times out.
    /// 0 (default) = no limit (infinite, pre-10.6 behaviour preserved exactly).
    /// </param>
    public OracleBulkLoader(int timeoutSeconds = 0)
    {
        TimeoutSeconds = timeoutSeconds;
    }

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
        // Apply opt-in timeout (0 = no limit, preserving pre-10.6 default).
        cmd.CommandTimeout = TimeoutSeconds;

        foreach (var col in columns)
        {
            // Pre-resolve ordinal once to avoid per-row string dictionary lookup.
            int ord = col.Ordinal;
            var values = new object[batch.Rows.Count];
            for (int i = 0; i < batch.Rows.Count; i++)
                values[i] = batch.Rows[i][ord];

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

        // Public interface path: no pre-resolved column list available,
        // so fall back to the USER_TAB_COLUMNS round-trip.
        var columns = await GetColumnsAsync(conn, stagingTableName, cancellationToken);
        var keyColumns = MssqlBulkLoader.ParseMergeKeys(mergeKeyColumn);
        await ExecuteMergeAsync(conn, stagingTableName, targetTableName, keyColumns,
            columns, cancellationToken);
    }

    /// <summary>
    /// Additive internal overload: accepts a pre-resolved column list from the
    /// caller (typically the batch <see cref="DataTable.Columns"/> names) to
    /// skip the USER_TAB_COLUMNS metadata round-trip. Preserves identical
    /// SQL/semantics as the public path. Called by the pipeline executor when
    /// it has the column names already.
    /// <para>
    /// This overload intentionally does NOT appear on <see cref="IBulkLoader"/>
    /// so third-party implementors are unaffected.
    /// </para>
    /// </summary>
    internal async Task MergeAsync(
        string connectionString, string stagingTableName,
        string targetTableName, string mergeKeyColumn,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new OracleConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var keyColumns = MssqlBulkLoader.ParseMergeKeys(mergeKeyColumn);
        await ExecuteMergeAsync(conn, stagingTableName, targetTableName, keyColumns,
            columns, cancellationToken);
    }

    private async Task ExecuteMergeAsync(
        OracleConnection conn,
        string stagingTableName, string targetTableName,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        var keySet = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
        var updateCols = columns.Where(c => !keySet.Contains(c)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"MERGE INTO {targetTableName} t");
        sb.AppendLine($"USING {stagingTableName} s");
        // ETL-009: composite ON clause — AND-join all key columns inside parentheses
        sb.AppendLine("ON (" + string.Join(" AND ",
            keyColumns.Select(k => $"t.{k} = s.{k}")) + ")");

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
        // 0 = no limit (Oracle default); opt-in timeout when TimeoutSeconds > 0.
        cmd.CommandTimeout = TimeoutSeconds;
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
        // 0 = no limit (Oracle default); opt-in timeout when TimeoutSeconds > 0.
        cmd.CommandTimeout = TimeoutSeconds;
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
                del.CommandTimeout = TimeoutSeconds;
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
                ins.CommandTimeout = TimeoutSeconds;
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
        checkCmd.CommandTimeout = TimeoutSeconds;
        var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(cancellationToken));

        if (count == 0)
        {
            var sb = new StringBuilder();
            // Intentionally leave table and column identifiers unquoted so Oracle
            // folds them to uppercase, staying consistent with the USER_TABLES /
            // USER_TAB_COLUMNS existence probes above (which use .ToUpperInvariant()).
            // Quoted identifiers are case-sensitive in Oracle: mixing quoted DDL with
            // uppercase dictionary probes caused ORA-00955 on run 2+ (#499 / reverts #485).
            sb.AppendLine($"CREATE TABLE {stagingTableName} (");
            sb.AppendLine(string.Join(",\n",
                spec.Columns.Select(c => $"  {c.Name} {c.SqlType}")));
            sb.Append(')');

            await using var createCmd = conn.CreateCommand();
            createCmd.CommandText = sb.ToString();
            createCmd.CommandTimeout = TimeoutSeconds;
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
