using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages.Vue3;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests.Vue3
{
    /// <summary>
    /// The sample modules of the demo (SchoolData and testvue areas): City, School and SchoolVue3 have a page in the SPA and are driven
    /// through it; Student and Major have none and are covered by <see cref="Vue3ApiTests"/>. These pages are generated from the code
    /// generator templates, so a broken template shows up here.
    /// </summary>
    [TestClass]
    [TestCategory("vue3demo")]
    public class Vue3SchoolDataTests : Vue3Test
    {
        // ---- City ----

        [TestMethod]
        public async Task City_CreateEditDetailDelete_RoundTrip()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/schooldata/city");
            await list.OpenAsync(App);
            var name = UniqueName("City ");

            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogField("名称").FillAsync(name);
            await list.DialogField("Level").FillAsync("2");
            await list.SubmitDialogAsync();
            await list.SearchAsync("名称", name);
            await Expect(list.Row(name)).ToContainTextAsync("2");

            await list.RowButton(name, "Edit").ClickAsync();
            await Expect(list.DialogField("名称")).ToHaveValueAsync(name);
            await list.DialogField("Level").FillAsync("5");
            await list.SubmitDialogAsync();
            await Expect(list.Row(name)).ToContainTextAsync("5");

            await list.RowButton(name, "Detail").ClickAsync();
            await Expect(list.DialogField("名称")).ToHaveValueAsync(name);
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            await list.DeleteRowAsync(name);
        }

        [TestMethod]
        public async Task City_BatchDelete_RemovesTheSelectedRows()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var prefix = UniqueName("Batch city ");
            for (var i = 0; i < 2; i++)
            {
                await api.PostAsync("/api/City/Add", new { Entity = new { Name = prefix + i, Level = 1 } });
            }
            var list = new ListPage(Page, "/schooldata/city");
            await list.OpenAsync(App);
            await list.SearchAsync("名称", prefix);
            await Expect(list.Rows).ToHaveCountAsync(2);

            await list.CheckRowAsync(prefix + "0");
            await list.CheckRowAsync(prefix + "1");
            await list.ToolbarButton("BatchDelete").ClickAsync();
            await Page.Locator(".el-popconfirm:visible .el-button--primary").ClickAsync();

            await Expect(list.Rows).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task City_Import_CreatesRowsFromTemplate()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/schooldata/city");
            await list.OpenAsync(App);
            var name = UniqueName("Imported city ");

            await list.ImportAsync("Import", new[] { name, "3", "" });

            await list.SearchAsync("名称", name);
            await Expect(list.Row(name)).ToContainTextAsync("3");
            await list.DeleteRowAsync(name);
        }

        [TestMethod]
        [Ignore("#290: the export button of the generated city page checks a permission key that no action has, so it is never shown")]
        public async Task City_Export_ButtonIsShown()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/schooldata/city");
            await list.OpenAsync(App);

            await Expect(list.ToolbarButton("Export")).ToBeVisibleAsync();
        }

        // ---- School (page of the _admin area, API /api/School) ----

        [TestMethod]
        public async Task School_CreateEditDetailDelete_RoundTrip()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/school");
            await list.OpenAsync(App);
            var code = UniqueDigits(3);
            var name = UniqueName("School ");

            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogField("编号").FillAsync(code);
            await list.DialogField("名称").FillAsync(name);
            await list.SubmitDialogAsync();
            await Expect(list.Row(name)).ToContainTextAsync(code);

            await list.RowButton(name, "Edit").ClickAsync();
            await Expect(list.DialogField("名称")).ToHaveValueAsync(name);
            await list.DialogField("名称").FillAsync(name + " 2");
            await list.SubmitDialogAsync();
            await Expect(list.Row(name + " 2")).ToContainTextAsync(code);

            await list.RowButton(name, "Detail").ClickAsync();
            await Expect(list.DialogField("名称")).ToHaveValueAsync(name + " 2");
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            await list.DeleteRowAsync(name);
        }

        [TestMethod]
        public async Task School_Export_DownloadsAllRows()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var name = UniqueName("School ");
            await api.PostAsync("/api/School/Add", new { Entity = new { SchoolCode = UniqueDigits(3), SchoolName = name, Level = 1 } });
            var list = new ListPage(Page, "/_admin/school");
            await list.OpenAsync(App);

            // exports everything (exporting selected rows is broken on this page, see the ignored test below)
            var download = await Page.RunAndWaitForDownloadAsync(async () => await list.ToolbarButton("Export").ClickAsync());

            StringAssert.Contains(Support.ExcelTemplate.SheetXml(await ListPage.ReadDownloadAsync(download)), name);
            await list.DeleteRowAsync(name);
        }

        [TestMethod]
        public async Task School_Import_CreatesRowsFromTemplate()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/school");
            await list.OpenAsync(App);
            var name = UniqueName("Imported school ");

            // code, name, type, remark, level, place
            await list.ImportAsync("Import", new[] { UniqueDigits(3), name, "", "", "1", "" });

            await Expect(list.Row(name)).ToBeVisibleAsync();
            await list.DeleteRowAsync(name);
        }

        [TestMethod]
        [Ignore("#290: exporting the selected rows on the school page does not download a file (the request for the selected rows does not ask for a binary response, so the page cannot read the file name)")]
        public async Task School_ExportSelectedRows_DownloadsAFile()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var name = UniqueName("School ");
            await api.PostAsync("/api/School/Add", new { Entity = new { SchoolCode = UniqueDigits(3), SchoolName = name, Level = 1 } });
            var list = new ListPage(Page, "/_admin/school");
            await list.OpenAsync(App);
            await list.CheckRowAsync(name);

            var download = await Page.RunAndWaitForDownloadAsync(async () => await list.ToolbarButton("Export").ClickAsync());

            StringAssert.Contains(Support.ExcelTemplate.SheetXml(await ListPage.ReadDownloadAsync(download)), name);
        }

        // ---- SchoolVue3 (area testvue): fields of several types and two kinds of file upload ----

        private static readonly byte[] Png = System.Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        [TestMethod]
        public async Task SchoolVue3_CreateWithFilesEditDetailDownloadDelete_RoundTrip()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var city = UniqueName("Place ");
            await api.PostAsync("/api/City/Add", new { Entity = new { Name = city, Level = 1 } });
            var list = new ListPage(Page, "/testvue/schoolvue3");
            await list.OpenAsync(App);
            var code = UniqueDigits(3);
            var name = UniqueName("School ");
            var attachment = "attachment-" + UniqueName("") + ".txt";

            await list.ToolbarButton("Add").ClickAsync();
            await list.DialogField("学校编码").FillAsync(code);
            await list.DialogField("学校名称").FillAsync(name);
            await ChooseAsync(list.DialogItem("学校类型").Locator(".el-select"), "公立学校");
            await list.DialogField("级别").FillAsync("3");
            await ChooseAsync(list.DialogItem("地点").Locator(".el-select"), city);
            await ChooseAsync(list.DialogItem("是学校").Locator(".el-select"), "Yes");
            // the first file input of the dialog is the photo, the second the attachment
            await list.Dialog.Locator("input[type=file]").Nth(0).SetInputFilesAsync(new FilePayload { Name = "photo.png", MimeType = "image/png", Buffer = Png });
            await Expect(list.Dialog.Locator(".el-upload--picture-card img.el-image__inner")).ToBeVisibleAsync();
            await list.Dialog.Locator("input[type=file]").Nth(1).SetInputFilesAsync(new FilePayload { Name = attachment, MimeType = "text/plain", Buffer = System.Text.Encoding.UTF8.GetBytes("attachment content") });
            await Expect(list.Dialog.Locator(".el-upload-list__item.is-success").Filter(new() { HasTextString = attachment })).ToBeVisibleAsync();
            await list.SubmitDialogAsync();

            await list.SearchAsync("学校名称", name);
            await Expect(list.Row(name)).ToContainTextAsync(code);
            await Expect(list.Row(name)).ToContainTextAsync(city);
            await Expect(list.Row(name).Locator("img.el-image__inner")).ToBeVisibleAsync();

            // the edit dialog shows the saved attachment, clicking its name downloads the file that was uploaded
            await list.RowButton(name, "Edit").ClickAsync();
            await Expect(list.DialogField("学校名称")).ToHaveValueAsync(name);
            var saved = list.Dialog.Locator(".el-upload-list__item-name").Filter(new() { HasTextString = attachment });
            var download = await Page.RunAndWaitForDownloadAsync(async () => await saved.ClickAsync());
            Assert.AreEqual(attachment, download.SuggestedFilename);
            Assert.AreEqual("attachment content", System.Text.Encoding.UTF8.GetString(await ListPage.ReadDownloadAsync(download)));
            await list.DialogField("学校名称").FillAsync(name + " 2");
            await list.SubmitDialogAsync();
            await Expect(list.Row(name + " 2")).ToBeVisibleAsync();

            await list.RowButton(name, "Detail").ClickAsync();
            await Expect(list.DialogField("学校名称")).ToHaveValueAsync(name + " 2");
            await Expect(list.Dialog.Locator(".el-upload-list__item-name").Filter(new() { HasTextString = attachment })).ToBeVisibleAsync();
            await list.DialogButton("Close").ClickAsync();
            await Expect(list.Dialog).ToBeHiddenAsync();

            await list.DeleteRowAsync(name);
            await DeleteCityAsync(api, city);
        }

        [TestMethod]
        [Ignore("#290: the export button of the generated SchoolVue3 page checks a permission key that no action has, so it is never shown")]
        public async Task SchoolVue3_Export_ButtonIsShown()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/testvue/schoolvue3");
            await list.OpenAsync(App);

            await Expect(list.ToolbarButton("Export")).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task SchoolVue3_Import_CreatesRowsFromTemplate()
        {
            await SignInAsync();
            var list = new ListPage(Page, "/testvue/schoolvue3");
            await list.OpenAsync(App);
            var code = UniqueDigits(3);
            var name = UniqueName("Imported school ");

            // code, name, type, remark, level, place, is school
            await list.ImportAsync("Import", new[] { code, name, "", "", "1", "", "Yes" });

            await list.SearchAsync("学校名称", name);
            await Expect(list.Row(name)).ToContainTextAsync(code);
            await list.DeleteRowAsync(name);
        }

        private async Task ChooseAsync(ILocator select, string optionText)
        {
            await select.ClickAsync();
            await Page.Locator(".el-select-dropdown__item:visible").Filter(new() { HasTextString = optionText }).First.ClickAsync();
        }

        private static async Task DeleteCityAsync(Vue3Api api, string name)
        {
            var found = await api.PostAsync("/api/City/Search", new { Name = name });
            var ids = found.GetProperty("Data").EnumerateArray().Select(x => x.GetProperty("ID").GetString()).ToArray();
            if (ids.Length > 0)
            {
                await api.PostAsync("/api/City/BatchDelete", ids);
            }
        }
    }
}
