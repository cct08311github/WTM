#nullable enable
using System;
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
    /// InvalidateType 使用 per-type CancellationTokenSource 實現批次清除。
    /// </para>
    /// </summary>
    public class LookupCacheService : ILookupCacheService
    {
        private readonly IMemoryCache _cache;

        // per-type CTS，用於 InvalidateType（IMemoryCache 無 Clear 方法）
        private readonly Dictionary<Type, CancellationTokenSource> _ctsByType = new();
        private readonly object _ctsLock = new();

        // 啟動時掃描結果
        private readonly Dictionary<Type, CacheLookupAttribute> _registry;

        public LookupCacheService(IMemoryCache cache, IEnumerable<Assembly> assemblies)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _registry = ScanAssemblies(assemblies);
        }

        // ─── ILookupCacheService ──────────────────────────────────────────────

        public List<T> GetAll<T>(DbContext dc, string? tenantId = null) where T : TopBasePoco
        {
            var key = BuildKey(typeof(T), tenantId);
            return _cache.GetOrCreate(key, entry =>
            {
                ConfigureEntry<T>(entry, tenantId);
                return LoadFromDb<T>(dc);
            }) ?? new List<T>();
        }

        public async Task<List<T>> GetAllAsync<T>(
            DbContext dc,
            string? tenantId = null,
            CancellationToken ct = default) where T : TopBasePoco
        {
            var key = BuildKey(typeof(T), tenantId);

            // Note: GetOrCreateAsync is not atomic under concurrent misses — concurrent requests
            // may each execute the factory once with their own scoped DbContext.
            // The cost is at most one extra DB read per burst; data safety is preserved
            // because each caller supplies its own DbContext instance.
            return await _cache.GetOrCreateAsync(key, async entry =>
            {
                ConfigureEntry<T>(entry, tenantId);
                return await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);
            }).ConfigureAwait(false) ?? new List<T>();
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

        // ─── 私有輔助 ─────────────────────────────────────────────────────────

        private static string BuildKey(Type type, string? tenantId)
        {
            var tid = string.IsNullOrEmpty(tenantId) ? "_" : tenantId;
            return $"wtm:lookup:{type.FullName}:{tid}";
        }

        private void ConfigureEntry<T>(ICacheEntry entry, string? tenantId)
        {
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
