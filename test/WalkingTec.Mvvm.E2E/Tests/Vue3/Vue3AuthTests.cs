using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages.Vue3;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests.Vue3
{
    /// <summary>Sign in, sign out, protected routes, language switch and change of password of the Vue3Demo SPA.</summary>
    [TestClass]
    [TestCategory("vue3demo")]
    public class Vue3AuthTests : Vue3Test
    {
        [TestMethod]
        public async Task SignIn_WithValidCredentials_ShowsFrameWithUserNameAndMenu()
        {
            await SignInAsync();

            await Expect(App.UserMenu).ToContainTextAsync("Admin");
            await Expect(Page).ToHaveURLAsync(new Regex("#/home"));
            await Expect(App.SideMenu).ToContainTextAsync("System");
            await Expect(App.SideMenu).ToContainTextAsync("Cities");
        }

        [TestMethod]
        public async Task SignIn_WithWrongPassword_ShowsErrorAndStaysOnLogin()
        {
            await App.OpenLoginAsync();

            await App.SubmitLoginAsync(Site.AdminAccount, "not-the-password");

            await Expect(Page.Locator(".el-message--error")).ToBeVisibleAsync();
            await Expect(App.LoginButton).ToBeVisibleAsync();
            await Expect(Page).ToHaveURLAsync(new Regex("#/login"));
        }

        [TestMethod]
        public async Task SignIn_WithUnknownTenantCode_IsRefused()
        {
            await App.OpenLoginAsync();

            await App.SubmitLoginAsync(Site.AdminAccount, Site.AdminPassword, "zz99");

            await Expect(Page.Locator(".el-message--error")).ToBeVisibleAsync();
            await Expect(App.LoginButton).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task ProtectedRoute_WithoutSession_RedirectsToLogin()
        {
            await Page.GotoAsync("/#/_admin/frameworkgroup");

            await Expect(App.LoginButton).ToBeVisibleAsync();
            await Expect(Page).ToHaveURLAsync(new Regex("#/login"));
        }

        [TestMethod]
        public async Task SignOut_ReturnsToLoginAndEndsTheSession()
        {
            await SignInAsync();

            await App.LogoutAsync();

            await Expect(Page).ToHaveURLAsync(new Regex("#/login"));
            Assert.IsNull(await Page.EvaluateAsync<string>("() => localStorage.getItem('wtm-vue:token')"));
            await Page.GotoAsync("/#/_admin/frameworkgroup");
            await Expect(App.LoginButton).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task LanguageSwitch_ChangesLabelsAndPersistsAcrossReload()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/frameworkgroup");
            await list.OpenAsync(App);
            await Expect(list.ToolbarButton("Add")).ToBeVisibleAsync();

            await App.LanguageMenu.ClickAsync();
            await Page.Locator(".el-dropdown-menu__item").Filter(new() { HasTextString = "简体中文" }).DispatchEventAsync("click");

            // the app reloads with the new language: the labels of the page are Chinese now
            await Expect(ListPage.ButtonIn(list.Toolbar, "新增")).ToBeVisibleAsync();
            await Page.ReloadAsync();
            await Expect(ListPage.ButtonIn(list.Toolbar, "新增")).ToBeVisibleAsync();

            await App.LanguageMenu.ClickAsync();
            await Page.Locator(".el-dropdown-menu__item").Filter(new() { HasTextString = "English" }).DispatchEventAsync("click");
            await Expect(ListPage.ButtonIn(list.Toolbar, "Add")).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task ChangePassword_FromHeader_ReplacesThePassword()
        {
            var api = await ApiAsync();
            await Vue3Seed.EnsureAsync(api);
            var account = UniqueName("pw").ToLowerInvariant();
            var oldPassword = "Old-" + UniqueName("");
            var newPassword = "New-" + UniqueName("");
            // a second administrator, so that the administrator of the run keeps its password
            await api.PostAsync("/api/_FrameworkUser/Add", new
            {
                Entity = new { ITCode = account, Password = oldPassword, Name = "Password User" },
                SelectedRolesCodes = new[] { "001" },
                SelectedGroupCodes = new string[0]
            });
            await App.LoginAsync(account, oldPassword, "Password User");

            await App.ChooseUserMenuAsync("Change password");
            var dialog = Page.Locator(".el-dialog:visible");
            var passwords = dialog.Locator("input[type=password]");
            await passwords.Nth(0).FillAsync(oldPassword);
            await passwords.Nth(1).FillAsync(newPassword);
            await passwords.Nth(2).FillAsync(newPassword);
            await ListPage.ButtonIn(dialog, "Submit").ClickAsync();
            await Expect(dialog).ToBeHiddenAsync();

            // the new password signs in, the old one does not
            var anonymous = await Playwright.APIRequest.NewContextAsync(new() { BaseURL = Site.BaseUri.ToString() });
            Assert.IsFalse(string.IsNullOrEmpty(await Vue3Api.LoginAsync(anonymous, account, newPassword)));
            var refused = await anonymous.PostAsync("/api/_account/loginjwt", new() { DataObject = new { Account = account, Password = oldPassword } });
            Assert.AreEqual(400, refused.Status);
            await anonymous.DisposeAsync();

            var found = await api.PostAsync("/api/_FrameworkUser/Search", new { ITCode = account });
            foreach (var row in found.GetProperty("Data").EnumerateArray())
            {
                await api.PostAsync("/api/_FrameworkUser/BatchDelete", new[] { row.GetProperty("ID").GetString() });
            }
        }
    }
}
