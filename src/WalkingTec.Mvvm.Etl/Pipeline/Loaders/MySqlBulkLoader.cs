#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace WalkingTec.Mvvm.Etl.Pipeline.Loaders;

/// <summary>
/// MySQL 批量載入器 — 批次多列 INSERT + INSERT … ON DUPLICATE KEY UPDATE（upsert）。
/// <para>
/// BulkLoad: 使用批次 multi-row INSERT（單一往返插入整批資料），
/// 效能顯著優於 row-by-row INSERT。
/// </para>
/// <para>
/// Merge: 產生 <c>INSERT INTO target … SELECT … FROM staging
/// ON DUPLICATE KEY UPDATE col = VALUES(col), …</c> 語法（MySQL 5.1+）。
/// 支援複合 merge key（逗號分隔），唯一性需透過 PK 或 UNIQUE INDEX 保障。
/// </para>
/// </summary>
public class MySqlBulkLoader : IBulkLoader
{
    /// <summary>
    /// Timeout in seconds applied to all <c>MySqlCommand.CommandTimeout</c> calls.
    /// <para>
    /// Defaults to 300 s (5 minutes). Pass 0 to disable (infinite wait).
    /// </para>
    /// </summary>
    public int TimeoutSeconds { get; }

    /// <summary>
    /// Number of rows per INSERT sub-batch during <see cref="BulkLoadAsync"/>.
    /// <para>
    /// 0 (default) = insert all rows in one statement (lowest round-trip count,
    /// highest memory peak). Set a positive value to split large batches into
    /// smaller INSERT statements, reducing peak memory at the cost of extra round-trips.
    /// </para>
    /// </summary>
    public int InternalBatchSize { get; }

    /// <summary>
    /// Initialises the loader.
    /// </summary>
    /// <param name="timeoutSeconds">Per-command timeout. Defaults to 300. Pass 0 for no limit.</param>
    /// <param name="internalBatchSize">
    /// Rows per INSERT sub-batch. 0 (default) = one INSERT per BulkLoadAsync call.
    /// </param>
    public MySqlBulkLoader(int timeoutSeconds = 300, int internalBatchSize = 0)
    {
        TimeoutSeconds = timeoutSeconds;
        InternalBatchSize = internalBatchSize;
    }

    public async Task BulkLoadAsync(
        string connectionString, string stagingTableName,
        DataTable batch, CancellationToken cancellationToken = default)
    {
        if (batch.Rows.Count == 0) return;

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var columns = batch.Columns.Cast<DataColumn>().ToList();
        var subBatchSize = InternalBatchSize > 0 ? InternalBatchSize : batch.Rows.Count;

        var allRows = batch.Rows.Cast<DataRow>().ToList();
        for (int offset = 0; offset < allRows.Count; offset += subBatchSize)
        {
            var chunk = allRows.Skip(offset).Take(subBatchSize).ToList();
            await InsertChunkAsync(conn, stagingTableName, columns, chunk, cancellationToken);
        }
    }

    private async Task InsertChunkAsync(
        MySqlConnection conn,
        string stagingTableName,
        List<DataColumn> columns,
        List<DataRow> rows,
        CancellationToken cancellationToken)
    {
        var qTable = QuoteIdentifier(stagingTableName);
        var colList = string.Join(", ", columns.Select(c => QuoteIdentifier(c.ColumnName)));

        // Build parameterised multi-row INSERT
        // INSERT INTO `table` (`c1`, `c2`) VALUES (@r0c0, @r0c1), (@r1c0, @r1c1), ...
        var sb = new StringBuilder();
        sb.Append($"INSERT INTO {qTable} ({colList}) VALUES ");

        var rowPlaceholders = new List<string>();
        for (int r = 0; r < rows.Count; r++)
        {
            var colPlaceholders = columns.Select((_, c) => $"@r{r}c{c}");
            rowPlaceholders.Add("(" + string.Join(", ", colPlaceholders) + ")");
        }
        sb.Append(string.Join(", ", rowPlaceholders));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        cmd.CommandTimeout = TimeoutSeconds;

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < columns.Count; c++)
            {
                var val = rows[r][columns[c].Ordinal];
                cmd.Parameters.AddWithValue($"@r{r}c{c}", val == DBNull.Value ? DBNull.Value : val);
            }
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MergeAsync(
        string connectionString, string stagingTableName,
        string targetTableName, string mergeKeyColumn,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(conn, stagingTableName, cancellationToken);
        var keyColumns = MssqlBulkLoader.ParseMergeKeys(mergeKeyColumn);
        await ExecuteUpsertAsync(conn, stagingTableName, targetTableName, keyColumns, columns, cancellationToken);
    }

