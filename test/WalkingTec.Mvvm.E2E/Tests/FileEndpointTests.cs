using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// The file endpoints (<c>GetFile</c>, <c>ViewFile</c>, <c>GetFileName</c>) and the upload endpoints: references, the mode in which the host runs
    /// (<c>FileEndpoints:Mode</c>; the default of the product is Audit, a whole run in another mode is chosen with the environment variable
    /// <see cref="LayUiSetup.FileEndpointsModeVariable"/>), embedded files and the choice of the database connection. The assertions that depend on
    /// the mode check what that mode promises.
    /// </summary>
    [TestClass]
    public class FileEndpointTests : LabTest
    {
        private static readonly string Mode = (Environment.GetEnvironmentVariable(LayUiSetup.FileEndpointsModeVariable) is { Length: > 0 } m ? m : "Audit").Trim();
        private static bool Enforcing => Mode.Equals("Enforce", StringComparison.OrdinalIgnoreCase);

        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private IAPIRequestContext Api => Page.Context.APIRequest;

        private async Task<IAPIRequestContext> AnonymousAsync() =>
            await Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions { BaseURL = Site.BaseUri.ToString() });

        private static FilePayload Payload(string name, byte[] content) => new FilePayload { Name = name, MimeType = "application/octet-stream", Buffer = content };

        private async Task<JsonElement> UploadAsync(string name, string content, string query = "")
        {
            var response = await Api.PostAsync("/_Framework/Upload" + query, new APIRequestContextOptions
            {
                Multipart = Api.CreateFormData().Set("file", Payload(name, Encoding.UTF8.GetBytes(content)))
            });
            Assert.AreEqual(200, response.Status);
            return JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("Data").Clone();
        }

        private static string Unique() => Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>True when the answer is the framework's refusal instead of the file.</summary>
        private static async Task<bool> IsRefusedAsync(IAPIResponse response, string expectedContent)
        {
            var body = await response.TextAsync();
            return body != expectedContent && body.Contains("not allowed to access this page", StringComparison.OrdinalIgnoreCase);
        }

        // ---- upload answers -----------------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task Upload_Answer_KeepsIdAndName_AndAddsTheUrlAndTheReference()
        {
            var content = "content " + Unique();
            var data = await UploadAsync("answer.txt", content);

            Assert.IsTrue(Guid.TryParse(data.GetProperty("Id").GetString(), out _));
            Assert.AreEqual("answer.txt", data.GetProperty("Name").GetString());
            var url = data.GetProperty("Url").GetString();
            StringAssert.Contains(url, "_DONOT_USE_FREF=");
            Assert.IsFalse(string.IsNullOrEmpty(data.GetProperty("Ref").GetString()));

            var file = await Api.GetAsync(url);
            Assert.AreEqual(200, file.Status);
            Assert.AreEqual(content, await file.TextAsync());
        }

        [TestMethod]
        public async Task TheReferenceOfAnUpload_ServesGetFile_ViewFile_AndGetFileName()
        {
            var content = "ref " + Unique();
            var data = await UploadAsync("served.txt", content);
            var id = data.GetProperty("Id").GetString();
            var reference = Uri.EscapeDataString(data.GetProperty("Ref").GetString());

            Assert.AreEqual(content, await (await Api.GetAsync($"/_Framework/GetFile/{id}?_DONOT_USE_FREF={reference}")).TextAsync());
            Assert.AreEqual(200, (await Api.GetAsync($"/_Framework/ViewFile/{id}?_DONOT_USE_FREF={reference}")).Status);
            StringAssert.Contains(await (await Api.GetAsync($"/_Framework/GetFileName/{id}?_DONOT_USE_FREF={reference}")).TextAsync(), "served.txt");
        }

        // ---- modes ------------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task ABareId_IsServedInLegacyAndAudit_AndRefusedInEnforce()
        {
            var content = "bare " + Unique();
            var id = (await UploadAsync("bare.txt", content)).GetProperty("Id").GetString();

            var response = await Api.GetAsync("/_Framework/GetFile/" + id);

            Assert.AreEqual(200, response.Status);
            if (Enforcing)
            {
                Assert.IsTrue(await IsRefusedAsync(response, content), "Enforce must refuse a request that only knows the id.");
            }
            else
            {
                Assert.AreEqual(content, await response.TextAsync());
            }
        }

        [TestMethod]
        public async Task AReferenceOfAnotherFile_OrAnAlteredOne_IsNotAccepted()
        {
            var content = "mine " + Unique();
            var mine = (await UploadAsync("mine.txt", content)).GetProperty("Id").GetString();
            var other = await UploadAsync("other.txt", "other " + Unique());
            var otherRef = Uri.EscapeDataString(other.GetProperty("Ref").GetString());
            var altered = Uri.EscapeDataString(other.GetProperty("Ref").GetString() + "x");

            foreach (var reference in new[] { otherRef, altered })
            {
                var response = await Api.GetAsync($"/_Framework/GetFile/{mine}?_DONOT_USE_FREF={reference}");
                Assert.AreEqual(200, response.Status);
                if (Enforcing)
                {
                    Assert.IsTrue(await IsRefusedAsync(response, content));
                }
                else
                {
                    Assert.AreEqual(content, await response.TextAsync(), "Audit serves the file and logs the request.");
                }
            }
        }

        [TestMethod]
        public async Task AReferenceIsOnlyForThePurposeItWasIssuedFor()
        {
            var content = "purpose " + Unique();
            var id = (await UploadAsync("purpose.txt", content)).GetProperty("Id").GetString();
            var nameOnly = await FileRefAsync(id, "Name");

            Assert.AreEqual(200, (await Api.GetAsync($"/_Framework/GetFileName/{id}?_DONOT_USE_FREF={nameOnly}")).Status);
            var download = await Api.GetAsync($"/_Framework/GetFile/{id}?_DONOT_USE_FREF={nameOnly}");
            if (Enforcing)
            {
                Assert.IsTrue(await IsRefusedAsync(download, content));
            }
            else
            {
                Assert.AreEqual(content, await download.TextAsync());
            }
        }

        [TestMethod]
        public async Task AnonymousCaller_NeedsTheFileToBeEmbedded_OrAReferenceOfItsOwn()
        {
            // the test host runs with IsFilePublic: the endpoints are open to callers who are not signed in, the file check decides
            var content = "anonymous " + Unique();
            var data = await UploadAsync("anon.txt", content);
            var id = data.GetProperty("Id").GetString();
            var adminRef = Uri.EscapeDataString(data.GetProperty("Ref").GetString());
            await using var anonymous = await AnonymousAsync();

            var bare = await anonymous.GetAsync("/_Framework/GetFile/" + id);
            var withAdminsReference = await anonymous.GetAsync($"/_Framework/GetFile/{id}?_DONOT_USE_FREF={adminRef}");

            Assert.AreEqual(200, bare.Status);
            Assert.AreEqual(200, withAdminsReference.Status);
            if (Enforcing)
            {
                Assert.IsTrue(await IsRefusedAsync(bare, content), "A bare id does not work anonymously in Enforce.");
                Assert.IsTrue(await IsRefusedAsync(withAdminsReference, content), "The reference of the signed-in user is not the visitor's.");
            }
            else
            {
                Assert.AreEqual(content, await bare.TextAsync());
            }
        }

        // ---- embedded files -------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task AFileEmbeddedByTheRichTextBox_StaysReachableByItsId_ForEveryoneTheEndpointsAreOpenTo()
        {
            var response = await Api.PostAsync("/_Framework/UploadForLayUIRichTextBox", new APIRequestContextOptions
            {
                Multipart = Api.CreateFormData().Set("file", Payload("inline.png", Png))
            });
            var src = JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("data").GetProperty("src").GetString();
            StringAssert.StartsWith(src, "/_Framework/GetFile?id=");
            Assert.IsFalse(src.Contains("_DONOT_USE_CS=default"), "No connection key is written when the file is on the default connection.");

            Assert.AreEqual(200, (await Api.GetAsync(src)).Status);
            await using var anonymous = await AnonymousAsync();
            var visitor = await anonymous.GetAsync(src);
            Assert.AreEqual(200, visitor.Status);
            CollectionAssert.AreEqual(Png, await visitor.BodyAsync());
        }

        [TestMethod]
        public async Task TheEmbeddedMarker_CannotBeSetByTheGenericUpload()
        {
            var content = "marker " + Unique();
            var data = await UploadAsync("marker.txt", content, "?extra=" + Uri.EscapeDataString("wtm:embedded;note"));
            var id = data.GetProperty("Id").GetString();

            var bare = await Api.GetAsync("/_Framework/GetFile/" + id);

            Assert.AreEqual(200, bare.Status);
            if (Enforcing)
            {
                Assert.IsTrue(await IsRefusedAsync(bare, content), "A file uploaded through the generic endpoint is not an embedded file, whatever its extra information says.");
            }
        }

        // ---- the connection ---------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task ANotConfiguredConnectionKey_IsRefusedCleanly_ByEveryFileEndpoint()
        {
            var content = "conn " + Unique();
            var id = (await UploadAsync("conn.txt", content)).GetProperty("Id").GetString();
            var reference = await FileRefAsync(id);

            var upload = await Api.PostAsync("/_Framework/Upload?_DONOT_USE_CS=nowhere", new APIRequestContextOptions
            {
                Multipart = Api.CreateFormData().Set("file", Payload("x.txt", Encoding.UTF8.GetBytes("x")))
            });
            var getFile = await Api.GetAsync($"/_Framework/GetFile/{id}?_DONOT_USE_CS=nowhere");
            var viewFile = await Api.GetAsync($"/_Framework/ViewFile/{id}?_DONOT_USE_CS=nowhere&_DONOT_USE_FREF={reference}");
            var getName = await Api.GetAsync($"/_Framework/GetFileName/{id}?_DONOT_USE_CS=nowhere");

            foreach (var response in new[] { upload, getFile, viewFile, getName })
            {
                Assert.AreEqual(200, response.Status, response.Url);
                StringAssert.Contains(await response.TextAsync(), "not allowed to access this page");
            }
        }

        [TestMethod]
        public async Task TheConnectionKeyOfARequest_DoesNotChooseTheDatabase()
        {
            // "other" is a configured connection that is not listed in FileEndpoints:RequestConnectionKeys: the request cannot use it, the file
            // goes to (and is read from) the default connection
            var content = "default db " + Unique();
            var data = await UploadAsync("db.txt", content, "?_DONOT_USE_CS=other");
            var id = data.GetProperty("Id").GetString();
            var reference = Uri.EscapeDataString(data.GetProperty("Ref").GetString());

            var viaDefault = await Api.GetAsync($"/_Framework/GetFile/{id}?_DONOT_USE_FREF={reference}");
            var viaOther = await Api.GetAsync($"/_Framework/GetFile/{id}?_DONOT_USE_CS=other&_DONOT_USE_FREF={reference}");

            Assert.AreEqual(content, await viaDefault.TextAsync());
            Assert.AreEqual(content, await viaOther.TextAsync(), "The key in the request is ignored.");
        }

        [TestMethod]
        public async Task TheConnectionOfAValidReference_DecidesWhereTheFileIsRead()
        {
            var content = "elsewhere " + Unique();
            var id = (await UploadAsync("elsewhere.txt", content)).GetProperty("Id").GetString();
            var forOther = await FileRefAsync(id, null, "other");

            var response = await Api.GetAsync($"/_Framework/GetFile/{id}?_DONOT_USE_FREF={forOther}");

            // the file is in the default database; the reference sends the request to the other one, where there is no such file
            Assert.AreEqual(200, response.Status);
            Assert.AreEqual("", await response.TextAsync());
        }

        // ---- ViewFile ------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task ViewFile_EncodesTheRequestValuesItWritesIntoTheMarkup()
        {
            var id = (await UploadAsync("shot.png", "not really a png")).GetProperty("Id").GetString();
            var reference = await FileRefAsync(id);

            var response = await Api.GetAsync($"/_Framework/ViewFile/{id}?width={Uri.EscapeDataString("1' onload='x\"><b>")}&_DONOT_USE_FREF={reference}");

            var html = await response.TextAsync();
            Assert.AreEqual(200, response.Status);
            Assert.IsFalse(html.Contains("onload='x"), "The width must not end the style attribute: " + html);
            Assert.IsFalse(html.Contains("<b>"));
            Assert.IsFalse(html.Contains("\"><"), "The width must not end the attribute: " + html);
        }

        [TestMethod]
        public async Task ViewFile_WritesALinkThatCarriesItsOwnReference()
        {
            var id = (await UploadAsync("pic.png", "image bytes")).GetProperty("Id").GetString();
            var reference = await FileRefAsync(id, "View");

            var html = await (await Api.GetAsync($"/_Framework/ViewFile/{id}?_DONOT_USE_FREF={reference}")).TextAsync();

            var src = System.Net.WebUtility.HtmlDecode(Regex.Match(html, "src='([^']+)'").Groups[1].Value);
            StringAssert.Contains(src, "_DONOT_USE_FREF=");
            Assert.AreEqual("image bytes", await (await Api.GetAsync(src)).TextAsync());
        }

        [TestMethod]
        public async Task ViewFile_ForAFileThatDoesNotExist_AnswersWithoutAnError()
        {
            var response = await Api.GetAsync("/_Framework/ViewFile/" + Guid.NewGuid());

            Assert.AreEqual(200, response.Status);
            Assert.AreEqual("", await response.TextAsync());
        }
    }
}
