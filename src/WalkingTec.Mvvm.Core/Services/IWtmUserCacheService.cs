#nullable enable
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Encapsulates user-info cache invalidation logic previously in WTMContext.
    /// </summary>
    public interface IWtmUserCacheService
    {
        /// <summary>Remove cached LoginUserInfo for given user IDs.</summary>
        Task RemoveUserCacheAsync(string? currentTenant, params string[] userIds);

        /// <summary>Remove cached LoginUserInfo for all users in the given roles.</summary>
        Task RemoveUserCacheByRoleAsync(string? currentTenant, bool hasMainHost, IDataContext? dc, IWtmApiClient? apiClient, params string[] roleCodes);

        /// <summary>Remove cached LoginUserInfo for all users in the given groups.</summary>
        Task RemoveUserCacheByGroupAsync(string? currentTenant, bool hasMainHost, IDataContext? dc, IWtmApiClient? apiClient, params string[] groupCodes);
    }
}