    /// <summary>
    /// Additive internal overload: accepts a pre-resolved column list from the caller
    /// to skip the information_schema metadata round-trip.
    /// </summary>
    internal async Task MergeAsync(
        string connectionString, string stagingTableName,
        string targetTableName, string mergeKeyColumn,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var keyColumns = MssqlBulkLoader.ParseMergeKeys(mergeKeyColumn);
        await ExecuteUpsertAsync(conn, stagingTableName, targetTableName, keyColumns, columns, cancellationToken);
    }

    /// <summary>
    /// Builds an <c>INSERT … SELECT … ON DUPLICATE KEY UPDATE …</c> upsert SQL.
    /// <para>
    /// SQL shape (single key example):
    /// <code>
    /// INSERT INTO `target` (`Id`, `Name`, `Value`)
    /// SELECT `Id`, `Name`, `Value` FROM `staging`
    /// ON DUPLICATE KEY UPDATE
    ///   `Name` = VALUES(`Name`),
    ///   `Value` = VALUES(`Value`);
    /// </code>
    /// </para>
    /// <para>
    /// For composite keys, the same SQL shape applies — the uniqueness is enforced by
    /// the target table's composite PK or UNIQUE INDEX; MySQL resolves conflicts using
    /// ON DUPLICATE KEY UPDATE regardless of how many columns form the key.
    /// </para>
    /// </summary>
    internal static string BuildUpsertSql(
        string stagingTableName,
        string targetTableName,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> allColumns)
    {
        var keySet = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
        var updateCols = allColumns.Where(c => !keySet.Contains(c)).ToList();

        var qTarget = QuoteIdentifier(targetTableName);
        var qStaging = QuoteIdentifier(stagingTableName);
        var colList = string.Join(", ", allColumns.Select(QuoteIdentifier));

        var sb = new StringBuilder();
        sb.AppendLine($"INSERT INTO {qTarget} ({colList})");
        sb.AppendLine($"SELECT {colList} FROM {qStaging}");

        if (updateCols.Count > 0)
        {
            sb.AppendLine("ON DUPLICATE KEY UPDATE");
            sb.Append(string.Join(",\n",
                updateCols.Select(c => $"  {QuoteIdentifier(c)} = VALUES({QuoteIdentifier(c)})")));
        }
        else
        {
            // All columns are key columns — use INSERT IGNORE to avoid duplicate-key error
            // (rewrite header to INSERT IGNORE … SELECT)
            var header = $"INSERT IGNORE INTO {qTarget} ({colList})";
            return $"{header}\nSELECT {colList} FROM {qStaging}";
        }

        return sb.ToString();
    }

    private async Task ExecuteUpsertAsync(
        MySqlConnection conn,
        string stagingTableName, string targetTableName,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        var sql = BuildUpsertSql(stagingTableName, targetTableName, keyColumns, columns);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = TimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceAsync(
        string connectionString, string stagingTableName,
        string targetTableName, string? whereClause,
        CancellationToken cancellationToken = default)
    {
        if (!MssqlBulkLoader.IsSafeWhereClause(whereClause))
        {
            throw new ArgumentException(
                $"Replace whereClause contains disallowed characters or token: '{whereClause}'. " +
                "Reject: ';', '--', '/*', system-procedure prefixes (xp_/sp_).",
                nameof(whereClause));
        }

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(conn, stagingTableName, cancellationToken);
        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Replace mode aborted: staging table '{stagingTableName}' has no columns.");
        }

        await using var tran = await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            var deleteSql = string.IsNullOrWhiteSpace(whereClause)
                ? $"DELETE FROM {QuoteIdentifier(targetTableName)}"
                : $"DELETE FROM {QuoteIdentifier(targetTableName)} WHERE {whereClause}";

            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tran;
                del.CommandText = deleteSql;
                del.CommandTimeout = TimeoutSeconds;
                await del.ExecuteNonQueryAsync(cancellationToken);
            }

