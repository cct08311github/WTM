#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;

namespace WalkingTec.Mvvm.Etl.Schema;

/// <summary>
/// Oracle schema introspection via <c>ALL_TABLES</c> /
/// <c>ALL_TAB_COLUMNS</c> / <c>ALL_CONSTRAINTS</c>. Read-only.
/// </summary>
/// <remarks>
/// "Schema" maps to Oracle <c>OWNER</c>. By default Oracle returns
/// the current user's tables AND tables they've been granted access
/// to — the connection user determines visibility, just like MSSQL.
/// </remarks>
public class OracleEtlSchemaService : IEtlSchemaService
{
    public async Task<IReadOnlyList<EtlTableInfo>> ListTablesAsync(
        string connectionString,
        string? schemaFilter = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new OracleConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(schemaFilter))
        {
            // USER_TABLES is faster than ALL_TABLES + filter; the
            // connection user's own tables are the dominant case.
            cmd.CommandText = @"
                SELECT '' AS OWNER, TABLE_NAME
                FROM USER_TABLES
                ORDER BY TABLE_NAME";
        }
        else
        {
            cmd.CommandText = @"
                SELECT OWNER, TABLE_NAME
                FROM ALL_TABLES
                WHERE UPPER(OWNER) = UPPER(:schema)
                ORDER BY TABLE_NAME";
            cmd.Parameters.Add(new OracleParameter("schema", schemaFilter));
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

        await using var conn = new OracleConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        // PK columns via ALL_CONSTRAINTS + ALL_CONS_COLUMNS join.
        var sql = @"
            SELECT
                c.COLUMN_NAME,
                CASE
                    WHEN c.DATA_TYPE IN ('VARCHAR2','CHAR','NVARCHAR2','NCHAR')
                        THEN c.DATA_TYPE || '(' || c.CHAR_LENGTH || ')'
                    WHEN c.DATA_TYPE = 'NUMBER' AND c.DATA_PRECISION IS NOT NULL
                        THEN 'NUMBER(' || c.DATA_PRECISION || NVL2(c.DATA_SCALE, ',' || c.DATA_SCALE, '') || ')'
                    ELSE c.DATA_TYPE
                END AS DATA_TYPE_DISPLAY,
                c.NULLABLE,
                CASE WHEN pk.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS IS_PK
            FROM " + (string.IsNullOrWhiteSpace(schemaName) ? "USER_TAB_COLUMNS" : "ALL_TAB_COLUMNS") + @" c
            LEFT JOIN (
                SELECT cc.OWNER, cc.TABLE_NAME, cc.COLUMN_NAME
                FROM " + (string.IsNullOrWhiteSpace(schemaName) ? "USER_CONSTRAINTS" : "ALL_CONSTRAINTS") + @" con
                JOIN " + (string.IsNullOrWhiteSpace(schemaName) ? "USER_CONS_COLUMNS" : "ALL_CONS_COLUMNS") + @" cc
                  ON con.CONSTRAINT_NAME = cc.CONSTRAINT_NAME
                WHERE con.CONSTRAINT_TYPE = 'P'
            ) pk
              ON c.TABLE_NAME  = pk.TABLE_NAME
             AND c.COLUMN_NAME = pk.COLUMN_NAME
            WHERE UPPER(c.TABLE_NAME) = UPPER(:tbl)
        " + (string.IsNullOrWhiteSpace(schemaName) ? string.Empty : "  AND UPPER(c.OWNER) = UPPER(:schema)\n")
        + " ORDER BY c.COLUMN_ID";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new OracleParameter("tbl", tableName));
        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            cmd.Parameters.Add(new OracleParameter("schema", schemaName));
        }

        var result = new List<EtlColumnInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EtlColumnInfo(
                Name: reader.GetString(0),
                DataType: reader.GetString(1),
                IsNullable: string.Equals(reader.GetString(2), "Y", StringComparison.OrdinalIgnoreCase),
                IsPrimaryKey: reader.GetInt32(3) != 0));
        }
        return result;
    }
}
