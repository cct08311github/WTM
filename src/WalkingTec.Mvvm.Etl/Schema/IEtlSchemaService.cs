#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Etl.Schema;

/// <summary>
/// Schema introspection (10.5+) — reads table/column metadata from a
/// connected source or target database so the WTM admin UI can offer
/// "browse tables → pick one → see columns" without requiring the
/// operator to type table/column names by hand.
/// </summary>
/// <remarks>
/// Implementations exist per DB type (<see cref="DBTypeEnum.SqlServer"/>
/// and <see cref="DBTypeEnum.Oracle"/>) — pick via
/// <see cref="EtlSchemaServiceFactory.Create"/>. Read-only by design;
/// the service must never issue DDL / DML against the inspected DB.
/// Caller is responsible for permission gating: the connection string
/// determines which schemas the service can see, so an admin who can
/// connect to "PROD" can list its tables.
/// </remarks>
public interface IEtlSchemaService
{
    /// <summary>
    /// List tables visible on the connection. Returns table-name +
    /// schema (DB-dependent — empty string when N/A).
    /// </summary>
    /// <param name="connectionString">Resolved connection string.</param>
    /// <param name="schemaFilter">Optional schema filter (case-insensitive
    /// equality). For Oracle, this maps to <c>OWNER</c>; for MSSQL
    /// this maps to <c>TABLE_SCHEMA</c>. <c>null</c> = all schemas
    /// the connection user has access to.</param>
    Task<IReadOnlyList<EtlTableInfo>> ListTablesAsync(
        string connectionString,
        string? schemaFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List columns of a single table. Throws when the table doesn't
    /// exist (or when the connection user can't see it — DB returns
    /// the same empty result as missing).
    /// </summary>
    Task<IReadOnlyList<EtlColumnInfo>> ListColumnsAsync(
        string connectionString,
        string tableName,
        string? schemaName = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Table metadata returned by <see cref="IEtlSchemaService.ListTablesAsync"/>.</summary>
public sealed record EtlTableInfo(string Schema, string Name)
{
    /// <summary>"<c>schema.name</c>" if schema is non-empty, else just "<c>name</c>".</summary>
    public string FullName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

/// <summary>Column metadata returned by <see cref="IEtlSchemaService.ListColumnsAsync"/>.</summary>
public sealed record EtlColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey);
