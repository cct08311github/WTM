#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Default implementation of <see cref="IWtmAuthorizationService"/>.
    /// Singleton service — uses compiled regex cache for performance.
    /// </summary>
    public class WtmAuthorizationService : IWtmAuthorizationService
    {
        private static readonly ConcurrentDictionary<string, Regex> _regexCache = new();
        private static readonly Regex _batchRewrite = new("/do(batch.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ILogger<WtmAuthorizationService>? _logger;

        public WtmAuthorizationService(ILogger<WtmAuthorizationService>? logger = null)
        {
            _logger = logger;
        }

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
                    var hostonly = globalData?.AllMainTenantOnlyUrls ?? [];
                    foreach (var au in hostonly)
                    {
                        if (MatchUrl(au, url))
                        {
                            return false;
                        }
                    }
                }
            }

            // Check unrestricted (public action) URLs
            var publicActions = globalData?.AllAccessUrls ?? [];
            foreach (var au in publicActions)
            {
                if (au != "/" && MatchUrl(au, url))
                {
                    return true;
                }
            }

            // No function privileges at all → deny
            if (loginUser?.FunctionPrivileges == null)
            {
                return false;
            }

            url = _batchRewrite.Replace(url, "/$1");
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
                return IsMenuAccessable(menu, menus, loginUser);
            }
        }

        public bool IsUrlPublic(string? url, GlobalData? globalData)
        {
            try
            {
                url = _batchRewrite.Replace(url ?? "", "/$1");
                url = url.Trim();

                if (url.StartsWith("#"))
                {
                    return true;
                }
                var menus = globalData?.AllMenus;
                var menu = Utils.FindMenu(url, menus);
                if (menu != null && menu.IsPublic == true)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error checking if URL '{Url}' is public", LogSanitizer.Sanitize(url));
            }
            return false;
        }

        /// <summary>Match a URL against a pattern using cached compiled regex.</summary>
        private static bool MatchUrl(string pattern, string url)
        {
            var regex = _regexCache.GetOrAdd(pattern, p =>
            {
                // Use NonBacktracking to prevent ReDoS attacks — guarantees linear time complexity.
                // NotSupportedException is thrown at construction (not at match time) when the pattern
                // uses features unsupported by NonBacktracking (e.g. backreferences, look-aheads).
                // ArgumentException is thrown for invalid regex patterns.
                // Both must be caught here because ConcurrentDictionary.GetOrAdd does not retry
                // a failed factory, and an uncaught exception would surface as an unhandled 500.
                try
                {
                    // Escape the pattern (it is a literal controller/action URL, e.g. "/Home/Index" —
                    // never an author-supplied regex fragment) and anchor the match to a path boundary
                    // so a public URL only matches itself, itself + "/", or itself + "?" — never a
                    // longer sibling path segment that merely shares the same prefix
                    // (e.g. "/Home/Index" must not match "/Home/IndexAdmin"). See Issue #531.
                    return new Regex("^" + Regex.Escape(p) + "($|[/\\?])", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.NonBacktracking);
                }
                catch (Exception ex) when (ex is RegexMatchTimeoutException or NotSupportedException or ArgumentException)
                {
                    // Log so operators can detect problematic URL patterns.
                    CoreProgram.GetLogger(nameof(WtmAuthorizationService))
                        ?.LogWarning(ex, "MatchUrl: NonBacktracking regex failed for pattern '{Pattern}'; falling back to compiled regex", LogSanitizer.Sanitize(p));

                    // Fall back to compiled regex without NonBacktracking engine.
                    return new Regex("^" + Regex.Escape(p) + "($|[/\\?])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }
            });
            return regex.IsMatch(url);
        }

        private static bool IsMenuAccessable(SimpleMenu? menu, List<SimpleMenu>? menus, LoginUserInfo? loginUser)
        {
            if (loginUser?.CurrentTenant != null && menu?.TenantAllowed == false)
            {
                return false;
            }
            var find = loginUser?.FunctionPrivileges?.Where(x => x.MenuItemId == menu?.ID && x.Allowed == true).FirstOrDefault();
            return find != null;
        }
    }
}
