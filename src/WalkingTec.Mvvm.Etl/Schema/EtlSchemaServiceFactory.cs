#nullable enable
using System;
using Microsoft.Extensions.Caching.Memory;
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
    /// Creates a plain (non-caching) schema service for <paramref name="dbType"/>.
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

    /// <summary>
    /// Creates a caching schema service (opt-in, 10.6+) for <paramref name="dbType"/>,
    /// wrapping the plain implementation with a <see cref="CachingEtlSchemaService"/>
    /// decorator backed by <paramref name="cache"/>.
    ///
    /// <para>
    /// The bare <see cref="Create"/> overload remains no-cache — existing callers
    /// that do not pass a cache are unaffected.
    /// </para>
    /// </summary>
    /// <param name="dbType">The database type (determines inner service).</param>
    /// <param name="cache">Memory cache to store schema results.</param>
    /// <param name="ttl">Cache TTL; <c>null</c> uses <see cref="CachingEtlSchemaService.DefaultTtlSeconds"/>.</param>
    public static IEtlSchemaService CreateWithCache(
        DBTypeEnum dbType,
        IMemoryCache cache,
        TimeSpan? ttl = null)
    {
        var inner = Create(dbType);
        return new CachingEtlSchemaService(inner, cache, ttl);
    }
}
