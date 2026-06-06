#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<LookupCacheService>? _logger;

        // per-type CTS，用於 InvalidateType（IMemoryCache 無 Clear 方法）
        private readonly Dictionary<Type, CancellationTokenSource> _ctsByType = new();
        private readonly Lock _ctsLock = new();

        // per-key SemaphoreSlim，防 stampede
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();
        private static readonly TimeSpan StampedeTimeout = TimeSpan.FromSeconds(10);

        // 啟動時掃描結果（Build 後不再修改，FrozenDictionary 優化讀取路徑）
        private readonly FrozenDictionary<Type, CacheLookupAttribute> _registry;

        // Bug #112 (1): 實作 ITenant 的型別集合，必須強制 tenant 隔離，
        // 無論 [CacheLookup(TenantIsolation = false)] 如何設定。
        // 原因：EF Core global query filter 會依 TenantCode 過濾資料，
        // 若以 global key 快取，Tenant A 的過濾結果將洩漏給所有租戶。
        private readonly FrozenSet<Type> _forcedTenantIsolationTypes;

        // Issue #826: per-type stats tracker (thread-safe counters + timestamps
        // + live tenant-key set). Lazy-populated the first time a type is
        // touched via any stats-affecting path.
        private readonly ConcurrentDictionary<Type, TypeStatsTracker> _stats = new();

        public LookupCacheService(IMemoryCache cache, IEnumerable<Assembly> assemblies, LookupCacheOptions? options = null)
            : this(cache, assemblies, options, logger: null) { }

        public LookupCacheService(
            IMemoryCache cache,
            IEnumerable<Assembly> assemblies,
            LookupCacheOptions? options,
            ILogger<LookupCacheService>? logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _options = options ?? new LookupCacheOptions();
            _logger = logger;
            _registry = ScanAssemblies(assemblies);

            // Bug #112 (1): build the forced-isolation set once at startup.
            // Any type that implements ITenant MUST use tenant isolation even if
            // [CacheLookup(TenantIsolation = false)] is present, because the EF
            // Core global query filter scopes the DB query by TenantCode — caching
            // the filtered result under a global key would serve Tenant A's rows
            // to all other tenants for the full TTL.
            var forced = new HashSet<Type>();
            foreach (var t in _registry.Keys)
            {
                if (typeof(ITenant).IsAssignableFrom(t))
                {
                    forced.Add(t);
                    var attr = _registry[t];
                    // Warn only when the developer explicitly opted out of isolation.
                    if (attr.TenantIsolationOrNull == false)
                    {
                        _logger?.LogWarning(
                            "[WTM] LookupCache: {TypeName} implements ITenant but has " +
                            "[CacheLookup(TenantIsolation = false)]. " +
                            "TenantIsolation=false is ignored for ITenant types to prevent " +
                            "cross-tenant data leaks. Tenant isolation will be enforced.",
                            t.FullName ?? t.Name);
                    }
                }
            }
            _forcedTenantIsolationTypes = forced.ToFrozenSet();
        }

        // ─── Issue #826: internal stats tracker ──────────────────────────────

        private sealed class TypeStatsTracker
        {
            public long Hits;
            public long Misses;
            public long InvalidateCount;
            public long LastAccessAtTicks;       // 0 = never
            public long LastWarmAtTicks;         // 0 = never
            public long LastInvalidatedAtTicks;  // 0 = never

            // Tenant keys currently in memory. Guarded by lock because
            // set/remove happens in small bursts, never hot-loop.
            public readonly HashSet<string> TenantKeys = new();
            public readonly Lock TenantKeysLock = new();
        }

        private TypeStatsTracker GetOrCreateTracker(Type t) =>
            _stats.GetOrAdd(t, _ => new TypeStatsTracker());

        private void RecordHit(Type t)
        {
            var tracker = GetOrCreateTracker(t);
            Interlocked.Increment(ref tracker.Hits);
            Interlocked.Exchange(ref tracker.LastAccessAtTicks, DateTimeOffset.UtcNow.Ticks);
        }

        private void RecordMiss(Type t)
        {
            var tracker = GetOrCreateTracker(t);
            Interlocked.Increment(ref tracker.Misses);
            Interlocked.Exchange(ref tracker.LastAccessAtTicks, DateTimeOffset.UtcNow.Ticks);
        }

        private void RecordWarm(Type t, string key)
        {
            var tracker = GetOrCreateTracker(t);
            Interlocked.Exchange(ref tracker.LastWarmAtTicks, DateTimeOffset.UtcNow.Ticks);
            lock (tracker.TenantKeysLock)
            {
                tracker.TenantKeys.Add(key);
            }
        }

        private void RecordInvalidate(Type t, string? tenantSpecificKey)
        {
            var tracker = GetOrCreateTracker(t);
            Interlocked.Increment(ref tracker.InvalidateCount);
            Interlocked.Exchange(ref tracker.LastInvalidatedAtTicks, DateTimeOffset.UtcNow.Ticks);
            lock (tracker.TenantKeysLock)
            {
                if (tenantSpecificKey != null)
                {
                    tracker.TenantKeys.Remove(tenantSpecificKey);
                }
                else
                {
                    tracker.TenantKeys.Clear();
                }
            }
        }

        // ─── ILookupCacheService ──────────────────────────────────────────────

        public IReadOnlyList<T> GetAll<T>(DbContext dc, string? tenantId = null) where T : TopBasePoco
        {
            // Bug #112 (2): non-[CacheLookup] types must never be stored in the
            // cache — without a TTL they would be immortal, and SaveChanges
            // invalidation skips them (IsCacheable=false). Query directly and return.
            if (!_registry.ContainsKey(typeof(T)))
            {
                return LoadFromDb<T>(dc);
            }

            // Bug #112 (1): ITenant types MUST use per-tenant cache keys.
            // If the caller passed tenantId=null for an ITenant type (e.g. warmup
            // service), we cannot safely cache: the DC's global query filter may
            // scope to a specific tenant or return all-tenant rows depending on
            // context — caching either under the global key risks a cross-tenant
            // data leak. Skip caching and load directly from DB.
            //
            // Bug #168: only apply this bypass when tenant isolation is actually
            // enabled (DefaultTenantIsolation == true). In single-tenant deployments
            // (DefaultTenantIsolation == false) there is no cross-tenant boundary, so
            // it is safe to cache ITenant types under the shared (null-tenant) global
            // key "wtm:lookup:{Type}:_" instead of querying the DB on every call.
            if (_forcedTenantIsolationTypes.Contains(typeof(T)) && tenantId == null && DefaultTenantIsolation)
            {
                return LoadFromDb<T>(dc);
            }

            var key = BuildKey(typeof(T), tenantId);

            // Fast path：快取命中直接回傳
            if (_cache.TryGetValue<IReadOnlyList<T>>(key, out var cached) && cached != null)
            {
                RecordHit(typeof(T));
                return cached;
            }

            // Slow path：per-key lock 防 stampede
            var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            bool acquired = semaphore.Wait(StampedeTimeout);
            try
            {
                // Double-check：其他執行緒可能已填入
                if (_cache.TryGetValue<IReadOnlyList<T>>(key, out cached) && cached != null)
                {
                    RecordHit(typeof(T));
                    return cached;
                }

                // Bug #112 (4) / M10 fix: semaphore timeout handling.
                // If we timed out, re-check the cache — the holder may have already
                // filled it while we were waiting. If still absent, fall through to
                // a DB load as a safe fallback so the caller is never blocked forever.
                // IMPORTANT: timed-out callers must NOT call SetCache because they do
                // NOT hold the lock. Writing to the cache without the semaphore would
                // race with the legitimate lock holder's SetCache, potentially
                // overwriting a fresher value with a stale one and defeating the
                // stampede-protection invariant. Return the raw DB result directly.
                if (!acquired)
                {
                    if (_cache.TryGetValue<IReadOnlyList<T>>(key, out cached) && cached != null)
                    {
                        RecordHit(typeof(T));
                        return cached;
                    }
                    // Still absent: load from DB but skip SetCache (no lock held).
                    // Log once so operators can tune StampedeTimeout if this is frequent.
                    _logger?.LogWarning(
                        "[WTM] LookupCache stampede-protection bypassed for key '{Key}': " +
                        "semaphore timed out after {TimeoutMs}ms. " +
                        "DB query result returned to caller without caching. " +
                        "Consider increasing StampedeTimeout if this occurs frequently.",
                        key, (int)StampedeTimeout.TotalMilliseconds);
                    RecordMiss(typeof(T));
                    return LoadFromDb<T>(dc);
                    // NOTE: semaphore was NOT acquired, so we must NOT Release it below.
                }

                // 此為唯一查 DB 的執行緒（持有 semaphore）
                RecordMiss(typeof(T));
                var data = LoadFromDb<T>(dc);
                return SetCache<T>(key, data, tenantId);
            }
            finally
            {
                // Bug #112 (4): only release if we actually acquired the semaphore.
                if (acquired) semaphore.Release();
            }
        }

        public async Task<IReadOnlyList<T>> GetAllAsync<T>(
            DbContext dc,
            string? tenantId = null,
            CancellationToken ct = default) where T : TopBasePoco
        {
            // Bug #112 (2): non-[CacheLookup] types must never be stored in cache.
            if (!_registry.ContainsKey(typeof(T)))
            {
                return await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);
            }

            // Bug #112 (1): ITenant types with tenantId=null — skip cache (same
            // rationale as sync path above).
            //
            // Bug #168: only apply this bypass when tenant isolation is actually
            // enabled (DefaultTenantIsolation == true). In single-tenant deployments
            // (DefaultTenantIsolation == false) there is no cross-tenant boundary, so
            // it is safe to cache ITenant types under the shared (null-tenant) global
            // key "wtm:lookup:{Type}:_" instead of querying the DB on every call.
            if (_forcedTenantIsolationTypes.Contains(typeof(T)) && tenantId == null && DefaultTenantIsolation)
            {
                return await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);
            }

            var key = BuildKey(typeof(T), tenantId);

            // Fast path
            if (_cache.TryGetValue<IReadOnlyList<T>>(key, out var cached) && cached != null)
            {
                RecordHit(typeof(T));
                return cached;
            }

            // Slow path：per-key async lock 防 stampede
            var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            bool acquired = await semaphore.WaitAsync(StampedeTimeout, ct).ConfigureAwait(false);
            try
            {
                // Double-check
                if (_cache.TryGetValue<IReadOnlyList<T>>(key, out cached) && cached != null)
                {
                    RecordHit(typeof(T));
                    return cached;
                }

                // Bug #112 (4) / M10 fix: semaphore timeout handling (async path).
                // Mirror the sync path: timed-out callers must not call SetCache
                // because they do not hold the lock. Return the DB result directly.
                if (!acquired)
                {
                    if (_cache.TryGetValue<IReadOnlyList<T>>(key, out cached) && cached != null)
                    {
                        RecordHit(typeof(T));
                        return cached;
                    }
                    // Still absent: load from DB but skip SetCache (no lock held).
                    _logger?.LogWarning(
                        "[WTM] LookupCache stampede-protection bypassed for key '{Key}' (async): " +
                        "semaphore timed out after {TimeoutMs}ms. " +
                        "DB query result returned to caller without caching. " +
                        "Consider increasing StampedeTimeout if this occurs frequently.",
                        key, (int)StampedeTimeout.TotalMilliseconds);
                    RecordMiss(typeof(T));
                    return await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);
                    // NOTE: semaphore was NOT acquired, so we must NOT Release it below.
                }

                RecordMiss(typeof(T));
                var data = await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);
                return SetCache<T>(key, data, tenantId);
            }
            finally
            {
                // Bug #112 (4): only release if actually acquired.
                if (acquired) semaphore.Release();
            }
        }

        public void Invalidate<T>(string? tenantId = null) where T : TopBasePoco
        {
            var key = BuildKey(typeof(T), tenantId);
            _cache.Remove(key);
            RecordInvalidate(typeof(T), key);
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

            // Issue #826: stats — all tenant keys for this type dropped.
            RecordInvalidate(entityType, tenantSpecificKey: null);
        }

        public bool IsCacheable(Type entityType) => _registry.ContainsKey(entityType);

        public IReadOnlyList<Type> GetWarmupTypes() =>
            [.. _registry
                .Where(kv => kv.Value.WarmOnStartup)
                .Select(kv => kv.Key)];

        public CacheLookupAttribute? GetAttribute(Type entityType) =>
            _registry.TryGetValue(entityType, out var attr) ? attr : null;

        public bool DefaultTenantIsolation => _options.DefaultTenantIsolation;

        /// <summary>
        /// 判斷型別是否強制 tenant 隔離。
        /// <para>
        /// Bug #112 (1): ITenant 型別無論 [CacheLookup(TenantIsolation=false)] 設定如何，
        /// 一律強制使用 tenant 隔離，避免 EF Core global query filter 過濾後的結果
        /// 被快取在 global key 下洩漏給其他租戶。
        /// </para>
        /// </summary>
        public bool IsEffectivelyTenantIsolated(Type entityType) =>
            _forcedTenantIsolationTypes.Contains(entityType);

        public async Task RefreshAsync<T>(DbContext dc, string? tenantId = null, CancellationToken ct = default)
            where T : TopBasePoco
        {
            var key = BuildKey(typeof(T), tenantId);
            var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            bool acquired = await semaphore.WaitAsync(StampedeTimeout, ct).ConfigureAwait(false);
            try
            {
                // Bug #112 (3): was InvalidateType(typeof(T)) which cancels the
                // shared CTS → evicts ALL tenants' entries, causing a cross-tenant
                // stampede. Replace with single-tenant Invalidate<T>(tenantId) which
                // only removes the one cache key for this tenant.
                Invalidate<T>(tenantId);
                var data = await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);
                SetCache<T>(key, data, tenantId);
            }
            finally
            {
                if (acquired) semaphore.Release();
            }
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

            // Bug #112 (5): check for already-cancelled token before registering.
            // A concurrent InvalidateType call may have cancelled the CTS between
            // when we read the token and when we call AddExpirationToken. If the
            // token is already cancelled the CancellationChangeToken fires
            // immediately, evicting the entry we are still building and driving
            // cache-hit rate to zero under high-frequency invalidation.
            // Skip the CTS-based expiration when already cancelled; the
            // AbsoluteExpirationRelativeToNow TTL above is sufficient for
            // correctness — the CTS registration is only an eager-invalidation
            // optimisation.
            if (!token.IsCancellationRequested)
            {
                entry.AddExpirationToken(new CancellationChangeToken(token));
            }

            // Issue #826: record warm + add to tenant-key set.
            RecordWarm(typeof(T), key);

            return data;
        }

        // Issue #826: GetStats implementations.
        public LookupCacheStats? GetStats(Type entityType)
        {
            if (!_registry.TryGetValue(entityType, out var attr)) { return null; }

            _stats.TryGetValue(entityType, out var tracker);
            return BuildStats(entityType, attr, tracker);
        }

        public IReadOnlyList<LookupCacheStats> GetStats()
        {
            var results = new List<LookupCacheStats>(_registry.Count);
            foreach (var kv in _registry.OrderBy(x => x.Key.FullName, StringComparer.Ordinal))
            {
                _stats.TryGetValue(kv.Key, out var tracker);
                results.Add(BuildStats(kv.Key, kv.Value, tracker));
            }
            return results;
        }

        private static LookupCacheStats BuildStats(Type t, CacheLookupAttribute attr, TypeStatsTracker? tracker)
        {
            static DateTimeOffset? FromTicks(long ticks) =>
                ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);

            if (tracker == null)
            {
                return new LookupCacheStats
                {
                    EntityTypeName = t.FullName ?? t.Name,
                    TtlMinutesConfigured = attr.TtlMinutes,
                    WarmOnStartup = attr.WarmOnStartup,
                };
            }

            int currentKeys;
            lock (tracker.TenantKeysLock) { currentKeys = tracker.TenantKeys.Count; }

            return new LookupCacheStats
            {
                EntityTypeName = t.FullName ?? t.Name,
                Hits = Interlocked.Read(ref tracker.Hits),
                Misses = Interlocked.Read(ref tracker.Misses),
                InvalidateCount = Interlocked.Read(ref tracker.InvalidateCount),
                CurrentlyCachedTenantKeys = currentKeys,
                LastAccessAt = FromTicks(Interlocked.Read(ref tracker.LastAccessAtTicks)),
                LastWarmAt = FromTicks(Interlocked.Read(ref tracker.LastWarmAtTicks)),
                LastInvalidatedAt = FromTicks(Interlocked.Read(ref tracker.LastInvalidatedAtTicks)),
                TtlMinutesConfigured = attr.TtlMinutes,
                WarmOnStartup = attr.WarmOnStartup,
            };
        }

        private static List<T> LoadFromDb<T>(DbContext dc) where T : TopBasePoco =>
            [.. dc.Set<T>().AsNoTracking()];

        private static Task<List<T>> LoadFromDbAsync<T>(DbContext dc, CancellationToken ct)
            where T : TopBasePoco =>
            dc.Set<T>().AsNoTracking().ToListAsync(ct);

        private static FrozenDictionary<Type, CacheLookupAttribute> ScanAssemblies(IEnumerable<Assembly> assemblies)
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
                    types = ex.Types == null ? [] : [.. ex.Types.Where(t => t != null).Cast<Type>()];
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
            return result.ToFrozenDictionary();
        }
    }
}
