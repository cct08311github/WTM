using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.LayUI.E2EHost.Fixtures;
using WalkingTec.Mvvm.LayUI.E2EHost.Models;

namespace WalkingTec.Mvvm.LayUI.E2EHost
{
    public class DataContext : FrameworkContext
    {
        public DbSet<FrameworkUser> FrameworkUsers { get; set; }
        public DbSet<Gadget> Gadgets { get; set; }
        public DbSet<LabItem> LabItems { get; set; }

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

                    // role of the permission matrix tests (#273): the list page and read-only actions of Gadget, nothing else
                    if (string.IsNullOrEmpty(E2ESeed.LimitedPassword) == false)
                    {
                        Set<FunctionPrivilege>().AddRange(folder.FlatTree()
                            .Where(x => x == folder || x == page || E2ESeed.LimitedRoleActions.Contains(x.MethodName))
                            .Select(x => new FunctionPrivilege { RoleCode = E2ESeed.LimitedRoleCode, MenuItemId = x.ID, Allowed = true }));
                    }
                }

                // the Report page: a menu entry that only the administrator role gets
                var report = modules.FirstOrDefault(x => x.ClassName == "Report" && x.Area == null);
                if (report != null)
                {
                    var reportFolder = new FrameworkMenu
                    {
                        PageName = "Reports",
                        Children = new List<FrameworkMenu>(),
                        ShowOnMenu = true,
                        IsInside = true,
                        FolderOnly = true,
                        IsPublic = false,
                        TenantAllowed = true,
                        DisplayOrder = 3,
                        Icon = "layui-icon layui-icon-chart"
                    };
                    var reportPage = MakeMenu(report.Actions.First(x => x.MethodName == "Index"), true, 1);
                    reportPage.PageName = "Gadget report";
                    reportFolder.Children.Add(reportPage);
                    Set<FrameworkMenu>().Add(reportFolder);
                    Set<FunctionPrivilege>().AddRange(reportFolder.FlatTree().Select(x => new FunctionPrivilege { RoleCode = "001", MenuItemId = x.ID, Allowed = true }));
                }

                SeedMatrixAccounts();

                // menu for the Lab fixture pages (/Lab/*): a folder with one entry per page. The actions are AllRights, so
                // only the menu entry is needed; the role gets the privilege rows like for any other menu entry.
                var labFolder = new FrameworkMenu
                {
                    PageName = "Lab",
                    Children = new List<FrameworkMenu>(),
                    ShowOnMenu = true,
                    IsInside = true,
                    FolderOnly = true,
                    IsPublic = false,
                    TenantAllowed = true,
                    DisplayOrder = 2,
                    Icon = "layui-icon layui-icon-component"
                };
                var labOrder = 1;
                foreach (var labPage in new[] { "Inputs", "Choices", "Pickers", "Grid", "Detail", "Layout", "Charts", "Trees" })
                {
                    labFolder.Children.Add(new FrameworkMenu
                    {
                        PageName = "Lab " + labPage,
                        Url = "/Lab/" + labPage,
                        ModuleName = "Lab",
                        ActionName = labPage,
                        ShowOnMenu = true,
                        FolderOnly = false,
                        IsPublic = false,
                        IsInside = true,
                        TenantAllowed = true,
                        DisplayOrder = labOrder++,
                        Children = new List<FrameworkMenu>(),
                        Icon = "layui-icon layui-icon-file"
                    });
                }
                // the export button of the Lab grid posts to /_Framework/GetExportExcel; like every action behind a button it needs a menu
                // entry (not shown in the menu) so that the grid shows the button for this user
                labFolder.Children.Add(new FrameworkMenu
                {
                    PageName = "Lab export",
                    Url = "/_Framework/GetExportExcel",
                    ModuleName = "Lab",
                    ActionName = "Export",
                    ShowOnMenu = false,
                    FolderOnly = false,
                    IsPublic = false,
                    IsInside = true,
                    TenantAllowed = true,
                    DisplayOrder = labOrder++,
                    Children = new List<FrameworkMenu>()
                });
                Set<FrameworkMenu>().Add(labFolder);
                Set<FunctionPrivilege>().AddRange(labFolder.FlatTree().Select(x => new FunctionPrivilege { RoleCode = "001", MenuItemId = x.ID, Allowed = true }));

                // rows for the data table fixture: several pages, a sortable number, some names that need encoding
                var cats = new[] { GadgetCategory.Tool, GadgetCategory.Toy, GadgetCategory.Appliance };
                for (var i = 1; i <= 35; i++)
                {
                    Set<LabItem>().Add(new LabItem
                    {
                        Name = i % 5 == 0 ? $"Special {i:00} {LabText.SpecialItem}" : $"Lab item {i:00}",
                        Category = cats[i % cats.Length],
                        Score = (i * 7) % 31,
                        Active = i % 3 != 0,
                        Created = new DateTime(2024, 1, 1).AddDays(i)
                    });
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

        /// <summary>
        /// Accounts of the permission matrix tests (#273), only seeded when the test run supplies their passwords: a signed-in user with no
        /// role, a user whose role has a few Gadget privileges (see E2ESeed.LimitedRoleActions), and a second administrator, so that the
        /// matrix requests never touch the session of the administrator the browser tests use.
        /// </summary>
        private void SeedMatrixAccounts()
        {
            if (string.IsNullOrEmpty(E2ESeed.NoRolePassword) == false)
            {
                Set<FrameworkUser>().Add(new FrameworkUser { ITCode = E2ESeed.NoRoleAccount, Password = PasswordHasher.Default.Hash(E2ESeed.NoRolePassword), IsValid = true, Name = "E2E No Role" });
            }
            if (string.IsNullOrEmpty(E2ESeed.LimitedPassword) == false)
            {
                Set<FrameworkRole>().Add(new FrameworkRole { RoleCode = E2ESeed.LimitedRoleCode, RoleName = "E2E Limited" });
                Set<FrameworkUser>().Add(new FrameworkUser { ITCode = E2ESeed.LimitedAccount, Password = PasswordHasher.Default.Hash(E2ESeed.LimitedPassword), IsValid = true, Name = "E2E Limited" });
                Set<FrameworkUserRole>().Add(new FrameworkUserRole { UserCode = E2ESeed.LimitedAccount, RoleCode = E2ESeed.LimitedRoleCode });
            }
            if (string.IsNullOrEmpty(E2ESeed.MatrixAdminPassword) == false)
            {
                Set<FrameworkUser>().Add(new FrameworkUser { ITCode = E2ESeed.MatrixAdminAccount, Password = PasswordHasher.Default.Hash(E2ESeed.MatrixAdminPassword), IsValid = true, Name = "E2E Matrix Admin" });
                Set<FrameworkUserRole>().Add(new FrameworkUserRole { UserCode = E2ESeed.MatrixAdminAccount, RoleCode = "001" });
            }
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

        // permission matrix accounts (#273); the passwords come from E2E:Matrix*Password and are generated for every run
        public const string NoRoleAccount = "e2enorole";
        public const string LimitedAccount = "e2elimited";
        public const string MatrixAdminAccount = "e2ematrixadmin";

        /// <summary>Role code of the limited role (the code must be digits).</summary>
        public const string LimitedRoleCode = "900";

        /// <summary>Gadget actions the limited role may use besides the list page itself: read-only ones.</summary>
        public static readonly string[] LimitedRoleActions = { "Search", "Details", "ExportExcel" };

        public static string NoRolePassword { get; set; }
        public static string LimitedPassword { get; set; }
        public static string MatrixAdminPassword { get; set; }
    }
}
