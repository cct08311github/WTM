using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages.Vue3;

namespace WalkingTec.Mvvm.E2E.Tests.Vue3
{
    /// <summary>
    /// The file API of the Vue3Demo (<c>/api/_file/*</c>) over HTTP. The expectations depend on <c>FileEndpoints:Mode</c> of the run
    /// (<c>E2E_FILE_ENDPOINTS_MODE</c>, default Audit): an id alone is served in Audit and refused in Enforce, a reference works in both, a delete
    /// always needs the delete reference of the upload.
    /// </summary>
    [TestClass]
    [TestCategory("vue3demo")]
    public class Vue3FileApiTests : Vue3Test
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private static readonly string Mode = (Environment.GetEnvironmentVariable(LayUiSetup.FileEndpointsModeVariable) is { Length: > 0 } m ? m : "Audit").Trim();
        private static bool Enforce => string.Equals(Mode, "Enforce", StringComparison.OrdinalIgnoreCase);

        /// <summary>The status a request that has only the id of the file gets.</summary>
        private static int BareIdStatus => Enforce ? 403 : 200;

        private async Task<Vue3Api> StartAsync()
        {
            var api = await ApiAsync();
            await Vue3Seed.EnsureAsync(api);
            return api;
        }

        private static string WithRef(string url, string reference) => url + (url.Contains('?') ? "&" : "?") + "_DONOT_USE_FREF=" + Uri.EscapeDataString(reference);

        [TestMethod]
        public async Task File_Api_UploadReadDownloadAndDelete_WithTheReferencesOfTheUpload()
        {
            var api = await StartAsync();
            var upload = await api.UploadFullAsync("e2e-image.png", "image/png", Png);
            var id = upload.GetProperty("Id").GetString();
            var capability = upload.GetProperty("Ref").GetString();
            var deleteRef = upload.GetProperty("DeleteRef").GetString();
            Assert.AreEqual("/api/_file/getfile/" + id, upload.GetProperty("data").GetProperty("url").GetString());
            Assert.AreEqual(0, upload.GetProperty("errno").GetInt32());
            StringAssert.Contains(upload.GetProperty("Url").GetString(), "_DONOT_USE_FREF=");

            var info = await api.GetAsync(WithRef("/api/_file/GetFileInfo/" + id, capability));
            Assert.AreEqual("e2e-image.png", info.GetProperty("FileName").GetString());
            Assert.AreEqual(id, info.GetProperty("Id").GetString());
            CollectionAssert.AreEquivalent(new[] { "Id", "FileName", "FileExt", "Length", "UploadTime" }, info.EnumerateObject().Select(x => x.Name).ToArray(), "metadata only");
            Assert.AreEqual("e2e-image.png", (await api.GetAsync(WithRef("/api/_file/GetFileName/" + id, capability))).GetString());

            var raw = await api.GetRawAsync(WithRef("/api/_file/GetFile/" + id, capability));
            Assert.AreEqual(200, raw.Status);
            Assert.AreEqual("nosniff", raw.Headers["x-content-type-options"]);
            Assert.AreEqual("image/png", raw.Headers["content-type"]);
            Assert.AreEqual(200, (await api.GetRawAsync(WithRef("/api/_file/GetFile/" + id + "?width=10&height=10", capability))).Status);
            var download = await api.GetRawAsync(WithRef("/api/_file/DownloadFile/" + id, capability));
            Assert.AreEqual(200, download.Status);
            StringAssert.Contains(download.Headers["content-disposition"], "e2e-image.png");
            StringAssert.StartsWith(download.Headers["content-disposition"], "attachment");
            CollectionAssert.AreEqual(Png, await download.BodyAsync());

            // the image upload resizes, answers like Upload, and its reference works too
            var small = await api.UploadFullAsync("e2e-small.png", "image/png", Png, "/api/_file/UploadImage?width=1&height=1");
            Assert.IsFalse(string.IsNullOrEmpty(small.GetProperty("DeleteRef").GetString()));

            // delete: the reference of the upload, with DELETE (POST and GET work too)
            Assert.AreEqual(200, (await api.Context.DeleteAsync(WithRef("/api/_file/DeletedFile/" + id, deleteRef))).Status);
            Assert.AreEqual(400, (await api.GetRawAsync(WithRef("/api/_file/DownloadFile/" + id, capability))).Status, "a deleted file is gone");
            var second = await api.UploadFullAsync("second.png", "image/png", Png);
            Assert.AreEqual(200, (await api.Context.PostAsync(WithRef("/api/_file/DeletedFile/" + second.GetProperty("Id").GetString(), second.GetProperty("DeleteRef").GetString()))).Status);
            var third = await api.UploadFullAsync("third.png", "image/png", Png);
            Assert.AreEqual(200, (await api.GetRawAsync(WithRef("/api/_file/DeletedFile/" + third.GetProperty("Id").GetString(), third.GetProperty("DeleteRef").GetString()))).Status);
        }

