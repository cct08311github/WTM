using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.LayUI.E2EHost.Models;

namespace WalkingTec.Mvvm.LayUI.E2EHost
{
    public class DataContext : FrameworkContext
    {
        public DbSet<FrameworkUser> FrameworkUsers { get; set; }
        public DbSet<Gadget> Gadgets { get; set; }

        public DataContext(CS cs) : base(cs)
        {
        }

        public DataContext(string cs, DBTypeEnum dbtype) : base(cs, dbtype)
        {
        }

        public override async Task<bool> DataInit(object allModules, bool isSpa)
        {
            var state = await base.DataInit(allModules, isSpa);
            var empty = false;
            try
            {
                empty = Set<FrameworkUser>().Count() == 0 && Set<FrameworkUserRole>().Count() == 0;
            }
            catch
            {
            }

            if (state || empty)
            {
                // All data below is synthetic. The administrator password is read from configuration (E2E:AdminPassword),
                // the test run generates it; a manual run uses the throw-away value in appsettings.json.
                var password = E2ESeed.AdminPassword;
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("Configuration value E2E:AdminPassword is required to seed the test administrator.");
                }
                var admin = new FrameworkUser
                {
                    ITCode = E2ESeed.AdminAccount,
                    Password = PasswordHasher.Default.Hash(password),
                    IsValid = true,
                    Name = "E2E Admin"
                };
                Set<FrameworkUser>().Add(admin);
                Set<FrameworkUserRole>().Add(new FrameworkUserRole { UserCode = admin.ITCode, RoleCode = "001" });

                // menu: folder -> Gadget list page, the other Gadget actions are children of the list page
                var modules = (allModules as List<SimpleModule>) ?? new List<SimpleModule>();
                var gadget = modules.FirstOrDefault(x => x.ClassName == "Gadget" && x.Area == null);
                if (gadget != null)
                {
                    var folder = new FrameworkMenu
                    {
                        PageName = "Inventory",
                        Children = new List<FrameworkMenu>(),
                        ShowOnMenu = true,
                        IsInside = true,
                        FolderOnly = true,
                        IsPublic = false,
                        TenantAllowed = true,
                        DisplayOrder = 1,
                        Icon = "layui-icon layui-icon-app"
                    };
                    var index = gadget.Actions.First(x => x.MethodName == "Index");
                    var page = MakeMenu(index, true, 1);
                    page.PageName = "Gadgets";
                    page.Icon = "layui-icon layui-icon-form";
                    var order = 1;
                    foreach (var act in gadget.Actions.Where(x => x.MethodName != "Index" && x.IgnorePrivillege == false))
                    {
                        var sub = MakeMenu(act, false, order++);
                        sub.PageName = "Gadgets";
                        page.Children.Add(sub);
                    }
                    folder.Children.Add(page);
                    Set<FrameworkMenu>().Add(folder);
                    Set<FunctionPrivilege>().AddRange(folder.FlatTree().Select(x => new FunctionPrivilege { RoleCode = "001", MenuItemId = x.ID, Allowed = true }));
                }

                // synthetic rows so that the list page has something to show on first load
                var names = new[] { "Alpha Wrench", "Beta Spinner", "Gamma Blender", "Delta Level", "Epsilon Kite" };
                var categories = new[] { GadgetCategory.Tool, GadgetCategory.Toy, GadgetCategory.Appliance, GadgetCategory.Tool, GadgetCategory.Toy };
                for (var i = 0; i < names.Length; i++)
                {
                    Set<Gadget>().Add(new Gadget
                    {
                        Name = names[i],
                        Code = "G-" + (100 + i),
                        Category = categories[i],
                        Price = 10 + i * 5,
                        LaunchDate = new DateTime(2024, 1, 1).AddDays(i * 30),
                        InStock = i % 2 == 0
                    });
                }
                await SaveChangesAsync();
            }
            return state;
        }

        private static FrameworkMenu MakeMenu(SimpleAction act, bool isMainLink, int order)
        {
            var menu = new FrameworkMenu
            {
                ClassName = act.Module.FullName,
                MethodName = act.MethodName,
                Url = act.Url,
                ShowOnMenu = isMainLink,
                FolderOnly = false,
                Children = new List<FrameworkMenu>(),
                IsPublic = false,
                IsInside = true,
                DisplayOrder = order,
                TenantAllowed = true,
                ModuleName = act.Module.ModuleName,
                ActionName = act.ActionDes?.Description ?? act.ActionName
            };
            if (isMainLink)
            {
                menu.MethodName = null;
            }
            return menu;
        }
    }

    /// <summary>
    /// Synthetic seed values. The administrator account and password come from configuration so that the E2E test project can
    /// generate a throw-away password for each run.
    /// </summary>
    public static class E2ESeed
    {
        public const string AdminAccount = "e2eadmin";

        /// <summary>
        /// Set by Startup from configuration (E2E:AdminPassword) before the database is initialised.
        /// </summary>
        public static string AdminPassword { get; set; }
    }
}
