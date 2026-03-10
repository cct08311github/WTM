#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace WalkingTec.Mvvm.Core.Cache
{
    /// <summary>
    /// 基於 IMemoryCache 的靜態/參數表快取實作。
    /// <para>
    /// 快取鍵格式：<c>wtm:lookup:{type.FullName}:{tenantId}</c>（tenantId 為空時用 "_"）。
    /// Stampede 防護：per-key SemaphoreSlim(1,1) + double-check，確保 cache miss 時只有一個執行緒查 DB。
    /// InvalidateType 使用 per-type CancellationTokenSource 實現批次清除。
    /// </para>
    /// </summary>
    public class LookupCacheService : ILookupCacheService
    {
        private readonly IMemoryCache _cache;
        private readonly LookupCacheOptions _options;

        // per-type CTS，用於 InvalidateType（IMemoryCache 無 Clear 方法）
        private readonly Dictionary<Type, CancellationTokenSource> _ctsByType = new();
        private readonly object _ctsLock = new();

        // per-key SemaphoreSlim，防 stampede
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();
        private static readonly TimeSpan StampedeTimeout = TimeSpan.FromSeconds(10);

        // 啟動時掃描結果
        private readonly Dictionary<Type, CacheLookupAttribute> _registry;

        public LookupCacheService(IMemoryCache cache, IEnumerable<Assembly> assemblies, LookupCacheOptions? options = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _options = options ?? new LookupCacheOptions();
            _registry = ScanAssemblies(assemblies);
        }

        // ─── ILookupCacheService ──────────────────────────────────────────────

        public IReadOnlyList<T> GetAll<T>(DbContext dc, string? tenantId = null) where T : TopBasePoco
        {
            var key = BuildKey(typeof(T), tenantId);

            // Fast path：快取命中直接回傳
            if (_cache.TryGetValue<IReadOnlyList<T>>(key, out var cached) && cached != null)
                return cached;

            // Slow path：per-key lock 防 stampede
            var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            bool acquired = semaphore.Wait(StampedeTimeout);
            try
            {
                // Double-check：其他執行緒可能已填入
                if (_cache.TryGetValue<IReadOnlyList<T>>(key, out cached) && cached != null)
                    return cached;

                // 此為唯一查 DB 的執行緒（或 timeout fallback）
                var data = LoadFromDb<T>(dc);
                return SetCache<T>(key, data, tenantId);
            }
            finally
            {
                if (acquired) semaphore.Release();
            }
        }

        public async Task<IReadOnlyList<T>> GetAllAsync<T>(
            DbContext dc,
            string? tenantId = null,
            CancellationToken ct = default) where T : TopBasePoco
        {
            var key = BuildKey(typeof(T), tenantId);

            // Fast path
            if (_cache.TryGetValue<IReadOnlyList<T>>(key, out var cached) && cached != null)
                return cached;

            // Slow path：per-key async lock 防 stampede
            var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            bool acquired = await semaphore.WaitAsync(StampedeTimeout, ct).ConfigureAwait(false);
            try
            {
                // Double-check
                if (_cache.TryGetValue<IReadOnlyList<T>>(key, out cached) && cached != null)
                    return cached;

                var data = await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);
                return SetCache<T>(key, data, tenantId);
            }
            finally
            {
                if (acquired) semaphore.Release();
            }
        }

        public void Invalidate<T>(string? tenantId = null) where T : TopBasePoco
        {
            _cache.Remove(BuildKey(typeof(T), tenantId));
        }

        public void InvalidateType(Type entityType)
        {
            // Replace the CTS under lock so new GetAll calls see the new token.
            // Cancel and delay disposal outside the lock: ConfigureEntry reads the old token
            // outside the lock and may call AddExpirationToken after we exit — disposing
            // immediately inside the lock would race with that registration and throw
            // ObjectDisposedException. Leaving the cancelled CTS un-disposed is safe;
            // it holds no significant resources and will be GC'd naturally.
            CancellationTokenSource? old;
            lock (_ctsLock)
            {
                _ctsByType.TryGetValue(entityType, out old);
                _ctsByType[entityType] = new CancellationTokenSource();
            }
            old?.Cancel();
            // Intentionally not calling old.Dispose() — see comment above.
        }

        public bool IsCacheable(Type entityType) => _registry.ContainsKey(entityType);

        public IReadOnlyList<Type> GetWarmupTypes() =>
            _registry
                .Where(kv => kv.Value.WarmOnStartup)
                .Select(kv => kv.Key)
                .ToList();

        public CacheLookupAttribute? GetAttribute(Type entityType) =>
            _registry.TryGetValue(entityType, out var attr) ? attr : null;

        public bool DefaultTenantIsolation => _options.DefaultTenantIsolation;

        public async Task RefreshAsync<T>(DbContext dc, string? tenantId = null, CancellationToken ct = default)
            where T : TopBasePoco
        {
            InvalidateType(typeof(T));
            await GetAllAsync<T>(dc, tenantId, ct).ConfigureAwait(false);
        }

        // ─── 私有輔助 ─────────────────────────────────────────────────────────

        private static string BuildKey(Type type, string? tenantId)
        {
            var tid = string.IsNullOrEmpty(tenantId) ? "_" : tenantId;
            return $"wtm:lookup:{type.FullName}:{tid}";
        }

        /// <summary>將資料寫入快取並設定 TTL + CTS token。</summary>
        private IReadOnlyList<T> SetCache<T>(string key, List<T> data, string? tenantId) where T : TopBasePoco
        {
            using var entry = _cache.CreateEntry(key);
            entry.Value = data;

            if (_registry.TryGetValue(typeof(T), out var attr))
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(attr.TtlMinutes);
            }

            // 掛上 per-type CTS token，使 InvalidateType 可一次清除所有租戶 key
            CancellationToken token;
            lock (_ctsLock)
            {
                if (!_ctsByType.TryGetValue(typeof(T), out var cts))
                {
                    cts = new CancellationTokenSource();
                    _ctsByType[typeof(T)] = cts;
                }
                token = cts.Token;
            }
            entry.AddExpirationToken(new CancellationChangeToken(token));

            return data;
        }

        private static List<T> LoadFromDb<T>(DbContext dc) where T : TopBasePoco =>
            dc.Set<T>().AsNoTracking().ToList();

        private static Task<List<T>> LoadFromDbAsync<T>(DbContext dc, CancellationToken ct)
            where T : TopBasePoco =>
            dc.Set<T>().AsNoTracking().ToListAsync(ct);

        private static Dictionary<Type, CacheLookupAttribute> ScanAssemblies(IEnumerable<Assembly> assemblies)
        {
            var result = new Dictionary<Type, CacheLookupAttribute>();
            foreach (var asm in assemblies)
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
                }

                foreach (var type in types)
                {
                    if (type.IsAbstract || type.IsInterface)
                        continue;
                    var attr = type.GetCustomAttribute<CacheLookupAttribute>();
                    if (attr != null && typeof(TopBasePoco).IsAssignableFrom(type))
                    {
                        result[type] = attr;
                    }
                }
            }
            return result;
        }
    }
}
