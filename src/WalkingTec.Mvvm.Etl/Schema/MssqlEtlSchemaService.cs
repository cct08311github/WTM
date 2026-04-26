#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace WalkingTec.Mvvm.Etl.Schema;

/// <summary>
/// MSSQL schema introspection via <c>INFORMATION_SCHEMA</c> and
/// <c>sys</c>-catalog views. Read-only.
/// </summary>
public class MssqlEtlSchemaService : IEtlSchemaService
{
    public async Task<IReadOnlyList<EtlTableInfo>> ListTablesAsync(
        string connectionString,
        string? schemaFilter = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(schemaFilter))
        {
            cmd.CommandText = @"
                SELECT TABLE_SCHEMA, TABLE_NAME
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_TYPE = 'BASE TABLE'
                ORDER BY TABLE_SCHEMA, TABLE_NAME";
        }
        else
        {
            cmd.CommandText = @"
                SELECT TABLE_SCHEMA, TABLE_NAME
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_TYPE = 'BASE TABLE'
                  AND LOWER(TABLE_SCHEMA) = LOWER(@schema)
                ORDER BY TABLE_NAME";
            cmd.Parameters.AddWithValue("@schema", schemaFilter);
        }

        var result = new List<EtlTableInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EtlTableInfo(
                Schema: reader.GetString(0),
                Name: reader.GetString(1)));
        }
        return result;
    }

    public async Task<IReadOnlyList<EtlColumnInfo>> ListColumnsAsync(
        string connectionString,
        string tableName,
        string? schemaName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        // One round-trip with LEFT JOIN against the PK constraint
        // metadata so callers don't need a second query.
        var sql = @"
            SELECT
                c.COLUMN_NAME,
                c.DATA_TYPE
                  + CASE
                      WHEN c.CHARACTER_MAXIMUM_LENGTH IS NOT NULL
                       AND c.DATA_TYPE IN ('varchar','nvarchar','char','nchar','varbinary','binary')
                      THEN '(' + IIF(c.CHARACTER_MAXIMUM_LENGTH = -1, 'MAX',
                              CAST(c.CHARACTER_MAXIMUM_LENGTH AS NVARCHAR(20))) + ')'
                      ELSE '' END AS DATA_TYPE_DISPLAY,
                c.IS_NULLABLE,
                CASE WHEN kcu.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS IS_PK
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN (
                SELECT k.TABLE_SCHEMA, k.TABLE_NAME, k.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
                  ON tc.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                 AND tc.TABLE_SCHEMA   = k.TABLE_SCHEMA
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ) kcu
              ON c.TABLE_SCHEMA = kcu.TABLE_SCHEMA
             AND c.TABLE_NAME   = kcu.TABLE_NAME
             AND c.COLUMN_NAME  = kcu.COLUMN_NAME
            WHERE c.TABLE_NAME = @table
        " + (string.IsNullOrWhiteSpace(schemaName)
                ? string.Empty
                : "  AND LOWER(c.TABLE_SCHEMA) = LOWER(@schema)\n")
        + " ORDER BY c.ORDINAL_POSITION";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@table", tableName);
        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            cmd.Parameters.AddWithValue("@schema", schemaName);
        }

        var result = new List<EtlColumnInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EtlColumnInfo(
                Name: reader.GetString(0),
                DataType: reader.GetString(1),
                IsNullable: string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase),
                IsPrimaryKey: reader.GetInt32(3) != 0));
        }
        return result;
    }
}
