using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class WtmLocalFileHandlerPathTest
    {
        private string _base;
        private string _hostRoot;
        private string _outsideDir;
        private List<(LogLevel level, string message)> _logs;

        [TestInitialize]
        public void Init()
        {
            _base = Path.Combine(Path.GetTempPath(), "wtm-local-handler-" + Guid.NewGuid().ToString("N"));
            _hostRoot = Path.Combine(_base, "host");
            _outsideDir = Path.Combine(_base, "outside");
            Directory.CreateDirectory(_hostRoot);
            _logs = new List<(LogLevel, string)>();
            WtmFileProvider._subDirFunc = null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            WtmFileProvider._subDirFunc = null;
            try { Directory.Delete(_base, true); } catch { }
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly List<(LogLevel level, string message)> _sink;
            public CapturingLogger(List<(LogLevel, string)> sink) { _sink = sink; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                _sink.Add((logLevel, formatter(state, exception)));
            }
        }

        private WtmLocalFileHandler CreateHandler(bool withSettings = true, bool nullSettings = false)
        {
            var config = new Configs { HostRoot = _hostRoot };
            config.FileUploadOptions = new FileUploadOptions
            {
                SaveFileMode = "local",
                Settings = nullSettings ? null : withSettings
                    ? new Dictionary<string, List<FileHandlerOptions>>
                    {
                        {
                            "local", new List<FileHandlerOptions>
                            {
                                new FileHandlerOptions { GroupName = "default", GroupLocation = "./uploads" },
                                new FileHandlerOptions { GroupName = "docs", GroupLocation = "./docs" }
                            }
                        }
                    }
                    : new Dictionary<string, List<FileHandlerOptions>>()
            };
            var monitor = new Mock<IOptionsMonitor<Configs>>();
            monitor.Setup(x => x.CurrentValue).Returns(config);
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new CapturingLogger(_logs));
            return new WtmLocalFileHandler(new WTMContext(monitor.Object, loggerFactory: loggerFactory.Object));
        }

        private void AssertDirectory(string expectedRelativeToHost, string storedPath)
        {
            var expected = Path.GetFullPath(Path.Combine(_hostRoot, expectedRelativeToHost));
            var actual = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(_hostRoot, storedPath)));
            Assert.AreEqual(expected, actual);
        }

        private static bool CallIsInside(string root, string fullPath)
        {
            var method = typeof(WtmLocalFileHandler).GetMethod("IsInside", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (bool)method.Invoke(null, new object[] { root, fullPath });
        }

        private static MemoryStream Data(string text = "hello") => new MemoryStream(Encoding.UTF8.GetBytes(text));

        private string[] AllFilesUnderBase() =>
            Directory.Exists(_base) ? Directory.GetFiles(_base, "*", SearchOption.AllDirectories) : new string[0];

        [TestMethod]
        public void NormalSubdirAndFile_SavedUnderGroupRoot_ReadBackAndDeleted()
        {
            var handler = CreateHandler();

            var (path, _) = handler.Upload("photo.png", 5, Data("hello"), "docs", "2024/05");

            AssertDirectory(Path.Combine("docs", "2024", "05"), path);
            Assert.IsTrue(path.EndsWith(".png"));
            var full = Path.GetFullPath(Path.Combine(_hostRoot, path));
            Assert.IsTrue(File.Exists(full));
            Assert.AreEqual(Path.GetFullPath(Path.Combine(_hostRoot, "docs", "2024", "05")), Path.GetDirectoryName(full));

            using (var stream = handler.GetFileData(new FileAttachment { Path = path }))
            using (var reader = new StreamReader(stream))
            {
                Assert.AreEqual("hello", reader.ReadToEnd());
            }

            handler.DeleteFile(new FileAttachment { Path = path });
            Assert.IsFalse(File.Exists(full));
            Assert.AreEqual(0, _logs.Count);
        }

        [TestMethod]
        public void NoSubdirNoGroup_UsesDefaultGroup()
        {
            var handler = CreateHandler();

            var (path, _) = handler.Upload("a.txt", 1, Data());

            AssertDirectory("uploads", path);
            Assert.IsTrue(File.Exists(Path.GetFullPath(Path.Combine(_hostRoot, path))));
        }

        [TestMethod]
        public void SubdirFunc_IsStillUsedWhenNoSubdirGiven()
        {
            WtmFileProvider._subDirFunc = _ => "tenant1";
            var handler = CreateHandler();

            var (path, _) = handler.Upload("a.txt", 1, Data(), "docs");

            AssertDirectory(Path.Combine("docs", "tenant1"), path);
        }

        [TestMethod]
        [DataRow("../../outside")]
        [DataRow("../uploads-sibling")]
        [DataRow("a/../../../outside")]
        [DataRow("..")]
        [DataRow("..\\..\\outside")]
        public void SubdirEscapingTheRoot_IsRejected_AndNothingWrittenOutside(string subdir)
        {
            if (subdir.Contains('\\') && Path.DirectorySeparatorChar == '/')
            {
                Assert.Inconclusive("Windows-only: a backslash is an ordinary file name character on this platform.");
            }
            var handler = CreateHandler();

            Assert.ThrowsExactly<ArgumentException>(() => handler.Upload("photo.png", 5, Data(), "default", subdir));

            Assert.AreEqual(0, AllFilesUnderBase().Length);
            Assert.IsFalse(Directory.Exists(_outsideDir));
            Assert.IsFalse(Directory.Exists(Path.Combine(_hostRoot, "uploads-sibling")));
        }

        [TestMethod]
        public void SiblingDirectoryWithSamePrefix_IsRejected()
        {
            var handler = CreateHandler();

            // "./uploads/../uploads-evil" shares the "uploads" prefix with the root but is not inside it.
            Assert.ThrowsExactly<ArgumentException>(() => handler.Upload("photo.png", 5, Data(), "default", "../uploads-evil"));

            Assert.IsFalse(Directory.Exists(Path.Combine(_hostRoot, "uploads-evil")));
            Assert.AreEqual(0, AllFilesUnderBase().Length);
        }

        [TestMethod]
        public void SubdirFuncEscapingTheRoot_IsRejected()
        {
            WtmFileProvider._subDirFunc = _ => "../../outside";
            var handler = CreateHandler();

            Assert.ThrowsExactly<ArgumentException>(() => handler.Upload("photo.png", 5, Data()));

            Assert.AreEqual(0, AllFilesUnderBase().Length);
            Assert.IsFalse(Directory.Exists(_outsideDir));
        }

        [TestMethod]
        public void AbsoluteSubdirFromSubdirFunc_IsRejected()
        {
            WtmFileProvider._subDirFunc = _ => _outsideDir;
            var handler = CreateHandler();

            Assert.ThrowsExactly<ArgumentException>(() => handler.Upload("photo.png", 5, Data()));

            Assert.IsFalse(Directory.Exists(_outsideDir));
            Assert.AreEqual(0, AllFilesUnderBase().Length);
        }

        [TestMethod]
        public void AbsoluteSubdir_IsRejected_AndNothingWritten()
        {
            var handler = CreateHandler();

            Assert.ThrowsExactly<ArgumentException>(() => handler.Upload("photo.png", 5, Data(), "default", _outsideDir));

            Assert.IsFalse(Directory.Exists(_outsideDir));
            Assert.AreEqual(0, AllFilesUnderBase().Length);
        }

        [TestMethod]
        [DataRow("../../outside/photo.png")]
        [DataRow("..\\..\\outside\\photo.png")]
        [DataRow("some/dir/photo.png")]
        public void FileNameWithDirectoryComponents_OnlyLeafIsUsed(string fileName)
        {
            var handler = CreateHandler();

            var (path, _) = handler.Upload(fileName, 5, Data());

            AssertDirectory("uploads", path);
            Assert.IsTrue(path.EndsWith(".png"));
            var files = AllFilesUnderBase();
            Assert.AreEqual(1, files.Length);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(_hostRoot, "uploads")), Path.GetDirectoryName(files[0]));
        }

        [TestMethod]
        public void DotInDirectoryPartOfFileName_CannotLeaveTheRoot()
        {
            var handler = CreateHandler();

            var (path, _) = handler.Upload("a.b/../../../escape", 5, Data());

            AssertDirectory("uploads", path);
            var files = AllFilesUnderBase();
            Assert.AreEqual(1, files.Length);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(_hostRoot, "uploads")), Path.GetDirectoryName(files[0]));
        }

        [TestMethod]
        [DataRow("photo.png", "png")]
        [DataRow("photo.PNG", "PNG")]
        [DataRow("archive.tar.gz", "gz")]
        [DataRow("photo.p!ng", "")]
        [DataRow("photo.png;x", "")]
        [DataRow("photo.averyverylongextension", "")]
        [DataRow("photo.", "")]
        public void Extension_IsSanitized(string fileName, string expectedExt)
        {
            var handler = CreateHandler();

            var (path, _) = handler.Upload(fileName, 5, Data());

            var name = Path.GetFileName(path);
            Assert.AreEqual(expectedExt, name.Substring(name.LastIndexOf('.') + 1));
            Assert.AreEqual(32, name.LastIndexOf('.'));
        }

        [TestMethod]
        public void NameWithoutDot_KeepsPreviousBehaviour()
        {
            var handler = CreateHandler();

            var (path, _) = handler.Upload("readme", 5, Data());

            // Previously the whole name was used as the extension when there was no dot.
            Assert.IsTrue(path.EndsWith(".readme"));
        }

        [TestMethod]
        [DataRow("missing")]
        [DataRow("../x")]
        [DataRow("")]
        [DataRow(null)]
        public void UnknownGroup_FallsBackToFirstConfiguredGroup(string group)
        {
            var handler = CreateHandler();

            var (path, _) = handler.Upload("a.txt", 1, Data(), group);

            AssertDirectory("uploads", path);
            Assert.IsTrue(File.Exists(Path.GetFullPath(Path.Combine(_hostRoot, path))));
        }

        [TestMethod]
        public void GroupNameMatch_IsCaseInsensitive()
        {
            var handler = CreateHandler();

            var (path, _) = handler.Upload("a.txt", 1, Data(), "DOCS");

            AssertDirectory("docs", path);
        }

        [TestMethod]
        public void NoLocalSettingsConfigured_UsesBuiltInDefaultLocation()
        {
            var handler = CreateHandler(withSettings: false);

            var (path, _) = handler.Upload("a.txt", 1, Data(), "anything");

            AssertDirectory("uploads", path);
        }

        [TestMethod]
        public void NullSettingsList_UsesBuiltInDefaultLocation()
        {
            var handler = CreateHandler(nullSettings: true);

            var (path, _) = handler.Upload("a.txt", 1, Data(), "anything");

            AssertDirectory("uploads", path);
            Assert.IsTrue(File.Exists(Path.GetFullPath(Path.Combine(_hostRoot, path))));
        }

        [TestMethod]
        public void ReadAndDelete_OutsideConfiguredRoots_StillWork_AndLogAWarning()
        {
            var handler = CreateHandler();
            Directory.CreateDirectory(_outsideDir);
            var readTarget = Path.Combine(_outsideDir, "read.txt");
            var deleteTarget = Path.Combine(_outsideDir, "delete.txt");
            File.WriteAllText(readTarget, "stored elsewhere");
            File.WriteAllText(deleteTarget, "to delete");

            using (var stream = handler.GetFileData(new FileAttachment { Path = readTarget }))
            using (var reader = new StreamReader(stream))
            {
                Assert.AreEqual("stored elsewhere", reader.ReadToEnd());
            }
            Assert.AreEqual(1, _logs.Count(x => x.level == LogLevel.Warning));

            handler.DeleteFile(new FileAttachment { Path = deleteTarget });
            Assert.IsFalse(File.Exists(deleteTarget));
            Assert.AreEqual(2, _logs.Count(x => x.level == LogLevel.Warning));
            Assert.IsFalse(_logs.Any(x => x.message.Contains(_outsideDir)), "The stored path itself must not be logged.");
        }

        [TestMethod]
        public void ReadAndDelete_RelativePathOutsideRoots_StillWork()
        {
            var handler = CreateHandler();
            Directory.CreateDirectory(_outsideDir);
            var target = Path.Combine(_outsideDir, "keep.txt");
            File.WriteAllText(target, "data");

            using (var stream = handler.GetFileData(new FileAttachment { Path = "../outside/keep.txt" }))
            {
                Assert.IsNotNull(stream);
            }
            handler.DeleteFile(new FileAttachment { Path = "../outside/keep.txt" });

            Assert.IsFalse(File.Exists(target));
            Assert.AreEqual(2, _logs.Count(x => x.level == LogLevel.Warning));
        }

        [TestMethod]
        [DataRow(".")]
        [DataRow("a/..")]
        public void SubdirResolvingToTheRoot_IsAccepted(string subdir)
        {
            var handler = CreateHandler();

            var (path, _) = handler.Upload("a.txt", 1, Data(), "default", subdir);

            Assert.IsTrue(File.Exists(Path.GetFullPath(Path.Combine(_hostRoot, path))));
            Assert.AreEqual(Path.GetFullPath(Path.Combine(_hostRoot, "uploads")),
                Path.GetDirectoryName(AllFilesUnderBase().Single()));
        }

        [TestMethod]
        public void GroupLocationWithTrailingSeparator_AcceptsSubdirs_AndStillRejectsEscapes()
        {
            var config = new Configs { HostRoot = _hostRoot };
            config.FileUploadOptions = new FileUploadOptions
            {
                SaveFileMode = "local",
                Settings = new Dictionary<string, List<FileHandlerOptions>>
                {
                    { "local", new List<FileHandlerOptions> { new FileHandlerOptions { GroupName = "default", GroupLocation = "./uploads/" } } }
                }
            };
            var monitor = new Mock<IOptionsMonitor<Configs>>();
            monitor.Setup(x => x.CurrentValue).Returns(config);
            var handler = new WtmLocalFileHandler(new WTMContext(monitor.Object));

            var (path, _) = handler.Upload("a.txt", 1, Data(), "default", "2024");

            Assert.IsTrue(File.Exists(Path.GetFullPath(Path.Combine(_hostRoot, path))));
            Assert.ThrowsExactly<ArgumentException>(() => handler.Upload("a.txt", 1, Data(), "default", "../uploads-evil"));
            Assert.IsFalse(Directory.Exists(Path.Combine(_hostRoot, "uploads-evil")));
        }

        [TestMethod]
        public void IsInside_FilesystemRoot_ContainsItsChildren()
        {
            var fsRoot = Path.GetPathRoot(Path.GetFullPath(_base));
            var child = Path.GetFullPath(_base);

            Assert.IsTrue(CallIsInside(fsRoot, child));
            Assert.IsTrue(CallIsInside(fsRoot, fsRoot));
            Assert.IsFalse(CallIsInside(_base, fsRoot));
        }

        [TestMethod]
        public void IsInside_RootWithAndWithoutTrailingSeparator_BehaveTheSame()
        {
            var root = Path.GetFullPath(_hostRoot);
            var withSep = root + Path.DirectorySeparatorChar;
            var child = Path.Combine(root, "a", "b");
            var sibling = root + "-sibling";

            foreach (var r in new[] { root, withSep })
            {
                Assert.IsTrue(CallIsInside(r, child));
                Assert.IsTrue(CallIsInside(r, root));
                Assert.IsFalse(CallIsInside(r, sibling));
                Assert.IsFalse(CallIsInside(r, Path.GetDirectoryName(root)));
            }
        }
    }
}
