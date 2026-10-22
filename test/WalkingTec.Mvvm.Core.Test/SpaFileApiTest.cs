using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Mvc.Filters;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// The file API of a single page application: modes x public files x signed in or not, capabilities, the embedded and grandfathered files,
    /// metadata, headers, uploads, the marker of embedded files, connections and deletion.
    /// </summary>
    [TestClass]
    public class SpaFileApiTest
    {
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

        private string _seed;
        private object _oldHandlers;
        private object _oldDefault;
        private readonly IDataProtectionProvider _provider = new EphemeralDataProtectionProvider();

        private sealed class FileTestController : BaseApiController
        {
        }

        private sealed class DenyingPolicy : IFileAccessPolicy
        {
            public int Calls;
            public bool IsAllowed(FileAccessRequest request)
            {
                Calls++;
                return false;
            }
        }

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
            var handlers = typeof(WtmFileProvider).GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Static);
            var defaults = typeof(WtmFileProvider).GetField("_defaultHandler", BindingFlags.NonPublic | BindingFlags.Static);
            _oldHandlers = handlers.GetValue(null);
            _oldDefault = defaults.GetValue(null);
            var ctor = typeof(WtmDataBaseFileHandler).GetConstructor(new[] { typeof(WTMContext) });
            handlers.SetValue(null, new Dictionary<string, ConstructorInfo> { { "database", ctor } });
            defaults.SetValue(null, ctor);
        }

        [TestCleanup]
        public void Cleanup()
        {
            typeof(WtmFileProvider).GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, _oldHandlers);
            typeof(WtmFileProvider).GetField("_defaultHandler", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, _oldDefault);
        }

        private sealed class Harness
        {
            public FileTestController Controller;
            public WTMContext Wtm;
            public WtmFileProvider Provider;
            public CapturingLoggerFactory Logs;
            public Func<IDataContext> NewDc;
            public RecordingResponseFeature Response;
        }

        /// <summary>Keeps what is registered to run when the response is complete (disposables), so a test can run it.</summary>
        private sealed class RecordingResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
        {
            private readonly List<(Func<object, Task> Callback, object State)> _completed = new List<(Func<object, Task>, object)>();
            public override void OnCompleted(Func<object, Task> callback, object state) => _completed.Add((callback, state));
            public System.Collections.Generic.IEnumerable<object> Registered => _completed.Select(x => x.State);
            public void Complete() { foreach (var (callback, state) in _completed) { callback(state).GetAwaiter().GetResult(); } }
        }

        private sealed class TrackedContext : LoginTestContext
        {
            public TrackedContext(string seed) : base(seed) { }
        }

        private Harness Make(string user = "alice", FileEndpointMode mode = FileEndpointMode.Audit, bool filePublic = false, IFileAccessPolicy policy = null,
            Action<Configs> configure = null, string requestContentType = null, IEnumerable<IFormFile> files = null, IDataProtectionProvider provider = null, Func<IDataContext> dcFactory = null)
        {
            var seed = _seed;
            Func<IDataContext> newDc = dcFactory ?? (() => new LoginTestContext(seed));
            var configs = new Configs();
            configs.FileEndpoints.Mode = mode;
            configs.IsFilePublic = filePublic;
            configs.Connections = new List<CS>
            {
                new CS { Key = "default", Value = "x", DbType = DBTypeEnum.Memory },
                new CS { Key = "other", Value = "y", DbType = DBTypeEnum.Memory },
            };
            configure?.Invoke(configs);
            var logs = new CapturingLoggerFactory();
            var wtm = MockWtmContext.CreateWtmContext(newDc(), user ?? "alice", dataProtection: provider ?? _provider, loggerFactory: logs, configs: configs, fileAccessPolicy: policy, createDc: key => newDc());
            wtm.GlobaInfo.CustomUserType = typeof(LoginTestUser);
            if (user == null)
            {
                wtm.LoginUserInfo = null;
            }
            var http = new DefaultHttpContext();
            var feature = new RecordingResponseFeature();
            http.Features.Set<IHttpResponseFeature>(feature);
            if (files != null)
            {
                http.Request.ContentType = requestContentType ?? "multipart/form-data; boundary=x";
                http.Request.Form = new FormCollection(new Dictionary<string, StringValues>(), new FormFileCollection().Also(files));
            }
            var controller = new FileTestController { Wtm = wtm, ControllerContext = new ControllerContext { HttpContext = http } };
            return new Harness { Controller = controller, Wtm = wtm, Provider = new WtmFileProvider(wtm), Logs = logs, NewDc = newDc, Response = feature };
        }

        private Guid AddFile(string ext = "png", string extra = null, DateTime? uploaded = null, byte[] data = null, string name = null)
        {
            using var dc = new LoginTestContext(_seed);
            var id = Guid.NewGuid();
            dc.Set<FileAttachment>().Add(new FileAttachment
            {
                ID = id,
                FileName = name ?? ("f." + ext),
                FileExt = ext,
                Length = (data ?? Png).Length,
                UploadTime = uploaded ?? new DateTime(2026, 1, 1),
                SaveMode = "database",
                ExtraInfo = extra,
                FileData = data ?? Png
            });
            dc.SaveChanges();
            return id;
        }

        private bool Exists(Guid id)
        {
            using var dc = new LoginTestContext(_seed);
            return dc.Set<FileAttachment>().Any(x => x.ID == id);
        }

        private static IFormFile Form(string name, byte[] data) => new FormFile(new MemoryStream(data), 0, data.Length, "file", name);

        private static int Status(IActionResult result)
        {
            switch (result)
            {
                case ContentResult c: return c.StatusCode ?? 200;
                case ObjectResult o: return o.StatusCode ?? 200;
                case StatusCodeResult s: return s.StatusCode;
                case FileStreamResult _: return 200;
                case RedirectResult _: return 302;
                case EmptyResult _: return 200;
                default: throw new AssertFailedException("unexpected result " + result?.GetType().Name);
            }
        }

        private static string Capability(Harness issuer, Guid id, FilePurpose purposes = FilePurpose.Read) => issuer.Wtm.CreateFileCapability(id, purposes);

        private string CapabilityFor(Guid id, FileEndpointMode mode = FileEndpointMode.Enforce)
        {
            var issuer = Make("alice", mode);
            return issuer.Wtm.CreateFileCapability(id);
        }

        private IActionResult Do(Harness h, string action, Guid id, string reference, string csName = null)
        {
            var sid = id.ToString();
            switch (action)
            {
                case "GetFile": return SpaFileApi.GetFile(h.Controller, h.Provider, sid, csName, null, null, reference);
                case "DownloadFile": return SpaFileApi.DownloadFile(h.Controller, h.Provider, sid, csName, reference);
                case "GetFileName": return SpaFileApi.GetFileName(h.Controller, h.Provider, sid, csName, reference);
                case "GetFileInfo": return SpaFileApi.GetFileInfo(h.Controller, sid, csName, reference);
                case "GetUserPhoto": return SpaFileApi.GetUserPhoto(h.Controller, h.Provider, sid, csName, null, null, reference);
                default: throw new ArgumentException(action);
            }
        }

        private static readonly string[] Reads = { "GetFile", "DownloadFile", "GetFileName", "GetFileInfo", "GetUserPhoto" };

        // ---- references --------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ACapability_ServesAnAnonymousCaller_InEveryAction_InEnforce()
        {
            var id = AddFile();
            var capability = CapabilityFor(id);
            foreach (var action in Reads)
            {
                var h = Make(null, FileEndpointMode.Enforce);
                Assert.AreEqual(200, Status(Do(h, action, id, capability)), action);
            }
        }

        [TestMethod]
        public void ACapability_IsForOneFile()
        {
            var id = AddFile();
            var other = AddFile();
            var capability = CapabilityFor(id);
            foreach (var action in Reads)
            {
                Assert.AreEqual(403, Status(Do(Make(null, FileEndpointMode.Enforce), action, other, capability)), action);
            }
        }

        [TestMethod]
        public void ABareId_SignedIn_IsServedAndLoggedInAudit_RefusedInEnforce_AndServedInLegacy()
        {
            var id = AddFile();
            foreach (var action in Reads)
            {
                var audit = Make("alice", FileEndpointMode.Audit);
                Assert.AreEqual(200, Status(Do(audit, action, id, null)), action);
                Assert.AreEqual(1, audit.Logs.Entries.Count(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning && x.Message.Contains("without a valid file reference")), action);

                var enforce = Make("alice", FileEndpointMode.Enforce);
                Assert.AreEqual(403, Status(Do(enforce, action, id, null)), action);
                Assert.IsTrue(enforce.Logs.Entries.Any(x => x.Message.Contains("refused")), "refusals are logged");

                var legacy = Make("alice", FileEndpointMode.Legacy);
                Assert.AreEqual(200, Status(Do(legacy, action, id, null)), action);
                Assert.AreEqual(0, legacy.Logs.Entries.Count, action);
            }
        }

        [TestMethod]
        public void ABareId_Anonymous_FilesNotPublic_IsServedInAudit_RefusedInEnforce()
        {
            var id = AddFile();
            foreach (var action in Reads)
            {
                var audit = Make(null, FileEndpointMode.Audit);
                Assert.AreEqual(200, Status(Do(audit, action, id, null)), action);
                Assert.IsTrue(audit.Logs.Entries.Any(x => x.Message.Contains("without a valid file reference")), "logged: " + action);

                Assert.AreEqual(403, Status(Do(Make(null, FileEndpointMode.Enforce), action, id, null)), action);
                Assert.AreEqual(200, Status(Do(Make(null, FileEndpointMode.Legacy), action, id, null)), action);
            }
        }

        [TestMethod]
        public void ABareId_Anonymous_FilesPublic_FollowsTheSameModes()
        {
            var id = AddFile();
            foreach (var action in Reads)
            {
                Assert.AreEqual(200, Status(Do(Make(null, FileEndpointMode.Audit, true), action, id, null)), action);
                Assert.AreEqual(403, Status(Do(Make(null, FileEndpointMode.Enforce, true), action, id, null)), action);
            }
        }

        [TestMethod]
        public void AnEmbeddedFile_Anonymous_FilesNotPublic_IsOnlyShown_NotDownloadedOrDescribed()
        {
            var id = AddFile(extra: FileEndpointGuard.MarkEmbedded("n"));
            Assert.AreEqual(200, Status(Do(Make(null, FileEndpointMode.Enforce), "GetFile", id, null)));
            Assert.AreEqual(200, Status(Do(Make(null, FileEndpointMode.Enforce), "GetUserPhoto", id, null)), "other ids follow the rules of GetFile");
            foreach (var action in new[] { "DownloadFile", "GetFileName", "GetFileInfo" })
            {
                var h = Make(null, FileEndpointMode.Enforce);
                Assert.AreEqual(403, Status(Do(h, action, id, null)), action);
                Assert.IsTrue(h.Logs.Entries.Any(x => x.Message.Contains("needs a reference")));
            }
        }

        [TestMethod]
        public void AnEmbeddedFile_FilesPublic_OrSignedIn_IsServedByItsId_InEnforce()
        {
            var id = AddFile(extra: FileEndpointGuard.MarkEmbedded("n"));
            foreach (var action in Reads)
            {
                Assert.AreEqual(200, Status(Do(Make(null, FileEndpointMode.Enforce, true), action, id, null)), "public " + action);
                Assert.AreEqual(200, Status(Do(Make("alice", FileEndpointMode.Enforce), action, id, null)), "signed in " + action);
            }
        }

        [TestMethod]
        public void AGrandfatheredFile_FollowsTheRulesOfAnEmbeddedOne()
        {
            var id = AddFile(uploaded: new DateTime(2026, 1, 1));
            Action<Configs> cutoff = c => c.FileEndpoints.LegacyUploadedBefore = new DateTime(2026, 6, 1);
            Assert.AreEqual(200, Status(Do(Make(null, FileEndpointMode.Enforce, configure: cutoff), "GetFile", id, null)));
            Assert.AreEqual(403, Status(Do(Make(null, FileEndpointMode.Enforce, configure: cutoff), "DownloadFile", id, null)));
            Assert.AreEqual(200, Status(Do(Make("alice", FileEndpointMode.Enforce, configure: cutoff), "DownloadFile", id, null)));
            Assert.AreEqual(403, Status(Do(Make(null, FileEndpointMode.Enforce), "GetFile", id, null)), "no cut-off: not grandfathered");
        }

        [TestMethod]
        public void TheConnectionOfTheRequest_IsNotChosenByTheCaller()
        {
            var id = AddFile();
            var capability = CapabilityFor(id);
            // a name that is no connection: refused in every mode, whatever else the request carries
            foreach (var mode in new[] { FileEndpointMode.Legacy, FileEndpointMode.Audit, FileEndpointMode.Enforce })
            {
                Assert.AreEqual(403, Status(Do(Make("alice", mode), "GetFile", id, capability, "nowhere")), mode.ToString());
                Assert.AreEqual(403, Status(Do(Make(null, mode), "GetFile", id, null, "nowhere")), mode.ToString());
            }
            // a configured one without a reference does not choose either (the server's connection is used)
            Assert.AreEqual(200, Status(Do(Make("alice", FileEndpointMode.Audit), "GetFile", id, null, "other")));
        }

        [TestMethod]
        public void AReference_IsNeededToTell_AFileThatExists_FromOneThatDoesNot()
        {
            var missing = Guid.NewGuid();
            var existing = AddFile();
            // without a reference the answer is the same
            var a = Do(Make("alice", FileEndpointMode.Enforce), "GetFile", existing, null);
            var b = Do(Make("alice", FileEndpointMode.Enforce), "GetFile", missing, null);
            Assert.AreEqual(Status(a), Status(b));
            Assert.AreEqual(((ContentResult)a).Content, ((ContentResult)b).Content);
            // with a valid reference a missing file is a missing file
            var capability = CapabilityFor(missing);
            Assert.AreEqual(400, Status(Do(Make(null, FileEndpointMode.Enforce), "GetFile", missing, capability)));
        }

        [TestMethod]
        public void AnIdThatIsNoId_DoesNotBlowUp()
        {
            var h = Make("alice", FileEndpointMode.Audit);
            Assert.AreEqual(400, Status(SpaFileApi.GetFile(h.Controller, h.Provider, "not-a-guid", null, null, null, null)));
            Assert.AreEqual(403, Status(SpaFileApi.GetFile(Make("alice", FileEndpointMode.Enforce).Controller, h.Provider, "not-a-guid", null, null, null, null)));
            Assert.AreEqual(400, Status(SpaFileApi.GetFileInfo(h.Controller, "not-a-guid", null, null)));
        }

        [TestMethod]
        public void TheAccessPolicy_CanRefuse_EvenWithAValidReference_InAuditAndEnforce()
        {
            var id = AddFile();
            foreach (var mode in new[] { FileEndpointMode.Audit, FileEndpointMode.Enforce })
            {
                var issuer = Make("alice", mode);
                var capability = issuer.Wtm.CreateFileCapability(id);
                var policy = new DenyingPolicy();
                foreach (var action in Reads)
                {
                    Assert.AreEqual(403, Status(Do(Make(null, mode, policy: policy), action, id, capability)), mode + " " + action);
                }
                Assert.IsTrue(policy.Calls >= Reads.Length);
            }
            // Legacy does not ask
            var legacyPolicy = new DenyingPolicy();
            Assert.AreEqual(200, Status(Do(Make("alice", FileEndpointMode.Legacy, policy: legacyPolicy), "GetFile", id, null)));
            Assert.AreEqual(0, legacyPolicy.Calls);
        }

        // ---- metadata ----------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void GetFileInfo_ReturnsTheMetadataOnly_InEveryMode()
        {
            var id = AddFile("png", extra: "secret-extra", data: Png, name: "photo.png");
            foreach (var mode in new[] { FileEndpointMode.Legacy, FileEndpointMode.Audit, FileEndpointMode.Enforce })
            {
                var issuer = Make("alice", mode);
                var result = SpaFileApi.GetFileInfo(Make("alice", mode).Controller, id.ToString(), null, mode == FileEndpointMode.Enforce ? issuer.Wtm.CreateFileCapability(id) : null);
                var json = JsonSerializer.Serialize(((ObjectResult)result).Value);
                using var doc = JsonDocument.Parse(json);
                var names = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
                CollectionAssert.AreEqual(new[] { "FileExt", "FileName", "Id", "Length", "UploadTime" }, names, mode.ToString());
                Assert.AreEqual("photo.png", doc.RootElement.GetProperty("FileName").GetString());
                Assert.AreEqual(id.ToString(), doc.RootElement.GetProperty("Id").GetString());
                Assert.IsFalse(json.Contains("secret-extra"));
            }
        }

        // ---- response headers --------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void GetFile_StatesTheType_NoSniffing_AndShowsPicturesInline()
        {
            var id = AddFile("png");
            var h = Make("alice", FileEndpointMode.Legacy);
            var result = (FileStreamResult)SpaFileApi.GetFile(h.Controller, h.Provider, id.ToString(), null, null, null, null);
            Assert.AreEqual("image/png", result.ContentType);
            Assert.AreEqual("nosniff", h.Controller.Response.Headers["X-Content-Type-Options"].ToString());
            StringAssert.StartsWith(h.Controller.Response.Headers["Content-Disposition"].ToString(), "inline;");
            using var ms = new MemoryStream();
            result.FileStream.CopyTo(ms);
            CollectionAssert.AreEqual(Png, ms.ToArray());
        }

        [TestMethod]
        public void GetFile_SendsActiveContentAsAnAttachment_EvenWhenTheNameSaysOtherwise()
        {
            foreach (var ext in new[] { "html", "htm", "svg", "xml", "js" })
            {
                var id = AddFile(ext, data: Encoding.UTF8.GetBytes("<html><script>x</script></html>"));
                var h = Make("alice", FileEndpointMode.Legacy);
                var result = (FileStreamResult)SpaFileApi.GetFile(h.Controller, h.Provider, id.ToString(), null, null, null, null);
                StringAssert.StartsWith(h.Controller.Response.Headers["Content-Disposition"].ToString(), "attachment;", ext);
                Assert.AreEqual("nosniff", h.Controller.Response.Headers["X-Content-Type-Options"].ToString());
            }
            // an unknown extension whose content looks like a page
            var odd = AddFile("zzz", data: Encoding.UTF8.GetBytes("  <!DOCTYPE html><title>x</title>"));
            var h2 = Make("alice", FileEndpointMode.Legacy);
            SpaFileApi.GetFile(h2.Controller, h2.Provider, odd.ToString(), null, null, null, null);
            StringAssert.StartsWith(h2.Controller.Response.Headers["Content-Disposition"].ToString(), "attachment;");
            // plain text stays inline
            var txt = AddFile("txt", data: Encoding.UTF8.GetBytes("hello"));
            var h3 = Make("alice", FileEndpointMode.Legacy);
            SpaFileApi.GetFile(h3.Controller, h3.Provider, txt.ToString(), null, null, null, null);
            StringAssert.StartsWith(h3.Controller.Response.Headers["Content-Disposition"].ToString(), "inline;");
        }

        [TestMethod]
        public void DownloadFile_IsAlwaysAnAttachment_WithTheStatedType()
        {
            var id = AddFile("png", name: "my photo.png");
            var h = Make("alice", FileEndpointMode.Legacy);
            var result = (FileStreamResult)SpaFileApi.DownloadFile(h.Controller, h.Provider, id.ToString(), null, null);
            Assert.AreEqual("image/png", result.ContentType);
            Assert.IsFalse(string.IsNullOrEmpty(result.FileDownloadName));
            Assert.AreEqual("nosniff", h.Controller.Response.Headers["X-Content-Type-Options"].ToString());
            var html = AddFile("html", data: Encoding.UTF8.GetBytes("<html></html>"));
            var r2 = (FileStreamResult)SpaFileApi.DownloadFile(h.Controller, h.Provider, html.ToString(), null, null);
            Assert.AreEqual("text/html", r2.ContentType);
            Assert.IsFalse(string.IsNullOrEmpty(r2.FileDownloadName), "an attachment, never rendered");
        }

        [TestMethod]
        public void Mp4_KeepsRangeRequests()
        {
            var id = AddFile("mp4", data: new byte[] { 0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70 });
            var h = Make("alice", FileEndpointMode.Legacy);
            var result = (FileStreamResult)SpaFileApi.GetFile(h.Controller, h.Provider, id.ToString(), null, null, null, null);
            Assert.IsTrue(result.EnableRangeProcessing);
            Assert.AreEqual("video/mpeg4", result.ContentType);
        }

        [TestMethod]
        public void GetFileName_AlsoSaysNoSniffing()
        {
            var id = AddFile();
            var h = Make("alice", FileEndpointMode.Legacy);
            var result = SpaFileApi.GetFileName(h.Controller, h.Provider, id.ToString(), null, null);
            Assert.AreEqual("f.png", ((OkObjectResult)result).Value);
            Assert.AreEqual("nosniff", h.Controller.Response.Headers["X-Content-Type-Options"].ToString());
        }

        // ---- resizing ----------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void GetFile_UsesTheResizerOfTheApplication_AndServesTheOriginalWhenItRejects()
        {
            var id = AddFile("png");
            var small = new byte[] { 9, 9 };
            var h = Make("alice", FileEndpointMode.Legacy);
            var resized = (FileStreamResult)SpaFileApi.GetFile(h.Controller, h.Provider, id.ToString(), null, 10, 10, null, (s, w, hh) => new SpaImageResult { Resized = new MemoryStream(small) });
            Assert.AreEqual("image/jpeg", resized.ContentType);
            using (var ms = new MemoryStream())
            {
                resized.FileStream.CopyTo(ms);
                CollectionAssert.AreEqual(small, ms.ToArray());
            }

            var h2 = Make("alice", FileEndpointMode.Legacy);
            var fallback = (FileStreamResult)SpaFileApi.GetFile(h2.Controller, h2.Provider, id.ToString(), null, 10, 10, null, (s, w, hh) =>
            {
                s.CopyTo(Stream.Null); // the source is consumed
                return new SpaImageResult { Buffered = new MemoryStream(Png), Error = "Unsupported image format" };
            });
            Assert.AreEqual("image/png", fallback.ContentType);
            using (var ms = new MemoryStream())
            {
                fallback.FileStream.CopyTo(ms);
                CollectionAssert.AreEqual(Png, ms.ToArray());
            }
            Assert.IsTrue(h2.Logs.Entries.Any(x => x.Message.Contains("image rejected") && x.Message.Contains("Unsupported image format")));

            var h3 = Make("alice", FileEndpointMode.Legacy);
            var thrown = (FileStreamResult)SpaFileApi.GetFile(h3.Controller, h3.Provider, id.ToString(), null, 10, 10, null, (s, w, hh) => throw new InvalidOperationException());
            Assert.AreEqual("image/png", thrown.ContentType);
        }

        // ---- the photo of a user -----------------------------------------------------------------------------------------------------------

        private Guid AddUserWithPhoto(string ext = "png", bool valid = true, string tenant = null)
        {
            var photo = AddFile(ext);
            using var dc = new LoginTestContext(_seed);
            dc.Set<LoginTestUser>().Add(new LoginTestUser { ITCode = "u" + Guid.NewGuid().ToString("N").Substring(0, 8), Name = "n", Password = "p", PhotoId = photo, IsValid = valid, TenantCode = tenant });
            dc.SaveChanges();
            return photo;
        }

        [TestMethod]
        public void TheAvatarOfAUser_IsPublic_ButNotEverythingElse()
        {
            var photo = AddUserWithPhoto();
            var someFile = AddFile();
            foreach (var anonymous in new[] { true, false })
            {
                var user = anonymous ? null : "alice";
                var h = Make(user, FileEndpointMode.Enforce);
                var result = SpaFileApi.GetUserPhoto(h.Controller, h.Provider, photo.ToString(), null, null, null, null);
                Assert.AreEqual(200, Status(result), "avatar, anonymous=" + anonymous);
                Assert.AreEqual("image/png", ((FileStreamResult)result).ContentType);
                Assert.AreEqual(403, Status(Do(Make(user, FileEndpointMode.Enforce), "GetUserPhoto", someFile, null)), "another file, anonymous=" + anonymous);
                // the avatar rule belongs to GetUserPhoto
                Assert.AreEqual(403, Status(Do(Make(user, FileEndpointMode.Enforce), "GetFile", photo, null)), "GetFile, anonymous=" + anonymous);
                Assert.AreEqual(403, Status(Do(Make(user, FileEndpointMode.Enforce), "DownloadFile", photo, null)));
            }
        }

        [TestMethod]
        public void AnAvatar_MustBeAPicture_OfAValidUser_AndStillGoesThroughThePolicy()
        {
            var svg = AddUserWithPhoto("svg");
            var html = AddUserWithPhoto("html");
            var invalid = AddUserWithPhoto("png", valid: false);
            foreach (var id in new[] { svg, html, invalid })
            {
                Assert.AreEqual(403, Status(Do(Make(null, FileEndpointMode.Enforce), "GetUserPhoto", id, null)));
            }
            var ok = AddUserWithPhoto("jpg");
            var policy = new DenyingPolicy();
            Assert.AreEqual(403, Status(Do(Make(null, FileEndpointMode.Enforce, policy: policy), "GetUserPhoto", ok, null)));
            Assert.AreEqual(1, policy.Calls);
        }

        [TestMethod]
        public void AnAvatar_OfAUserInAnotherTenant_IsNotVisible()
        {
            var photo = AddUserWithPhoto("png", tenant: "t9");
            // the context of the request is scoped to the main tenant: the user of t9 is not visible
            Assert.AreEqual(403, Status(Do(Make(null, FileEndpointMode.Enforce), "GetUserPhoto", photo, null)));
        }

        [TestMethod]
        public void GetUserPhoto_SendsAnAnonymousCallerToTheMainHost_LikeBefore()
        {
            var photo = AddUserWithPhoto();
            var h = Make(null, FileEndpointMode.Enforce, configure: c => c.Domains = new Dictionary<string, WalkingTec.Mvvm.Core.ConfigOptions.Domain> { { "mainhost", new WalkingTec.Mvvm.Core.ConfigOptions.Domain { Address = "https://main.example" } } });
            h.Controller.Request.Path = "/api/_file/GetUserPhoto/" + photo;
            h.Controller.Request.QueryString = new QueryString("?width=5&_DONOT_USE_FREF=abc");
            var result = SpaFileApi.GetUserPhoto(h.Controller, h.Provider, photo.ToString(), null, null, null, null);
            Assert.IsInstanceOfType(result, typeof(RedirectResult));
            Assert.AreEqual("https://main.example/api/_file/GetUserPhoto/" + photo + "?width=5&_DONOT_USE_FREF=abc", ((RedirectResult)result).Url, "the query string, with the reference, goes along");
        }

        [TestMethod]
        public void IsUserPhoto_KeepsAnswersForAShortTime_PositiveAndNegative()
        {
            var photo = AddUserWithPhoto();
            var h = Make(null, FileEndpointMode.Enforce);
            Assert.IsTrue(h.Wtm.IsUserPhoto(h.NewDc(), photo.ToString()));
            var nobody = AddFile();
            Assert.IsFalse(h.Wtm.IsUserPhoto(h.NewDc(), nobody.ToString()));

            // the data changes: the answers stay for now
            using (var dc = new LoginTestContext(_seed))
            {
                dc.Set<LoginTestUser>().RemoveRange(dc.Set<LoginTestUser>().Where(x => x.PhotoId == photo));
                dc.Set<LoginTestUser>().Add(new LoginTestUser { ITCode = "late", Name = "n", Password = "p", PhotoId = nobody, IsValid = true });
                dc.SaveChanges();
            }
            Assert.IsTrue(h.Wtm.IsUserPhoto(h.NewDc(), photo.ToString()), "cached");
            Assert.IsFalse(h.Wtm.IsUserPhoto(h.NewDc(), nobody.ToString()), "cached");

            // and are read again after the time is up
            var ttl = typeof(WTMContext).GetField("_userPhotoCacheTtl", BindingFlags.NonPublic | BindingFlags.Static);
            var old = ttl.GetValue(null);
            ttl.SetValue(null, TimeSpan.Zero);
            try
            {
                Assert.IsFalse(h.Wtm.IsUserPhoto(h.NewDc(), photo.ToString()));
                Assert.IsTrue(h.Wtm.IsUserPhoto(h.NewDc(), nobody.ToString()));
            }
            finally
            {
                ttl.SetValue(null, old);
            }
        }

        [TestMethod]
        public void TheReads_AreNotKeptByCaches_UnlessFilesArePublic()
        {
            var id = AddFile();
            foreach (var action in new[] { "GetFile", "DownloadFile", "GetFileName", "GetFileInfo", "GetUserPhoto" })
            {
                var h = Make("alice", FileEndpointMode.Legacy);
                Do(h, action, id, null);
                Assert.AreEqual("private, no-store", h.Controller.Response.Headers["Cache-Control"].ToString(), action);

                var open = Make("alice", FileEndpointMode.Legacy, filePublic: true);
                Do(open, action, id, null);
                Assert.IsFalse(open.Controller.Response.Headers.ContainsKey("Cache-Control"), "public files: " + action);
            }
        }

        private sealed class OneWayStream : Stream
        {
            private readonly MemoryStream _inner;
            public OneWayStream(byte[] data) { _inner = new MemoryStream(data); }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [TestMethod]
        public void AStreamThatCannotSeek_IsPeekedAtAndPassedOn_NotCopiedFirst()
        {
            var id = AddFile("png");
            var content = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
            var h = Make("alice", FileEndpointMode.Legacy);
            var result = (FileStreamResult)SpaFileApi.GetFile(h.Controller, h.Provider, id.ToString(), null, 10, 10, null, (s, w, hh) => new SpaImageResult { Resized = new OneWayStream(content) });
            Assert.IsFalse(result.FileStream is MemoryStream, "not buffered into memory as a whole");
            using var ms = new MemoryStream();
            result.FileStream.CopyTo(ms);
            CollectionAssert.AreEqual(content, ms.ToArray(), "the part that was peeked at is not lost");
            StringAssert.StartsWith(h.Controller.Response.Headers["Content-Disposition"].ToString(), "inline;");
        }

        [TestMethod]
        public void TheDataContextOfARequest_IsDisposedWhenTheResponseIsDone()
        {
            var id = AddFile();
            var seed = _seed;
            var h = Make("alice", FileEndpointMode.Legacy, dcFactory: () => new TrackedContext(seed));
            SpaFileApi.GetFile(h.Controller, h.Provider, id.ToString(), null, null, null, null);
            // handed to the response, which disposes it when it is done (not while the answer is still being written)
            Assert.IsTrue(h.Response.Registered.Any(x => x is TrackedContext));
            h.Response.Complete();
        }

        // ---- uploads -----------------------------------------------------------------------------------------------------------------------

        private static JsonElement Body(IActionResult result)
        {
            var text = result is OkObjectResult ok ? (string)ok.Value : ((ContentResult)result).Content;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        [TestMethod]
        public void AnUpload_NeedsASignedInCaller()
        {
            var h = Make(null, FileEndpointMode.Audit, files: new[] { Form("a.png", Png) });
            Assert.AreEqual(401, Status(SpaFileApi.Upload(h.Controller, h.Provider, null, null, null, null, null, null)));
            Assert.AreEqual(401, Status(SpaFileApi.UploadImage(h.Controller, h.Provider, 10, 10, null, null, null, null, null, null, (s, w, hh) => null)));
            Assert.AreEqual(401, Status(SpaFileApi.UploadForEditor(h.Controller, h.Provider, null, null, null, null, null, null)));
            Assert.AreEqual(0, new LoginTestContext(_seed).Set<FileAttachment>().Count());
        }

        [TestMethod]
        public void AnUpload_KeepsTheOldAnswer_AndAddsTheUrlTheCapabilityAndTheDeleteReference()
        {
            foreach (var mode in new[] { FileEndpointMode.Legacy, FileEndpointMode.Audit, FileEndpointMode.Enforce })
            {
                var h = Make("alice", mode, files: new[] { Form("a.png", Png) });
                var body = Body(SpaFileApi.Upload(h.Controller, h.Provider, null, null, null, null, null, null));
                var id = body.GetProperty("Id").GetString();
                Assert.AreEqual("a.png", body.GetProperty("Name").GetString());
                Assert.AreEqual(0, body.GetProperty("errno").GetInt32());
                Assert.AreEqual("/api/_file/getfile/" + id, body.GetProperty("data").GetProperty("url").GetString(), "the bare address for editors of old pages");
                var url = body.GetProperty("Url").GetString();
                StringAssert.StartsWith(url, "/api/_file/GetFile/" + id + "?");
                var capability = body.GetProperty("Ref").GetString();
                StringAssert.Contains(url, "_DONOT_USE_FREF=" + Uri.EscapeDataString(capability));
                Assert.IsFalse(string.IsNullOrEmpty(body.GetProperty("DeleteRef").GetString()));

                // the capability shows, downloads and names the file for anybody
                foreach (var action in new[] { "GetFile", "DownloadFile", "GetFileName", "GetFileInfo" })
                {
                    Assert.AreEqual(200, Status(Do(Make(null, FileEndpointMode.Enforce), action, Guid.Parse(id), capability)), mode + " " + action);
                }
                // and does not delete
                Assert.AreEqual(403, Status(SpaFileApi.DeleteFile(Make("alice", FileEndpointMode.Enforce).Controller, Make("alice").Provider, id, null, capability)));
            }
        }

        [TestMethod]
        public void AnUpload_StripsTheMarkerOfEmbeddedFiles_ThatAClientSends()
        {
            var h = Make("alice", FileEndpointMode.Enforce, files: new[] { Form("a.png", Png) });
            var id = Body(SpaFileApi.Upload(h.Controller, h.Provider, null, null, null, FileEndpointGuard.MarkEmbedded("x"), null, null)).GetProperty("Id").GetString();
            using var dc = new LoginTestContext(_seed);
            var stored = dc.Set<FileAttachment>().Single(x => x.ID == Guid.Parse(id));
            Assert.IsFalse(FileEndpointGuard.IsEmbedded(stored.ExtraInfo));
            Assert.AreEqual("x", stored.ExtraInfo);
            // so it is not reachable by its id alone
            Assert.AreEqual(403, Status(Do(Make(null, FileEndpointMode.Enforce), "GetFile", Guid.Parse(id), null)));
            Assert.AreEqual(403, Status(Do(Make("alice", FileEndpointMode.Enforce), "GetFile", Guid.Parse(id), null)));
        }

        [TestMethod]
        public void AnUpload_ToAConnectionThatDoesNotExist_IsRefused_AndStoresNothing()
        {
            var h = Make("alice", FileEndpointMode.Audit, files: new[] { Form("a.png", Png) });
            Assert.AreEqual(403, Status(SpaFileApi.Upload(h.Controller, h.Provider, null, null, null, null, "nowhere", null)));
            Assert.AreEqual(0, new LoginTestContext(_seed).Set<FileAttachment>().Count());
            // a connection of the application is not chosen by the request either: the server's connection is used
            var h2 = Make("alice", FileEndpointMode.Audit, files: new[] { Form("a.png", Png) });
            Assert.AreEqual(200, Status(SpaFileApi.Upload(h2.Controller, h2.Provider, null, null, null, null, "other", null)));
        }

        [TestMethod]
        public void AnUpload_WithoutAFile_IsABadRequest()
        {
            var h = Make("alice", FileEndpointMode.Audit);
            Assert.AreEqual(400, Status(SpaFileApi.Upload(h.Controller, h.Provider, null, null, null, null, null, null)));
            var h2 = Make("alice", FileEndpointMode.Audit, files: new IFormFile[0]);
            Assert.AreEqual(400, Status(SpaFileApi.Upload(h2.Controller, h2.Provider, null, null, null, null, null, null)));
        }

        [TestMethod]
        public void UploadImage_WithASize_StoresWhatTheResizerReturns_AndNothingWhenItRejects()
        {
            var small = new byte[] { 7, 7, 7 };
            var h = Make("alice", FileEndpointMode.Enforce, files: new[] { Form("a.png", Png) });
            var body = Body(SpaFileApi.UploadImage(h.Controller, h.Provider, 150, 150, null, null, null, null, null, null, (s, w, hh) =>
            {
                Assert.AreEqual(150, w);
                return new SpaImageResult { Resized = new MemoryStream(small) };
            }));
            using (var dc = new LoginTestContext(_seed))
            {
                CollectionAssert.AreEqual(small, dc.Set<FileAttachment>().Single(x => x.ID == Guid.Parse(body.GetProperty("Id").GetString())).FileData);
            }
            Assert.IsFalse(string.IsNullOrEmpty(body.GetProperty("DeleteRef").GetString()));

            var before = new LoginTestContext(_seed).Set<FileAttachment>().Count();
            var h2 = Make("alice", FileEndpointMode.Enforce, files: new[] { Form("a.png", Png) });
            var rejected = SpaFileApi.UploadImage(h2.Controller, h2.Provider, 150, 150, null, null, null, null, null, null, (s, w, hh) => new SpaImageResult { Error = "Image file too large" });
            Assert.AreEqual(400, Status(rejected));
            Assert.AreEqual(before, new LoginTestContext(_seed).Set<FileAttachment>().Count(), "a rejected picture is not stored");
            Assert.IsTrue(h2.Logs.Entries.Any(x => x.Message.Contains("image rejected") && x.Message.Contains("Image file too large")));
        }

        [TestMethod]
        public void UploadImage_WithoutASize_IsAPlainUpload()
        {
            var h = Make("alice", FileEndpointMode.Audit, files: new[] { Form("a.png", Png) });
            var body = Body(SpaFileApi.UploadImage(h.Controller, h.Provider, null, null, null, null, null, null, null, null, (s, w, hh) => throw new InvalidOperationException("not asked")));
            using var dc = new LoginTestContext(_seed);
            CollectionAssert.AreEqual(Png, dc.Set<FileAttachment>().Single(x => x.ID == Guid.Parse(body.GetProperty("Id").GetString())).FileData);
        }

        [TestMethod]
        public void UploadForEditor_MarksTheFileEmbedded_AndAnswersInTheShapeOfTheEditor()
        {
            var h = Make("alice", FileEndpointMode.Enforce, files: new[] { Form("a.png", Png) });
            var result = SpaFileApi.UploadForEditor(h.Controller, h.Provider, null, null, null, FileEndpointGuard.MarkEmbedded("x"), null, null);
            var body = Body(result);
            Assert.AreEqual(0, body.GetProperty("errno").GetInt32());
            var url = body.GetProperty("data").GetProperty("url").GetString();
            StringAssert.StartsWith(url, "/api/_file/getfile/");
            Assert.IsFalse(url.Contains("?"), "the default connection is not named");
            var id = Guid.Parse(url.Substring("/api/_file/getfile/".Length));
            using (var dc = new LoginTestContext(_seed))
            {
                var stored = dc.Set<FileAttachment>().Single(x => x.ID == id);
                Assert.IsTrue(FileEndpointGuard.IsEmbedded(stored.ExtraInfo));
                Assert.AreEqual(FileEndpointGuard.EmbeddedMarker + "x", stored.ExtraInfo, "the marker is set once");
            }
            // the content outlives any reference: the id is enough to show it
            Assert.AreEqual(200, Status(Do(Make(null, FileEndpointMode.Enforce), "GetFile", id, null)));
            Assert.AreEqual(200, Status(Do(Make("alice", FileEndpointMode.Enforce), "GetFile", id, null)));
            Assert.AreEqual(403, Status(Do(Make(null, FileEndpointMode.Enforce), "DownloadFile", id, null)));
        }

        [TestMethod]
        public void UploadForEditor_NamesANonDefaultConnectionInTheAddress()
        {
            var h = Make("alice", FileEndpointMode.Audit, files: new[] { Form("a.png", Png) }, configure: c => c.FileEndpoints.RequestConnectionKeys.Add("other"));
            var body = Body(SpaFileApi.UploadForEditor(h.Controller, h.Provider, null, null, null, null, "other", null));
            StringAssert.EndsWith(body.GetProperty("data").GetProperty("url").GetString(), "?csName=other");
        }

        // ---- deletion ----------------------------------------------------------------------------------------------------------------------

        private string UploadAs(string user, FileEndpointMode mode, out string id)
        {
            var h = Make(user, mode, files: new[] { Form("a.png", Png) });
            var body = Body(SpaFileApi.Upload(h.Controller, h.Provider, null, null, null, null, null, null));
            id = body.GetProperty("Id").GetString();
            return body.GetProperty("DeleteRef").GetString();
        }

        [TestMethod]
        public void Delete_NeedsASignedInCaller()
        {
            var id = AddFile();
            var h = Make(null, FileEndpointMode.Legacy);
            Assert.AreEqual(401, Status(SpaFileApi.DeleteFile(h.Controller, h.Provider, id.ToString(), null, null)));
            Assert.IsTrue(Exists(id));
        }

        [TestMethod]
        public void Delete_WithoutAReference_IsRefusedAndLogged_InEveryModeButLegacy()
        {
            foreach (var mode in new[] { FileEndpointMode.Audit, FileEndpointMode.Enforce })
            {
                var id = AddFile();
                var h = Make("alice", mode);
                Assert.AreEqual(403, Status(SpaFileApi.DeleteFile(h.Controller, h.Provider, id.ToString(), null, null)), mode.ToString());
                Assert.IsTrue(Exists(id), mode + ": the file is still there");
                Assert.IsTrue(h.Logs.Entries.Any(x => x.Message.Contains("DeletedFile") && x.Message.Contains("refused")), mode + ": logged");
            }
            var legacyId = AddFile();
            var legacy = Make("alice", FileEndpointMode.Legacy);
            Assert.AreEqual(200, Status(SpaFileApi.DeleteFile(legacy.Controller, legacy.Provider, legacyId.ToString(), null, null)));
            Assert.IsFalse(Exists(legacyId));
        }

        [TestMethod]
        public void Delete_WithTheReferenceOfTheUpload_DeletesTheFile_InEveryMode()
        {
            foreach (var mode in new[] { FileEndpointMode.Legacy, FileEndpointMode.Audit, FileEndpointMode.Enforce })
            {
                var deleteRef = UploadAs("alice", mode, out var id);
                var h = Make("alice", mode);
                Assert.AreEqual(200, Status(SpaFileApi.DeleteFile(h.Controller, h.Provider, id, null, deleteRef)), mode.ToString());
                Assert.IsFalse(Exists(Guid.Parse(id)), mode.ToString());
            }
        }

        [TestMethod]
        public void Delete_RefusesAReferenceOfAnotherUser_AnotherFile_OrARead()
        {
            var deleteRef = UploadAs("alice", FileEndpointMode.Enforce, out var id);
            var other = AddFile();
            var readCapability = Make("alice", FileEndpointMode.Enforce).Wtm.CreateFileCapability(Guid.Parse(id));
            var readReference = Make("alice", FileEndpointMode.Enforce).Wtm.CreateFileReference(Guid.Parse(id), FilePurpose.Read);

            Assert.AreEqual(403, Status(SpaFileApi.DeleteFile(Make("bob", FileEndpointMode.Enforce).Controller, Make("bob").Provider, id, null, deleteRef)), "another user");
            Assert.AreEqual(403, Status(SpaFileApi.DeleteFile(Make("alice", FileEndpointMode.Enforce).Controller, Make("alice").Provider, other.ToString(), null, deleteRef)), "another file");
            Assert.AreEqual(403, Status(SpaFileApi.DeleteFile(Make("alice", FileEndpointMode.Enforce).Controller, Make("alice").Provider, id, null, readCapability)), "a capability");
            Assert.AreEqual(403, Status(SpaFileApi.DeleteFile(Make("alice", FileEndpointMode.Enforce).Controller, Make("alice").Provider, id, null, readReference)), "a read reference");
            Assert.AreEqual(403, Status(SpaFileApi.DeleteFile(Make("alice", FileEndpointMode.Enforce).Controller, Make("alice").Provider, id, null, "garbage")), "garbage");
            Assert.IsTrue(Exists(Guid.Parse(id)));
            Assert.IsTrue(Exists(other));
        }

        [TestMethod]
        public void Delete_AsksThePolicy()
        {
            var deleteRef = UploadAs("alice", FileEndpointMode.Enforce, out var id);
            var policy = new DenyingPolicy();
            var h = Make("alice", FileEndpointMode.Enforce, policy: policy);
            Assert.AreEqual(403, Status(SpaFileApi.DeleteFile(h.Controller, h.Provider, id, null, deleteRef)));
            Assert.AreEqual(1, policy.Calls);
            Assert.IsTrue(Exists(Guid.Parse(id)));
        }

        [TestMethod]
        public void Delete_WithAReferenceOfAConnectionThatIsGone_IsRefused()
        {
            var deleteRef = UploadAs("alice", FileEndpointMode.Enforce, out var id);
            var h = Make("alice", FileEndpointMode.Enforce);
            Assert.AreEqual(403, Status(SpaFileApi.DeleteFile(h.Controller, h.Provider, id, "nowhere", deleteRef)));
        }

        // ---- the marker and the startup check ----------------------------------------------------------------------------------------------

        [Route("api/_file")]
        public class UnpatchedFileApi : BaseApiController
        {
            public IActionResult DeletedFile(string id) => Ok();
            public IActionResult GetFileInfo(string id) => Ok();
            public IActionResult GetFile(string id) => Ok();
        }

        [Route("/API/_FILE/")]
        public class PatchedFileApi : BaseApiController
        {
            [FileEndpoint]
            public IActionResult DeletedFile(string id) => Ok();
            [FileEndpoint(PublicWhenFilesPublic = true)]
            public IActionResult GetFileInfo(string id) => Ok();
        }

        [Route("api/_file")]
        [FileEndpoint]
        public class ClassMarkedFileApi : BaseApiController
        {
            public IActionResult DeletedFile(string id) => Ok();
        }

        [Route("api/other")]
        public class OtherApi : BaseApiController
        {
            public IActionResult DeletedFile(string id) => Ok();
        }

        [Route("api/_file")]
        public class HalfPatchedFileApi : BaseApiController
        {
            [FileEndpoint]
            public IActionResult DeletedFile(string id) => Ok();
            public IActionResult GetFileInfo(string id) => Ok();
        }

        [TestMethod]
        public void ACopyOfTheTemplateWithoutTheMarker_IsFoundAtStartup()
        {
            var found = SpaFileApi.FindUnpatchedControllers(new[] { typeof(UnpatchedFileApi), typeof(PatchedFileApi), typeof(ClassMarkedFileApi), typeof(OtherApi), typeof(HalfPatchedFileApi), typeof(string) });
            CollectionAssert.AreEquivalent(new[]
            {
                typeof(UnpatchedFileApi).FullName + ".DeletedFile",
                typeof(UnpatchedFileApi).FullName + ".GetFileInfo",
                typeof(HalfPatchedFileApi).FullName + ".GetFileInfo",
            }, found);
            Assert.AreEqual(0, SpaFileApi.FindUnpatchedControllers(null).Count);
        }

        [TestMethod]
        public void TheStartupWarning_NamesTheActions_AndNeverThrows()
        {
            var logs = new CapturingLoggerFactory();
            SpaFileApi.LogUnpatchedControllerWarning(new[] { typeof(UnpatchedFileApi).Assembly }, logs.CreateLogger("t"));
            var warnings = logs.Entries.Where(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning).ToList();
            Assert.IsTrue(warnings.Any(x => x.Message.Contains("UnpatchedFileApi.DeletedFile")));
            Assert.IsFalse(warnings.Any(x => x.Message.Contains("PatchedFileApi.DeletedFile") && !x.Message.Contains("UnpatchedFileApi")));
            SpaFileApi.LogUnpatchedControllerWarning(null, logs.CreateLogger("t"));
            SpaFileApi.LogUnpatchedControllerWarning(new Assembly[] { null }, null);
        }

        private class MarkedActions
        {
            [FileEndpoint(PublicWhenFilesPublic = true)] public void Open() { }
            [FileEndpoint] public void Marked() { }
            public void Plain() { }
        }

        [FileEndpoint(PublicWhenFilesPublic = true)]
        private class MarkedController
        {
            public void Any() { }
        }

        private class FileApiController
        {
            public void GetFile() { }
            public void GetFileName() { }
            public void DownloadFile() { }
            public void DeletedFile() { }
        }

        [TestMethod]
        public void ThePrivilegeFilter_OpensActionsMarkedPublicWhenFilesPublic_AndOnlyThose()
        {
            Assert.IsTrue(PrivilegeFilter.IsOpenFileApiAction(typeof(MarkedActions), typeof(MarkedActions).GetMethod("Open")));
            Assert.IsFalse(PrivilegeFilter.IsOpenFileApiAction(typeof(MarkedActions), typeof(MarkedActions).GetMethod("Marked")));
            Assert.IsFalse(PrivilegeFilter.IsOpenFileApiAction(typeof(MarkedActions), typeof(MarkedActions).GetMethod("Plain")));
            Assert.IsTrue(PrivilegeFilter.IsOpenFileApiAction(typeof(MarkedController), typeof(MarkedController).GetMethod("Any")), "the marker on the controller");
            // the rule by name is only for the template's own controller, which is not this test class
            Assert.IsFalse(PrivilegeFilter.IsOpenFileApiAction(typeof(FileApiController), typeof(FileApiController).GetMethod("GetFile")));
        }
    }

    internal static class FormFileCollectionExtensions
    {
        public static FormFileCollection Also(this FormFileCollection self, IEnumerable<IFormFile> files)
        {
            foreach (var file in files)
            {
                self.Add(file);
            }
            return self;
        }
    }
}
