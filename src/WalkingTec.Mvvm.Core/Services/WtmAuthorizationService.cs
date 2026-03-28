#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Default implementation of <see cref="IWtmAuthorizationService"/>.
    /// Logic is an exact copy of WTMContext.IsAccessable / IsUrlPublic
    /// (WTMContext.cs lines 971-1075) to guarantee identical behaviour.
    /// </summary>
    public class WtmAuthorizationService : IWtmAuthorizationService
    {
        public bool IsAccessable(string? url, LoginUserInfo? loginUser, Configs? config, GlobalData? globalData)
        {
            if (config?.IsQuickDebug == true || string.IsNullOrEmpty(url) || IsUrlPublic(url, globalData))
            {
                return true;
            }

            // Tenant users cannot access [HostOnly] methods
            if (config?.EnableTenant == true)
            {
                if (loginUser?.TenantCode != null)
                {
                    var hostonly = globalData?.AllMainTenantOnlyUrls ?? new List<string>();
                    foreach (var au in hostonly)
                    {
                        if (new Regex("^" + au + "[/\\?]?", RegexOptions.IgnoreCase).IsMatch(url))
                        {
                            return false;
                        }
                    }
                }
            }

            // Check unrestricted (public action) URLs
            var publicActions = globalData?.AllAccessUrls ?? new List<string>();
            foreach (var au in publicActions)
            {
                if (au != "/" && new Regex("^" + au + "[/\\?]?", RegexOptions.IgnoreCase).IsMatch(url))
                {
                    return true;
                }
            }

            // No function privileges at all → deny
            if (loginUser?.FunctionPrivileges == null)
            {
                return false;
            }

            url = Regex.Replace(url ?? "", "/do(batch.*)", "/$1", RegexOptions.IgnoreCase);
            url = url.Trim();

            if (url.StartsWith("#"))
            {
                return true;
            }

            var menus = globalData?.AllMenus;
            var menu = Utils.FindMenu(url, menus);
            if (menu == null)
            {
                return false;
            }
            else
            {
                return IsAccessable(menu, menus, loginUser);
            }
        }

        public bool IsUrlPublic(string? url, GlobalData? globalData)
        {
            var isPublic = false;
            try
            {
                url = Regex.Replace(url ?? "", "/do(batch.*)", "/$1", RegexOptions.IgnoreCase);
                url = url.Trim();

                if (url.StartsWith("#"))
                {
                    isPublic = true;
                }
                var menus = globalData?.AllMenus;
                var menu = Utils.FindMenu(url, menus);
                if (menu != null && menu.IsPublic == true)
                {
                    isPublic = true;
                }
            }
            catch { }
            return isPublic;
        }

        private static bool IsAccessable(SimpleMenu? menu, List<SimpleMenu>? menus, LoginUserInfo? loginUser)
        {
            if (loginUser?.CurrentTenant != null && menu?.TenantAllowed == false)
            {
                return false;
            }
            var find = loginUser?.FunctionPrivileges?.Where(x => x.MenuItemId == menu?.ID && x.Allowed == true).FirstOrDefault();
            if (find != null)
            {
                return true;
            }
            return false;
        }
    }
}
