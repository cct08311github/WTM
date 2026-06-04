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
                ? $"DELETE FROM [{targetTableName}]"
                : $"DELETE FROM [{targetTableName}] WHERE {whereClause}";
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tran;
                del.CommandText = deleteSql;
                del.CommandTimeout = 0;
                await del.ExecuteNonQueryAsync(cancellationToken);
            }

            // 2. INSERT FROM staging
            var colList = string.Join(", ", columns.Select(c => $"[{c}]"));
            var insertSql =
                $"INSERT INTO [{targetTableName}] ({colList}) " +
                $"SELECT {colList} FROM [{stagingTableName}]";
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
        List<string> columns = [];
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
