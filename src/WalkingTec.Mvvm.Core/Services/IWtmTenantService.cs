#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Encapsulates tenant management logic previously in WTMContext
    /// (GetTenantGroups, GetTenantRoles, RemoveGroupCache, RemoveRoleCache, SetCurrentTenant).
    /// </summary>
    public interface IWtmTenantService
    {
        /// <summary>Get all groups for a tenant (cached with 360000s TTL).</summary>
        List<SimpleGroup>? GetTenantGroups(string? tenant);

        /// <summary>Get all roles for a tenant (cached with 360000s TTL).</summary>
        List<SimpleRole>? GetTenantRoles(string? tenant);

        /// <summary>Remove the cached groups for a tenant.</summary>
        Task RemoveGroupCacheAsync(string tenant);

        /// <summary>Remove the cached roles for a tenant.</summary>
        Task RemoveRoleCacheAsync(string tenant);
    }
}