        [TestMethod]
        public async Task File_Api_ACapability_ServesACallerWithoutCredentials_ForThatFileOnly()
        {
            var api = await StartAsync();
            var upload = await api.UploadFullAsync("anon.png", "image/png", Png);
            var id = upload.GetProperty("Id").GetString();
            var other = (await api.UploadFullAsync("other.png", "image/png", Png)).GetProperty("Id").GetString();
            var capability = upload.GetProperty("Ref").GetString();
            var anonymous = await Vue3Api.CreateAnonymousAsync(Playwright, Site);
            try
            {
                var shown = await anonymous.GetAsync(upload.GetProperty("Url").GetString());
                Assert.AreEqual(200, shown.Status);
                CollectionAssert.AreEqual(Png, await shown.BodyAsync());
                Assert.AreEqual(200, (await anonymous.GetAsync(WithRef("/api/_file/DownloadFile/" + id, capability))).Status);
                Assert.AreEqual(200, (await anonymous.GetAsync(WithRef("/api/_file/GetFileName/" + id, capability))).Status);
                Assert.AreEqual(BareIdStatus, (await anonymous.GetAsync(WithRef("/api/_file/GetFile/" + other, capability))).Status, "the capability of another file is no reference for this one");
                // it never deletes
                Assert.AreEqual(401, (await anonymous.GetAsync(WithRef("/api/_file/DeletedFile/" + id, capability))).Status);
            }
            finally
            {
                await anonymous.DisposeAsync();
            }
            Assert.AreEqual(403, (await api.Context.DeleteAsync(WithRef("/api/_file/DeletedFile/" + id, capability))).Status, "a read capability is not a delete reference");
        }

        [TestMethod]
        public async Task File_Api_AnIdAlone_IsServedInAudit_AndRefusedInEnforce()
        {
            var api = await StartAsync();
            var id = (await api.UploadFullAsync("bare.png", "image/png", Png)).GetProperty("Id").GetString();
            var anonymous = await Vue3Api.CreateAnonymousAsync(Playwright, Site);
            try
            {
                foreach (var action in new[] { "GetFile", "DownloadFile", "GetFileName", "GetFileInfo" })
                {
                    Assert.AreEqual(BareIdStatus, (await api.GetRawAsync($"/api/_file/{action}/{id}")).Status, "signed in " + action);
                    Assert.AreEqual(BareIdStatus, (await anonymous.GetAsync($"/api/_file/{action}/{id}")).Status, "anonymous " + action);
                }
                // an id that does not exist is answered like one that does when the reference is missing
                var missing = Guid.NewGuid();
                if (Enforce)
                {
                    Assert.AreEqual(403, (await anonymous.GetAsync($"/api/_file/GetFile/{missing}")).Status);
                    Assert.AreEqual(403, (await api.GetRawAsync($"/api/_file/GetFile/{missing}")).Status);
                }
            }
            finally
            {
                await anonymous.DisposeAsync();
            }
        }

