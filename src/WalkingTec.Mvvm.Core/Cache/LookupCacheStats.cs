#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Cache
{
    /// <summary>
    /// Observability snapshot for a single Lookup Cache entity type.
    /// Returned by <see cref="ILookupCacheService.GetStats(Type)"/> /
    /// <see cref="ILookupCacheService.GetStats()"/>. Introduced by
    /// issue #826 to close the "is my cache actually warm?" /
    /// "did the invalidate take effect?" observability gap.
    /// </summary>
    /// <remarks>
    /// Counters are **process-lifetime only** — they reset on app restart.
    /// Timestamps are <see cref="DateTimeOffset"/> in UTC.
    /// Per-type aggregate across tenants; tenant-level breakdown is
    /// intentionally out-of-scope (see issue body).
    /// </remarks>
    public class LookupCacheStats
    {
        /// <summary>
        /// Full name of the cached entity type (e.g.
        /// <c>MyApp.Models.CountryCode</c>). Used as the stable
        /// identifier for serialization / dashboard display.
        /// </summary>
        public string EntityTypeName { get; set; } = string.Empty;

        /// <summary>
        /// Count of cache-hit reads (fast-path where the value was
        /// served directly from memory). Incremented by
        /// <c>GetAll</c> / <c>GetAllAsync</c>.
        /// </summary>
        public long Hits { get; set; }

        /// <summary>
        /// Count of cache-miss reads that fell through to the
        /// database load path. Includes both the very first load
        /// and reloads after invalidation / TTL expiry.
        /// </summary>
        public long Misses { get; set; }

        /// <summary>
        /// Count of invalidation calls for this type — whether via
        /// <c>Invalidate&lt;T&gt;</c>, <c>InvalidateType</c>, or
        /// <c>RefreshAsync</c>.
        /// </summary>
        public long InvalidateCount { get; set; }

        /// <summary>
        /// Number of tenant-specific cache keys currently held in
        /// memory for this type. When multi-tenant is disabled this
        /// is at most 1; with tenant isolation each tenant gets its
        /// own key. Drops to 0 immediately after
        /// <c>InvalidateType</c>.
        /// </summary>
        public int CurrentlyCachedTenantKeys { get; set; }

        /// <summary>
        /// UTC timestamp of the most recent <c>GetAll</c> /
        /// <c>GetAllAsync</c> call for this type (hit OR miss).
        /// Null if never accessed since process start.
        /// </summary>
        public DateTimeOffset? LastAccessAt { get; set; }

        /// <summary>
        /// UTC timestamp of the most recent successful warmup / reload
        /// — either the startup warmup service populating the cache
        /// for a <c>WarmOnStartup=true</c> type, or an explicit
        /// <c>RefreshAsync</c> completion. Null if never warmed.
        /// </summary>
        public DateTimeOffset? LastWarmAt { get; set; }

        /// <summary>
        /// UTC timestamp of the most recent invalidation. Null if
        /// never invalidated.
        /// </summary>
        public DateTimeOffset? LastInvalidatedAt { get; set; }

        /// <summary>
        /// TTL configured on the <see cref="CacheLookupAttribute"/>.
        /// </summary>
        public int TtlMinutesConfigured { get; set; }

        /// <summary>
        /// Mirrors <see cref="CacheLookupAttribute.WarmOnStartup"/>.
        /// </summary>
        public bool WarmOnStartup { get; set; }
    }
}
