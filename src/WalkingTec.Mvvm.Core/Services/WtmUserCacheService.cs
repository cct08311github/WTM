#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Default implementation of <see cref="IWtmUserCacheService"/>.
    /// Logic copied from WTMContext lines 733-793.
    /// </summary>
    public class WtmUserCacheService : IWtmUserCacheService
    {
        private readonly IDistributedCache _cache;

        public WtmUserCacheService(IDistributedCache cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public async Task RemoveUserCacheAsync(string? currentTenant, params string[] userIds)
        {
            foreach (var userId in userIds)
            {
                var key = $"{GlobalConstants.CacheKey.UserInfo}:{userId + "$`$" + currentTenant}";
                await _cache.RemoveAsync(key);
            }
        }

        public async Task RemoveUserCacheByRoleAsync(
            string? currentTenant, bool hasMainHost, IDataContext? dc,
            IWtmApiClient? apiClient, params string[] roleCodes)
        {
            List<string> userids = new List<string>();
            if (hasMainHost && string.IsNullOrEmpty(currentTenant))
            {
                if (apiClient != null)
                {
                    foreach (var item in roleCodes)
                    {
                        var rv = await apiClient.CallAPI<List<string>>("mainhost",
                            $"/api/_frameworkuser/GetUserByRole?keywords={item}");
                        if (rv?.Data != null)
                        {
                            userids.AddRange(rv.Data);
                        }
                    }
                }
            }
            else if (dc != null)
            {
                userids = dc.Set<FrameworkUserRole>()
                    .Where(x => roleCodes.Contains(x.RoleCode))
                    .Select(x => x.UserCode)
                    .ToList();
            }

            foreach (var userId in userids)
            {
                var key = $"{GlobalConstants.CacheKey.UserInfo}:{userId + "$`$" + currentTenant}";
                await _cache.RemoveAsync(key);
            }
        }

        public async Task RemoveUserCacheByGroupAsync(
            string? currentTenant, bool hasMainHost, IDataContext? dc,
            IWtmApiClient? apiClient, params string[] groupCodes)
        {
            List<string> userids = new List<string>();
            if (hasMainHost && string.IsNullOrEmpty(currentTenant))
            {
                if (apiClient != null)
                {
                    foreach (var item in groupCodes)
                    {
                        var rv = await apiClient.CallAPI<List<string>>("mainhost",
                            $"/api/_frameworkuser/GetUserByGroup?keywords={item}");
                        if (rv?.Data != null)
                        {
                            userids.AddRange(rv.Data);
                        }
                    }
                }
            }
            else if (dc != null)
            {
                userids = dc.Set<FrameworkUserGroup>()
                    .Where(x => groupCodes.Contains(x.GroupCode))
                    .Select(x => x.UserCode)
                    .ToList();
            }

            foreach (var userId in userids)
            {
                var key = $"{GlobalConstants.CacheKey.UserInfo}:{userId + "$`$" + currentTenant}";
                await _cache.RemoveAsync(key);
            }
        }
    }
}