        [TestMethod]
        public async Task File_Api_DeleteNeedsTheDeleteReference_OfTheSameUser_InEveryMode()
        {
            var api = await StartAsync();
            var upload = await api.UploadFullAsync("mine.png", "image/png", Png);
            var id = upload.GetProperty("Id").GetString();
            var deleteRef = upload.GetProperty("DeleteRef").GetString();

            // without a reference the file stays, whatever the mode (Audit does not relax this)
            Assert.AreEqual(403, (await api.Context.DeleteAsync("/api/_file/DeletedFile/" + id)).Status);
            Assert.AreEqual(403, (await api.GetRawAsync("/api/_file/DeletedFile/" + id)).Status);

            // another signed-in user cannot use it
            var group = UniqueDigits(8);
            var account = UniqueName("fileu").ToLowerInvariant();
            var password = "Pw-" + UniqueName("");
            await api.PostAsync("/api/_FrameworkUser/Add", new { Entity = new { ITCode = account, Password = password, Name = "File User" }, SelectedRolesCodes = new[] { "002" }, SelectedGroupCodes = new string[0] });
            var userId = Ids(await api.PostAsync("/api/_FrameworkUser/Search", new { ITCode = account })).Single();
            var other = await Vue3Api.CreateAsync(Playwright, Site, account, password);
            try
            {
                Assert.AreEqual(403, (await other.Context.DeleteAsync(WithRef("/api/_file/DeletedFile/" + id, deleteRef))).Status, "the reference was issued to someone else");
                // and a user uploads and deletes his own
                var own = await other.UploadFullAsync("own.png", "image/png", Png);
                Assert.AreEqual(200, (await other.Context.DeleteAsync(WithRef("/api/_file/DeletedFile/" + own.GetProperty("Id").GetString(), own.GetProperty("DeleteRef").GetString()))).Status);
            }
            finally
            {
                await other.DisposeAsync();
                await api.PostAsync("/api/_FrameworkUser/BatchDelete", new[] { userId });
            }

            // the file survived all of it; the owner deletes it
            var capability = upload.GetProperty("Ref").GetString();
            Assert.AreEqual(200, (await api.GetRawAsync(WithRef("/api/_file/GetFile/" + id, capability))).Status);
            Assert.AreEqual(200, (await api.Context.DeleteAsync(WithRef("/api/_file/DeletedFile/" + id, deleteRef))).Status);
        }

        [TestMethod]
        public async Task File_Api_TheEmbeddedMarkerSentByAClient_IsNotHonoured_TheEditorUploadIsMarked()
        {
            var api = await StartAsync();
            var anonymous = await Vue3Api.CreateAnonymousAsync(Playwright, Site);
            try
            {
                // a client cannot make a file reachable by its id by sending the marker in "extra"
                var marked = await api.UploadFullAsync("sneaky.png", "image/png", Png, "/api/_file/Upload?extra=" + Uri.EscapeDataString("wtm:embedded;x"));
                var markedId = marked.GetProperty("Id").GetString();
                Assert.AreEqual(BareIdStatus, (await anonymous.GetAsync("/api/_file/GetFile/" + markedId)).Status);
                Assert.AreEqual(BareIdStatus, (await api.GetRawAsync("/api/_file/GetFile/" + markedId)).Status);

                // the editor upload is marked by the server: the content outlives any reference, so the id is enough to show it
                var editor = await api.UploadFullAsync("editor.png", "image/png", Png, "/api/_file/UploadForEditor");
                Assert.AreEqual(0, editor.GetProperty("errno").GetInt32());
                var url = editor.GetProperty("data").GetProperty("url").GetString();
                StringAssert.StartsWith(url, "/api/_file/getfile/");
                Assert.AreEqual(200, (await anonymous.GetAsync(url)).Status, "shown by its id, signed in or not");
                Assert.AreEqual(200, (await api.GetRawAsync(url)).Status);
                // the demo has IsFilePublic on, so an embedded file is also served to the other read actions (with IsFilePublic off an anonymous
                // caller is only shown it, see SpaFileApiTest)
                var editorId = url.Substring("/api/_file/getfile/".Length);
                Assert.AreEqual(200, (await anonymous.GetAsync("/api/_file/DownloadFile/" + editorId)).Status);
            }
            finally
            {
                await anonymous.DisposeAsync();
            }
        }

