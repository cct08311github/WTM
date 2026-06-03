#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Default implementation of <see cref="IWtmTenantService"/>.
    /// Logic copied from WTMContext lines 795-884 (GetTenantGroups, GetTenantRoles,
    /// RemoveGroupCache, RemoveRoleCache).
    /// </summary>
    public class WtmTenantService : IWtmTenantService
    {
        private readonly IDistributedCache _cache;
        private readonly IOptionsMonitor<Configs> _configs;
        private readonly GlobalData _globalData;
        private readonly ILogger<WtmTenantService>? _logger;
        // Static so the per-key locks are shared across all scoped instances.
        // WtmTenantService is AddScoped: a fresh instance per request means an
        // instance-field ConcurrentDictionary would be empty for every request,
        // so concurrent requests for the same cold key would all acquire "their
        // own" semaphore and each issue a duplicate DB query (stampede).
        // Making the dictionary static ensures the semaphores are process-shared
        // and truly serialise concurrent cold-key requests. The service lifetime
        // is NOT changed (remains AddScoped) — only the lock table is promoted.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

        public WtmTenantService(
            IDistributedCache cache,
            IOptionsMonitor<Configs> configs,
            GlobalData globalData,
            ILogger<WtmTenantService>? logger = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _configs = configs ?? throw new ArgumentNullException(nameof(configs));
            _globalData = globalData ?? throw new ArgumentNullException(nameof(globalData));
            _logger = logger;
        }

        public List<SimpleGroup>? GetTenantGroups(string? tenant)
        {
            var key = $"{GlobalConstants.CacheKey.TenantGroups}:{tenant}";
            return ReadFromCache(key, () =>
            {
                List<SimpleGroup>? groups = null;
                try
                {
                    var tenants = _globalData.AllTenant ?? [];
                    var dbtenant = tenants.Where(x => x.TCode == tenant && x.IsUsingDB == true).FirstOrDefault();
                    using var dc = CreateDCForTenant(dbtenant);
                    groups = [.. dc?.Set<FrameworkGroup>()
                        .IgnoreQueryFilters() // IgnoreQueryFilters: tenant data is queried cross-filter
                        .Where(x => x.TenantCode == tenant)
                        .Select(x => new SimpleGroup
                        {
                            ID = x.ID,
                            GroupCode = x.GroupCode,
                            GroupName = x.GroupName,
                            Manager = x.Manager,
                            ParentId = x.ParentId,
                            Tenant = x.TenantCode
                        })];
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to load tenant groups for '{Tenant}'", LogSanitizer.Sanitize(tenant));
                    groups = [];
                }
                return groups;
            }, 360000);
        }

        public List<SimpleRole>? GetTenantRoles(string? tenant)
        {
            var key = $"{GlobalConstants.CacheKey.TenantRoles}:{tenant}";
            return ReadFromCache(key, () =>
            {
                List<SimpleRole>? roles = null;
                try
                {
                    var tenants = _globalData.AllTenant ?? [];
                    var dbtenant = tenants.Where(x => x.TCode == tenant && x.IsUsingDB == true).FirstOrDefault();
                    using var dc = CreateDCForTenant(dbtenant);
                    roles = [.. dc?.Set<FrameworkRole>()
                        .IgnoreQueryFilters() // IgnoreQueryFilters: tenant data is queried cross-filter
                        .Where(x => x.TenantCode == tenant)
                        .Select(x => new SimpleRole
                        {
                            ID = x.ID,
                            RoleCode = x.RoleCode,
                            RoleName = x.RoleName,
                            Tenant = x.TenantCode
                        })];
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to load tenant roles for '{Tenant}'", LogSanitizer.Sanitize(tenant));
                    roles = [];
                }
                return roles;
            }, 360000);
        }

        public async Task RemoveGroupCacheAsync(string? tenant)
        {
            var key = $"{GlobalConstants.CacheKey.TenantGroups}:{tenant}";
            await _cache.DeleteAsync(key);
        }

        public async Task RemoveRoleCacheAsync(string? tenant)
        {
            var key = $"{GlobalConstants.CacheKey.TenantRoles}:{tenant}";
            await _cache.DeleteAsync(key);
        }

        /// <summary>
        /// Create a DataContext for tenant queries. If the tenant has its own DB,
        /// use it; otherwise fall back to the default connection.
        /// </summary>
        private IDataContext? CreateDCForTenant(FrameworkTenant? dbtenant)
        {
            if (dbtenant != null)
            {
                // Tenant-specific DB: replicate FrameworkTenant.CreateDC logic for IsUsingDB==true
                var context = string.IsNullOrEmpty(dbtenant.DbContext) ? "DataContext" : dbtenant.DbContext;
                var dcConstructor = CS.CisFull
                    .Where(x => x.DeclaringType?.Name.ToLower() == context.ToLower())
                    .FirstOrDefault();
                return (IDataContext?)dcConstructor?.Invoke(new object[] { dbtenant.TDb!, dbtenant.TDbType! });
            }
            else
            {
                // Default connection
                var configInfo = _configs.CurrentValue;
                return configInfo?.Connections?
                    .Where(x => x.Key.ToLower() == "default")
                    .FirstOrDefault()?.CreateDC();
            }
        }

        /// <summary>
        /// Cache-aside helper with stampede protection via per-key SemaphoreSlim.
        /// Uses SemaphoreSlim.Wait() for synchronous lock. This is correct for
        /// synchronous callers; WaitAsync().GetAwaiter().GetResult() would add
        /// overhead and risk deadlock in contexts with a SynchronizationContext.
        /// </summary>
        private T? ReadFromCache<T>(string key, Func<T?> setFunc, int timeoutSeconds)
        {
            if (_cache.TryGetValue(key, out T? rv) && rv != null)
            {
                return rv;
            }

            var keyLock = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            keyLock.Wait();
            try
            {
                // Double-check after acquiring lock
                if (_cache.TryGetValue(key, out rv) && rv != null)
                {
                    return rv;
                }
                rv = setFunc();
                _cache.Add(key, rv, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(timeoutSeconds)
                });
                return rv;
            }
            finally
            {
                keyLock.Release();
            }
        }
    }
}
