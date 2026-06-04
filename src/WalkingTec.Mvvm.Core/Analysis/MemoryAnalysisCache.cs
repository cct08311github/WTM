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
    public class MemoryAnalysisCache : IAnalysisCache
    {
        private readonly IMemoryCache _cache;
        private readonly TimeSpan _defaultTtl;
        private CancellationTokenSource _cts;
        private readonly Lock _lock = new();

        public MemoryAnalysisCache(IMemoryCache cache, TimeSpan? defaultTtl = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5);
            _cts = new CancellationTokenSource();
        }

        public bool TryGet(string queryHash, out AnalysisQueryResponse? cached)
        {
            return _cache.TryGetValue(queryHash, out cached);
        }

        public void Set(string queryHash, AnalysisQueryResponse response, TimeSpan? ttl = null)
        {
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl ?? _defaultTtl
            };

            // L2: AddExpirationToken AND _cache.Set must be inside the same lock block.
            // If Set were called outside the lock, InvalidateAll could cancel+replace _cts
            // between the token capture and the cache write, causing the just-stored entry to
            // be immediately evicted by the new CTS token that was never registered on it.
            lock (_lock)
            {
                options.AddExpirationToken(new CancellationChangeToken(_cts.Token));
                _cache.Set(queryHash, response, options);
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
