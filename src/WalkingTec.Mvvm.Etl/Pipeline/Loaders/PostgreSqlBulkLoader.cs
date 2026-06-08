#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace WalkingTec.Mvvm.Etl.Pipeline.Loaders;

/// <summary>
/// PostgreSQL 批量載入器 — Npgsql binary COPY + INSERT … ON CONFLICT (upsert)。
/// <para>
/// BulkLoad: 使用 <see cref="NpgsqlBinaryImporter"/>（binary COPY protocol）
/// 將資料高效寫入 staging table，效能顯著優於 row-by-row INSERT。
/// </para>
/// <para>
/// Merge: 產生 <c>INSERT INTO target … SELECT … FROM staging
/// ON CONFLICT (key) DO UPDATE SET …</c> 語法（PostgreSQL 9.5+）。
/// 支援複合 merge key（逗號分隔）。
/// </para>
/// </summary>
public class PostgreSqlBulkLoader : IBulkLoader
{
    /// <summary>
    /// Timeout in seconds applied to all <c>NpgsqlCommand.CommandTimeout</c> calls
    /// inside this loader (Merge, Replace, Truncate, EnsureStaging).
    /// <para>
    /// Defaults to 300 s (5 minutes). Pass 0 to disable (infinite wait).
    /// </para>
    /// </summary>
    public int TimeoutSeconds { get; }

    /// <summary>
    /// Initialises the loader with a configurable per-command timeout.
    /// </summary>
    /// <param name="timeoutSeconds">
    /// Seconds before each PostgreSQL command times out.
    /// Defaults to 300. Pass 0 for no limit.
    /// </param>
    public PostgreSqlBulkLoader(int timeoutSeconds = 300)
    {
        TimeoutSeconds = timeoutSeconds;
    }

    public async Task BulkLoadAsync(
        string connectionString, string stagingTableName,
        DataTable batch, CancellationToken cancellationToken = default)
    {
        if (batch.Rows.Count == 0) return;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var columns = batch.Columns.Cast<DataColumn>().ToList();

        // Use binary COPY protocol for high-throughput bulk insert into staging.
        // Quote identifiers to handle mixed-case table and column names.
        var quotedTable = QuoteIdentifier(stagingTableName);
        var quotedCols = columns.Select(c => QuoteIdentifier(c.ColumnName));
        var copyCmd = $"COPY {quotedTable} ({string.Join(", ", quotedCols)}) FROM STDIN (FORMAT BINARY)";

        await using var writer = await conn.BeginBinaryImportAsync(copyCmd, cancellationToken);

        foreach (DataRow row in batch.Rows)
        {
            await writer.StartRowAsync(cancellationToken);
            foreach (var col in columns)
            {
                var val = row[col.Ordinal];
                if (val == DBNull.Value || val == null)
                    await writer.WriteNullAsync(cancellationToken);
                else
                    await writer.WriteAsync(val, cancellationToken);
            }
        }

        await writer.CompleteAsync(cancellationToken);
    }

    public async Task MergeAsync(
        string connectionString, string stagingTableName,
        string targetTableName, string mergeKeyColumn,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
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
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var keyColumns = MssqlBulkLoader.ParseMergeKeys(mergeKeyColumn);
        await ExecuteUpsertAsync(conn, stagingTableName, targetTableName, keyColumns, columns, cancellationToken);
    }

    /// <summary>
    /// Builds and executes an <c>INSERT … ON CONFLICT (keys) DO UPDATE SET …</c> upsert
    /// from staging into target. Supports composite merge keys (AND-joined conflict target).
    /// <para>
    /// SQL shape (single key example):
    /// <code>
    /// INSERT INTO "target" ("Id", "Name", "Value")
    /// SELECT "Id", "Name", "Value" FROM "staging"
    /// ON CONFLICT ("Id") DO UPDATE SET
    ///   "Name" = EXCLUDED."Name",
    ///   "Value" = EXCLUDED."Value";
    /// </code>
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