        [TestMethod]
        public async Task File_Api_ThePhotoOfAUser_IsShownToEverybody_ButNotAnyOtherFile()
        {
            var api = await StartAsync();
            var photo = (await api.UploadFullAsync("avatar.png", "image/png", Png)).GetProperty("Id").GetString();
            var notAvatar = (await api.UploadFullAsync("not-avatar.png", "image/png", Png)).GetProperty("Id").GetString();
            var account = UniqueName("avatar").ToLowerInvariant();
            await api.PostAsync("/api/_FrameworkUser/Add", new { Entity = new { ITCode = account, Password = "Pw-" + UniqueName(""), Name = "Avatar User", PhotoId = photo }, SelectedRolesCodes = new[] { "002" }, SelectedGroupCodes = new string[0] });
            var userId = Ids(await api.PostAsync("/api/_FrameworkUser/Search", new { ITCode = account })).Single();
            var anonymous = await Vue3Api.CreateAnonymousAsync(Playwright, Site);
            try
            {
                var shown = await anonymous.GetAsync("/api/_file/GetUserPhoto/" + photo + "?width=5&height=5");
                Assert.AreEqual(200, shown.Status, "the photo of a user needs no reference");
                Assert.AreEqual(BareIdStatus, (await anonymous.GetAsync("/api/_file/GetUserPhoto/" + notAvatar)).Status, "another file follows the rules of GetFile");
                Assert.AreEqual(BareIdStatus, (await anonymous.GetAsync("/api/_file/GetFile/" + photo)).Status, "the exception belongs to GetUserPhoto");
                Assert.AreEqual(BareIdStatus, (await anonymous.GetAsync("/api/_file/DownloadFile/" + photo)).Status);
            }
            finally
            {
                await anonymous.DisposeAsync();
                await api.PostAsync("/api/_FrameworkUser/BatchDelete", new[] { userId });
            }
        }

        [TestMethod]
        public async Task File_Api_TheAnonymousRegistration_CannotNameAFileAsItsPhoto()
        {
            var api = await StartAsync();
            var victim = (await api.UploadFullAsync("victim.png", "image/png", Png)).GetProperty("Id").GetString();
            var account = UniqueName("reg").ToLowerInvariant();
            var anonymous = await Vue3Api.CreateAnonymousAsync(Playwright, Site);
            try
            {
                var reg = await anonymous.PostAsync("/api/_account/Reg", new() { DataString = JsonSerializer.Serialize(new { ITCode = account, Name = "Reg User", Password = "Pw-" + UniqueName(""), PhotoId = victim }), Headers = new System.Collections.Generic.Dictionary<string, string> { ["Content-Type"] = "application/json" } });
                Assert.AreEqual(200, reg.Status, await reg.TextAsync());
                Assert.AreEqual(BareIdStatus, (await anonymous.GetAsync("/api/_file/GetUserPhoto/" + victim)).Status, "the file did not become anybody's photo");
                var userId = Ids(await api.PostAsync("/api/_FrameworkUser/Search", new { ITCode = account })).Single();
                var entity = (await api.GetAsync("/api/_FrameworkUser/" + userId)).GetProperty("Entity");
                Assert.IsFalse(entity.TryGetProperty("PhotoId", out var p) && p.ValueKind == JsonValueKind.String);
                await api.PostAsync("/api/_FrameworkUser/BatchDelete", new[] { userId });
            }
            finally
            {
                await anonymous.DisposeAsync();
            }
        }

