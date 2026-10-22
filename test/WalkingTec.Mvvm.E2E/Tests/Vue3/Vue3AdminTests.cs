using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages.Vue3;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests.Vue3
{
    [TestClass]
    [TestCategory("vue3demo")]
    public class Vue3AdminTests : Vue3Test
    {
        [TestMethod]
        public async Task Group_CreateEditDetailDelete_RoundTrip()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/frameworkgroup");
            await list.OpenAsync(App);
            var code = UniqueDigits(10);
            var name = UniqueName("Group ");

            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogField("GroupCode").FillAsync(code);
            await list.DialogField("GroupName").FillAsync(name);
            await list.SubmitDialogAsync();
            await list.SearchAsync("GroupCode", code);
            await Expect(list.Row(code)).ToContainTextAsync(name);

            await list.RowButton(code, "Edit").ClickAsync();
            await Expect(list.DialogField("GroupName")).ToHaveValueAsync(name);
            await list.DialogField("Remark").FillAsync("changed by e2e");
            await list.SubmitDialogAsync();

            await list.RowButton(code, "Detail").ClickAsync();
            await Expect(list.DialogField("GroupName")).ToHaveValueAsync(name);
            await Expect(list.DialogField("Remark")).ToHaveValueAsync("changed by e2e");
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            await list.DeleteRowAsync(code);
        }

        [TestMethod]
        public async Task Role_CreateEditDetailPrivilegeDelete_RoundTrip()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/frameworkrole");
            await list.OpenAsync(App);
            var code = UniqueDigits(8);
            var name = UniqueName("Role ");

            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogField("RoleCode").FillAsync(code);
            await list.DialogField("RoleName").FillAsync(name);
            await list.SubmitDialogAsync();
            await list.SearchAsync("RoleCode", code);
            await Expect(list.Row(code)).ToContainTextAsync(name);

            await list.RowButton(code, "Edit").ClickAsync();
            await Expect(list.DialogField("RoleName")).ToHaveValueAsync(name);
            await list.DialogField("RoleRemark").FillAsync("role remark");
            await list.SubmitDialogAsync();
            await Expect(list.Row(code)).ToContainTextAsync("role remark");

            // page and action privileges of the role: grant every action of the Cities page
            await list.RowButton(code, "Privilege").ClickAsync();
            var pageRow = list.Dialog.Locator("tr.el-table__row").Filter(new() { HasTextString = "Cities" });
            await pageRow.GetByText("All", new() { Exact = true }).ClickAsync();
            await list.DialogButton("Submit").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();
            await list.RowButton(code, "Privilege").ClickAsync();
            await Expect(list.Dialog.Locator("tr.el-table__row").Filter(new() { HasTextString = "Cities" }).Locator(".el-checkbox.is-checked")).Not.ToHaveCountAsync(0);
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            await list.RowButton(code, "Detail").ClickAsync();
            await Expect(list.DialogField("RoleCode")).ToHaveValueAsync(code);
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            await list.DeleteRowAsync(code);
        }

        // A 1x1 PNG: the upload goes through the image checks of the demo (ImageDecodeGuard), so it has to be a real image.
        private static readonly byte[] Png = System.Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        [TestMethod]
        public async Task User_List_ShowsRoleGroupAndPhoto()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var groupCode = UniqueDigits(8);
            var groupName = UniqueName("Team ");
            await api.PostAsync("/api/_FrameworkGroup/Add", new { Entity = new { GroupCode = groupCode, GroupName = groupName } });
            var account = UniqueName("u").ToLowerInvariant();
            var photoId = await api.UploadAsync("photo.png", "image/png", Png);
            await api.PostAsync("/api/_FrameworkUser/Add", new
            {
                Entity = new { ITCode = account, Password = "Pw-" + UniqueName(""), Name = "Listed User", PhotoId = photoId },
                SelectedRolesCodes = new[] { "002" },
                SelectedGroupCodes = new[] { groupCode }
            });
            var list = new ListPage(Page, "/_admin/frameworkuser");

            await list.OpenAsync(App);

            await Expect(list.Row(account)).ToContainTextAsync("Listed User");
            await Expect(list.Row(account)).ToContainTextAsync(groupName);
            await Expect(list.Row(account).Locator("img.el-image__inner")).ToBeVisibleAsync();
            await DeleteUserAsync(api, account);
            await api.PostAsync("/api/_FrameworkGroup/BatchDelete", new[] { await GroupIdAsync(api, groupCode) });
        }

        [TestMethod]
        [Ignore("#290: the user management page calls /api/_Admin/FrameworkUser/*, which is not the route of the user API (/api/_FrameworkUser/*), so create, edit, delete, export and import fail from the page")]
        public async Task User_CreateWithPhotoEditDelete_RoundTrip()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var groupCode = UniqueDigits(8);
            var groupName = UniqueName("Team ");
            await api.PostAsync("/api/_FrameworkGroup/Add", new { Entity = new { GroupCode = groupCode, GroupName = groupName } });
            var list = new ListPage(Page, "/_admin/frameworkuser");
            await list.OpenAsync(App);
            var account = UniqueName("u").ToLowerInvariant();
            var name = UniqueName("User ");

            // create: account, password, name, role, group and a photo
            await list.ToolbarButton("新增").ClickAsync();
            await list.Dialog.Locator("input[type=file]").First.SetInputFilesAsync(new FilePayload { Name = "photo.png", MimeType = "image/png", Buffer = Png });
            await Expect(list.Dialog.Locator(".el-upload-list__item-thumbnail")).ToHaveCountAsync(1);
            await list.Dialog.Locator("input.el-input__inner").First.FillAsync(account);
            await list.Dialog.Locator("input[type=password]").FillAsync("Pw-" + UniqueName(""));
            await list.Dialog.GetByLabel("姓名").FillAsync(name);
            var roleBox = list.Dialog.Locator("label.el-checkbox").Filter(new() { HasTextString = "用户" });
            await roleBox.ClickAsync();
            await Expect(roleBox).ToHaveClassAsync(new Regex("is-checked"));
            await list.Dialog.Locator(".el-cascader").ClickAsync();
            await Page.Locator(".el-cascader-node").Filter(new() { HasTextString = groupName }).Locator(".el-checkbox").ClickAsync();
            await Page.Keyboard.PressAsync("Escape");
            await list.Dialog.GetByRole(AriaRole.Button, new() { Name = "提交" }).ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            await Expect(list.Row(account)).ToContainTextAsync(name);
            await Expect(list.Row(account)).ToContainTextAsync(groupName);
            await Expect(list.Row(account).Locator("img.el-image__inner")).ToBeVisibleAsync();

            // edit
            await list.RowButton(account, "修改").ClickAsync();
            await Expect(list.Dialog.GetByLabel("姓名")).ToHaveValueAsync(name);
            await list.Dialog.GetByLabel("姓名").FillAsync(name + " 2");
            await list.Dialog.GetByRole(AriaRole.Button, new() { Name = "提交" }).ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();
            await Expect(list.Row(account)).ToContainTextAsync(name + " 2");

            // delete
            await RowDeleteAsync(list, account, "删除");
            await api.PostAsync("/api/_FrameworkGroup/BatchDelete", new[] { await GroupIdAsync(api, groupCode) });
        }

        private static async Task<string> GroupIdAsync(Vue3Api api, string code)
        {
            var found = await api.PostAsync("/api/_FrameworkGroup/Search", new { GroupCode = code });
            return found.GetProperty("Data")[0].GetProperty("ID").GetString();
        }

        /// <summary>The user list is not localized: its buttons are Chinese.</summary>
        private async Task RowDeleteAsync(ListPage list, string rowText, string buttonText)
        {
            await ListPage.ButtonIn(list.Row(rowText), buttonText).ClickAsync();
            await Page.Locator(".el-popconfirm:visible .el-button--primary").ClickAsync();
            await Expect(list.Row(rowText)).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Group_ChildGroupAndValidation()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var parentCode = UniqueDigits(8);
            var parentName = UniqueName("Parent ");
            await api.PostAsync("/api/_FrameworkGroup/Add", new { Entity = new { GroupCode = parentCode, GroupName = parentName } });
            var list = new ListPage(Page, "/_admin/frameworkgroup");
            await list.OpenAsync(App);

            // the code must be digits: the server answers with a field error and the dialog stays open
            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogField("GroupCode").FillAsync("not-digits");
            await list.DialogField("GroupName").FillAsync("x");
            await list.DialogButton("Submit").ClickAsync();
            await Expect(list.Dialog.Locator(".el-form-item__error")).Not.ToHaveCountAsync(0);
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            // a child group under the parent
            var childCode = UniqueDigits(8);
            var childName = UniqueName("Child ");
            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogField("GroupCode").FillAsync(childCode);
            await list.DialogField("GroupName").FillAsync(childName);
            await list.Dialog.Locator(".el-cascader").ClickAsync();
            await Page.Locator(".el-cascader-node").Filter(new() { HasTextString = parentName }).ClickAsync();
            await list.SubmitDialogAsync();
            // the list is a tree: the child is listed together with its parent (searching for the child alone, see the ignored test below)
            await Expect(list.Row(childCode)).ToContainTextAsync(childName);
            await Expect(list.Row(parentCode)).ToBeVisibleAsync();

            await list.DeleteRowAsync(childCode);
            await list.DeleteRowAsync(parentCode);
        }

        [TestMethod]
        [Ignore("#291: a search that matches only a child group returns an empty list, the tree is built from the matching rows only")]
        public async Task Group_SearchByChildCode_FindsTheChild()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var parentCode = UniqueDigits(8);
            var childCode = UniqueDigits(8);
            await api.PostAsync("/api/_FrameworkGroup/Add", new { Entity = new { GroupCode = parentCode, GroupName = "p" } });
            var parentId = await GroupIdAsync(api, parentCode);
            await api.PostAsync("/api/_FrameworkGroup/Add", new { Entity = new { GroupCode = childCode, GroupName = "c", ParentId = parentId } });
            var list = new ListPage(Page, "/_admin/frameworkgroup");
            await list.OpenAsync(App);

            await list.SearchAsync("GroupCode", childCode);

            await Expect(list.Row(childCode)).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task Group_BatchDeleteAndExport()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var prefix = UniqueName("Batch ");
            var codes = new[] { UniqueDigits(8), UniqueDigits(8) };
            for (var i = 0; i < codes.Length; i++)
            {
                await api.PostAsync("/api/_FrameworkGroup/Add", new { Entity = new { GroupCode = codes[i], GroupName = prefix + i } });
            }
            var list = new ListPage(Page, "/_admin/frameworkgroup");
            await list.OpenAsync(App);
            await list.SearchAsync("GroupName", prefix);
            await Expect(list.Rows).ToHaveCountAsync(2);

            // export the selection, then everything
            await list.CheckRowAsync(codes[0]);
            var selected = await Page.RunAndWaitForDownloadAsync(async () => await list.ToolbarButton("Export").ClickAsync());
            StringAssert.EndsWith(selected.SuggestedFilename, ".xlsx");
            var workbook = await ListPage.ReadDownloadAsync(selected);
            Assert.AreEqual("PK", System.Text.Encoding.ASCII.GetString(workbook, 0, 2));
            var sheet = Support.ExcelTemplate.SheetXml(workbook);
            StringAssert.Contains(sheet, prefix + "0");
            Assert.IsFalse(sheet.Contains(prefix + "1"), "Only the selected row is exported.");

            // batch delete the two rows
            await list.CheckRowAsync(codes[1]);
            await list.ToolbarButton("Delete").ClickAsync();
            await Expect(list.Rows).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Group_Import_CreatesRowsFromTemplate()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/frameworkgroup");
            await list.OpenAsync(App);
            var code = UniqueDigits(8);
            var name = UniqueName("Imported ");

            await list.ImportAsync("Import", new[] { code, name, "from import" });

            await list.SearchAsync("GroupCode", code);
            await Expect(list.Row(code)).ToContainTextAsync(name);
            await list.DeleteRowAsync(code);
        }

        [TestMethod]
        public async Task Group_Import_WithInvalidRow_ReportsErrors()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/frameworkgroup");
            await list.OpenAsync(App);

            // a code that is not made of digits is refused by the validation of the import
            await list.ToolbarButton("Import").ClickAsync();
            var download = await Page.RunAndWaitForDownloadAsync(async () => await ListPage.ButtonIn(list.Dialog, "Download template").ClickAsync());
            var template = await ListPage.ReadDownloadAsync(download);
            await list.Dialog.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
            {
                Name = "invalid.xlsx",
                MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Buffer = Support.ExcelTemplate.WithRows(template, new[] { "not-digits", "invalid row" })
            });
            await ListPage.ButtonIn(list.Dialog, "Submit").ClickAsync();

            await Expect(ListPage.ButtonIn(list.Dialog, "Import failed, please download error file for details")).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task Role_Import_CreatesRowsFromTemplate()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/frameworkrole");
            await list.OpenAsync(App);
            var code = UniqueDigits(8);
            var name = UniqueName("Imported role ");

            await list.ImportAsync("Import", new[] { code, name, "from import" });

            await list.SearchAsync("RoleCode", code);
            await Expect(list.Row(code)).ToContainTextAsync(name);
            await list.DeleteRowAsync(code);
        }

        [TestMethod]
        public async Task Menu_CreateEditDetailRefreshDelete_RoundTrip()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/frameworkmenu");
            await list.OpenAsync(App);
            var name = UniqueName("Link ");

            // a menu entry that points to an outside address needs no module
            await list.ToolbarButton("Add").ClickAsync();
            await list.Dialog.GetByText("Ourside Url").ClickAsync();
            await list.DialogField("Url").FillAsync("https://example.invalid/e2e");
            await list.DialogField("PageName").FillAsync(name);
            await list.DialogField("DisplayOrder").FillAsync("900");
            await list.SubmitDialogAsync();
            await Expect(list.Row(name)).ToBeVisibleAsync();

            await list.RowButton(name, "Edit").ClickAsync();
            await Expect(list.DialogField("PageName")).ToHaveValueAsync(name);
            await list.DialogField("PageName").FillAsync(name + " edited");
            await list.SubmitDialogAsync();
            await Expect(list.Row(name + " edited")).ToBeVisibleAsync();

            await list.RowButton(name + " edited", "Detail").ClickAsync();
            await Expect(list.DialogField("PageName")).ToHaveValueAsync(name + " edited");
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            await list.ToolbarButton("Refresh").ClickAsync();
            await Expect(list.Toast).ToBeVisibleAsync();

            await list.DeleteRowAsync(name + " edited");
        }

        [TestMethod]
        public async Task Tenant_CreateEditDetailDelete_AndSwitch()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/frameworktenant");
            await list.OpenAsync(App);
            var code = UniqueDigits(4);
            var name = UniqueName("Tenant ");

            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogField("TenantCode").FillAsync(code);
            await list.DialogField("TenantName").FillAsync(name);
            await ChooseAdminRoleAsync(list);
            await list.SubmitDialogAsync();
            await list.SearchAsync("TenantCode", code);
            await Expect(list.Row(code)).ToBeVisibleAsync();

            // (the name column of the list is empty, see the ignored test below, so the name is checked in the dialogs)
            await list.RowButton(code, "Edit").ClickAsync();
            await Expect(list.DialogField("TenantName")).ToHaveValueAsync(name);
            await list.DialogField("TenantName").FillAsync(name + " 2");
            await ChooseAdminRoleAsync(list); // the edit form asks for the role again
            await list.SubmitDialogAsync();

            await list.RowButton(code, "Detail").ClickAsync();
            await Expect(list.DialogField("TenantCode")).ToHaveValueAsync(code);
            await Expect(list.DialogField("TenantName")).ToHaveValueAsync(name + " 2");
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            await list.DeleteRowAsync(code);
        }

        private async Task ChooseAdminRoleAsync(ListPage list)
        {
            await list.DialogItem("AdminRoleCode").Locator(".el-select").ClickAsync();
            await Page.Locator(".el-select-dropdown__item:visible").First.ClickAsync();
        }

        [TestMethod]
        [Ignore("#290: the tenant list reads the name from a property the list does not return, so its name column is empty")]
        public async Task Tenant_List_ShowsTenantName()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var code = UniqueDigits(4);
            var name = UniqueName("Tenant ");
            await api.PostAsync("/api/_FrameworkTenant/Add", new { Entity = new { TCode = code, TName = name, Enabled = true, EnableSub = false }, AdminRoleCode = "001" });
            var list = new ListPage(Page, "/_admin/frameworktenant");
            await list.OpenAsync(App);

            await list.SearchAsync("TenantCode", code);

            await Expect(list.Row(code)).ToContainTextAsync(name);
        }

        [TestMethod]
        public async Task ActionLog_SearchByAccount_ShowsOwnRequestsAndDetail()
        {
            await SignInAsync();
            var api = await ApiAsync();
            await api.PostAsync("/api/_FrameworkGroup/Search", new { GroupCode = "does-not-exist" });
            var list = new ListPage(Page, "/_admin/actionlog");
            await list.OpenAsync(App);

            await list.SearchAsync("ITCode", Site.AdminAccount);
            await Expect(list.Rows.First).ToBeVisibleAsync();
            await Expect(list.Rows.First).ToContainTextAsync(Site.AdminAccount);

            await list.RowButton(Site.AdminAccount, "Detail").First.ClickAsync();
            await Expect(list.Dialog).ToBeVisibleAsync();
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();
        }

        [TestMethod]
        public async Task DataPrivilege_AddEditDetailDelete_ForGroup()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var groupCode = UniqueDigits(8);
            var groupName = UniqueName("DpGroup ");
            await api.PostAsync("/api/_FrameworkGroup/Add", new { Entity = new { GroupCode = groupCode, GroupName = groupName } });
            var cityA = UniqueName("CityA ");
            var cityB = UniqueName("CityB ");
            await api.PostAsync("/api/City/Add", new { Entity = new { Name = cityA, Level = 1 } });
            await api.PostAsync("/api/City/Add", new { Entity = new { Name = cityB, Level = 1 } });
            var list = new ListPage(Page, "/_admin/dataprivilege");
            await list.OpenAsync(App);

            // the page lists the privileges of groups by default
            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogItem("Group").Locator(".el-cascader").ClickAsync();
            await Page.Locator(".el-cascader-node").Filter(new() { HasTextString = groupName }).ClickAsync();
            await Page.Keyboard.PressAsync("Escape");
            await ChooseOptionAsync(list.DialogItem("Name").Locator(".el-select"), null);
            await ChooseOptionAsync(list.DialogItem("Data").Locator(".el-select"), cityA);
            await list.SubmitDialogAsync();
            await Expect(list.Row(groupName)).ToBeVisibleAsync();

            await list.RowButton(groupName, "Edit").ClickAsync();
            await ChooseOptionAsync(list.DialogItem("Data").Locator(".el-select"), cityB);
            await list.SubmitDialogAsync();

            // the edit dialog shows both cities (the detail button is hidden, see the ignored test below)
            await list.RowButton(groupName, "Edit").ClickAsync();
            await Expect(list.DialogItem("Data")).ToContainTextAsync(cityA);
            await Expect(list.DialogItem("Data")).ToContainTextAsync(cityB);
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            await list.DeleteRowAsync(groupName);
            await api.PostAsync("/api/_FrameworkGroup/BatchDelete", new[] { await GroupIdAsync(api, groupCode) });
            await DeleteCitiesAsync(api, cityA, cityB);
        }

        [TestMethod]
        [Ignore("#290: the detail button of the data privilege list checks a permission key that no action has, so it is never shown")]
        public async Task DataPrivilege_Detail_IsReachableFromList()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var groupCode = UniqueDigits(8);
            var groupName = UniqueName("DpGroup ");
            await api.PostAsync("/api/_FrameworkGroup/Add", new { Entity = new { GroupCode = groupCode, GroupName = groupName } });
            var list = new ListPage(Page, "/_admin/dataprivilege");
            await list.OpenAsync(App);
            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogItem("Group").Locator(".el-cascader").ClickAsync();
            await Page.Locator(".el-cascader-node").Filter(new() { HasTextString = groupName }).ClickAsync();
            await Page.Keyboard.PressAsync("Escape");
            await ChooseOptionAsync(list.DialogItem("Name").Locator(".el-select"), null);
            await list.DialogItem("All").Locator(".el-select").ClickAsync();
            await Page.Locator(".el-select-dropdown__item:visible").Filter(new() { HasTextString = "Yes" }).ClickAsync();
            await list.SubmitDialogAsync();

            await Expect(list.RowButton(groupName, "Detail")).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task DataPrivilege_AddAndDelete_ForUser()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var account = UniqueName("dp").ToLowerInvariant();
            await CreateUserAsync(api, account, "DP User");
            var city = UniqueName("City ");
            await api.PostAsync("/api/City/Add", new { Entity = new { Name = city, Level = 1 } });
            var list = new ListPage(Page, "/_admin/dataprivilege");
            await list.OpenAsync(App);

            // switch the page to user privileges, then add one for a user
            await list.SearchFieldAsync("Type");
            await list.SearchItem("Type").Locator(".el-select").ClickAsync();
            await Page.Locator(".el-select-dropdown__item:visible").Filter(new() { HasTextString = "User", HasNotTextString = "Group" }).ClickAsync();
            await list.ClickSearchAsync();
            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogField("Account").FillAsync(account);
            await ChooseOptionAsync(list.DialogItem("Name").Locator(".el-select"), null);
            await ChooseOptionAsync(list.DialogItem("Data").Locator(".el-select"), city);
            await list.SubmitDialogAsync();
            await Expect(list.Row(account)).ToBeVisibleAsync();

            await list.DeleteRowAsync(account);
            await DeleteUserAsync(api, account);
            await DeleteCitiesAsync(api, city);
        }

        /// <summary>Opens an Element Plus select and picks the option with this text (the first one when the text is null).</summary>
        private async Task ChooseOptionAsync(ILocator select, string optionText)
        {
            await select.ClickAsync();
            var option = Page.Locator(".el-select-dropdown__item:visible");
            if (optionText != null)
            {
                option = option.Filter(new() { HasTextString = optionText });
            }
            await option.First.ClickAsync();
            await Page.Keyboard.PressAsync("Escape");
        }

        private static async Task CreateUserAsync(Vue3Api api, string account, string name)
        {
            await api.PostAsync("/api/_FrameworkUser/Add", new
            {
                Entity = new { ITCode = account, Password = "Pw-" + System.Guid.NewGuid().ToString("N"), Name = name },
                SelectedRolesCodes = new[] { "002" },
                SelectedGroupCodes = new string[0]
            });
        }

        private static async Task DeleteUserAsync(Vue3Api api, string account)
        {
            var found = await api.PostAsync("/api/_FrameworkUser/Search", new { ITCode = account });
            foreach (var row in found.GetProperty("Data").EnumerateArray())
            {
                if (row.GetProperty("ITCode").GetString() == account)
                {
                    await api.PostAsync("/api/_FrameworkUser/BatchDelete", new[] { row.GetProperty("ID").GetString() });
                }
            }
        }

        private static async Task DeleteCitiesAsync(Vue3Api api, params string[] names)
        {
            foreach (var name in names)
            {
                var found = await api.PostAsync("/api/City/Search", new { Name = name });
                var ids = found.GetProperty("Data").EnumerateArray().Select(x => x.GetProperty("ID").GetString()).ToArray();
                if (ids.Length > 0)
                {
                    await api.PostAsync("/api/City/BatchDelete", ids);
                }
            }
        }

        [TestMethod]
        public async Task Tenant_SwitchFromHeader_ShowsTheTenantAndSwitchesBack()
        {
            var api = await ApiAsync();
            await Vue3Seed.EnsureAsync(api);
            var code = UniqueDigits(4);
            var name = UniqueName("Tenant ");
            await api.PostAsync("/api/_FrameworkTenant/Add", new { Entity = new { TCode = code, TName = name, Enabled = true, EnableSub = false }, AdminRoleCode = "001" });
            await SignInAsync();
            var tenantMenu = Page.Locator(".layout-navbars-breadcrumb-user .el-dropdown").Filter(new() { HasTextString = "MainSite" });

            // the header lists the main site and the tenants; choosing a tenant reloads the app inside that tenant
            await tenantMenu.HoverAsync();
            await Page.Locator(".el-dropdown-menu__item").Filter(new() { HasTextString = name }).DispatchEventAsync("click");
            await Expect(Page.Locator(".layout-navbars-breadcrumb-user .el-dropdown").Filter(new() { HasTextString = name })).ToBeVisibleAsync();

            // the tenant is part of the session of the user: switch back through the API (a tenant without sub tenants lists no other site) and check it
            Assert.AreEqual(code, (await api.GetAsync("/api/_account/checkuserinfo")).GetProperty("CurrentTenant").GetString());
            await api.GetAsync("/api/_account/SetTenant?tenant=");
            Assert.IsFalse((await api.GetAsync("/api/_account/checkuserinfo")).TryGetProperty("CurrentTenant", out var current) && current.ValueKind != JsonValueKind.Null);

            await api.PostAsync("/api/_FrameworkTenant/BatchDelete", Ids(await api.PostAsync("/api/_FrameworkTenant/Search", new { TCode = code })));
        }

        // One list per test run: the browser limits repeated automatic downloads of one page, so each export starts from its own page.
        [TestMethod]
        [DataRow("/_admin/frameworkrole")]
        [DataRow("/_admin/frameworkmenu")]
        [DataRow("/_admin/frameworktenant")]
        [DataRow("/_admin/frameworkgroup")]
        public async Task List_Export_DownloadsAWorkbook(string route)
        {
            await SignInAsync();
            var list = new ListPage(Page, route);
            await list.OpenAsync(App);

            var download = await Page.RunAndWaitForDownloadAsync(async () => await list.ToolbarButton("Export").ClickAsync());

            StringAssert.EndsWith(download.SuggestedFilename, ".xlsx", route);
            var workbook = await ListPage.ReadDownloadAsync(download);
            Assert.AreEqual("PK", System.Text.Encoding.ASCII.GetString(workbook, 0, 2), route);
        }
    }
}
