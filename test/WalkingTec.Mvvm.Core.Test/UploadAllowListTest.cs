using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Mvc.Imaging;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// The optional upload allow-list (<c>FileEndpoints:UploadAllowList</c>) and the replaceable <see cref="IUploadFileRule"/>: the rule itself, the
    /// <c>/_Framework</c> upload endpoints, the single page application file API, the base64 branch of the UEditor endpoint and the thumbnail name.
    /// </summary>
    [TestClass]
    public class UploadAllowListTest
    {
        private static readonly string[] Standard = { "xls", "xlsx", "jpg", "jpeg", "png", "gif" };

        private string _seed;
        private object _oldHandlers;
        private object _oldDefault;

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

        // ---- fixtures -----------------------------------------------------------------------------------------------------------

        private static byte[] Picture(string format, int w = 40, int h = 20)
        {
            using var ms = new MemoryStream();
            using (var img = new Image<Rgba32>(w, h))
            {
                switch (format)
                {
                    case "png": img.Save(ms, new PngEncoder()); break;
                    case "jpg": img.Save(ms, new JpegEncoder()); break;
                    case "gif": img.Save(ms, new GifEncoder()); break;
                    case "bmp": img.Save(ms, new BmpEncoder()); break;
                    case "webp": img.Save(ms, new WebpEncoder()); break;
                    default: throw new ArgumentException(format);
                }
            }
            return ms.ToArray();
        }

        private static byte[] Zip(params string[] parts)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var part in parts)
                {
                    var entry = zip.CreateEntry(part);
                    using var w = new StreamWriter(entry.Open());
                    w.Write("<x/>");
                }
            }
            return ms.ToArray();
        }

        private static byte[] Xlsx() => Zip("[Content_Types].xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml");
        private static byte[] Docx() => Zip("[Content_Types].xml", "word/document.xml");
        private static byte[] Xls() => new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0, 1, 2, 3, 4 };
        private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

        private static byte[] Content(string kind)
        {
            switch (kind)
            {
                case "xls": return Xls();
                case "xlsx": return Xlsx();
                case "jpg":
                case "jpeg": return Picture("jpg");
                case "png": return Picture("png");
                case "gif": return Picture("gif");
                default: throw new ArgumentException(kind);
            }
        }

        private sealed class ForwardOnlyStream : MemoryStream
        {
            public ForwardOnlyStream(byte[] data) : base(data) { }
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
        }

        /// <summary>A form file whose stream cannot seek and can be opened once, like the body of a request that is read as it arrives.</summary>
        private sealed class OneShotFormFile : IFormFile
        {
            private readonly byte[] _data;
            public int Opened;
            public OneShotFormFile(string name, byte[] data) { FileName = name; _data = data; }
            public string ContentType => "application/octet-stream";
            public string ContentDisposition => string.Empty;
            public IHeaderDictionary Headers => new HeaderDictionary();
            public long Length => _data.Length;
            public string Name => "file";
            public string FileName { get; }
            public void CopyTo(Stream target) => OpenReadStream().CopyTo(target);
            public System.Threading.Tasks.Task CopyToAsync(Stream target, System.Threading.CancellationToken cancellationToken = default) => OpenReadStream().CopyToAsync(target, cancellationToken);
            public Stream OpenReadStream()
            {
                Opened++;
                if (Opened > 1)
                {
                    throw new InvalidOperationException("the upload can be read once");
                }
                return new ForwardOnlyStream(_data);
            }
        }

        private sealed class DelegateRule : IUploadFileRule
        {
            private readonly Func<UploadFileRequest, bool> _rule;
            public int Calls;
            public DelegateRule(Func<UploadFileRequest, bool> rule) { _rule = rule; }
            public bool IsAllowed(UploadFileRequest request) { Calls++; return _rule(request); }
        }

        private static IFormFile Form(string name, byte[] data) => new FormFile(new MemoryStream(data), 0, data.Length, "file", name);

        private sealed class Api : BaseApiController { }

        private sealed class Harness
        {
            public _FrameworkController Controller;
            public Api Spa;
            public WTMContext Wtm;
            public WtmFileProvider Provider;
            public CapturingLoggerFactory Logs;
        }

        private Harness Make(IEnumerable<string> allowed = null, bool verify = true, IUploadFileRule rule = null, bool registerDefaultRule = true,
            Dictionary<string, string> limits = null, IEnumerable<IFormFile> files = null, Dictionary<string, string> fields = null, long? uploadLimit = null, IServiceProvider ruleProvider = null)
        {
            var seed = _seed;
            var configs = new Configs();
            configs.Connections = new List<CS> { new CS { Key = "default", Value = "x", DbType = DBTypeEnum.Memory } };
            if (allowed != null)
            {
                configs.FileEndpoints.UploadAllowList.Extensions = allowed.ToList();
            }
            configs.FileEndpoints.UploadAllowList.VerifyContent = verify;
            if (uploadLimit != null)
            {
                configs.FileUploadOptions.UploadLimit = uploadLimit.Value;
            }
            var logs = new CapturingLoggerFactory();
            var wtm = MockWtmContext.CreateWtmContext(new LoginTestContext(seed), "alice", loggerFactory: logs, configs: configs, createDc: key => new LoginTestContext(seed),
                uploadFileRule: rule ?? (registerDefaultRule && ruleProvider == null ? new ConfiguredUploadFileRule() : null), uploadRuleProvider: ruleProvider);
            wtm.GlobaInfo.CustomUserType = typeof(LoginTestUser);
            var services = new ServiceCollection();
            var values = new Dictionary<string, string>();
            foreach (var item in limits ?? new Dictionary<string, string>())
            {
                values["ImageUploadLimits:" + item.Key] = item.Value;
            }
            services.AddSingleton<ILoggerFactory>(logs);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
            http.Response.Body = new MemoryStream();
            if (files != null || fields != null)
            {
                var form = new FormFileCollection();
                foreach (var f in files ?? Enumerable.Empty<IFormFile>())
                {
                    form.Add(f);
                }
                http.Request.ContentType = "multipart/form-data; boundary=x";
                http.Request.Form = new FormCollection((fields ?? new Dictionary<string, string>()).ToDictionary(x => x.Key, x => new StringValues(x.Value)), form);
            }
            var controller = new _FrameworkController(null) { Wtm = wtm, ControllerContext = new ControllerContext { HttpContext = http } };
            var spa = new Api { Wtm = wtm, ControllerContext = new ControllerContext { HttpContext = http } };
            return new Harness { Controller = controller, Spa = spa, Wtm = wtm, Provider = new WtmFileProvider(wtm), Logs = logs };
        }

        private List<FileAttachment> Stored()
        {
            using var dc = new LoginTestContext(_seed);
            return dc.Set<FileAttachment>().ToList();
        }

        private static (int Code, string Id, string Msg) Answer(IActionResult result)
        {
            var ok = (OkObjectResult)result;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            return (doc.RootElement.GetProperty("Code").GetInt32(), doc.RootElement.GetProperty("Data").GetProperty("Id").GetString(), doc.RootElement.GetProperty("Msg").GetString());
        }

        private static JsonElement Json(IActionResult result)
        {
            var text = result is OkObjectResult ok && ok.Value is string s ? s : ((ContentResult)result).Content;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        private static int Status(IActionResult result) => result switch
        {
            ContentResult c => c.StatusCode ?? 200,
            ObjectResult o => o.StatusCode ?? 200,
            StatusCodeResult s => s.StatusCode,
            _ => 200
        };

        private static UploadFileCheck Check(WTMContext wtm, string name, byte[] data, out int opened)
        {
            var count = 0;
            var check = UploadFileGuard.Check(wtm, "Upload", name, data.Length, () => { count++; return new MemoryStream(data); });
            opened = count;
            return check;
        }

        // ---- the rule -----------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void NotConfigured_NothingIsChecked_AndTheContentIsNotRead()
        {
            var h = Make();

            using var check = Check(h.Wtm, "anything.bin", Text("binary"), out var opened);

            Assert.IsFalse(check.Applied);
            Assert.IsTrue(check.Allowed);
            Assert.IsNull(check.Content);
            Assert.AreEqual(0, opened);
        }

        [TestMethod]
        public void NoRuleRegistered_NothingIsChecked()
        {
            var h = Make(allowed: Standard, registerDefaultRule: false);

            using var check = Check(h.Wtm, "anything.bin", Text("binary"), out _);

            Assert.IsFalse(check.Applied);
            Assert.IsTrue(check.Allowed);
        }

        [TestMethod]
        [DataRow("xls")]
        [DataRow("xlsx")]
        [DataRow("jpg")]
        [DataRow("jpeg")]
        [DataRow("png")]
        [DataRow("gif")]
        public void AFileWhoseExtensionAndContentAgree_IsAllowed(string ext)
        {
            var h = Make(allowed: Standard);

            using var check = Check(h.Wtm, "report." + ext, Content(ext), out _);

            Assert.IsTrue(check.Applied);
            Assert.IsTrue(check.Allowed, ext);
        }

        [TestMethod]
        [DataRow("report.bin")]
        [DataRow("report.txt")]
        [DataRow("report.html")]
        [DataRow("report.pdf")]
        [DataRow("report")]
        [DataRow("report.")]
        [DataRow(".")]
        [DataRow("")]
        [DataRow("report.png.bin")]
        [DataRow("report.jpg ")]
        [DataRow("report.png::$DATA")]
        public void AnExtensionOutsideTheList_IsRefused_WhateverTheContent(string name)
        {
            var h = Make(allowed: Standard);

            using var check = Check(h.Wtm, name, Picture("png"), out var opened);

            Assert.IsTrue(check.Applied);
            Assert.IsFalse(check.Allowed);
            Assert.AreEqual(ConfiguredUploadFileRule.ExtensionRefused, check.Reason);
            Assert.AreEqual(0, opened, "the content is not read for an extension that is refused anyway");
        }

        [TestMethod]
        public void OnlyTheLastExtensionCounts_SoADoubleExtensionIsJudgedByItsEnd()
        {
            var h = Make(allowed: Standard);

            using (var allowed = Check(h.Wtm, "archive.bin.png", Picture("png"), out _))
            {
                Assert.IsTrue(allowed.Allowed);
            }
            using (var refused = Check(h.Wtm, "photo.png.bin", Picture("png"), out _))
            {
                Assert.IsFalse(refused.Allowed);
            }
            using (var mismatch = Check(h.Wtm, "photo.png.gif", Picture("png"), out _))
            {
                Assert.IsFalse(mismatch.Allowed, "the content has to agree with the last extension");
            }
        }

        [TestMethod]
        [DataRow("PHOTO.PNG")]
        [DataRow("photo.Png")]
        [DataRow("c:\\temp\\photo.PNG")]
        [DataRow("folder/photo.png")]
        public void TheExtensionIsMatchedWithoutRegardToCase_AndAfterAnyPath(string name)
        {
            var h = Make(allowed: new[] { "png" });

            using var check = Check(h.Wtm, name, Picture("png"), out _);

            Assert.IsTrue(check.Allowed, name);
        }

        [TestMethod]
        public void TheListIsReadWithoutRegardToCaseDotsAndSpaces()
        {
            var h = Make(allowed: new[] { " .PNG ", "Gif" });

            using (var png = Check(h.Wtm, "a.png", Picture("png"), out _)) { Assert.IsTrue(png.Allowed); }
            using (var gif = Check(h.Wtm, "a.gif", Picture("gif"), out _)) { Assert.IsTrue(gif.Allowed); }
            using (var jpg = Check(h.Wtm, "a.jpg", Picture("jpg"), out _)) { Assert.IsFalse(jpg.Allowed); }
        }

        [TestMethod]
        [DataRow("jpg", "photo.jpeg")]
        [DataRow("jpeg", "photo.jpg")]
        [DataRow("jpeg", "photo.JPG")]
        [DataRow("jpg", "photo.JPEG")]
        public void JpgAndJpeg_AreTheSameType(string listed, string name)
        {
            var h = Make(allowed: new[] { listed });

            using var check = Check(h.Wtm, name, Picture("jpg"), out _);

            Assert.IsTrue(check.Allowed, name);
        }

        [TestMethod]
        public void ContentThatDoesNotMatchTheExtension_IsRefused()
        {
            var h = Make(allowed: Standard);
            var cases = new (string Name, byte[] Data)[]
            {
                ("a.png", Picture("jpg")),
                ("a.jpg", Picture("png")),
                ("a.gif", Picture("png")),
                ("a.png", Text("<html><script>alert(1)</script></html>")),
                ("a.png", Array.Empty<byte>()),
                ("a.xlsx", Xls()),
                ("a.xls", Xlsx()),
                ("a.xlsx", Docx()),
                ("a.xlsx", Zip("hello.txt")),
                ("a.xlsx", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 }),
                ("a.xlsx", Picture("png")),
                ("a.xls", Text("binary content")),
            };
            foreach (var (name, data) in cases)
            {
                using var check = Check(h.Wtm, name, data, out _);
                Assert.IsFalse(check.Allowed, name);
                Assert.AreEqual(ConfiguredUploadFileRule.ContentRefused, check.Reason, name);
            }
        }

        [TestMethod]
        public void WithoutContentVerification_TheExtensionAloneDecides()
        {
            var h = Make(allowed: Standard, verify: false);

            using var mismatch = Check(h.Wtm, "a.png", Text("not a picture"), out var opened);
            using var refused = Check(h.Wtm, "a.bin", Text("binary"), out _);

            Assert.IsTrue(mismatch.Allowed);
            Assert.AreEqual(0, opened);
            Assert.IsFalse(refused.Allowed);
        }

        [TestMethod]
        public void AnAllowedExtensionWithoutAContentRule_PassesOnItsExtension_AndIsNotRead()
        {
            var h = Make(allowed: new[] { "pdf", "txt" });

            using var check = Check(h.Wtm, "a.pdf", Text("whatever"), out var opened);

            Assert.IsTrue(check.Allowed);
            Assert.AreEqual(0, opened);
        }

        [TestMethod]
        public void AListThatMatchesNothing_RefusesEverything()
        {
            var h = Make(allowed: new[] { "*", "" });

            using var check = Check(h.Wtm, "a.png", Picture("png"), out _);

            Assert.IsTrue(check.Applied);
            Assert.IsFalse(check.Allowed);
        }

        [TestMethod]
        public void ARefusal_IsLoggedWithAFixedReason_NotTheFileNameOrContent()
        {
            var h = Make(allowed: Standard);

            using (Check(h.Wtm, "secret-name-for-the-log.bin", Text("secret-content"), out _)) { }
            using (Check(h.Wtm, "secret-name-for-the-log.png", Text("secret-content"), out _)) { }

            var warnings = h.Logs.Entries.Where(x => x.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(2, warnings.Count);
            StringAssert.Contains(warnings[0].Message, ConfiguredUploadFileRule.ExtensionRefused);
            StringAssert.Contains(warnings[1].Message, ConfiguredUploadFileRule.ContentRefused);
            foreach (var w in warnings)
            {
                Assert.IsFalse(w.Message.Contains("secret"), w.Message);
                StringAssert.Contains(w.Message, "Upload");
            }
        }

        [TestMethod]
        public void ASourceThatCannotSeek_IsCopiedSoThatItCanBeCheckedAndThenStored()
        {
            var h = Make(allowed: Standard);
            var data = Xlsx();

            using var check = UploadFileGuard.Check(h.Wtm, "Upload", "a.xlsx", data.Length, () => new ForwardOnlyStream(data));

            Assert.IsTrue(check.Allowed);
            Assert.IsNotNull(check.Content);
            Assert.IsTrue(check.Content.CanSeek);
            Assert.AreEqual(0, check.Content.Position, "the content is handed on from its start");
            var copy = new MemoryStream();
            check.Content.CopyTo(copy);
            CollectionAssert.AreEqual(data, copy.ToArray());
        }

        [TestMethod]
        public void ASourceThatCannotSeek_AndIsLargerThanTheUploadLimit_IsRefused_NotBuffered()
        {
            var h = Make(allowed: Standard, uploadLimit: 100);
            var data = new byte[1000];
            Array.Copy(Picture("png"), data, 8);

            using var check = UploadFileGuard.Check(h.Wtm, "Upload", "a.png", data.Length, () => new ForwardOnlyStream(data));

            Assert.IsFalse(check.Allowed);
            Assert.AreEqual(ConfiguredUploadFileRule.ContentUnreadable, check.Reason);
        }

        [TestMethod]
        public void ACustomRule_ReplacesTheDefault_AndSeesTheRequest()
        {
            UploadFileRequest seen = null;
            var rule = new DelegateRule(r => { seen = r; r.RefusalReason = "custom reason"; return false; });
            var h = Make(allowed: null, rule: rule);

            using var check = Check(h.Wtm, "A.Docx", Text("x"), out var opened);

            Assert.IsTrue(check.Applied, "a rule that does not say otherwise is always in force");
            Assert.IsFalse(check.Allowed);
            Assert.AreEqual("custom reason", check.Reason);
            Assert.AreEqual("docx", seen.Extension);
            Assert.AreEqual("Upload", seen.Endpoint);
            Assert.AreEqual(0, opened);
            StringAssert.Contains(h.Logs.Entries.Single(x => x.Level == LogLevel.Warning).Message, "custom reason");
        }

        [TestMethod]
        public void ACustomRule_CanReadTheContent_AndTheEndpointStoresFromTheSameCopy()
        {
            var seenLength = -1L;
            var rule = new DelegateRule(r => { seenLength = r.Content.Length; r.Content.ReadByte(); return true; });
            var h = Make(rule: rule);
            var data = Picture("png");

            using var check = UploadFileGuard.Check(h.Wtm, "Upload", "a.png", data.Length, () => new ForwardOnlyStream(data));

            Assert.IsTrue(check.Allowed);
            Assert.AreEqual(data.Length, seenLength);
            Assert.AreEqual(0, check.Content.Position, "rewound after the rule has read it");
        }

        [TestMethod]
        public void ARuleThatThrows_RefusesTheUpload_AndLogsOnlyTheExceptionType()
        {
            var rule = new DelegateRule(_ => throw new InvalidOperationException("secret detail"));
            var h = Make(rule: rule);

            using var check = Check(h.Wtm, "a.png", Picture("png"), out _);

            Assert.IsFalse(check.Allowed);
            var error = h.Logs.Entries.Single(x => x.Level == LogLevel.Error);
            StringAssert.Contains(error.Message, nameof(InvalidOperationException));
            Assert.IsFalse(error.Message.Contains("secret detail"));
        }

        [TestMethod]
        public void ARuleThatIsNotActive_LeavesEverythingAsItWas()
        {
            var h = Make(rule: new InactiveRule());

            using var check = Check(h.Wtm, "a.bin", Text("x"), out var opened);

            Assert.IsFalse(check.Applied);
            Assert.IsTrue(check.Allowed);
            Assert.AreEqual(0, opened);
        }

        [TestMethod]
        public void ARuleThatCannotBeCreated_RefusesTheUpload_AndLogsOnlyTheExceptionType()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IUploadFileRule>(_ => throw new InvalidOperationException("secret detail"));
            var h = Make(ruleProvider: services.BuildServiceProvider());

            using var check = Check(h.Wtm, "a.png", Picture("png"), out var opened);

            Assert.IsTrue(check.Applied);
            Assert.IsFalse(check.Allowed);
            Assert.AreEqual(0, opened);
            var error = h.Logs.Entries.Single(x => x.Level == LogLevel.Error);
            StringAssert.Contains(error.Message, nameof(InvalidOperationException));
            Assert.IsFalse(error.Message.Contains("secret detail"));
        }

        [TestMethod]
        public void ARuleThatCannotBeCreated_RefusesTheUploadAtTheEndpoints()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IUploadFileRule>(_ => throw new InvalidOperationException("x"));
            var h = Make(ruleProvider: services.BuildServiceProvider(), files: new[] { Form("a.png", Picture("png")) });

            Assert.AreEqual(400, Answer(h.Controller.Upload(h.Provider)).Code);
            Assert.AreEqual(0, Stored().Count);
        }

        private static byte[] ZipWithEntries(int count)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                zip.CreateEntry("[Content_Types].xml");
                zip.CreateEntry("xl/workbook.xml");
                for (var i = 2; i < count; i++)
                {
                    zip.CreateEntry("xl/worksheets/s" + i + ".xml");
                }
            }
            return ms.ToArray();
        }

        /// <summary>A package with the parts of a small real workbook (content types, relationships, workbook, one sheet, styles).</summary>
        private static byte[] RealXlsx()
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                void Part(string name, string xml)
                {
                    using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                    w.Write(xml);
                }
                Part("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                Part("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Part("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Part("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Part("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>hello</t></is></c></row></sheetData></worksheet>");
                Part("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills><borders count=\"1\"><border/></borders><cellXfs count=\"1\"><xf/></cellXfs></styleSheet>");
            }
            return ms.ToArray();
        }

        [TestMethod]
        public void Xlsx_ARealWorkbookPackage_Passes()
        {
            var data = RealXlsx();
            using (var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
            {
                Assert.AreEqual(6, archive.Entries.Count, "the fixture is a package with its usual parts");
            }
            using var ms = new MemoryStream(data);
            Assert.AreEqual(true, UploadContentSniffer.Matches("xlsx", ms));
            Assert.AreEqual(0, ms.Position);
        }

        [TestMethod]
        public void Xlsx_AZipWithoutTheWorkbookPart_OrWithoutTheContentTypes_IsRefused()
        {
            using var noWorkbook = new MemoryStream(Zip("[Content_Types].xml", "word/document.xml"));
            using var noTypes = new MemoryStream(Zip("xl/workbook.xml", "xl/worksheets/sheet1.xml"));

            Assert.AreEqual(false, UploadContentSniffer.Matches("xlsx", noWorkbook));
            Assert.AreEqual(false, UploadContentSniffer.Matches("xlsx", noTypes));
        }

        [TestMethod]
        public void Xlsx_ADirectoryWithMoreEntriesThanTheCap_IsRefused_BeforeItIsRead()
        {
            using var atCap = new MemoryStream(ZipWithEntries(10000));
            using var overCap = new MemoryStream(ZipWithEntries(10001));

            Assert.AreEqual(true, UploadContentSniffer.Matches("xlsx", atCap));
            Assert.AreEqual(false, UploadContentSniffer.Matches("xlsx", overCap));
            Assert.AreEqual(0, overCap.Position);
        }

        [TestMethod]
        public void Xlsx_NoEndRecord_OrZip64_OrACorruptedTail_IsRefused()
        {
            var good = RealXlsx();
            // no end-of-central-directory record: the tail is cut off
            using var cut = new MemoryStream(good.Take(good.Length - 30).ToArray());
            // a record whose entry count is the Zip64 marker
            var zip64 = (byte[])good.Clone();
            var at = zip64.Length - 22;
            zip64[at + 10] = 0xFF;
            zip64[at + 11] = 0xFF;
            using var marker = new MemoryStream(zip64);
            // a record that claims a comment that is not there
            var wrongComment = (byte[])good.Clone();
            wrongComment[at + 20] = 5;
            using var comment = new MemoryStream(wrongComment);
            // only the signature of a zip
            using var tiny = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 });

            Assert.AreEqual(false, UploadContentSniffer.Matches("xlsx", cut));
            Assert.AreEqual(false, UploadContentSniffer.Matches("xlsx", marker));
            Assert.AreEqual(false, UploadContentSniffer.Matches("xlsx", comment));
            Assert.AreEqual(false, UploadContentSniffer.Matches("xlsx", tiny));
        }

        [TestMethod]
        public void Xlsx_APackageWithACommentOnTheArchive_Passes()
        {
            var data = RealXlsx();
            // append a comment of 10 bytes and set its length in the end record
            var withComment = data.Concat(new byte[10]).ToArray();
            var at = data.Length - 22;
            withComment[at + 20] = 10;
            using var ms = new MemoryStream(withComment);

            Assert.AreEqual(true, UploadContentSniffer.Matches("xlsx", ms));
        }

        private sealed class InactiveRule : IUploadFileRule
        {
            public bool IsActive(WTMContext wtm) => false;
            public bool IsAllowed(UploadFileRequest request) => throw new InvalidOperationException("not asked");
        }

        [TestMethod]
        public void TheDefaultRuleIsRegisteredWithTryAdd_SoAnApplicationCanReplaceIt()
        {
            var source = File.ReadAllText(FindFile("src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs"));
            StringAssert.Contains(source, "services.TryAddSingleton<IUploadFileRule, ConfiguredUploadFileRule>();");
            var services = new ServiceCollection();
            services.AddSingleton<IUploadFileRule>(new DelegateRule(_ => true));
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IUploadFileRule, ConfiguredUploadFileRule>(services);
            Assert.IsInstanceOfType(services.BuildServiceProvider().GetServices<IUploadFileRule>().First(), typeof(DelegateRule));
        }

        private static string FindFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && File.Exists(Path.Combine(dir.FullName, relative)) == false)
            {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, relative);
            return Path.Combine(dir.FullName, relative);
        }

        [TestMethod]
        public void TheOptionsBindFromConfiguration()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                { "FileEndpoints:UploadAllowList:Extensions:0", "xls" },
                { "FileEndpoints:UploadAllowList:Extensions:1", "png" },
                { "FileEndpoints:UploadAllowList:VerifyContent", "false" },
            }).Build();
            var configs = new Configs();

            config.Bind(configs);

            CollectionAssert.AreEqual(new[] { "xls", "png" }, configs.FileEndpoints.UploadAllowList.Extensions);
            Assert.IsFalse(configs.FileEndpoints.UploadAllowList.VerifyContent);
            Assert.IsTrue(configs.FileEndpoints.UploadAllowList.IsEnabled);
            Assert.IsFalse(new Configs().FileEndpoints.UploadAllowList.IsEnabled, "off by default");
            Assert.IsTrue(new Configs().FileEndpoints.UploadAllowList.VerifyContent, "content verification is on by default");
        }

        [TestMethod]
        [DataRow("photo.gif", "photo.jpg")]
        [DataRow("photo.png", "photo.jpg")]
        [DataRow("photo.bmp", "photo.jpg")]
        [DataRow("photo.jpg", "photo.jpg")]
        [DataRow("Photo.JPEG", "Photo.JPEG")]
        [DataRow("a.b.gif", "a.b.jpg")]
        [DataRow("photo", "photo.jpg")]
        [DataRow("photo.", "photo.jpg")]
        public void ThumbnailFileName_SaysJpeg(string name, string expected)
        {
            Assert.AreEqual(expected, UploadFileNames.ThumbnailFileName(name));
        }

        // ---- /_Framework endpoints ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Upload_NotConfigured_StoresWhateverItIs_AsBefore()
        {
            var h = Make(files: new[] { Form("a.bin", Text("binary")) });

            var (code, id, _) = Answer(h.Controller.Upload(h.Provider));

            Assert.AreEqual(200, code);
            Assert.AreEqual("a.bin", Stored().Single(x => x.ID == Guid.Parse(id)).FileName);
        }

        [TestMethod]
        [DataRow("xls")]
        [DataRow("xlsx")]
        [DataRow("jpg")]
        [DataRow("png")]
        [DataRow("gif")]
        public void Upload_Configured_StoresAMatchingFile_AndTheAnswerIsTheSame(string ext)
        {
            var data = Content(ext);
            var h = Make(allowed: Standard, files: new[] { Form("a." + ext, data) });

            var (code, id, _) = Answer(h.Controller.Upload(h.Provider));

            Assert.AreEqual(200, code);
            var stored = Stored().Single();
            Assert.AreEqual(id, stored.ID.ToString());
            Assert.AreEqual("a." + ext, stored.FileName);
            CollectionAssert.AreEqual(data, stored.FileData);
        }

        [TestMethod]
        [DataRow("a.bin", "binary")]
        [DataRow("a", "binary")]
        [DataRow("a.png", "<html>")]
        public void Upload_Configured_RefusesAFileOutsideTheList_AndStoresNothing(string name, string content)
        {
            var h = Make(allowed: Standard, files: new[] { Form(name, Text(content)) });

            var (code, id, msg) = Answer(h.Controller.Upload(h.Provider));

            Assert.AreEqual(400, code);
            Assert.AreEqual(string.Empty, id, "the components report an empty id as a failed upload");
            Assert.AreEqual("Upload failed", msg);
            Assert.AreEqual(0, Stored().Count);
        }

        [TestMethod]
        public void Upload_Configured_FromASourceThatCannotSeek_StoresTheWholeFile()
        {
            var data = Xlsx();
            var file = new OneShotFormFile("a.xlsx", data);
            var h = Make(allowed: Standard, files: new IFormFile[] { file });

            var (code, _, _) = Answer(h.Controller.Upload(h.Provider));

            Assert.AreEqual(200, code);
            CollectionAssert.AreEqual(data, Stored().Single().FileData);
            Assert.AreEqual(1, file.Opened);
        }

        [TestMethod]
        public void Upload_NotConfigured_FromASourceThatCannotSeek_IsOpenedOnceByTheEndpointAsBefore()
        {
            var data = Picture("png");
            var file = new OneShotFormFile("a.png", data);
            var h = Make(files: new IFormFile[] { file });

            Answer(h.Controller.Upload(h.Provider));

            CollectionAssert.AreEqual(data, Stored().Single().FileData);
            Assert.AreEqual(1, file.Opened);
        }

        [TestMethod]
        public void Upload_WithACustomRule_UsesIt()
        {
            var rule = new DelegateRule(r => r.Extension == "csv");
            var h = Make(rule: rule, files: new[] { Form("a.csv", Text("a,b")) });
            var h2 = Make(rule: rule, files: new[] { Form("a.png", Picture("png")) });

            Assert.AreEqual(200, Answer(h.Controller.Upload(h.Provider)).Code);
            Assert.AreEqual(400, Answer(h2.Controller.Upload(h2.Provider)).Code);
            Assert.AreEqual(1, Stored().Count);
        }

        [TestMethod]
        public void UploadImage_WithoutASize_FollowsTheAllowList()
        {
            var h = Make(allowed: Standard, files: new[] { Form("a.png", Text("not a picture")) });

            Assert.AreEqual(400, Answer(h.Controller.UploadImage(h.Provider)).Code);
            Assert.AreEqual(0, Stored().Count);
        }

        [TestMethod]
        [DataRow("photo.gif", "gif", "photo.jpg")]
        [DataRow("photo.png", "png", "photo.jpg")]
        [DataRow("photo.jpeg", "jpg", "photo.jpeg")]
        [DataRow("photo.JPG", "jpg", "photo.JPG")]
        public void UploadImage_WithASize_Configured_StoresTheThumbnailUnderAJpegName(string name, string kind, string expectedName)
        {
            var h = Make(allowed: Standard, files: new[] { Form(name, Picture(kind)) });

            var (code, id, _) = Answer(h.Controller.UploadImage(h.Provider, width: 10));

            Assert.AreEqual(200, code);
            var stored = Stored().Single(x => x.ID == Guid.Parse(id));
            Assert.AreEqual(expectedName, stored.FileName);
            Assert.AreEqual(stored.FileData.Length, stored.Length, "the recorded length is the length of the stored thumbnail");
            Assert.AreEqual("jpg", stored.FileExt.TrimStart('.').ToLowerInvariant().Replace("jpeg", "jpg"));
            var info = Image.Identify(stored.FileData);
            Assert.AreEqual("JPEG", info.Metadata.DecodedImageFormat.Name);
            Assert.AreEqual(10, info.Width);
        }

        [TestMethod]
        public void UploadImage_WithASize_NotConfigured_KeepsTheOriginalNameAsBefore()
        {
            var h = Make(files: new[] { Form("photo.gif", Picture("gif")) });

            var (code, id, _) = Answer(h.Controller.UploadImage(h.Provider, width: 10));

            Assert.AreEqual(200, code);
            var stored = Stored().Single(x => x.ID == Guid.Parse(id));
            Assert.AreEqual("photo.gif", stored.FileName, "the name is stored as it came, although the content is JPEG");
            Assert.AreEqual(stored.FileData.Length, stored.Length);
            Assert.AreEqual("JPEG", Image.Identify(stored.FileData).Metadata.DecodedImageFormat.Name);
        }

        [TestMethod]
        public void UploadImage_WithASize_Configured_ChecksTheOriginal_BeforeAnyThumbnailIsMade()
        {
            // a JPEG named .png: without the allow-list a thumbnail is made; with it the original is refused first (400, not the 404 of the image guard)
            var h = Make(allowed: Standard, files: new[] { Form("photo.png", Picture("jpg")) });
            var h2 = Make(allowed: Standard, files: new[] { Form("photo.bin", Picture("png")) });

            Assert.AreEqual(400, Answer(h.Controller.UploadImage(h.Provider, width: 10)).Code);
            Assert.AreEqual(400, Answer(h2.Controller.UploadImage(h2.Provider, width: 10)).Code);
            Assert.AreEqual(0, Stored().Count);
        }

        [TestMethod]
        public void UploadImage_WithASize_FromASourceThatCannotSeek_Works_WithAndWithoutTheAllowList()
        {
            var data = Picture("gif", 40, 20);
            var configured = new OneShotFormFile("a.gif", data);
            var plain = new OneShotFormFile("b.gif", data);
            var h = Make(allowed: Standard, files: new IFormFile[] { configured });
            var h2 = Make(files: new IFormFile[] { plain });

            Assert.AreEqual(200, Answer(h.Controller.UploadImage(h.Provider, width: 10)).Code);
            Assert.AreEqual(200, Answer(h2.Controller.UploadImage(h2.Provider, width: 10)).Code);

            Assert.AreEqual(1, configured.Opened);
            Assert.AreEqual(1, plain.Opened);
            Assert.AreEqual(2, Stored().Count);
        }

        [TestMethod]
        public void UploadForLayUIRichTextBox_Configured_RefusesAFileOutsideTheList_InTheShapeOfAFailure()
        {
            var h = Make(allowed: Standard, files: new[] { Form("a.html", Text("<html>")) });

            var result = (ContentResult)h.Controller.UploadForLayUIRichTextBox(h.Provider);

            var json = Json(result);
            Assert.AreEqual(1, json.GetProperty("code").GetInt32());
            Assert.AreEqual(string.Empty, json.GetProperty("data").GetProperty("src").GetString());
            Assert.AreEqual(0, Stored().Count);
        }

        [TestMethod]
        public void UploadForLayUIRichTextBox_Configured_StoresAMatchingFile()
        {
            var h = Make(allowed: Standard, files: new[] { Form("a.png", Picture("png")) });

            var json = Json(h.Controller.UploadForLayUIRichTextBox(h.Provider));

            Assert.AreEqual(0, json.GetProperty("code").GetInt32());
            Assert.AreEqual(1, Stored().Count);
        }

        [TestMethod]
        public void UploadForLayUIUEditor_File_Configured_RefusesAFileOutsideTheList_ForTheUEditorDialogs()
        {
            var h = Make(allowed: Standard, files: new[] { Form("a.html", Text("<html>")) });

            var json = Json(h.Controller.UploadForLayUIUEditor(h.Provider));

            // the dialogs read Code (200 is success), Msg and Data.src; the members of the earlier failure answer stay
            Assert.AreNotEqual("200", json.GetProperty("Code").ToString());
            Assert.AreEqual("Upload failed", json.GetProperty("Msg").GetString());
            Assert.AreEqual(string.Empty, json.GetProperty("Data").GetProperty("src").GetString());
            Assert.AreEqual(1, json.GetProperty("code").GetInt32());
            Assert.AreEqual(string.Empty, json.GetProperty("data").GetProperty("src").GetString());
            Assert.AreEqual(0, Stored().Count);
        }

        [TestMethod]
        public void UploadForLayUIUEditor_File_Configured_StoresAMatchingFile_AndAnswersAsBefore()
        {
            var h = Make(allowed: Standard, files: new[] { Form("a.png", Picture("png")) });

            var json = Json(h.Controller.UploadForLayUIUEditor(h.Provider));

            Assert.AreEqual("200", json.GetProperty("Code").ToString());
            Assert.AreEqual("a.png", json.GetProperty("Data").GetProperty("FileName").GetString());
            Assert.AreEqual(1, Stored().Count);
        }

        // ---- UEditor base64 branch ------------------------------------------------------------------------------------------------

        private static Dictionary<string, string> Scrawl(byte[] data) => new Dictionary<string, string> { { "FileID", Convert.ToBase64String(data) } };

        [TestMethod]
        [DataRow("png", "png")]
        [DataRow("jpg", "jpg")]
        [DataRow("gif", "gif")]
        [DataRow("bmp", "bmp")]
        [DataRow("webp", "webp")]
        public void UEditorBase64_StoresAPicture_UnderTheExtensionOfTheDetectedFormat(string kind, string expectedExt)
        {
            var data = Picture(kind);
            var h = Make(fields: Scrawl(data));

            var json = Json(h.Controller.UploadForLayUIUEditor(h.Provider));

            Assert.AreEqual("200", json.GetProperty("Code").ToString());
            var stored = Stored().Single();
            StringAssert.StartsWith(stored.FileName, "SCRAWL_");
            StringAssert.EndsWith(stored.FileName, "." + expectedExt);
            CollectionAssert.AreEqual(data, stored.FileData);
            Assert.AreEqual(json.GetProperty("Data").GetProperty("FileName").GetString(), stored.FileName);
        }

        [TestMethod]
        public void UEditorBase64_ThePngTheScrawlDialogSends_IsStoredAsPng()
        {
            // the dialog posts canvas.toDataURL("image/png").substring(22): the base64 of a PNG without the data: prefix, urlencoded in the body
            var data = Picture("png", 300, 200);
            var h = Make(fields: Scrawl(data));

            var json = Json(h.Controller.UploadForLayUIUEditor(h.Provider));

            Assert.AreEqual("200", json.GetProperty("Code").ToString());
            StringAssert.EndsWith(Stored().Single().FileName, ".png");
            StringAssert.StartsWith(json.GetProperty("Data").GetProperty("src").GetString(), "/_Framework/GetFile?id=");
        }

        [TestMethod]
        [DataRow("%%% not base64 %%%")]
        [DataRow("QUJD=QUJD")]
        [DataRow("A")]
        [DataRow("")]
        [DataRow("   ")]
        public void UEditorBase64_InvalidBase64_AnswersUploadFailed_AndThrowsNothing(string text)
        {
            var h = Make(fields: new Dictionary<string, string> { { "FileID", text } });

            var json = Json(h.Controller.UploadForLayUIUEditor(h.Provider));

            AssertUEditorFailure(json);
            Assert.AreEqual(0, Stored().Count);
        }

        private static void AssertUEditorFailure(JsonElement json)
        {
            Assert.AreNotEqual("200", json.GetProperty("Code").ToString());
            Assert.AreEqual("Upload failed", json.GetProperty("Msg").GetString());
            Assert.AreEqual(1, json.GetProperty("code").GetInt32());
        }

        [TestMethod]
        public void UEditorBase64_ValidBase64ThatIsNoPicture_IsRefused()
        {
            var kinds = new[] { Text("<html><script>alert(1)</script></html>"), Xlsx(), Xls(), Text("GIF"), new byte[] { 0xFF, 0xD8, 0xFF }, Array.Empty<byte>().Concat(new byte[] { 1 }).ToArray() };
            foreach (var data in kinds)
            {
                var h = Make(fields: Scrawl(data));

                AssertUEditorFailure(Json(h.Controller.UploadForLayUIUEditor(h.Provider)));
            }
            Assert.AreEqual(0, Stored().Count);
        }

        [TestMethod]
        public void UEditorBase64_APictureOutsideImageUploadLimits_IsRefused()
        {
            var h = Make(limits: new Dictionary<string, string> { { "MaxPixels", "100" } }, fields: Scrawl(Picture("png", 40, 20)));
            var h2 = Make(limits: new Dictionary<string, string> { { "MaxBytes", "50" } }, fields: Scrawl(Picture("png", 40, 20)));

            AssertUEditorFailure(Json(h.Controller.UploadForLayUIUEditor(h.Provider)));
            AssertUEditorFailure(Json(h2.Controller.UploadForLayUIUEditor(h2.Provider)));
            Assert.AreEqual(0, Stored().Count);
        }

        [TestMethod]
        public void UEditorBase64_ATextFarLargerThanTheLimit_IsRefusedBeforeItIsDecoded()
        {
            Assert.IsFalse(ImageDecodeGuard.TryDecodeBase64(new string('A', 4_000_000), new ImageDecodeLimits { MaxBytes = 1000 }, out var data, out var ext, out var error));
            Assert.IsNull(data);
            Assert.IsNull(ext);
            Assert.AreEqual("Image file too large", error);
        }

        [TestMethod]
        public void UEditorBase64_Rejection_IsLoggedWithAFixedReason()
        {
            var h = Make(fields: new Dictionary<string, string> { { "FileID", Convert.ToBase64String(Text("secret-content")) } });

            h.Controller.UploadForLayUIUEditor(h.Provider);

            var warning = h.Logs.Entries.Single(x => x.Level == LogLevel.Warning);
            StringAssert.Contains(warning.Message, "UploadForLayUIUEditor");
            Assert.IsFalse(warning.Message.Contains("secret"));
        }

        [TestMethod]
        public void UEditorBase64_WithTheAllowList_AppliesItToTheDetectedFormat()
        {
            var png = Make(allowed: new[] { "png" }, fields: Scrawl(Picture("png")));
            var gif = Make(allowed: new[] { "png" }, fields: Scrawl(Picture("gif")));

            Assert.AreEqual("200", Json(png.Controller.UploadForLayUIUEditor(png.Provider)).GetProperty("Code").ToString());
            AssertUEditorFailure(Json(gif.Controller.UploadForLayUIUEditor(gif.Provider)));
            Assert.AreEqual(1, Stored().Count);
        }

        [TestMethod]
        public void UEditorBase64_ATinyPngIsFine_AndStoredBytesAreUntouched()
        {
            var data = Picture("png", 1, 1);
            var h = Make(allowed: Standard, fields: Scrawl(data));

            Json(h.Controller.UploadForLayUIUEditor(h.Provider));

            CollectionAssert.AreEqual(data, Stored().Single().FileData);
        }

        // ---- single page application file API ---------------------------------------------------------------------------------------

        [TestMethod]
        public void Spa_Upload_NotConfigured_StoresWhateverItIs_AsBefore()
        {
            var h = Make(files: new[] { Form("a.bin", Text("binary")) });

            Assert.AreEqual(200, Status(SpaFileApi.Upload(h.Spa, h.Provider, null, null, null, null, null, null)));
            Assert.AreEqual("a.bin", Stored().Single().FileName);
        }

        [TestMethod]
        public void Spa_Upload_Configured_StoresAMatchingFile_AndRefusesTheRest()
        {
            var good = Make(allowed: Standard, files: new[] { Form("a.xlsx", Xlsx()) });
            var bad = Make(allowed: Standard, files: new[] { Form("a.xlsx", Docx()) });
            var other = Make(allowed: Standard, files: new[] { Form("a.bin", Text("binary")) });

            Assert.AreEqual(200, Status(SpaFileApi.Upload(good.Spa, good.Provider, null, null, null, null, null, null)));
            Assert.AreEqual(400, Status(SpaFileApi.Upload(bad.Spa, bad.Provider, null, null, null, null, null, null)));
            Assert.AreEqual(400, Status(SpaFileApi.Upload(other.Spa, other.Provider, null, null, null, null, null, null)));
            Assert.AreEqual(1, Stored().Count);
        }

        [TestMethod]
        public void Spa_Upload_Configured_FromASourceThatCannotSeek_StoresTheWholeFile()
        {
            var data = Picture("png");
            var file = new OneShotFormFile("a.png", data);
            var h = Make(allowed: Standard, files: new IFormFile[] { file });

            Assert.AreEqual(200, Status(SpaFileApi.Upload(h.Spa, h.Provider, null, null, null, null, null, null)));
            CollectionAssert.AreEqual(data, Stored().Single().FileData);
        }

        [TestMethod]
        public void Spa_UploadImage_Configured_ChecksTheOriginalFirst_AndNamesTheThumbnailJpg()
        {
            var seen = new List<string>();
            SpaImageResizer resize = (s, w, hh) =>
            {
                seen.Add("resized");
                return ImageDecodeGuard.Resize(s, w, hh, new ImageDecodeLimits());
            };
            var good = Make(allowed: Standard, files: new[] { Form("photo.gif", Picture("gif")) });
            var bad = Make(allowed: Standard, files: new[] { Form("photo.gif", Picture("png")) });

            Assert.AreEqual(200, Status(SpaFileApi.UploadImage(good.Spa, good.Provider, 10, 10, null, null, null, null, null, null, resize)));
            Assert.AreEqual(1, seen.Count);
            var storedThumb = Stored().Single();
            Assert.AreEqual("photo.jpg", storedThumb.FileName);
            Assert.AreEqual(storedThumb.FileData.Length, storedThumb.Length);

            Assert.AreEqual(400, Status(SpaFileApi.UploadImage(bad.Spa, bad.Provider, 10, 10, null, null, null, null, null, null, resize)));
            Assert.AreEqual(1, seen.Count, "no thumbnail is made for a file that is refused");
        }

        [TestMethod]
        public void Spa_UploadImage_NotConfigured_KeepsTheOriginalName_AsBefore()
        {
            var h = Make(files: new[] { Form("photo.gif", Picture("gif")) });

            var result = SpaFileApi.UploadImage(h.Spa, h.Provider, 10, 10, null, null, null, null, null, null, ImageDecodeGuard.CreateResizer(() => new ImageDecodeLimits()));

            Assert.AreEqual(200, Status(result));
            var stored = Stored().Single();
            Assert.AreEqual("photo.gif", stored.FileName);
            Assert.AreEqual(stored.FileData.Length, stored.Length);
        }

        [TestMethod]
        public void Spa_UploadImage_Configured_FromASourceThatCannotSeek_Works()
        {
            var file = new OneShotFormFile("a.png", Picture("png"));
            var h = Make(allowed: Standard, files: new IFormFile[] { file });

            var result = SpaFileApi.UploadImage(h.Spa, h.Provider, 10, 10, null, null, null, null, null, null, ImageDecodeGuard.CreateResizer(() => new ImageDecodeLimits()));

            Assert.AreEqual(200, Status(result));
            Assert.AreEqual(1, file.Opened);
            var stored = Stored().Single();
            Assert.AreEqual("a.jpg", stored.FileName);
            Assert.AreEqual(stored.FileData.Length, stored.Length);
        }

        [TestMethod]
        public void Spa_UploadForEditor_Configured_RefusesAFileOutsideTheList_AndStoresAMatchingOne()
        {
            var bad = Make(allowed: Standard, files: new[] { Form("a.svg", Text("<svg/>")) });
            var good = Make(allowed: Standard, files: new[] { Form("a.png", Picture("png")) });

            Assert.AreEqual(400, Status(SpaFileApi.UploadForEditor(bad.Spa, bad.Provider, null, null, null, null, null, null)));
            Assert.AreEqual(200, Status(SpaFileApi.UploadForEditor(good.Spa, good.Provider, null, null, null, null, null, null)));
            Assert.AreEqual(1, Stored().Count);
        }

        // ---- the provider is not touched ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void TheProvider_StaysUnchecked_WhateverTheAllowList()
        {
            var h = Make(allowed: new[] { "png" });

            var file = h.Provider.Upload("server-side.bin", 2, new MemoryStream(Text("binary")));

            Assert.IsNotNull(file);
            Assert.AreEqual(1, Stored().Count);
        }
    }
}
