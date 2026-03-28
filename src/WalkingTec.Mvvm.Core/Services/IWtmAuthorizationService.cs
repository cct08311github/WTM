#nullable enable
using System.Collections.Generic;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Encapsulates URL-based access-control logic previously embedded in WTMContext.
    /// All state is passed explicitly so the service remains stateless and testable.
    /// </summary>
    public interface IWtmAuthorizationService
    {
        /// <summary>
        /// Check whether the given URL is accessible for the current user.
        /// </summary>
        /// <param name="url">URL to check.</param>
        /// <param name="loginUser">Current user info (may be null for anonymous).</param>
        /// <param name="config">Framework configuration.</param>
        /// <param name="globalData">Global metadata (menus, access URLs, etc.).</param>
        bool IsAccessable(string? url, LoginUserInfo? loginUser, Configs? config, GlobalData? globalData);

        /// <summary>
        /// Check whether the given URL is marked as public (no authentication required).
        /// </summary>
        /// <param name="url">URL to check.</param>
        /// <param name="globalData">Global metadata containing menu definitions.</param>
        bool IsUrlPublic(string? url, GlobalData? globalData);
    }
}
