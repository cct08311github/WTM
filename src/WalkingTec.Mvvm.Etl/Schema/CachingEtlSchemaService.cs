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
    // #676: defaults to TimeProvider.System — identical behavior to the pre-#676 code, which
    // relied entirely on IMemoryCache's own real-wall-clock expiration (see remarks on
    // GetOrCreateWithExpiryAsync for why that native expiration is no longer authoritative).
    private readonly TimeProvider _timeProvider;

    // Wraps a cached value with the absolute expiry computed when it was stored, so
    // GetOrCreateWithExpiryAsync can authoritatively decide "still fresh?" against
    // _timeProvider instead of trusting IMemoryCache's own (non-TimeProvider-aware,
    // ISystemClock-based) internal expiration timer. Same pattern as
    // WalkingTec.Mvvm.Core.Analysis.MemoryAnalysisCache.
    private sealed class CacheEntry<T>(T value, DateTimeOffset expiresAt)
    {
        public T Value { get; } = value;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }

    /// <summary>
    /// Creates a caching decorator wrapping <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The inner (non-caching) schema service.</param>
    /// <param name="cache">Memory cache instance (injected from DI or created ad-hoc).</param>
    /// <param name="ttl">Cache TTL; <c>null</c> uses <see cref="DefaultTtlSeconds"/>.</param>
    /// <param name="timeProvider">
    /// #676: clock seam for TTL expiry. Optional — defaults to <see cref="TimeProvider.System"/>.
    /// </param>
    public CachingEtlSchemaService(
        IEtlSchemaService inner,
        IMemoryCache cache,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _ttl = ttl ?? TimeSpan.FromSeconds(DefaultTtlSeconds);
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        return GetOrCreateWithExpiryAsync(
            cacheKey,
            () => _inner.ListTablesAsync(connectionString, schemaFilter, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EtlColumnInfo>> ListColumnsAsync(
        string connectionString,
        string tableName,
        string? schemaName = null,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"etl-schema:columns:{CsKey(connectionString)}:{schemaName ?? "*"}:{tableName}";
        return GetOrCreateWithExpiryAsync(
            cacheKey,
            () => _inner.ListColumnsAsync(connectionString, tableName, schemaName, cancellationToken));
    }

    /// <summary>
    /// Get-or-create with an explicit, TimeProvider-driven expiry check (#676).
    ///
    /// <para><see cref="Microsoft.Extensions.Caching.Memory.MemoryCache"/>'s own
    /// <c>AbsoluteExpirationRelativeToNow</c> expiration is driven by
    /// <c>MemoryCacheOptions.Clock</c> (the legacy <c>ISystemClock</c> abstraction, not
    /// <see cref="TimeProvider"/> — the pinned package version exposes no TimeProvider seam), so
    /// it cannot be faked deterministically in tests. Every entry is stamped with its own
    /// absolute expiry computed from <see cref="_timeProvider"/> and re-checked explicitly on
    /// every read — this is the authoritative expiry check. <c>IMemoryCache</c>'s own TTL is
    /// still set (using the same <see cref="_ttl"/>) purely as a real-wall-clock backstop so
    /// entries are still reclaimed for memory pressure; it is never relied upon for
    /// correctness.</para>
    /// </summary>
    private async Task<T> GetOrCreateWithExpiryAsync<T>(string cacheKey, Func<Task<T>> factory)
    {
        if (_cache.TryGetValue(cacheKey, out CacheEntry<T>? entry) && entry is not null
            && _timeProvider.GetUtcNow() < entry.ExpiresAt)
        {
            return entry.Value;
        }

        var value = await factory().ConfigureAwait(false);
        var newEntry = new CacheEntry<T>(value, _timeProvider.GetUtcNow() + _ttl);
        _cache.Set(cacheKey, newEntry, _ttl);
        return value;
    }

    /// <summary>
    /// Returns a collision-resistant, non-reversible hex key derived from the
    /// connection string via SHA-256. The full string is never stored in the
    /// cache key, preventing accidental credential exposure in diagnostic dumps.
    /// </summary>
    private static string CsKey(string cs) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cs)));
}