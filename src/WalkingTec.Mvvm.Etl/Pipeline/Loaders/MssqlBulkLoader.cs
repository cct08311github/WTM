#nullable enable
using System;
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
    /// <summary>
    /// Timeout in seconds applied to both <see cref="SqlBulkCopy.BulkCopyTimeout"/>
    /// and all <c>SqlCommand.CommandTimeout</c> calls.
    /// Defaults to 300 s (5 minutes) so pathological hangs on blocked network/SQL
    /// connections abort instead of hanging the ETL job permanently (L17/#153).
    /// Pass 0 to restore the previous infinite-wait behaviour.
    /// </summary>
    public int TimeoutSeconds { get; }

    /// <summary>
    /// <see cref="SqlBulkCopy"/> options used when copying rows to the staging table.
    /// Defaults to <see cref="SqlBulkCopyOptions.Default"/> to preserve existing behaviour.
    /// <para>
    /// <b>Opt-in recommendation:</b> pass <see cref="SqlBulkCopyOptions.TableLock"/> for
    /// private/exclusive staging tables (e.g. per-job temp staging tables) to maximise
    /// throughput by acquiring an exclusive lock on the destination and avoiding per-row
    /// lock escalation. Do NOT use TableLock on shared staging tables that multiple jobs
    /// write concurrently.
    /// </para>
    /// </summary>
    public SqlBulkCopyOptions BulkCopyOptions { get; }

    /// <summary>
    /// Internal batch size passed to <see cref="SqlBulkCopy.BatchSize"/>.
    /// <para>
    /// 0 (default) = one network round-trip per <c>BulkLoadAsync</c> call, which matches
    /// the pre-10.6 behaviour where <c>BatchSize</c> was set to the full
    /// <c>DataTable.Rows.Count</c>. When set to a positive value N, SqlBulkCopy will
    /// commit rows in sub-batches of N, which trades throughput for smaller individual
    /// transactions and lower peak memory on very large batches.
    /// </para>
    /// </summary>
    public int InternalBatchSize { get; }

    /// <summary>
    /// Initialises the loader with configurable timeout, SqlBulkCopy options,
    /// and internal batch size.
    /// </summary>
    /// <param name="timeoutSeconds">
    /// Seconds before bulk-copy and SQL commands time out.
    /// 0 = no limit (infinite, previous behaviour).
    /// Defaults to 300.
    /// </param>
    /// <param name="bulkCopyOptions">
    /// <see cref="SqlBulkCopyOptions"/> passed to the <see cref="SqlBulkCopy"/>
    /// constructor. Defaults to <see cref="SqlBulkCopyOptions.Default"/>, which
    /// preserves the pre-10.6 row-lock behaviour. Use
    /// <see cref="SqlBulkCopyOptions.TableLock"/> for private staging tables to
    /// improve throughput.
    /// </param>
    /// <param name="internalBatchSize">
    /// Number of rows per SqlBulkCopy sub-batch. 0 (default) = full
    /// <c>DataTable.Rows.Count</c> in one shot (pre-10.6 behaviour).
    /// </param>
    public MssqlBulkLoader(
        int timeoutSeconds = 300,
        SqlBulkCopyOptions bulkCopyOptions = SqlBulkCopyOptions.Default,
        int internalBatchSize = 0)
    {
        TimeoutSeconds = timeoutSeconds;
        BulkCopyOptions = bulkCopyOptions;
        InternalBatchSize = internalBatchSize;
    }

    public async Task BulkLoadAsync(
        string connectionString, string stagingTableName,
        DataTable batch, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        // Pass BulkCopyOptions (opt-in; default is SqlBulkCopyOptions.Default
        // which preserves pre-10.6 row-lock behaviour).
        using var bulkCopy = new SqlBulkCopy(conn, BulkCopyOptions, externalTransaction: null)
        {
            DestinationTableName = QuoteQualified(stagingTableName),
            // InternalBatchSize == 0 → use full count, matching pre-10.6 behaviour.
            BatchSize = InternalBatchSize > 0 ? InternalBatchSize : batch.Rows.Count,
            BulkCopyTimeout = TimeoutSeconds
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

        // Public interface path: no pre-resolved column list available,
        // so fall back to the INFORMATION_SCHEMA round-trip.
        var columns = await GetColumnsAsync(conn, stagingTableName, cancellationToken);
        var keyColumns = ParseMergeKeys(mergeKeyColumn);
        await ExecuteMergeAsync(conn, stagingTableName, targetTableName, keyColumns,
            columns, cancellationToken);
    }

    /// <summary>
    /// Additive internal overload: accepts a pre-resolved column list from the
    /// caller (typically the batch <see cref="DataTable.Columns"/> names) to
    /// skip the INFORMATION_SCHEMA metadata round-trip. Preserves identical
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
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var keyColumns = ParseMergeKeys(mergeKeyColumn);
        await ExecuteMergeAsync(conn, stagingTableName, targetTableName, keyColumns,
            columns, cancellationToken);
    }

    /// <summary>
    /// ETL-009: Parse a (possibly composite) merge-key string into an ordered
    /// list of individual column names.
    /// <para>
    /// Examples:
    /// <list type="bullet">
    /// <item><c>"OrderId"</c> → <c>["OrderId"]</c> (single-key — back-compat)</item>
    /// <item><c>"TenantId, OrderNo"</c> → <c>["TenantId", "OrderNo"]</c></item>
    /// </list>
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ParseMergeKeys(string mergeKeyColumn)
    {
        if (string.IsNullOrWhiteSpace(mergeKeyColumn))
            throw new ArgumentException("mergeKeyColumn must not be null or empty.", nameof(mergeKeyColumn));

        var keys = mergeKeyColumn
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => !string.IsNullOrEmpty(k))
            .ToList();

        if (keys.Count == 0)
            throw new ArgumentException(
                $"mergeKeyColumn '{mergeKeyColumn}' contains no valid column names.", nameof(mergeKeyColumn));

        return keys;
    }

    private async Task ExecuteMergeAsync(
        SqlConnection conn,
        string stagingTableName, string targetTableName,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        var keySet = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
        var updateCols = columns.Where(c => !keySet.Contains(c)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"MERGE {QuoteQualified(targetTableName)} AS target");
        sb.AppendLine($"USING {QuoteQualified(stagingTableName)} AS source");
        // ETL-009: build composite ON clause from all key columns (AND-joined)
        sb.AppendLine("ON " + string.Join(" AND ",
            keyColumns.Select(k => $"target.[{k}] = source.[{k}]")));

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
        cmd.CommandTimeout = TimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceAsync(
        string connectionString, string stagingTableName,
        string targetTableName, string? whereClause,
        CancellationToken cancellationToken = default)
    {
        // Basic SQL-injection guard. Operator owns the where clause
        // (it sits in EtlJobDefinition, edited by an admin), but we
        // still refuse the obvious foot-guns so a typo/paste from
        // user input can't escalate.
        if (!IsSafeWhereClause(whereClause))
        {
            throw new System.ArgumentException(
                $"Replace whereClause contains disallowed characters or token: '{whereClause}'. " +
                "Reject: ';', '--', '/*', system-procedure prefixes (xp_/sp_).",
                nameof(whereClause));
        }

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(conn, stagingTableName, cancellationToken);
        if (columns.Count == 0)
        {
            // Replace with no source columns is meaningless; surface
            // before the DELETE wipes target.
            throw new System.InvalidOperationException(
                $"Replace mode aborted: staging table '{stagingTableName}' has no columns.");
        }

        await using var tran = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            // 1. DELETE matching rows (or whole table if no WHERE)
            var deleteSql = string.IsNullOrWhiteSpace(whereClause)
                ? $"DELETE FROM {QuoteQualified(targetTableName)}"
                : $"DELETE FROM {QuoteQualified(targetTableName)} WHERE {whereClause}";
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tran;
                del.CommandText = deleteSql;
                del.CommandTimeout = TimeoutSeconds;
                await del.ExecuteNonQueryAsync(cancellationToken);
            }

            // 2. INSERT FROM staging
            var colList = string.Join(", ", columns.Select(c => $"[{c}]"));
            var insertSql =
                $"INSERT INTO {QuoteQualified(targetTableName)} ({colList}) " +
                $"SELECT {colList} FROM {QuoteQualified(stagingTableName)}";
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

    /// <summary>
    /// Conservative whitelist for the operator-supplied DELETE WHERE
    /// clause. Public for unit-test determinism. Returns true for
    /// null/empty (which means "delete entire table" — a deliberate
    /// caller choice, not an injection).
    /// </summary>
    public static bool IsSafeWhereClause(string? whereClause)
    {
        if (string.IsNullOrWhiteSpace(whereClause)) { return true; }
        // Statement separator / comments — block them outright.
        if (whereClause.Contains(';')) { return false; }
        if (whereClause.Contains("--")) { return false; }
        if (whereClause.Contains("/*")) { return false; }
        // Block extended-procedure prefixes regardless of case.
        var lower = whereClause.ToLowerInvariant();
        if (lower.Contains("xp_")) { return false; }
        if (lower.Contains("sp_")) { return false; }
        return true;
    }

    /// <summary>
    /// Splits a staging table name into (schema, tableName) pair.
    /// Supports bracketed and un-bracketed forms:
    ///   "audit.STG_Orders"      → ("audit", "STG_Orders")
    ///   "[audit].[STG_Orders]"  → ("audit", "STG_Orders")
    ///   "STG_Orders"            → ("dbo",   "STG_Orders")
    /// Public for unit-test determinism.
    /// </summary>
    public static (string Schema, string Table) ParseSchemaAndTable(string stagingTableName)
    {
        var dotIndex = stagingTableName.IndexOf('.');
        if (dotIndex >= 0)
        {
            var schema = stagingTableName[..dotIndex].Trim('[', ']');
            var table = stagingTableName[(dotIndex + 1)..].Trim('[', ']');
            return (schema, table);
        }
        return ("dbo", stagingTableName);
    }

    /// <summary>
    /// Returns a properly bracket-quoted, schema-qualified table identifier for use in
    /// T-SQL DDL/DML. Each part is quoted independently so schema-qualified names like
    /// "audit.STG_Orders" produce "[audit].[STG_Orders]" rather than "[audit.STG_Orders]".
    /// </summary>
    public static string QuoteQualified(string tableName)
    {
        var (schema, table) = ParseSchemaAndTable(tableName);
        return $"[{schema}].[{table}]";
    }

    public async Task TruncateStagingAsync(
        string connectionString, string stagingTableName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE TABLE {QuoteQualified(stagingTableName)}";
        // Apply timeout when explicitly set (opt-in; 0 = no limit as before).
        cmd.CommandTimeout = TimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnsureStagingTableAsync(
        string connectionString, string stagingTableName,
        StagingTableSpec spec, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        // Derive schema name from staging table name if it contains a schema prefix
        // (e.g. "audit.STG_Orders" → schema="audit", table="STG_Orders"),
        // otherwise default to 'dbo'. Without the schema filter, a same-named table in
        // another schema causes CREATE to be silently skipped (M23).
        var (schemaName, tableNameOnly) = ParseSchemaAndTable(stagingTableName);

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME = @tableName
              AND TABLE_SCHEMA = @schemaName";
        checkCmd.Parameters.AddWithValue("@tableName", tableNameOnly);
        checkCmd.Parameters.AddWithValue("@schemaName", schemaName);
        checkCmd.CommandTimeout = TimeoutSeconds;
        var exists = (int)(await checkCmd.ExecuteScalarAsync(cancellationToken))! > 0;

        if (!exists)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE {QuoteQualified(stagingTableName)} (");
            sb.AppendLine(string.Join(",\n",
                spec.Columns.Select(c => $"  [{c.Name}] {c.SqlType}")));
            sb.AppendLine(")");

            await using var createCmd = conn.CreateCommand();
            createCmd.CommandText = sb.ToString();
            createCmd.CommandTimeout = TimeoutSeconds;
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Queries INFORMATION_SCHEMA.COLUMNS for the column names of
    /// <paramref name="tableName"/>, filtering on both TABLE_NAME and
    /// TABLE_SCHEMA to correctly handle schema-qualified names such as
    /// "audit.STG_x" (previously TABLE_NAME-only filter returned 0 rows).
    /// </summary>
    private static async Task<List<string>> GetColumnsAsync(
        SqlConnection conn, string tableName, CancellationToken ct)
    {
        var (schemaName, tableNameOnly) = ParseSchemaAndTable(tableName);

        List<string> columns = [];
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME   = @tableName
              AND TABLE_SCHEMA = @schemaName
            ORDER BY ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@tableName", tableNameOnly);
        cmd.Parameters.AddWithValue("@schemaName", schemaName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(reader.GetString(0));
        }
        return columns;
    }

    /// <summary>
    /// SQL used by <see cref="IsUniqueColumnAsync"/> to check whether a column
    /// participates in a PRIMARY KEY or UNIQUE constraint.
    /// Exposed as <c>internal static readonly</c> so unit tests can assert that
    /// the query filters on both TABLE_SCHEMA and TABLE_NAME (Issue #391).
    /// </summary>
    internal static readonly string IsUniqueColumnQuery = @"
            SELECT COUNT(*)
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
            JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS t
              ON k.CONSTRAINT_NAME = t.CONSTRAINT_NAME
             AND k.TABLE_SCHEMA    = t.TABLE_SCHEMA
            WHERE k.TABLE_NAME   = @tableName
              AND k.TABLE_SCHEMA  = @schemaName
              AND k.COLUMN_NAME   = @colName
              AND t.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE')";

    public async Task<bool> IsUniqueColumnAsync(
        string connectionString, string tableName, string columnName,
        CancellationToken cancellationToken = default)
    {
        var (schemaName, tableNameOnly) = ParseSchemaAndTable(tableName);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        // Check PK or Unique constraints in MSSQL, scoped to the correct schema
        // so that a same-named table in another schema is never matched (#391).
        cmd.CommandText = IsUniqueColumnQuery;
        cmd.Parameters.AddWithValue("@tableName", tableNameOnly);
        cmd.Parameters.AddWithValue("@schemaName", schemaName);
        cmd.Parameters.AddWithValue("@colName", columnName);

        var count = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        return count > 0;
    }
}