            var qTarget = QuoteIdentifier(targetTableName);
            var qStaging = QuoteIdentifier(stagingTableName);
            var colList = string.Join(", ", columns.Select(QuoteIdentifier));
            var insertSql = $"INSERT INTO {qTarget} ({colList}) SELECT {colList} FROM {qStaging}";

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
            await tran.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task TruncateStagingAsync(
        string connectionString, string stagingTableName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE TABLE {QuoteIdentifier(stagingTableName)}";
        cmd.CommandTimeout = TimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnsureStagingTableAsync(
        string connectionString, string stagingTableName,
        StagingTableSpec spec, CancellationToken cancellationToken = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var (dbName, tableNameOnly) = ParseDatabaseAndTable(conn, stagingTableName);

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_name   = @tableName
              AND table_schema = @dbName";
        checkCmd.Parameters.AddWithValue("@tableName", tableNameOnly);
        checkCmd.Parameters.AddWithValue("@dbName", dbName);
        checkCmd.CommandTimeout = TimeoutSeconds;
        var exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(cancellationToken)) > 0;

        if (!exists)
        {
            var qTable = QuoteIdentifier(stagingTableName);
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE {qTable} (");
            sb.AppendLine(string.Join(",\n",
                spec.Columns.Select(c => $"  {QuoteIdentifier(c.Name)} {c.SqlType}")));
            sb.Append(')');

            await using var createCmd = conn.CreateCommand();
            createCmd.CommandText = sb.ToString();
            createCmd.CommandTimeout = TimeoutSeconds;
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<bool> IsUniqueColumnAsync(
        string connectionString, string tableName, string columnName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        // Check PK or Unique constraints via information_schema (MySQL-compatible)
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM information_schema.key_column_usage k
            JOIN information_schema.table_constraints t
              ON k.constraint_name = t.constraint_name
             AND k.constraint_schema = t.constraint_schema
            WHERE k.table_name = @tableName
              AND k.column_name = @colName
              AND t.constraint_type IN ('PRIMARY KEY', 'UNIQUE')";
        cmd.Parameters.AddWithValue("@tableName", tableName);
        cmd.Parameters.AddWithValue("@colName", columnName);

        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

    /// <summary>
    /// Queries <c>information_schema.columns</c> for the column names of
    /// <paramref name="tableName"/> in the current database.
    /// </summary>
    private static async Task<List<string>> GetColumnsAsync(
        MySqlConnection conn, string tableName, CancellationToken ct)
    {
        var (dbName, tableNameOnly) = ParseDatabaseAndTable(conn, tableName);

        var columns = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT column_name FROM information_schema.columns
            WHERE table_name   = @tableName
              AND table_schema = @dbName
            ORDER BY ordinal_position";
        cmd.Parameters.AddWithValue("@tableName", tableNameOnly);
        cmd.Parameters.AddWithValue("@dbName", dbName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));
        return columns;
    }

    /// <summary>
    /// Splits "db.table" or "table" into (database, tableName).
    /// Falls back to the current database from the open connection.
    /// </summary>
    private static (string Database, string Table) ParseDatabaseAndTable(
        MySqlConnection conn, string tableName)
    {
        var clean = tableName.Trim('`');
        var dotIndex = clean.IndexOf('.');
        if (dotIndex >= 0)
        {
            var db = clean[..dotIndex].Trim('`');
            var tbl = clean[(dotIndex + 1)..].Trim('`');
            return (db, tbl);
        }
        // Use current database from connection string
        return (conn.Database, clean);
    }

    /// <summary>
    /// Wraps a MySQL identifier in backticks, escaping any embedded backticks by doubling them.
    /// Handles "db.table" by quoting each part.
    /// </summary>
    internal static string QuoteIdentifier(string identifier)
    {
        var dotIndex = identifier.IndexOf('.');
        if (dotIndex >= 0)
        {
            var schema = identifier[..dotIndex].Trim('`');
            var table = identifier[(dotIndex + 1)..].Trim('`');
            return $"`{schema.Replace("`", "``")}`.`{table.Replace("`", "``")}`";
        }
        return $"`{identifier.Trim('`').Replace("`", "``")}`";
    }
}
