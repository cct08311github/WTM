using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class OssKeyAndFileExtTest
    {
        private static readonly Guid KeyGuid = Guid.NewGuid();

        [TestInitialize]
        public void Init()
        {
            WtmFileProvider._subDirFunc = null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            WtmFileProvider._subDirFunc = null;
        }

        private static WTMContext CreateContext(bool withOss = false)
        {
            var config = new Configs { HostRoot = Path.GetTempPath() };
            config.FileUploadOptions = new FileUploadOptions
            {
                SaveFileMode = "database",
                Settings = withOss
                    ? new Dictionary<string, List<FileHandlerOptions>>
                    {
                        {
                            "oss", new List<FileHandlerOptions>
                            {
                                // Unroutable endpoint: any request that wrongly reaches the SDK fails fast and loudly.
                                new FileHandlerOptions { GroupName = "default", GroupLocation = "bucket", ServerUrl = "http://127.0.0.1:1", Key = "k", Secret = "s" }
                            }
                        }
                    }
                    : new Dictionary<string, List<FileHandlerOptions>>()
            };
            var monitor = new Mock<IOptionsMonitor<Configs>>();
            monitor.Setup(x => x.CurrentValue).Returns(config);
            return new WTMContext(monitor.Object);
        }

        private static MemoryStream Data() => new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        // ---- OSS key building -------------------------------------------------------------

        [TestMethod]
        public void BuildObjectKey_NormalSubdir_IsUnchanged()
        {
            Assert.AreEqual("2024/05/abc.png", FileNameSanitizer.BuildObjectKey("2024/05", "abc.png"));
            Assert.AreEqual("2024/05/abc.png", FileNameSanitizer.BuildObjectKey("2024\\05", "abc.png"));
            Assert.AreEqual("2024/abc.png", FileNameSanitizer.BuildObjectKey("2024/", "abc.png"));
            Assert.AreEqual("abc.png", FileNameSanitizer.BuildObjectKey(null, "abc.png"));
            Assert.AreEqual("abc.png", FileNameSanitizer.BuildObjectKey("", "abc.png"));
            Assert.AreEqual("a..b/abc.png", FileNameSanitizer.BuildObjectKey("a..b", "abc.png"));
        }

        [DataTestMethod]
        [DataRow("/etc")]
        [DataRow("\\etc")]
        [DataRow("\\\\server\\share")]
        [DataRow("C:\\temp")]
        [DataRow("C:/temp")]
        [DataRow("..")]
        [DataRow("../x")]
        [DataRow("a/../x")]
        [DataRow("a\\..\\x")]
        [DataRow("a/..")]
        public void BuildObjectKey_RootedOrParentSubdir_Throws(string subdir)
        {
            Assert.ThrowsException<ArgumentException>(() => FileNameSanitizer.BuildObjectKey(subdir, "abc.png"));
        }

        [DataTestMethod]
        [DataRow("../x")]
        [DataRow("/etc")]
        [DataRow("a\\..\\x")]
        public void OssUpload_BadSubdir_ThrowsBeforeUploading(string subdir)
        {
            var handler = new WtmOssFileHandler(CreateContext(withOss: true));
            Assert.ThrowsException<ArgumentException>(() => handler.Upload("a.png", 5, Data(), null, subdir));
        }

        [TestMethod]
        public void OssUpload_BadSubdirFromSubDirFunc_ThrowsBeforeUploading()
        {
            WtmFileProvider._subDirFunc = _ => "../escape";
            var handler = new WtmOssFileHandler(CreateContext(withOss: true));
            Assert.ThrowsException<ArgumentException>(() => handler.Upload("a.png", 5, Data()));
        }

        [TestMethod]
        public void OssUpload_UnknownGroupWithoutAnySettings_ReturnsNullWithoutUploading()
        {
            var handler = new WtmOssFileHandler(CreateContext(withOss: false));
            var rv = handler.Upload("a.png", 5, Data(), "nogroup", "../x");
            Assert.IsNull(rv.path);
            Assert.IsNull(rv.handlerInfo);
        }

        // ---- sanitizer --------------------------------------------------------------------

        [DataTestMethod]
        [DataRow("photo.png", "png")]
        [DataRow("a.b.JPG", "JPG")]
        [DataRow("dir/sub/photo.png", "png")]
        [DataRow("dir\\sub\\photo.png", "png")]
        [DataRow("noext", "noext")]
        [DataRow("trailingdot.", "")]
        [DataRow("a.p ng", "")]
        [DataRow("a.<script>", "")]
        [DataRow("a.verylongextension", "")]
        [DataRow("a.pn/g", "g")]
        [DataRow("x.p\u00e9g", "")]
        [DataRow("", "")]
        [DataRow(null, "")]
        public void GetSafeExtension_Rules(string fileName, string expected)
        {
            Assert.AreEqual(expected, FileNameSanitizer.GetSafeExtension(fileName));
        }

        [DataTestMethod]
        [DataRow("photo.png", "photo.png")]
        [DataRow("../../etc/passwd", "passwd")]
        [DataRow("C:\\dir\\photo.png", "photo.png")]
        [DataRow("dir/photo.png", "photo.png")]
        public void GetLeafName_StripsDirectories(string fileName, string expected)
        {
            Assert.AreEqual(expected, FileNameSanitizer.GetLeafName(fileName));
        }

        // ---- database handler -------------------------------------------------------------

        private static (WtmDataBaseFileHandler handler, List<FileAttachment> saved) CreateDbHandler()
        {
            var saved = new List<FileAttachment>();
            var dc = new Mock<IDataContext>();
            dc.Setup(x => x.AddEntity(It.IsAny<FileAttachment>())).Callback<FileAttachment>(f => saved.Add(f));
            var wtm = CreateContext();
            wtm.DC = dc.Object;
            return (new WtmDataBaseFileHandler(wtm), saved);
        }

        [TestMethod]
        public void UploadToDB_NormalFile_StoresNameAndExtensionUnchanged()
        {
            var (handler, saved) = CreateDbHandler();
            handler.UploadToDB("photo.PNG", 5, Data());
            Assert.AreEqual(1, saved.Count);
            Assert.AreEqual("photo.PNG", saved[0].FileName);
            Assert.AreEqual("PNG", saved[0].FileExt);
        }

        [TestMethod]
        public void UploadToDB_MaliciousName_StoresLeafAndSanitizedExtension()
        {
            var (handler, saved) = CreateDbHandler();
            handler.UploadToDB("..\\..\\dir/evil.p/h<p", 5, Data());
            Assert.AreEqual("h<p", saved[0].FileName);
            Assert.AreEqual(string.Empty, saved[0].FileExt);

            handler.UploadToDB("../../a/photo.png", 5, Data());
            Assert.AreEqual("photo.png", saved[1].FileName);
            Assert.AreEqual("png", saved[1].FileExt);

            handler.UploadToDB("report.docx%00.exe;", 5, Data());
            Assert.AreEqual(string.Empty, saved[2].FileExt);
        }

        // ---- provider (non database handler) ----------------------------------------------

        public sealed class FakeHandler : WtmFileHandlerBase
        {
            public static string LastFileName;
            public FakeHandler(WTMContext wtm) : base(wtm) { }
            public override (string path, string handlerInfo) Upload(string fileName, long fileLength, Stream data, string group = null, string subdir = null, string extra = null)
            {
                LastFileName = fileName;
                return ("some/path/x.bin", "info");
            }
        }

        private static IWtmFile ProviderUpload(string fileName, out List<FileAttachment> saved)
        {
            var list = new List<FileAttachment>();
            var dc = new Mock<IDataContext>();
            dc.Setup(x => x.AddEntity(It.IsAny<FileAttachment>())).Callback<FileAttachment>(f => list.Add(f));
            var field = typeof(WtmFileProvider).GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Static);
            var old = field.GetValue(null);
            field.SetValue(null, new Dictionary<string, ConstructorInfo>
            {
                { "fake", typeof(FakeHandler).GetConstructor(new[] { typeof(WTMContext) }) }
            });
            try
            {
                var provider = new WtmFileProvider(CreateContext());
                var rv = provider.Upload(fileName, 5, Data(), null, null, null, "fake", dc.Object);
                saved = list;
                return rv;
            }
            finally
            {
                field.SetValue(null, old);
            }
        }

        [TestMethod]
        public void Provider_NormalFile_StoresNameAndExtensionUnchanged()
        {
            var rv = ProviderUpload("photo.png", out var saved);
            Assert.IsNotNull(rv);
            Assert.AreEqual(1, saved.Count);
            Assert.AreEqual("photo.png", saved[0].FileName);
            Assert.AreEqual("png", saved[0].FileExt);
            Assert.AreEqual("photo.png", FakeHandler.LastFileName);
        }

        [TestMethod]
        public void Provider_MaliciousName_StoresLeafAndSanitizedExtension()
        {
            ProviderUpload("../../dir\\photo.p/ng", out var saved);
            Assert.AreEqual("ng", saved[0].FileName);

            ProviderUpload("../../dir/photo.png", out saved);
            Assert.AreEqual("photo.png", saved[0].FileName);
            Assert.AreEqual("png", saved[0].FileExt);

            ProviderUpload("a.b;c", out saved);
            Assert.AreEqual(string.Empty, saved[0].FileExt);

            ProviderUpload("a.verylongextension", out saved);
            Assert.AreEqual(string.Empty, saved[0].FileExt);
        }
    }
}