        var sb = new StringBuilder();
        sb.Append($"INSERT INTO {qTarget} (");
        sb.Append(string.Join(", ", allColumns.Select(QuoteIdentifier)));
        sb.AppendLine(")");
        sb.Append($"SELECT ");
        sb.AppendLine(string.Join(", ", allColumns.Select(QuoteIdentifier)));
        sb.AppendLine($"FROM {qStaging}");
        sb.Append("ON CONFLICT (");
        sb.Append(string.Join(", ", keyColumns.Select(QuoteIdentifier)));
        sb.AppendLine(")");

        if (updateCols.Count > 0)
        {
            sb.AppendLine("DO UPDATE SET");
            sb.Append(string.Join(",\n",
                updateCols.Select(c => $"  {QuoteIdentifier(c)} = EXCLUDED.{QuoteIdentifier(c)}")));
        }
        else
        {
            // All columns are key columns — nothing to update; use DO NOTHING to avoid error
            sb.Append("DO NOTHING");
        }

        return sb.ToString();
    }

    private async Task ExecuteUpsertAsync(
        NpgsqlConnection conn,
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

        await using var conn = new NpgsqlConnection(connectionString);
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
        await using var conn = new NpgsqlConnection(connectionString);
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
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var (schemaName, tableNameOnly) = ParseSchemaAndTable(stagingTableName);

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_name = @tableName
              AND table_schema = @schemaName";
        checkCmd.Parameters.AddWithValue("@tableName", tableNameOnly.ToLowerInvariant());
        checkCmd.Parameters.AddWithValue("@schemaName", schemaName.ToLowerInvariant());
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
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        // Check PK or Unique constraints via information_schema (PostgreSQL-compatible)
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM information_schema.key_column_usage k
            JOIN information_schema.table_constraints t
              ON k.constraint_name = t.constraint_name
             AND k.constraint_schema = t.constraint_schema
            WHERE k.table_name = @tableName
              AND k.column_name = @colName
              AND t.constraint_type IN ('PRIMARY KEY', 'UNIQUE')";
        cmd.Parameters.AddWithValue("@tableName", tableName.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@colName", columnName.ToLowerInvariant());

        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

    /// <summary>
    /// Queries <c>information_schema.columns</c> for the column names of
    /// <paramref name="tableName"/>. PostgreSQL stores identifiers in lower-case
    /// by default (unless quoted at creation), so we normalise the lookup to lower-case.
    /// </summary>
    private async Task<List<string>> GetColumnsAsync(
        NpgsqlConnection conn, string tableName, CancellationToken ct)
    {
        var (schemaName, tableNameOnly) = ParseSchemaAndTable(tableName);

        var columns = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT column_name FROM information_schema.columns
            WHERE table_name   = @tableName
              AND table_schema = @schemaName
            ORDER BY ordinal_position";
        cmd.Parameters.AddWithValue("@tableName", tableNameOnly.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@schemaName", schemaName.ToLowerInvariant());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));
        return columns;
    }

    /// <summary>
    /// Splits a table name into (schema, tableName) pair.
    /// Supports dot-separated and quoted forms.
    ///   "public.stg_orders"     → ("public", "stg_orders")
    ///   "stg_orders"            → ("public", "stg_orders")
    /// </summary>
    public static (string Schema, string Table) ParseSchemaAndTable(string tableName)
    {
        var clean = tableName.Trim('"');
        var dotIndex = clean.IndexOf('.');
        if (dotIndex >= 0)
        {
            var schema = clean[..dotIndex].Trim('"');
            var table = clean[(dotIndex + 1)..].Trim('"');
            return (schema, table);
        }
        return ("public", clean);
    }

    /// <summary>
    /// Wraps a PostgreSQL identifier in double-quotes, escaping any embedded
    /// double-quotes by doubling them. Handles "schema.table" by quoting each part.
    /// </summary>
    internal static string QuoteIdentifier(string identifier)
    {
        var dotIndex = identifier.IndexOf('.');
        if (dotIndex >= 0)
        {
            var schema = identifier[..dotIndex].Trim('"');
            var table = identifier[(dotIndex + 1)..].Trim('"');
            return $"\"{schema.Replace("\"", "\"\"")}\".\"{table.Replace("\"", "\"\"")}\"";
        }
        return $"\"{identifier.Trim('"').Replace("\"", "\"\"")}\"";
    }
}
