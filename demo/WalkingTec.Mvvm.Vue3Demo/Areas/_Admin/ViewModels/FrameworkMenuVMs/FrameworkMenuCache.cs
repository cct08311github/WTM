// WTM默认页面 Wtm buidin page
using System;
using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkMenuVMs
{
    /// <summary>
    /// What has to be dropped from the cache when menus are removed, so that the side menu of the SPA no longer shows them.
    /// The menu list (<see cref="GlobalData.AllMenus"/>) is cached for an hour, and the menus of a signed in user are computed
    /// from that list and from the function privileges in the user cache.
    /// </summary>
    internal static class FrameworkMenuCache
    {
        /// <summary>The ids of the menus and of everything below them, which is what a cascade delete removes.</summary>
        public static List<Guid> WithDescendants(IDataContext dc, IEnumerable<Guid> ids)
        {
            var all = new List<Guid>(ids);
            var current = all.ToList();
            while (current.Count > 0)
            {
                current = dc.Set<FrameworkMenu>().Where(x => x.ParentId != null && current.Contains(x.ParentId.Value)).Select(x => x.ID).ToList()
                    .Where(x => all.Contains(x) == false).ToList();
                all.AddRange(current);
            }
            return all;
        }

        /// <summary>The roles that have a function privilege on one of the menus. Read before the menus are deleted.</summary>
        public static string[] RolesOf(IDataContext dc, IEnumerable<Guid> menuIds)
        {
            var ids = menuIds.ToList();
            return dc.Set<FunctionPrivilege>().Where(x => ids.Contains(x.MenuItemId)).Select(x => x.RoleCode).Distinct().ToList().ToArray();
        }

        /// <summary>Drops the cached menu list and the cached users of the roles, so that the next read builds the menus again.</summary>
        public static void Invalidate(WTMContext wtm, string[] roleCodes)
        {
            wtm.Cache.Delete(nameof(GlobalData.AllMenus));
            if (roleCodes != null && roleCodes.Length > 0)
            {
                wtm.RemoveUserCacheByRole(roleCodes).Wait();
            }
        }
    }
}
