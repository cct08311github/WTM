using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// Smoke tests of the LayUI front end: sign in, open menu pages, one create / edit / details / delete round,
    /// batch delete and export. Each test creates the rows it needs under a unique name.
    /// </summary>
    [TestClass]
    public class SmokeTests : E2ETest
    {
        private static string UniqueName(string prefix) => prefix + " " + Guid.NewGuid().ToString("N").Substring(0, 8);

        private async Task SignInAsync()
        {
            await new LoginPage(Page).LoginAsync(Site.AdminAccount, Site.AdminPassword);
        }

        [TestMethod]
        public async Task SignIn_WithValidCredentials_ShowsHomeFrameWithMenu()
        {
            await SignInAsync();
            var frame = new HomeFrame(Page);

            await Expect(frame.UserName).ToHaveTextAsync("E2E Admin");
            await Expect(frame.MenuLink("Inventory")).ToBeVisibleAsync();
            await Expect(Page.Locator("#frontpage")).ToContainTextAsync("Signed in as E2E Admin");
        }

        [TestMethod]
        public async Task SignIn_WithWrongPassword_StaysOnLoginPageWithError()
        {
            var login = new LoginPage(Page);
            await login.GotoAsync();
            await login.SubmitAsync(Site.AdminAccount, "not-the-password");

            await Expect(login.Error).Not.ToBeEmptyAsync();
            await Expect(Page).ToHaveURLAsync(new Regex("/Login/Login"));
        }

        [TestMethod]
        public async Task ProtectedPage_WithoutSession_RedirectsToLogin()
        {
            await Page.GotoAsync("/Gadget/Index");

            await Expect(Page).ToHaveURLAsync(new Regex("/Login/Login"));
            await Expect(Page.Locator("#login-submit")).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task MenuPage_Opens_ListsSeededRows()
        {
            await SignInAsync();
            var list = new GadgetListPage(Page);
            await list.OpenAsync();

            await Expect(list.Row("Alpha Wrench")).ToContainTextAsync("G-100");
            await Expect(list.Row("Epsilon Kite")).ToContainTextAsync("Toy");
            await Expect(new HomeFrame(Page).Tabs).ToHaveCountAsync(2);
        }

        [TestMethod]
        public async Task Search_ByName_FiltersTheGrid()
        {
            await SignInAsync();
            var list = new GadgetListPage(Page);
            await list.OpenAsync();

            await list.SearchByNameAsync("Gamma");

            await Expect(list.Rows).ToHaveCountAsync(1);
            await Expect(list.Row("Gamma Blender")).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task Gadget_CreateEditDetailsDelete_RoundTrip()
        {
            await SignInAsync();
            var list = new GadgetListPage(Page);
            await list.OpenAsync();
            var name = UniqueName("Round");

            // create
            await list.CreateAsync(name, "R-001", "12.5");
            await list.SearchByNameAsync(name);
            await Expect(list.Row(name)).ToContainTextAsync("R-001");
            await Expect(list.Row(name)).ToContainTextAsync("12.5");

            // edit
            await list.RowButton(name, "Edit").ClickAsync();
            await Expect(list.DialogField("Name")).ToHaveValueAsync(name);
            await list.DialogField("Price").FillAsync("99");
            await list.SubmitDialogAsync();
            await Expect(list.Row(name)).ToContainTextAsync("99");

            // details (read only dialog)
            await list.RowButton(name, "Details").ClickAsync();
            await Expect(list.Dialog).ToContainTextAsync(name);
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToHaveCountAsync(0);

            // delete
            await list.RowButton(name, "Delete").ClickAsync();
            await list.DialogButton("Delete").ClickAsync();
            await Expect(list.Dialog).ToHaveCountAsync(0);
            await list.SearchByNameAsync(name);
            await Expect(list.Rows).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Gadget_BatchDelete_RemovesSelectedRows()
        {
            await SignInAsync();
            var list = new GadgetListPage(Page);
            await list.OpenAsync();
            var prefix = UniqueName("Batch");
            await list.CreateAsync(prefix + " one", "B-001", "1");
            await list.CreateAsync(prefix + " two", "B-002", "2");

            await list.SearchByNameAsync(prefix);
            await Expect(list.Rows).ToHaveCountAsync(2);
            await list.CheckRowAsync(prefix + " one");
            await list.CheckRowAsync(prefix + " two");
            await list.ToolbarButton("Batch delete").ClickAsync();
            await list.DialogButton("Delete").ClickAsync();

            await list.AcceptAlertAsync("2 rows have been deleted");
            await Expect(list.Dialog).ToHaveCountAsync(0);
            await list.SearchByNameAsync(prefix);
            await Expect(list.Rows).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Gadget_Export_DownloadsWorkbook()
        {
            await SignInAsync();
            var list = new GadgetListPage(Page);
            await list.OpenAsync();

            var download = await Page.RunAndWaitForDownloadAsync(async () => await list.ToolbarButton("Export Excel").ClickAsync());

            var path = Path.Combine(E2ERun.OutputDir, "downloads", download.SuggestedFilename);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            await download.SaveAsAsync(path);
            var bytes = File.ReadAllBytes(path);
            Assert.IsTrue(bytes.Length > 1000, "The exported workbook is unexpectedly small.");
            Assert.AreEqual("PK", System.Text.Encoding.ASCII.GetString(bytes, 0, 2), "An .xlsx file is a zip container.");
            StringAssert.EndsWith(download.SuggestedFilename, ".xlsx");
        }

        [TestMethod]
        public async Task SignOut_ReturnsToLoginPage()
        {
            await SignInAsync();

            await new HomeFrame(Page).LogoutAsync();

            await Page.GotoAsync("/Gadget/Index");
            await Expect(Page).ToHaveURLAsync(new Regex("/Login/Login"));
        }
    }
}
