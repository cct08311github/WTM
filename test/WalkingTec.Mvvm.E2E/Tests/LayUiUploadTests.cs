using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// wt:upload, wt:multiupload and the files they store through /_Framework/Upload, UploadImage, GetFile, GetFileName and ViewFile
    /// (page /Lab/Pickers; the files are kept in the database of the test site).
    /// </summary>
    [TestClass]
    public class LayUiUploadTests : LabTest
    {
        // a 1x1 PNG
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private string _dir;

        [TestInitialize]
        public async Task OpenPage()
        {
            _dir = Path.Combine(Path.GetTempPath(), "wtm-e2e-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            await Lab.OpenAsync("Pickers");
        }

        [TestCleanup]
        public void RemoveFiles()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        private string MakeFile(string name, byte[] content)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        private async Task ChooseFilesAsync(string property, params string[] paths)
        {
            var chooser = await Page.RunAndWaitForFileChooserAsync(async () => await Body.Locator($"#LabFormVM_{property}button").ClickAsync());
            await chooser.SetFilesAsync(paths);
        }

        [TestMethod]
        public async Task Upload_AnyFile_StoresTheFileAndPostsItsId()
        {
            var path = MakeFile("report.txt", Encoding.UTF8.GetBytes("report body 測試"));

            var upload = Page.WaitForResponseAsync(r => r.Url.Contains("/_Framework/Upload") && r.Request.Method == "POST");
            await ChooseFilesAsync("AttachmentId", path);
            var response = await upload;
            Assert.AreEqual(200, response.Status);

            // the hidden field holds the id of the stored file, the label shows its name and a delete button
            await Expect(Body.Locator("#LabFormVM_AttachmentIdlabel")).ToContainTextAsync("report.txt");
            await Expect(Body.Locator("#LabFormVM_AttachmentId")).ToHaveValueAsync(new Regex("^[0-9a-f-]{36}$"));
            var id = await Body.Locator("#LabFormVM_AttachmentId").InputValueAsync();

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Pickers");
            CollectionAssert.AreEqual(new[] { id }, (await Lab.LastPostAsync("Pickers"))["AttachmentId"]);

            // download it again: GetFile returns the bytes, GetFileName the name
            var file = await Page.Context.APIRequest.GetAsync("/_Framework/GetFile/" + id);
            Assert.AreEqual(200, file.Status);
            Assert.AreEqual("report body 測試", Encoding.UTF8.GetString(await file.BodyAsync()));
            var name = await Page.Context.APIRequest.GetAsync("/_Framework/GetFileName/" + id);
            Assert.AreEqual(200, name.Status);
            StringAssert.Contains(await name.TextAsync(), "report.txt");
        }

        [TestMethod]
        public async Task Upload_FileNameWithMarkupCharacters_IsShownAsText()
        {
            var path = MakeFile("a&b 'q' <u>x.txt", Encoding.UTF8.GetBytes("x"));

            await ChooseFilesAsync("AttachmentId", path);

            var label = Body.Locator("#LabFormVM_AttachmentIdlabel");
            await Expect(label.Locator("button")).ToBeVisibleAsync();
            // the server removes characters that are not allowed in a file name (spaces and angle brackets here); the rest is shown as it is
            await Expect(label).ToContainTextAsync("a&b'q'ux.txt");
            // no element is created from the characters of the name
            await Expect(label.Locator("u")).ToHaveCountAsync(0);
            await Expect(label.Locator("*")).ToHaveCountAsync(1);
        }

        [TestMethod]
        public async Task Upload_Delete_ClearsTheFieldAndRemembersTheDeletedFile()
        {
            await ChooseFilesAsync("AttachmentId", MakeFile("remove-me.txt", Encoding.UTF8.GetBytes("x")));
            await Expect(Body.Locator("#LabFormVM_AttachmentId")).ToHaveValueAsync(new Regex("^[0-9a-f-]{36}$"));
            var id = await Body.Locator("#LabFormVM_AttachmentId").InputValueAsync();

            await Body.Locator("#LabFormVM_AttachmentIddel").ClickAsync();

            await Expect(Body.Locator("#LabFormVM_AttachmentId")).ToHaveValueAsync("");
            await Expect(Body.Locator("#LabFormVM_AttachmentIdlabel")).ToHaveTextAsync("");
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Pickers");
            // the form tells the server which file was removed
            CollectionAssert.AreEqual(new[] { id }, (await Lab.LastPostAsync("Pickers"))["DeletedFileIds"]);
        }

        [TestMethod]
        public async Task Upload_Image_ShowsAPreviewAndTheThumbnailIsServed()
        {
            var upload = Page.WaitForResponseAsync(r => r.Url.Contains("/_Framework/UploadImage") && r.Request.Method == "POST");
            await ChooseFilesAsync("PictureId", MakeFile("dot.png", Png));
            Assert.AreEqual(200, (await upload).Status);

            await Expect(Body.Locator("#LabFormVM_PictureIdpreview")).ToBeVisibleAsync();
            var id = await Body.Locator("#LabFormVM_PictureId").InputValueAsync();
            StringAssert.Matches(id, new Regex("^[0-9a-f-]{36}$"));

            var thumb = await Page.Context.APIRequest.GetAsync($"/_Framework/GetFile?id={id}&stream=true&width=64&height=64");
            Assert.AreEqual(200, thumb.Status);
            // a resized image comes back as JPEG (magic number FF D8)
            var bytes = await thumb.BodyAsync();
            Assert.IsTrue(bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8, "The thumbnail is not a JPEG image.");
        }

        [TestMethod]
        public async Task Upload_Image_RejectsOtherFileTypes()
        {
            var chooser = await Page.RunAndWaitForFileChooserAsync(async () => await Body.Locator("#LabFormVM_PictureIdbutton").ClickAsync());
            await chooser.SetFilesAsync(MakeFile("not-an-image.txt", Encoding.UTF8.GetBytes("x")));

            // layui checks the extension list of the tag helper (jpg|jpeg|gif|bmp|png|tif) and shows its message; nothing is stored
            await Expect(Page.Locator(".layui-layer-msg")).ToBeVisibleAsync();
            await Expect(Body.Locator("#LabFormVM_PictureId")).ToHaveValueAsync("");
        }

        [TestMethod]
        public async Task Upload_StoredFile_IsShownAgainWhenThePageIsRenderedFromThePostedValues()
        {
            await ChooseFilesAsync("AttachmentId", MakeFile("kept.txt", Encoding.UTF8.GetBytes("kept")));
            await Expect(Body.Locator("#LabFormVM_AttachmentId")).ToHaveValueAsync(new Regex("^[0-9a-f-]{36}$"));
            var id = await Body.Locator("#LabFormVM_AttachmentId").InputValueAsync();

            await Lab.SubmitAsync("Reflect", "/Lab/Reflect");

            // the page was rendered again with the id; the tag helper asks GetFileName for the label
            await Expect(Body.Locator("#LabFormVM_AttachmentId")).ToHaveValueAsync(id);
            await Expect(Body.Locator("#LabFormVM_AttachmentIdlabel")).ToContainTextAsync("kept.txt");
        }

        [TestMethod]
        public async Task MultiUpload_SeveralFiles_PostsOneEntryPerFile()
        {
            var upload = Page.WaitForResponseAsync(r => r.Url.Contains("/_Framework/Upload") && r.Request.Method == "POST");
            await ChooseFilesAsync("Files", MakeFile("one.txt", Encoding.UTF8.GetBytes("1")), MakeFile("two.txt", Encoding.UTF8.GetBytes("2")));
            await upload;

            await Expect(Body.Locator("#LabFormVM_Fileslabel label")).ToHaveCountAsync(2);
            await Expect(Body.Locator("#LabFormVM_Fileslabel")).ToContainTextAsync("one.txt");
            await Expect(Body.Locator("#LabFormVM_Fileslabel")).ToContainTextAsync("two.txt");

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Pickers");
            var form = await Lab.LastPostAsync("Pickers");
            Assert.IsTrue(form.ContainsKey("Files[0].FileId") && form.ContainsKey("Files[1].FileId"), "Keys: " + string.Join(",", form.Keys));
            Assert.AreNotEqual(form["Files[0].FileId"].Single(), form["Files[1].FileId"].Single());
        }

        [TestMethod]
        public async Task MultiUpload_StoredFiles_AreListedAgainAndCanBeViewed()
        {
            await ChooseFilesAsync("Files", MakeFile("view-me.txt", Encoding.UTF8.GetBytes("view me")));
            await Expect(Body.Locator("#LabFormVM_Fileslabel label")).ToHaveCountAsync(1);

            await Lab.SubmitAsync("Reflect", "/Lab/Reflect");

            await Expect(Body.Locator("#LabFormVM_Fileslabel")).ToContainTextAsync("view-me.txt");
            var id = (await Lab.LastPostAsync("Pickers"))["Files[0].FileId"].Single();
            var view = await Page.Context.APIRequest.GetAsync("/_Framework/ViewFile/" + id);
            Assert.AreEqual(200, view.Status);
        }

        [TestMethod]
        [Ignore("#282: the delete button of a multi upload calls /api/_file/DeletedFile/{id}, which no controller of the framework or of the LayUI host provides; the file stays in the list.")]
        public async Task MultiUpload_Delete_RemovesTheFileFromTheList()
        {
            await ChooseFilesAsync("Files", MakeFile("drop.txt", Encoding.UTF8.GetBytes("x")));
            await Expect(Body.Locator("#LabFormVM_Fileslabel label")).ToHaveCountAsync(1);

            await Body.Locator("#LabFormVM_Fileslabel button").ClickAsync();

            await Expect(Body.Locator("#LabFormVM_Fileslabel label")).ToHaveCountAsync(0);
        }
    }
}
