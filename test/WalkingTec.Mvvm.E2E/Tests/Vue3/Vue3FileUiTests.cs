using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages.Vue3;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests.Vue3
{
    /// <summary>
    /// What the single page application does with files, in the browser: pictures that are shown in an <c>img</c> tag (which sends no credentials)
    /// load in the list, the dialogs and the navigation bar, removing a saved attachment and cancelling keeps the file, removing an upload of this
    /// session deletes it, and the picture of an editor survives a reload. The run mode (<c>E2E_FILE_ENDPOINTS_MODE</c>) makes the references
    /// necessary or not; the tests hold in both.
    /// </summary>
    [TestClass]
    [TestCategory("vue3demo")]
    public class Vue3FileUiTests : Vue3Test
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private static string WithRef(string url, string reference) => url + (url.Contains('?') ? "&" : "?") + "_DONOT_USE_FREF=" + Uri.EscapeDataString(reference);

        private async Task<(string schoolId, string name, string city, string cityId, string fileId)> SeedSchoolAsync(Vue3Api api, string photoId, string fileId)
        {
            await Vue3Seed.EnsureAsync(api);
            var city = UniqueName("Place ");
            await api.PostAsync("/api/City/Add", new { Entity = new { Name = city, Level = 1 } });
            var cityId = Ids(await api.PostAsync("/api/City/Search", new { Name = city })).Single();
            var name = UniqueName("School ");
            await api.PostAsync("/api/SchoolVue3/Add", new { Entity = new { SchoolCode = UniqueDigits(3), SchoolName = name, Level = 1, PlaceId = cityId, IsSchool = true, PhotoId = photoId, FileId = fileId } });
            var id = Ids(await api.PostAsync("/api/SchoolVue3/Search", new { SchoolName = name })).Single();
            return (id, name, city, cityId, fileId);
        }

        private static async Task CleanAsync(Vue3Api api, string schoolId, string cityId)
        {
            await api.PostAsync("/api/SchoolVue3/BatchDelete", new[] { schoolId });
            await api.PostAsync("/api/City/BatchDelete", new[] { cityId });
        }

        private async Task<bool> LoadsAsync(ILocator image)
        {
            await Expect(image).ToBeVisibleAsync();
            return await image.EvaluateAsync<bool>("img => new Promise(r => { if (img.complete) r(img.naturalWidth > 0); else { img.onload = () => r(img.naturalWidth > 0); img.onerror = () => r(false); } })");
        }

        [TestMethod]
        public async Task Pictures_LoadInTheListAndTheDialogs()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var photo = (await api.UploadFullAsync("shown.png", "image/png", Png)).GetProperty("Id").GetString();
            var seed = await SeedSchoolAsync(api, photo, null);
            var list = new ListPage(Page, "/testvue/schoolvue3");
            await list.OpenAsync(App);
            await list.SearchAsync("学校名称", seed.name);

            Assert.IsTrue(await LoadsAsync(list.Row(seed.name).Locator("img.el-image__inner")), "the thumbnail of the list");
            await list.RowButton(seed.name, "Edit").ClickAsync();
            Assert.IsTrue(await LoadsAsync(list.Dialog.Locator(".el-upload--picture-card img.el-image__inner")), "the picture of the edit dialog");
            await list.DialogButton("Close").ClickAsync();
            await CleanAsync(api, seed.schoolId, seed.cityId);
        }

        [TestMethod]
        public async Task RemovingASavedAttachment_AndCancelling_KeepsTheFile()
        {
            await SignInAsync();
            var api = await ApiAsync();
            var attachment = await api.UploadFullAsync("keep-" + UniqueName("") + ".txt", "text/plain", Encoding.UTF8.GetBytes("keep me"));
            var fileId = attachment.GetProperty("Id").GetString();
            var seed = await SeedSchoolAsync(api, null, fileId);
            var list = new ListPage(Page, "/testvue/schoolvue3");
            await list.OpenAsync(App);
            await list.SearchAsync("学校名称", seed.name);

            await list.RowButton(seed.name, "Edit").ClickAsync();
            var item = list.Dialog.Locator(".el-upload-list__item").First;
            await Expect(item).ToBeVisibleAsync();
            await item.HoverAsync();
            await item.Locator(".el-icon--close, .el-upload-list__item-delete").First.DispatchEventAsync("click");
            await Expect(list.Dialog.Locator(".el-upload-list__item")).ToHaveCountAsync(0);
            await list.DialogButton("Close").ClickAsync();

            // the record still points at the file and the file is still there
            var record = await api.GetAsync("/api/SchoolVue3/" + seed.schoolId);
            Assert.AreEqual(fileId, record.GetProperty("Entity").GetProperty("FileId").GetString());
            var reference = record.GetProperty("FileRefs").GetProperty(fileId).GetString();
            var download = await api.GetRawAsync(WithRef("/api/_file/DownloadFile/" + fileId, reference));
            Assert.AreEqual(200, download.Status);
            Assert.AreEqual("keep me", Encoding.UTF8.GetString(await download.BodyAsync()));
            await CleanAsync(api, seed.schoolId, seed.cityId);
        }

        [TestMethod]
        public async Task RemovingAnUploadOfThisSession_DeletesIt()
        {
            await SignInAsync();
            var api = await ApiAsync();
            await Vue3Seed.EnsureAsync(api);
            var list = new ListPage(Page, "/testvue/schoolvue3");
            await list.OpenAsync(App);
            await list.ToolbarButton("Add").ClickAsync();

            var response = await Page.RunAndWaitForResponseAsync(
                async () => await list.Dialog.Locator("input[type=file]").Nth(1).SetInputFilesAsync(new FilePayload { Name = "temp.txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("temporary") }),
                r => r.Url.Contains("/api/_file/Upload", StringComparison.OrdinalIgnoreCase) && r.Request.Method == "POST");
            using var body = JsonDocument.Parse(await response.TextAsync());
            var id = body.RootElement.GetProperty("Id").GetString();
            var capability = body.RootElement.GetProperty("Ref").GetString();
            var item = list.Dialog.Locator(".el-upload-list__item").Filter(new() { HasTextString = "temp.txt" });
            await Expect(item).ToBeVisibleAsync();
            Assert.AreEqual(200, (await api.GetRawAsync(WithRef("/api/_file/DownloadFile/" + id, capability))).Status);

            var deletion = Page.WaitForResponseAsync(r => r.Url.Contains("/api/_file/DeletedFile/", StringComparison.OrdinalIgnoreCase));
            await item.HoverAsync();
            await item.Locator(".el-icon--close, .el-upload-list__item-delete").First.DispatchEventAsync("click");
            Assert.AreEqual(200, (await deletion).Status);
            Assert.AreEqual(400, (await api.GetRawAsync(WithRef("/api/_file/DownloadFile/" + id, capability))).Status, "the upload was deleted");
            await list.DialogButton("Close").ClickAsync();
        }

        [TestMethod]
        public async Task TheAvatarInTheNavigationBar_Loads()
        {
            var api = await ApiAsync();
            await Vue3Seed.EnsureAsync(api);
            var photo = (await api.UploadFullAsync("me.png", "image/png", Png)).GetProperty("Id").GetString();
            var account = UniqueName("face").ToLowerInvariant();
            var password = "Pw-" + UniqueName("");
            await api.PostAsync("/api/_FrameworkUser/Add", new { Entity = new { ITCode = account, Password = password, Name = "Face User", PhotoId = photo }, SelectedRolesCodes = new[] { "002" }, SelectedGroupCodes = new string[0] });
            var userId = Ids(await api.PostAsync("/api/_FrameworkUser/Search", new { ITCode = account })).Single();
            try
            {
                await App.LoginAsync(account, password, "Face User");
                var avatar = Page.Locator("img.layout-navbars-breadcrumb-user-link-photo");
                StringAssert.Contains(await avatar.GetAttributeAsync("src"), "/api/_file/GetUserPhoto/");
                Assert.IsTrue(await LoadsAsync(avatar));
            }
            finally
            {
                await api.PostAsync("/api/_FrameworkUser/BatchDelete", new[] { userId });
            }
        }

        [TestMethod]
        public async Task ThePictureOfAnEditor_IsStoredByTheEditorUpload_AndStaysReachableAfterAReload()
        {
            // The demo's School model strips markup from its text fields, so the content of the editor cannot be saved through it; this checks
            // the part that matters for files: the upload of the editor, the address it puts into the content, and that the address keeps
            // working without any reference or credentials after a reload.
            await SignInAsync();
            var list = new ListPage(Page, "/_admin/school");
            await list.OpenAsync(App);
            await list.ToolbarButton("Add").ClickAsync();
            var uploadResponse = Page.WaitForResponseAsync(r => r.Url.Contains("/api/_file/UploadForEditor", StringComparison.OrdinalIgnoreCase));
            // the picture menu of the toolbar is a group; its upload entry opens the file dialog
            await list.Dialog.Locator("[data-menu-key=group-image]").ClickAsync();
            var chooser = await Page.RunAndWaitForFileChooserAsync(async () => await Page.Locator("[data-menu-key=uploadImage]").First.ClickAsync());
            await chooser.SetFilesAsync(new FilePayload { Name = "inline.png", MimeType = "image/png", Buffer = Png });
            var response = await uploadResponse;
            Assert.AreEqual(200, response.Status);
            using var body = JsonDocument.Parse(await response.TextAsync());
            Assert.AreEqual(0, body.RootElement.GetProperty("errno").GetInt32());
            var url = body.RootElement.GetProperty("data").GetProperty("url").GetString();
            StringAssert.StartsWith(url, "/api/_file/getfile/");
            Assert.IsTrue(await LoadsAsync(list.Dialog.Locator(".w-e-text-container img").First), "the picture in the editor");
            await list.DialogButton("Close").ClickAsync();

            await Page.ReloadAsync();
            var anonymous = await Vue3Api.CreateAnonymousAsync(Playwright, Site);
            try
            {
                Assert.AreEqual(200, (await anonymous.GetAsync(url)).Status, "the address in the content works with nothing but the id");
            }
            finally
            {
                await anonymous.DisposeAsync();
            }
        }
    }
}
