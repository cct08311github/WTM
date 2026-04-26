#nullable enable
using System;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Etl.Schema;

/// <summary>
/// Picks the right <see cref="IEtlSchemaService"/> implementation per
/// <see cref="DBTypeEnum"/> — same dispatcher pattern as
/// <c>EtlSourceFactory</c>. Kept stateless so callers don't have to
/// worry about lifetime.
/// </summary>
public static class EtlSchemaServiceFactory
{
    /// <summary>
    /// Throws <see cref="NotSupportedException"/> for DB types that
    /// don't have an introspection service yet (PostgreSQL, MySQL,
    /// SQLite — would need <c>pg_catalog</c> / <c>information_schema</c> /
    /// <c>sqlite_master</c> implementations respectively).
    /// </summary>
    public static IEtlSchemaService Create(DBTypeEnum dbType) => dbType switch
    {
        DBTypeEnum.SqlServer => new MssqlEtlSchemaService(),
        DBTypeEnum.Oracle    => new OracleEtlSchemaService(),
        _ => throw new NotSupportedException(
            $"Schema introspection for DBTypeEnum.{dbType} is not yet implemented. " +
            "Currently supported: SqlServer, Oracle."),
    };
}
