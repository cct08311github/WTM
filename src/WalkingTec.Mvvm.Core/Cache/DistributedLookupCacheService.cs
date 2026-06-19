#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Cache
{
    /// <summary>
    /// 基於 <see cref="IDistributedCache"/> 的 Lookup Cache 實作（opt-in）。
    /// <para>
    /// 適用場景：需要跨程序節點共用靜態/參數表快取（例如 Redis 叢集）的多節點部署。
    /// 單節點部署請繼續使用預設的 <see cref="LookupCacheService"/>（IMemoryCache）。
    /// </para>
    /// <para>
    /// 啟用方式：在 Program.cs / Startup.cs 中 <c>AddWtmContext()</c> 之後呼叫
    /// <c>services.AddWtmDistributedLookupCache()</c>，
    /// 並確保已注冊 <see cref="IDistributedCache"/> 實作（如 <c>services.AddStackExchangeRedisCache(...)</c>）。
    /// 未呼叫此方法時，行為與原先完全相同（仍使用 in-memory backend）。
    /// </para>
    /// <para>
    /// 快取鍵格式：<c>wtm:lookup:{type.FullName}:{tenantId}</c>（tenantId 為空時用 "_"），
    /// 與 <see cref="LookupCacheService"/> 鍵格式一致（方便未來遷移）。
    /// </para>
    /// <para>
    /// Stampede 防護：per-key <see cref="SemaphoreSlim"/>(1,1) + double-check，
    /// 確保 cache miss 時只有一個執行緒查 DB（process-local 層級保護；
    /// 跨節點需搭配 Redis distributed lock，超出本實作範圍）。
    /// </para>
    /// <para>
    /// 失效語意：
    /// <list type="bullet">
    ///   <item><see cref="Invalidate{T}"/> — 刪除單一 tenant key。</item>
    ///   <item>
    ///     <see cref="InvalidateType"/> — 無法透過 <see cref="IDistributedCache"/> 按前綴批次刪除，
    ///     改由 "invalidation sentinel" 策略實作：在 <c>wtm:lookup:inv:{type.FullName}</c>
    ///     存入當前 UTC 版本戳。讀取時比對，若快取條目的戳比 sentinel 舊則視為失效。
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// 序列化：<see cref="System.Text.Json.JsonSerializer"/>，使用共用
    /// <see cref="JsonSerializerOptions"/>（啟用 <c>IncludeFields = false</c>，
    /// <c>PropertyNamingPolicy = null</c>；只序列化 public property）。
    /// </para>
    /// <para>
    /// 統計（Hits / Misses / Invalidates / Timestamps）為 process-local，
    /// 重啟歸零，與 <see cref="LookupCacheService"/> 行為一致。
    /// </para>
    /// </summary>
    public sealed class DistributedLookupCacheService : ILookupCacheService
    {
        private readonly IDistributedCache _cache;
        private readonly LookupCacheOptions _options;
        private readonly ILogger<DistributedLookupCacheService>? _logger;

        // per-key SemaphoreSlim，防 process-local stampede
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();
        private static readonly TimeSpan StampedeTimeout = TimeSpan.FromSeconds(10);

        // 啟動時掃描結果（Build 後不再修改，FrozenDictionary 優化讀取路徑）
        private readonly FrozenDictionary<Type, CacheLookupAttribute> _registry;

        // ITenant 型別強制 tenant 隔離（與 LookupCacheService 相同邏輯）
        private readonly FrozenSet<Type> _forcedTenantIsolationTypes;

        // Sentinel 版本戳快取（process-local 副本；per-type lock 保護）
        // key = type, value = UTC ticks of last InvalidateType call observed by this process
        private readonly ConcurrentDictionary<Type, long> _invalidationSentinels = new();

        // Process-local stats
        private readonly ConcurrentDictionary<Type, TypeStatsTracker> _stats = new();

        // Shared serializer options — allocated once per service instance.
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
        };

        public DistributedLookupCacheService(
            IDistributedCache cache,
            IEnumerable<Assembly> assemblies,
            LookupCacheOptions? options = null,
            ILogger<DistributedLookupCacheService>? logger = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _options = options ?? new LookupCacheOptions();
            _logger = logger;
            _registry = ScanAssemblies(assemblies);

            // Build forced-tenant-isolation set (mirrors LookupCacheService logic)
            var forced = new HashSet<Type>();
            foreach (var t in _registry.Keys)
            {
                if (typeof(ITenant).IsAssignableFrom(t))
                {
                    forced.Add(t);
                    var attr = _registry[t];
                    if (attr.TenantIsolationOrNull == false)
                    {
                        _logger?.LogWarning(
                            "[WTM] DistributedLookupCache: {TypeName} implements ITenant but has " +
                            "[CacheLookup(TenantIsolation = false)]. " +
                            "TenantIsolation=false is ignored for ITenant types to prevent " +
                            "cross-tenant data leaks. Tenant isolation will be enforced.",
                            t.FullName ?? t.Name);
                    }
                }
            }
            _forcedTenantIsolationTypes = forced.ToFrozenSet();
        }

        // ─── Internal stats tracker ─────────────────────────────────────────────

        private sealed class TypeStatsTracker
        {
            public long Hits;
            public long Misses;
            public long InvalidateCount;
            public long LastAccessAtTicks;
            public long LastWarmAtTicks;
            public long LastInvalidatedAtTicks;
        }

        private TypeStatsTracker GetOrCreateTracker(Type t) =>
            _stats.GetOrAdd(t, _ => new TypeStatsTracker());

        private void RecordHit(Type t)
        {
            var tk = GetOrCreateTracker(t);
            Interlocked.Increment(ref tk.Hits);
            Interlocked.Exchange(ref tk.LastAccessAtTicks, DateTimeOffset.UtcNow.Ticks);
        }

        private void RecordMiss(Type t)
        {
            var tk = GetOrCreateTracker(t);
            Interlocked.Increment(ref tk.Misses);
            Interlocked.Exchange(ref tk.LastAccessAtTicks, DateTimeOffset.UtcNow.Ticks);
        }

        private void RecordWarm(Type t)
        {
            var tk = GetOrCreateTracker(t);
            Interlocked.Exchange(ref tk.LastWarmAtTicks, DateTimeOffset.UtcNow.Ticks);
        }

        private void RecordInvalidate(Type t)
        {
            var tk = GetOrCreateTracker(t);
            Interlocked.Increment(ref tk.InvalidateCount);
            Interlocked.Exchange(ref tk.LastInvalidatedAtTicks, DateTimeOffset.UtcNow.Ticks);
        }

        // ─── ILookupCacheService ─────────────────────────────────────────────────

        public bool DefaultTenantIsolation => _options.DefaultTenantIsolation;

        public bool IsCacheable(Type entityType) => _registry.ContainsKey(entityType);

        public IReadOnlyList<Type> GetWarmupTypes() =>
            [.. _registry
                .Where(kv => kv.Value.WarmOnStartup)
                .Select(kv => kv.Key)];

        public CacheLookupAttribute? GetAttribute(Type entityType) =>
            _registry.TryGetValue(entityType, out var attr) ? attr : null;

        public IReadOnlyList<T> GetAll<T>(DbContext dc, string? tenantId = null) where T : TopBasePoco
        {
            if (!_registry.ContainsKey(typeof(T)))
                return LoadFromDb<T>(dc);

            if (_forcedTenantIsolationTypes.Contains(typeof(T)) && tenantId == null && DefaultTenantIsolation)
                return LoadFromDb<T>(dc);

            var key = BuildKey(typeof(T), tenantId);

            // Fast path: read distributed cache WITHOUT acquiring lock.
            // This is the common case (cache hit) and avoids unnecessary locking.
            var cached = TryGetFromDistributed<T>(key, typeof(T));
            if (cached != null)
            {
                RecordHit(typeof(T));
                return cached;
            }

            // Slow path: acquire per-key semaphore to prevent process-local stampede.
            var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            bool acquired = semaphore.Wait(StampedeTimeout);
            try
            {
                // Double-check: another thread may have populated the cache while we waited.
                cached = TryGetFromDistributed<T>(key, typeof(T));
                if (cached != null)
                {
                    RecordHit(typeof(T));
                    return cached;
                }

                if (!acquired)
                {
                    _logger?.LogWarning(
                        "[WTM] DistributedLookupCache stampede-protection bypassed for key '{Key}': " +
                        "semaphore timed out after {TimeoutMs}ms. DB result returned without caching.",
                        key, (int)StampedeTimeout.TotalMilliseconds);
                    RecordMiss(typeof(T));
                    return LoadFromDb<T>(dc);
                }

                RecordMiss(typeof(T));
                var data = LoadFromDb<T>(dc);
                SetDistributed(key, data, typeof(T));
                RecordWarm(typeof(T));
                return data;
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
            if (!_registry.ContainsKey(typeof(T)))
                return await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);

            if (_forcedTenantIsolationTypes.Contains(typeof(T)) && tenantId == null && DefaultTenantIsolation)
                return await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);

            var key = BuildKey(typeof(T), tenantId);

            // Fast path: read distributed cache WITHOUT acquiring lock.
            // This is the common case (cache hit) and avoids unnecessary locking.
            var cached = await TryGetFromDistributedAsync<T>(key, typeof(T), ct).ConfigureAwait(false);
            if (cached != null)
            {
                RecordHit(typeof(T));
                return cached;
            }

            // Slow path: acquire per-key semaphore to prevent process-local stampede.
            var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            bool acquired = await semaphore.WaitAsync(StampedeTimeout, ct).ConfigureAwait(false);
            try
            {
                // Double-check: another thread may have populated the cache while we waited.
                cached = await TryGetFromDistributedAsync<T>(key, typeof(T), ct).ConfigureAwait(false);
                if (cached != null)
                {
                    RecordHit(typeof(T));
                    return cached;
                }

                if (!acquired)
                {
                    _logger?.LogWarning(
                        "[WTM] DistributedLookupCache stampede-protection bypassed for key '{Key}' (async): " +
                        "semaphore timed out after {TimeoutMs}ms. DB result returned without caching.",
                        key, (int)StampedeTimeout.TotalMilliseconds);
                    RecordMiss(typeof(T));
                    return await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);
                }

                RecordMiss(typeof(T));
                var data = await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);
                await SetDistributedAsync(key, data, typeof(T), ct).ConfigureAwait(false);
                RecordWarm(typeof(T));
                return data;
            }
            finally
            {
                if (acquired) semaphore.Release();
            }
        }

        public void Invalidate<T>(string? tenantId = null) where T : TopBasePoco
        {
            var key = BuildKey(typeof(T), tenantId);
            try
            {
                _cache.Remove(key);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[WTM] DistributedLookupCache: failed to remove key '{Key}' from distributed cache.", key);
            }
            RecordInvalidate(typeof(T));
        }

        /// <summary>
        /// 使指定型別的所有租戶快取失效。
        /// <para>
        /// <see cref="IDistributedCache"/> 不支援按前綴批次刪除，改用 "invalidation sentinel" 策略：
        /// 在 <c>wtm:lookup:inv:{type.FullName}</c> 寫入當前 UTC ticks。
        /// 後續 <c>GetAll</c> 讀取時，若快取條目版本戳比 sentinel 舊，視同失效並重新查 DB。
        /// </para>
        /// </summary>
        public void InvalidateType(Type entityType)
        {
            var sentinelKey = BuildSentinelKey(entityType);
            var nowTicks = DateTimeOffset.UtcNow.Ticks;
            var ticksBytes = System.Text.Encoding.UTF8.GetBytes(nowTicks.ToString());

            // Write sentinel to distributed cache with TTL = entry TTL × 2 + 1 minute
            // (fallback to 1 day when type is not registered).
            var sentinelTtl = _registry.TryGetValue(entityType, out var attr)
                ? TimeSpan.FromMinutes(attr.TtlMinutes * 2 + 1)
                : TimeSpan.FromDays(1);

            var sentinelOpts = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = sentinelTtl
            };

            try
            {
                _cache.Set(sentinelKey, ticksBytes, sentinelOpts);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[WTM] DistributedLookupCache: failed to write invalidation sentinel for type '{TypeName}'.",
                    entityType.FullName ?? entityType.Name);
            }

            // Update process-local sentinel so this node knows immediately
            _invalidationSentinels[entityType] = nowTicks;
            RecordInvalidate(entityType);
        }

        public async Task RefreshAsync<T>(DbContext dc, string? tenantId = null, CancellationToken ct = default)
            where T : TopBasePoco
        {
            var key = BuildKey(typeof(T), tenantId);
            var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            bool acquired = await semaphore.WaitAsync(StampedeTimeout, ct).ConfigureAwait(false);
            try
            {
                Invalidate<T>(tenantId);
                var data = await LoadFromDbAsync<T>(dc, ct).ConfigureAwait(false);
                await SetDistributedAsync(key, data, typeof(T), ct).ConfigureAwait(false);
                RecordWarm(typeof(T));
            }
            finally
            {
                if (acquired) semaphore.Release();
            }
        }

        // ─── Stats ──────────────────────────────────────────────────────────────

        public LookupCacheStats? GetStats(Type entityType)
        {
            if (!_registry.TryGetValue(entityType, out var attr)) return null;
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

            return new LookupCacheStats
            {
                EntityTypeName = t.FullName ?? t.Name,
                Hits = Interlocked.Read(ref tracker.Hits),
                Misses = Interlocked.Read(ref tracker.Misses),
                InvalidateCount = Interlocked.Read(ref tracker.InvalidateCount),
                // Distributed backend: we don't track per-tenant key set locally
                CurrentlyCachedTenantKeys = 0,
                LastAccessAt = FromTicks(Interlocked.Read(ref tracker.LastAccessAtTicks)),
                LastWarmAt = FromTicks(Interlocked.Read(ref tracker.LastWarmAtTicks)),
                LastInvalidatedAt = FromTicks(Interlocked.Read(ref tracker.LastInvalidatedAtTicks)),
                TtlMinutesConfigured = attr.TtlMinutes,
                WarmOnStartup = attr.WarmOnStartup,
            };
        }

        // ─── Serialization envelope ──────────────────────────────────────────────

        /// <summary>
        /// Wire envelope stored in IDistributedCache: includes the entity list and a version
        /// timestamp so InvalidateType can validate staleness on read.
        /// </summary>
        private sealed class CacheEnvelope<T>
        {
            public List<T> Items { get; set; } = [];
            /// <summary>UTC ticks when this entry was written.</summary>
            public long WrittenAtTicks { get; set; }
        }

        // ─── Distributed cache read/write ────────────────────────────────────────

        private List<T>? TryGetFromDistributed<T>(string key, Type entityType) where T : TopBasePoco
        {
            try
            {
                var bytes = _cache.Get(key);
                if (bytes == null || bytes.Length == 0) return null;
                var envelope = JsonSerializer.Deserialize<CacheEnvelope<T>>(bytes, SerializerOptions);
                if (envelope == null) return null;
                if (IsInvalidatedBySentinel(entityType, envelope.WrittenAtTicks)) return null;
                return envelope.Items;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[WTM] DistributedLookupCache: failed to read/deserialize key '{Key}'. Cache miss.", key);
                return null;
            }
        }

        private async Task<List<T>?> TryGetFromDistributedAsync<T>(
            string key, Type entityType, CancellationToken ct) where T : TopBasePoco
        {
            try
            {
                var bytes = await _cache.GetAsync(key, ct).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0) return null;
                var envelope = JsonSerializer.Deserialize<CacheEnvelope<T>>(bytes, SerializerOptions);
                if (envelope == null) return null;
                if (await IsInvalidatedBySentinelAsync(entityType, envelope.WrittenAtTicks, ct).ConfigureAwait(false))
                    return null;
                return envelope.Items;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[WTM] DistributedLookupCache: failed to read/deserialize key '{Key}' (async). Cache miss.", key);
                return null;
            }
        }

        private void SetDistributed<T>(string key, List<T> data, Type entityType)
        {
            var attr = _registry.TryGetValue(entityType, out var a) ? a : null;
            var opts = BuildCacheEntryOptions(attr);
            var envelope = new CacheEnvelope<T>
            {
                Items = data,
                WrittenAtTicks = DateTimeOffset.UtcNow.Ticks
            };
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
                _cache.Set(key, bytes, opts);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[WTM] DistributedLookupCache: failed to write key '{Key}'.", key);
            }
        }

        private async Task SetDistributedAsync<T>(
            string key, List<T> data, Type entityType, CancellationToken ct)
        {
            var attr = _registry.TryGetValue(entityType, out var a) ? a : null;
            var opts = BuildCacheEntryOptions(attr);
            var envelope = new CacheEnvelope<T>
            {
                Items = data,
                WrittenAtTicks = DateTimeOffset.UtcNow.Ticks
            };
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
                await _cache.SetAsync(key, bytes, opts, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[WTM] DistributedLookupCache: failed to write key '{Key}' (async).", key);
            }
        }

        // ─── Sentinel validation ─────────────────────────────────────────────────

        /// <summary>
        /// Returns true if a distributed or process-local InvalidateType sentinel is newer than
        /// the entry's write timestamp, meaning the entry should be treated as stale.
        /// </summary>
        private bool IsInvalidatedBySentinel(Type entityType, long entryWrittenAtTicks)
        {
            // Check process-local sentinel first (cheapest)
            if (_invalidationSentinels.TryGetValue(entityType, out var localSentinel)
                && localSentinel > entryWrittenAtTicks)
            {
                return true;
            }

            // Check distributed sentinel
            var sentinelKey = BuildSentinelKey(entityType);
            try
            {
                var bytes = _cache.Get(sentinelKey);
                if (bytes == null || bytes.Length == 0) return false;
                if (long.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var distributedSentinel))
                {
                    if (distributedSentinel > entryWrittenAtTicks)
                    {
                        // Update process-local cache to avoid future distributed reads
                        _invalidationSentinels[entityType] = distributedSentinel;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[WTM] DistributedLookupCache: failed to read sentinel for type '{TypeName}'. Treating entry as valid.",
                    entityType.FullName ?? entityType.Name);
            }

            return false;
        }

        private async Task<bool> IsInvalidatedBySentinelAsync(
            Type entityType, long entryWrittenAtTicks, CancellationToken ct)
        {
            // Check process-local sentinel first (cheapest)
            if (_invalidationSentinels.TryGetValue(entityType, out var localSentinel)
                && localSentinel > entryWrittenAtTicks)
            {
                return true;
            }

            // Check distributed sentinel
            var sentinelKey = BuildSentinelKey(entityType);
            try
            {
                var bytes = await _cache.GetAsync(sentinelKey, ct).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0) return false;
                if (long.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var distributedSentinel))
                {
                    if (distributedSentinel > entryWrittenAtTicks)
                    {
                        _invalidationSentinels[entityType] = distributedSentinel;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[WTM] DistributedLookupCache: failed to read sentinel for type '{TypeName}' (async). Treating entry as valid.",
                    entityType.FullName ?? entityType.Name);
            }

            return false;
        }

        // ─── Private helpers ─────────────────────────────────────────────────────

        private static string BuildKey(Type type, string? tenantId)
        {
            var tid = string.IsNullOrEmpty(tenantId) ? "_" : tenantId;
            return $"wtm:lookup:{type.FullName}:{tid}";
        }

        private static string BuildSentinelKey(Type type) =>
            $"wtm:lookup:inv:{type.FullName}";

        private static DistributedCacheEntryOptions BuildCacheEntryOptions(CacheLookupAttribute? attr)
        {
            var ttl = attr != null
                ? TimeSpan.FromMinutes(attr.TtlMinutes)
                : TimeSpan.FromMinutes(30);
            return new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
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
                    if (type.IsAbstract || type.IsInterface) continue;
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
