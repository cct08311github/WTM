#nullable enable
using System;
using System.Threading;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 基於 IMemoryCache 的分析查詢快取實作。
    /// 使用 CancellationTokenSource 實現 InvalidateAll（IMemoryCache 無 Clear 方法）。
    /// </summary>
    /// <remarks>
    /// #676: <see cref="Microsoft.Extensions.Caching.Memory.MemoryCache"/>'s own TTL expiration
    /// is driven by <c>MemoryCacheOptions.Clock</c> (the legacy <c>ISystemClock</c> abstraction,
    /// NOT <see cref="System.TimeProvider"/> — the underlying package does not expose a
    /// TimeProvider seam as of the version pinned in <c>Directory.Packages.props</c>). To make
    /// TTL expiry deterministic under a fake clock without depending on that legacy internal
    /// timer, <see cref="TryGet"/> stamps every entry with its own absolute expiry (computed
    /// from <see cref="_timeProvider"/>) and re-checks it explicitly on every read — this is the
    /// authoritative expiry check. <c>IMemoryCache</c>'s own <c>AbsoluteExpirationRelativeToNow</c>
    /// is still set (using the same TTL) purely as a real-wall-clock backstop so entries are
    /// still reclaimed for memory pressure even if nobody ever calls <see cref="TryGet"/> again;
    /// it is never relied upon for correctness.
    /// </remarks>
    public class MemoryAnalysisCache : IAnalysisCache
    {
        private readonly IMemoryCache _cache;
        private readonly TimeSpan _defaultTtl;
        // #676: defaults to TimeProvider.System — identical behavior to the pre-#676 code, which
        // relied entirely on IMemoryCache's own real-wall-clock expiration.
        private readonly TimeProvider _timeProvider;
        private CancellationTokenSource _cts;
        private readonly Lock _lock = new();

        // Wraps the cached response with the absolute expiry computed at Set() time, so TryGet
        // can authoritatively decide "still fresh?" against _timeProvider instead of trusting
        // IMemoryCache's own (non-TimeProvider-aware) internal expiration timer.
        private sealed class CacheEntry(AnalysisQueryResponse response, DateTimeOffset expiresAt)
        {
            public AnalysisQueryResponse Response { get; } = response;
            public DateTimeOffset ExpiresAt { get; } = expiresAt;
        }

        public MemoryAnalysisCache(IMemoryCache cache, TimeSpan? defaultTtl = null, TimeProvider? timeProvider = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5);
            _timeProvider = timeProvider ?? TimeProvider.System;
            _cts = new CancellationTokenSource();
        }

        public bool TryGet(string queryHash, out AnalysisQueryResponse? cached)
        {
            if (_cache.TryGetValue(queryHash, out CacheEntry? entry) && entry is not null)
            {
                if (_timeProvider.GetUtcNow() < entry.ExpiresAt)
                {
                    cached = entry.Response;
                    return true;
                }

                // Logically expired per _timeProvider even though IMemoryCache's own
                // (real-wall-clock) internal timer may not have purged it yet — e.g. under a
                // FakeTimeProvider in tests, IMemoryCache's own expiration never fires at all
                // since it never observes the advanced fake time. Evict eagerly so a subsequent
                // Set() for the same key doesn't race a stale entry.
                _cache.Remove(queryHash);
            }

            cached = null;
            return false;
        }

        public void Set(string queryHash, AnalysisQueryResponse response, TimeSpan? ttl = null)
        {
            var effectiveTtl = ttl ?? _defaultTtl;
            var entry = new CacheEntry(response, _timeProvider.GetUtcNow() + effectiveTtl);
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = effectiveTtl
            };

            // L2: AddExpirationToken AND _cache.Set must be inside the same lock block.
            // If Set were called outside the lock, InvalidateAll could cancel+replace _cts
            // between the token capture and the cache write, causing the just-stored entry to
            // be immediately evicted by the new CTS token that was never registered on it.
            lock (_lock)
            {
                options.AddExpirationToken(new CancellationChangeToken(_cts.Token));
                _cache.Set(queryHash, entry, options);
            }
        }

        public void Invalidate(string queryHash)
        {
            _cache.Remove(queryHash);
        }

        public void InvalidateAll()
        {
            lock (_lock)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }
        }
    }
}