        [TestMethod]
        public async Task File_Api_ListRowsAndRecords_CarryTheReferencesOfTheirFiles()
        {
            var api = await StartAsync();
            var photo = (await api.UploadFullAsync("row.png", "image/png", Png)).GetProperty("Id").GetString();
            var name = UniqueName("School ");
            var city = UniqueName("Place ");
            await api.PostAsync("/api/City/Add", new { Entity = new { Name = city, Level = 1 } });
            var cityId = Ids(await api.PostAsync("/api/City/Search", new { Name = city })).Single();
            await api.PostAsync("/api/SchoolVue3/Add", new { Entity = new { SchoolCode = UniqueDigits(3), SchoolName = name, Level = 1, PlaceId = cityId, IsSchool = true, PhotoId = photo } });
            var row = (await api.PostAsync("/api/SchoolVue3/Search", new { SchoolName = name })).GetProperty("Data").EnumerateArray().Single();
            var id = row.GetProperty("ID").GetString();

            Assert.AreEqual(photo, row.GetProperty("PhotoId").GetString());
            var fref = row.GetProperty("PhotoId__fref").GetString();
            var record = await api.GetAsync("/api/SchoolVue3/" + id);
            Assert.AreEqual(fref == null, false);
            var refs = record.GetProperty("FileRefs");
            Assert.IsTrue(refs.TryGetProperty(photo, out var recordRef));

            // the references are for anybody who holds them
            var anonymous = await Vue3Api.CreateAnonymousAsync(Playwright, Site);
            try
            {
                Assert.AreEqual(200, (await anonymous.GetAsync(WithRef("/api/_file/GetFile/" + photo + "?width=150&height=150", fref))).Status);
                Assert.AreEqual(200, (await anonymous.GetAsync(WithRef("/api/_file/GetFile/" + photo, recordRef.GetString()))).Status);
            }
            finally
            {
                await anonymous.DisposeAsync();
            }
            await api.PostAsync("/api/SchoolVue3/BatchDelete", new[] { id });
            await api.PostAsync("/api/City/BatchDelete", new[] { cityId });
        }

        [TestMethod]
        public async Task File_Api_ARequestWithAConnectionThatIsNotConfigured_IsRefused_InEveryMode()
        {
            var api = await StartAsync();
            var upload = await api.UploadFullAsync("conn.png", "image/png", Png);
            var id = upload.GetProperty("Id").GetString();
            Assert.AreEqual(403, (await api.GetRawAsync(WithRef($"/api/_file/GetFile/{id}?csName=nowhere", upload.GetProperty("Ref").GetString()))).Status);
            var form = (await ApiAsync()).Context.CreateFormData();
            form.Set("file", new FilePayload { Name = "x.png", MimeType = "image/png", Buffer = Png });
            Assert.AreEqual(403, (await api.Context.PostAsync("/api/_file/Upload?csName=nowhere", new() { Multipart = form })).Status);
        }

        [TestMethod]
        public async Task File_Api_Upload_NeedsASignedInCaller()
        {
            await StartAsync();
            var anonymous = await Vue3Api.CreateAnonymousAsync(Playwright, Site);
            try
            {
                var form = anonymous.CreateFormData();
                form.Set("file", new FilePayload { Name = "x.png", MimeType = "image/png", Buffer = Png });
                foreach (var endpoint in new[] { "Upload", "UploadImage", "UploadForEditor" })
                {
                    Assert.AreEqual(401, (await anonymous.PostAsync("/api/_file/" + endpoint, new() { Multipart = form })).Status, endpoint);
                }
            }
            finally
            {
                await anonymous.DisposeAsync();
            }
        }
    }
}
