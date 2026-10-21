using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.E2E.Pages.Vue3;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>
    /// Data every Vue3Demo test run needs once. The demo seeds only the _Admin menus; the SchoolData and testvue pages exist in the
    /// SPA but no menu points at them, and without a menu even the administrator gets 403 from their API. The run therefore creates the
    /// menus (the same call the menu management page makes), grants all of their actions to the administrator role and refreshes the
    /// menu cache, exactly as an operator would after adding a module.
    /// </summary>
    internal static class Vue3Seed
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static bool _done;

        /// <summary>Module (controller) and route of the demo pages that the tests use. Student and Major have no screen in the SPA.</summary>
        public static readonly (string Page, string Module, string Url)[] Menus =
        {
            ("Cities", "WalkingTec.Mvvm.Vue3Demo.Controllers,City", "/schooldata/city"),
            ("Schools", "WalkingTec.Mvvm.ReactDemo.Controllers,School", "/_admin/school"),
            ("SchoolVue3", "WalkingTec.Mvvm.Vue3Demo.Controllers,SchoolVue3", "/testvue/schoolvue3"),
            ("Students", "WalkingTec.Mvvm.ReactDemo.Controllers,Student", "/schooldata/student"),
            ("Majors", "WalkingTec.Mvvm.ReactDemo.Controllers,Major", "/schooldata/major"),
        };

        public static async Task EnsureAsync(Vue3Api api)
        {
            await Gate.WaitAsync();
            try
            {
                if (_done)
                {
                    return;
                }
                var order = 100;
                foreach (var (page, module, url) in Menus)
                {
                    var actions = await api.GetAsync("/api/_FrameworkMenu/GetActionsByModel?ModelName=" + Uri.EscapeDataString(module));
                    await api.PostAsync("/api/_FrameworkMenu/Add", new
                    {
                        Entity = new { Url = url, PageName = page, ShowOnMenu = true, IsInside = true, DisplayOrder = order++ },
                        SelectedModule = module,
                        SelectedActionIDs = actions.EnumerateArray().Select(x => x.GetProperty("Value").GetString()).ToArray(),
                        SelectedRolesIds = Array.Empty<string>()
                    });
                }
                await GrantAllToAdministratorRoleAsync(api);
                await GrantAllCitiesToAdministratorAsync(api);
                await api.GetAsync("/api/_FrameworkMenu/RefreshMenu");
                _done = true;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// The demo declares a data privilege for City: a user without one gets no cities in the select lists. The administrator
        /// gets "all" through the data privilege API (the call of the data privilege page), as an operator would.
        /// </summary>
        private static async Task GrantAllCitiesToAdministratorAsync(Vue3Api api)
        {
            await api.PostAsync("/api/_DataPrivilege/Add", new
            {
                Entity = new { UserCode = "admin", TableName = "City" },
                IsAll = true,
                SelectedItemsID = Array.Empty<string>(),
                DpType = "User"
            });
        }

        /// <summary>Saves the privilege page of the administrator role (001) as the page itself shows it, with every action selected.</summary>
        private static async Task GrantAllToAdministratorRoleAsync(Vue3Api api)
        {
            var roles = await api.PostAsync("/api/_FrameworkRole/Search", new { RoleCode = "001" });
            var roleId = roles.GetProperty("Data")[0].GetProperty("ID").GetString();
            var page = await api.GetAsync("/api/_FrameworkRole/GetPageActions/" + roleId);
            var pages = page.GetProperty("Pages").EnumerateArray().Select(p => new Dictionary<string, object>
            {
                ["ID"] = p.GetProperty("ID").GetString(),
                ["Name"] = p.GetProperty("Name").GetString(),
                ["Actions"] = p.GetProperty("AllActions").EnumerateArray().Select(a => a.GetProperty("Value").GetString()).ToArray()
            }).ToArray();
            var entity = page.GetProperty("Entity");
            await api.PutAsync("/api/_FrameworkRole/EditPrivilege", new
            {
                Entity = new { ID = roleId, RoleCode = entity.GetProperty("RoleCode").GetString(), RoleName = entity.GetProperty("RoleName").GetString() },
                Pages = pages
            });
        }
    }
}
