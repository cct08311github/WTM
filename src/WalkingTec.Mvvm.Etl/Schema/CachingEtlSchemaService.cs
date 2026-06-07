#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace WalkingTec.Mvvm.Etl.Schema;

/// <summary>
/// Caching decorator for <see cref="IEtlSchemaService"/> (opt-in, 10.6+).
/// Wraps an inner service and caches results in <see cref="IMemoryCache"/>
/// with a configurable TTL (default <see cref="DefaultTtl"/>).
///
/// <para>
/// Cache keys include the connection-string key (<c>csKey</c>) and table name
/// so results are isolated per database and per table. The connection string
/// itself is never stored in the cache key.
/// </para>
///
/// <para>
/// Obtain via <see cref="EtlSchemaServiceFactory.CreateWithCache"/> —
/// do not construct directly if you want the correct inner service matched
/// to <c>dbType</c>.
/// </para>
/// </summary>
public sealed class CachingEtlSchemaService : IEtlSchemaService
{
    /// <summary>Default cache TTL — 60 seconds.</summary>
    public const int DefaultTtlSeconds = 60;

    private readonly IEtlSchemaService _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    /// <summary>
    /// Creates a caching decorator wrapping <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The inner (non-caching) schema service.</param>
    /// <param name="cache">Memory cache instance (injected from DI or created ad-hoc).</param>
    /// <param name="ttl">Cache TTL; <c>null</c> uses <see cref="DefaultTtlSeconds"/>.</param>
    public CachingEtlSchemaService(
        IEtlSchemaService inner,
        IMemoryCache cache,
        TimeSpan? ttl = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _ttl = ttl ?? TimeSpan.FromSeconds(DefaultTtlSeconds);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EtlTableInfo>> ListTablesAsync(
        string connectionString,
        string? schemaFilter = null,
        CancellationToken cancellationToken = default)
    {
        // Cache key includes a hash of the connection string (not the raw string)
        // so that different databases on the same host are cached separately,
        // without persisting the full connection string (which may contain credentials).
        var cacheKey = $"etl-schema:tables:{CsKey(connectionString)}:{schemaFilter ?? "*"}";
        return _cache.GetOrCreateAsync(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _ttl;
            return _inner.ListTablesAsync(connectionString, schemaFilter, cancellationToken)!;
        })!;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EtlColumnInfo>> ListColumnsAsync(
        string connectionString,
        string tableName,
        string? schemaName = null,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"etl-schema:columns:{CsKey(connectionString)}:{schemaName ?? "*"}:{tableName}";
        return _cache.GetOrCreateAsync(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _ttl;
            return _inner.ListColumnsAsync(connectionString, tableName, schemaName, cancellationToken)!;
        })!;
    }

    /// <summary>
    /// Returns a collision-resistant, non-reversible hex key derived from the
    /// connection string via SHA-256. The full string is never stored in the
    /// cache key, preventing accidental credential exposure in diagnostic dumps.
    /// </summary>
    private static string CsKey(string cs) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cs)));
}