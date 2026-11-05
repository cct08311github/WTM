using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// The optional upload allow-list (<c>FileEndpoints:UploadAllowList</c>, the list xls, xlsx, jpg, jpeg, png, gif) with the real LayUI components
    /// (<c>wt:upload</c> on /Lab/Pickers) and the UEditor endpoint. The test host applies the list only to requests that carry the header
    /// <c>X-E2E-Upload-AllowList</c> (see E2EUploadFileRule), so the rest of the run uploads as it does without a rule; the tests here switch it on, and
    /// one of them leaves it off to show that nothing is checked then.
    /// </summary>
    [TestClass]
    public class UploadAllowListTests : LabTest
    {
        private const string Header = "X-E2E-Upload-AllowList";

        // a 1x1 PNG and a 1x1 GIF
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        private static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

        private string _dir;

        [TestInitialize]
        public async Task OpenPage()
        {
            _dir = Path.Combine(Path.GetTempPath(), "wtm-e2e-allowlist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            await Context.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { [Header] = "1" });
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

        private IAPIRequestContext Api => Page.Context.APIRequest;

        private static readonly Dictionary<string, string> On = new Dictionary<string, string> { [Header] = "1" };

        private static byte[] Xlsx()
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var part in new[] { "[Content_Types].xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml" })
                {
                    using var w = new StreamWriter(zip.CreateEntry(part).Open());
                    w.Write("<x/>");
                }
            }
            return ms.ToArray();
        }

        private string MakeFile(string name, byte[] content)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        private FilePayload Payload(string name, byte[] content) => new FilePayload { Name = name, MimeType = "application/octet-stream", Buffer = content };

        /// <summary>Chooses the file in the component, waits for the upload request and returns the answer of the server.</summary>
        private async Task<JsonElement> UploadAsync(string property, string endpoint, string path)
        {
            var upload = Page.WaitForResponseAsync(r => r.Url.Contains("/_Framework/" + endpoint) && r.Request.Method == "POST");
            var chooser = await Page.RunAndWaitForFileChooserAsync(async () => await Body.Locator($"#LabFormVM_{property}button").ClickAsync());
            await chooser.SetFilesAsync(path);
            var response = await upload;
            Assert.AreEqual(200, response.Status);
            return JsonDocument.Parse(await response.TextAsync()).RootElement.Clone();
        }

        // ---- wt:upload ---------------------------------------------------------------------------------------------------------

        [TestMethod]
        [DataRow("allowed.png", "png")]
        [DataRow("sheet.xlsx", "xlsx")]
        [DataRow("old.xls", "xls")]
        [DataRow("photo.GIF", "gif")]
        public async Task Upload_AFileOfTheListWithMatchingContent_IsStored(string name, string kind)
        {
            var content = kind switch
            {
                "png" => Png,
                "gif" => Gif,
                "xlsx" => Xlsx(),
                _ => new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 1, 2, 3 }
            };

            var json = await UploadAsync("AttachmentId", "Upload", MakeFile(name, content));

            Assert.AreEqual("200", json.GetProperty("Code").ToString());
            var id = json.GetProperty("Data").GetProperty("Id").GetString();
            StringAssert.Matches(id, new Regex("^[0-9a-f-]{36}$"));
            await Expect(Body.Locator("#LabFormVM_AttachmentId")).ToHaveValueAsync(id);
            await Expect(Body.Locator("#LabFormVM_AttachmentIdlabel")).ToContainTextAsync(name);
        }

        [TestMethod]
        [DataRow("tool.bin", "binary data")]
        [DataRow("page.html", "<html><script>1</script></html>")]
        [DataRow("noextension", "text")]
        [DataRow("fake.png", "this is not a picture")]
        [DataRow("fake.xlsx", "this is not a workbook")]
        public async Task Upload_AFileOutsideTheListOrWithOtherContent_IsRefused_AndTheComponentReportsAFailedUpload(string name, string content)
        {
            var json = await UploadAsync("AttachmentId", "Upload", MakeFile(name, Encoding.UTF8.GetBytes(content)));

            // the shape of a failed upload: an empty id, which the component reports with its own message; nothing is stored
            Assert.AreEqual(string.Empty, json.GetProperty("Data").GetProperty("Id").GetString());
            Assert.AreNotEqual("200", json.GetProperty("Code").ToString());
            await Expect(Page.Locator(".layui-layer-msg")).ToBeVisibleAsync();
            await Expect(Body.Locator("#LabFormVM_AttachmentId")).ToHaveValueAsync("");
            await Expect(Body.Locator("#LabFormVM_AttachmentIdlabel")).ToHaveTextAsync("");
        }

        [TestMethod]
        public async Task UploadImage_AGif_IsStoredAsAThumbnailNamedJpg()
        {
            var json = await UploadAsync("PictureId", "UploadImage", MakeFile("dot.gif", Gif));

            Assert.AreEqual("200", json.GetProperty("Code").ToString());
            var data = json.GetProperty("Data");
            // thumbnails are JPEG, and with the allow-list the stored name says so
            Assert.AreEqual("dot.jpg", data.GetProperty("Name").GetString());
            await Expect(Body.Locator("#LabFormVM_PictureId")).ToHaveValueAsync(data.GetProperty("Id").GetString());
            await Expect(Body.Locator("#LabFormVM_PictureIdpreview")).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task UploadImage_APictureWhoseContentDoesNotMatchItsExtension_IsRefused()
        {
            var json = await UploadAsync("PictureId", "UploadImage", MakeFile("dot.png", Gif));

            Assert.AreEqual(string.Empty, json.GetProperty("Data").GetProperty("Id").GetString());
            await Expect(Page.Locator(".layui-layer-msg")).ToBeVisibleAsync();
            await Expect(Body.Locator("#LabFormVM_PictureId")).ToHaveValueAsync("");
        }

        [TestMethod]
        public async Task Upload_WithoutTheAllowList_StoresAnyFile_AsBefore()
        {
            await Context.SetExtraHTTPHeadersAsync(new Dictionary<string, string>());

            var json = await UploadAsync("AttachmentId", "Upload", MakeFile("tool.bin", Encoding.UTF8.GetBytes("binary data")));

            Assert.AreEqual("200", json.GetProperty("Code").ToString());
            StringAssert.Matches(json.GetProperty("Data").GetProperty("Id").GetString(), new Regex("^[0-9a-f-]{36}$"));
        }

        [TestMethod]
        public async Task UploadImage_WithoutTheAllowList_KeepsTheOriginalName_AsBefore()
        {
            await Context.SetExtraHTTPHeadersAsync(new Dictionary<string, string>());

            var json = await UploadAsync("PictureId", "UploadImage", MakeFile("dot.gif", Gif));

            Assert.AreEqual("dot.gif", json.GetProperty("Data").GetProperty("Name").GetString());
        }

        // ---- UEditor ------------------------------------------------------------------------------------------------------------

        private async Task<JsonElement> UEditorAsync(IFormData form, bool allowList = true, bool multipart = true)
        {
            // the extra headers of the browser context apply to its API requests as well
            await Context.SetExtraHTTPHeadersAsync(allowList ? On : new Dictionary<string, string>());
            var options = new APIRequestContextOptions();
            if (multipart)
            {
                options.Multipart = form;
            }
            else
            {
                options.Form = form;
            }
            var response = await Api.PostAsync("/_Framework/UploadForLayUIUEditor", options);
            var text = await response.TextAsync();
            Assert.AreEqual(200, response.Status, text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        private IFormData FileForm(string name, byte[] content) => Api.CreateFormData().Set("upfile", Payload(name, content));

        [TestMethod]
        public async Task UEditorUpload_AFileOfTheList_IsStored_AndTheAnswerIsTheUsualOne()
        {
            var json = await UEditorAsync(FileForm("editor.png", Png));

            Assert.AreEqual("200", json.GetProperty("Code").ToString());
            var data = json.GetProperty("Data");
            StringAssert.StartsWith(data.GetProperty("src").GetString(), "/_Framework/GetFile?id=");
            Assert.AreEqual("editor.png", data.GetProperty("FileName").GetString());
        }

        [TestMethod]
        [DataRow("editor.html", "<html><script>1</script></html>")]
        [DataRow("editor.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
        [DataRow("editor.png", "not a picture")]
        public async Task UEditorUpload_AFileOutsideTheList_IsRefused_InTheShapeTheDialogsRead(string name, string content)
        {
            var json = await UEditorAsync(FileForm(name, Encoding.UTF8.GetBytes(content)));

            // the dialogs treat Code 200 as success and show Msg otherwise
            Assert.AreNotEqual("200", json.GetProperty("Code").ToString());
            Assert.IsFalse(string.IsNullOrEmpty(json.GetProperty("Msg").GetString()));
            Assert.AreEqual(string.Empty, json.GetProperty("Data").GetProperty("src").GetString());
        }

        [TestMethod]
        public async Task UEditorUpload_WithoutTheAllowList_StoresAnyFile_AsBefore()
        {
            var json = await UEditorAsync(FileForm("editor.html", Encoding.UTF8.GetBytes("<html/>")), allowList: false);

            Assert.AreEqual("200", json.GetProperty("Code").ToString());
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task UEditorScrawl_APng_IsStoredWithAPngName(bool allowList)
        {
            // the scrawl dialog posts canvas.toDataURL("image/png") without its "data:image/png;base64," prefix, as a urlencoded form field
            var json = await UEditorAsync(Api.CreateFormData().Set("FileID", Convert.ToBase64String(Png)), allowList, multipart: false);

            Assert.AreEqual("200", json.GetProperty("Code").ToString());
            var name = json.GetProperty("Data").GetProperty("FileName").GetString();
            StringAssert.StartsWith(name, "SCRAWL_");
            StringAssert.EndsWith(name, ".png");
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task UEditorScrawl_AGif_IsStoredWithAGifName(bool allowList)
        {
            var json = await UEditorAsync(Api.CreateFormData().Set("FileID", Convert.ToBase64String(Gif)), allowList, multipart: false);

            Assert.AreEqual("200", json.GetProperty("Code").ToString());
            StringAssert.EndsWith(json.GetProperty("Data").GetProperty("FileName").GetString(), ".gif");
        }

        [TestMethod]
        [DataRow("%%% not base64 %%%", true)]
        [DataRow("%%% not base64 %%%", false)]
        [DataRow("PGh0bWw+PHNjcmlwdD4xPC9zY3JpcHQ+PC9odG1sPg==", true)]
        [DataRow("PGh0bWw+PHNjcmlwdD4xPC9zY3JpcHQ+PC9odG1sPg==", false)]
        public async Task UEditorScrawl_InvalidBase64OrNoPicture_IsRefused_WhateverTheAllowList(string text, bool allowList)
        {
            var json = await UEditorAsync(Api.CreateFormData().Set("FileID", text), allowList, multipart: false);

            Assert.AreNotEqual("200", json.GetProperty("Code").ToString());
            Assert.IsFalse(string.IsNullOrEmpty(json.GetProperty("Msg").GetString()));
        }
    }
}
