using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.WorkFlow
{
    /// <summary>
    /// Local database lookups for users, groups, group managers and roles used by the workflow (approve activity and
    /// <c>_WorkflowApi</c>). They read the local database of the given context; multi-host (HasMainHost) forwarding is the caller's concern.
    /// An empty or null id list returns everything, exactly like the original controller queries, so only call these from
    /// code that has already established who the caller is (a signed-in user, or trusted server-side code). Never expose these
    /// methods, or an endpoint built on them, to unauthenticated callers: with no ids they return all users (ITCode, name), groups, managers and roles.
    /// </summary>
    public static class WorkflowLookup
    {
        public static List<ComboSelectListItem> GetUsers(WTMContext wtm, IEnumerable<string> itcodes)
        {
            var tenant = wtm.LoginUserInfo?.CurrentTenant;
            return wtm.BaseUserQuery.IgnoreQueryFilters().CheckContain((itcodes ?? new string[0]).ToList(), x => x.ITCode).Where(x => x.TenantCode == tenant)
                .Select(x => new { x.ITCode, x.Name })
                .OrderBy(x => x.ITCode)
                .ToList().ToListItems(x => x.Name, x => x.ITCode);
        }

        public static List<ComboSelectListItem> GetGroups(WTMContext wtm, IEnumerable<string> ids)
        {
            return wtm.DC.Set<FrameworkGroup>().CheckIDs((ids ?? new string[0]).ToList())
                .Select(x => new { x.ID, x.GroupName })
                .OrderBy(x => x.GroupName)
                .ToList().ToListItems(x => x.GroupName, x => x.ID);
        }

        public static List<ComboSelectListItem> GetGroupManagers(WTMContext wtm, IEnumerable<string> ids)
        {
            return wtm.DC.Set<FrameworkGroup>().CheckIDs((ids ?? new string[0]).ToList())
                .Select(x => new { x.ID, x.Manager })
                .OrderBy(x => x.Manager)
                .ToList().ToListItems(x => x.Manager, x => x.ID);
        }

        public static List<ComboSelectListItem> GetMyGroupManagers(WTMContext wtm, string itcode)
        {
            return wtm.DC.Set<FrameworkUserGroup>().Where(x => x.UserCode == itcode)
                .Join(wtm.DC.Set<FrameworkGroup>(), x => x.GroupCode, y => y.GroupCode, (x, y) => y.Manager)
                .ToList().ToListItems(x => x, x => x);
        }

        public static List<ComboSelectListItem> GetRoles(WTMContext wtm, IEnumerable<string> ids)
        {
            return wtm.DC.Set<FrameworkRole>().CheckIDs((ids ?? new string[0]).ToList())
                .Select(x => new { x.ID, x.RoleName })
                .OrderBy(x => x.RoleName)
                .ToList().ToListItems(x => x.RoleName, x => x.ID);
        }
    }
}
