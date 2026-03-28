#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
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

        public WtmTenantService(
            IDistributedCache cache,
            IOptionsMonitor<Configs> configs,
            GlobalData globalData)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _configs = configs ?? throw new ArgumentNullException(nameof(configs));
            _globalData = globalData ?? throw new ArgumentNullException(nameof(globalData));
        }

        public List<SimpleGroup>? GetTenantGroups(string? tenant)
        {
            var key = $"{GlobalConstants.CacheKey.TenantGroups}:{tenant}";
            return ReadFromCache(key, () =>
            {
                List<SimpleGroup>? groups = null;
                try
                {
                    var tenants = _globalData.AllTenant ?? new List<FrameworkTenant>();
                    var dbtenant = tenants.Where(x => x.TCode == tenant && x.IsUsingDB == true).FirstOrDefault();
                    using var dc = CreateDCForTenant(dbtenant);
                    groups = dc?.Set<FrameworkGroup>()
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
                        }).ToList();
                }
                catch
                {
                    groups = new List<SimpleGroup>();
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
                    var tenants = _globalData.AllTenant ?? new List<FrameworkTenant>();
                    var dbtenant = tenants.Where(x => x.TCode == tenant && x.IsUsingDB == true).FirstOrDefault();
                    using var dc = CreateDCForTenant(dbtenant);
                    roles = dc?.Set<FrameworkRole>()
                        .IgnoreQueryFilters() // IgnoreQueryFilters: tenant data is queried cross-filter
                        .Where(x => x.TenantCode == tenant)
                        .Select(x => new SimpleRole
                        {
                            ID = x.ID,
                            RoleCode = x.RoleCode,
                            RoleName = x.RoleName,
                            Tenant = x.TenantCode
                        }).ToList();
                }
                catch
                {
                    roles = new List<SimpleRole>();
                }
                return roles;
            }, 360000);
        }

        public async Task RemoveGroupCacheAsync(string tenant)
        {
            var key = $"{GlobalConstants.CacheKey.TenantGroups}:{tenant}";
            await _cache.DeleteAsync(key);
        }

        public async Task RemoveRoleCacheAsync(string tenant)
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
        /// Cache-aside helper replicating WTMContext.ReadFromCache behaviour.
        /// </summary>
        private T? ReadFromCache<T>(string key, Func<T?> setFunc, int timeoutSeconds)
        {
            if (_cache.TryGetValue(key, out T? rv) == false || rv == null)
            {
                rv = setFunc();
                _cache.Add(key, rv, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(timeoutSeconds)
                });
            }
            return rv;
        }
    }
}
