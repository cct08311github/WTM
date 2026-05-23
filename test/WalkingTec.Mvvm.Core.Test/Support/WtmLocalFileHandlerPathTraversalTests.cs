#nullable enable
using System;
using System.IO;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    /// <summary>
    /// Verifies that WtmLocalFileHandler.GetFileData and DeleteFile reject paths
    /// that escape the configured upload root (defense-in-depth path-traversal guard).
    /// </summary>
    [TestClass]
    public class WtmLocalFileHandlerPathTraversalTests
    {
        private WtmLocalFileHandler _handler = null!;

        [TestInitialize]
        public void Setup()
        {
            var wtm = MockWtmContext.CreateWtmContext();
            // HostRoot defaults to "" → GetFullPath("./uploads") resolves under CWD
            _handler = new WtmLocalFileHandler(wtm);
        }

        // ─── GetFileData ──────────────────────────────────────────────────────────

        [TestMethod]
        public void GetFileData_TraversalPath_ThrowsUnauthorizedAccess()
        {
            // A path with ../ that would try to escape the uploads root
            var file = new TraversalTestFile("../etc/passwd");

            Action act = () => _handler.GetFileData(file);

            act.Should().Throw<UnauthorizedAccessException>(
                "resolved path escapes the upload root");
        }

        [TestMethod]
        public void GetFileData_AbsolutePathOutsideRoot_ThrowsUnauthorizedAccess()
        {
            // An absolute path pointing outside the upload root
            var file = new TraversalTestFile(Path.GetTempPath() + "secret.txt");

            Action act = () => _handler.GetFileData(file);

            act.Should().Throw<UnauthorizedAccessException>(
                "absolute paths outside the root must be rejected");
        }

        [TestMethod]
        public void GetFileData_NullPath_ThrowsUnauthorizedAccess()
        {
            var file = new TraversalTestFile(null);

            Action act = () => _handler.GetFileData(file);

            act.Should().Throw<UnauthorizedAccessException>();
        }

        // ─── DeleteFile ───────────────────────────────────────────────────────────

        [TestMethod]
        public void DeleteFile_TraversalPath_ThrowsUnauthorizedAccess()
        {
            var file = new TraversalTestFile("../../etc/passwd");

            Action act = () => _handler.DeleteFile(file);

            act.Should().Throw<UnauthorizedAccessException>(
                "resolved path escapes the upload root");
        }

        [TestMethod]
        public void DeleteFile_EmptyPath_DoesNotThrow()
        {
            // Existing contract: empty path → early return (no-op)
            var file = new TraversalTestFile(string.Empty);

            Action act = () => _handler.DeleteFile(file);

            act.Should().NotThrow("empty path is a no-op per existing contract");
        }

        [TestMethod]
        public void DeleteFile_AbsolutePathOutsideRoot_ThrowsUnauthorizedAccess()
        {
            var file = new TraversalTestFile("/etc/shadow");

            Action act = () => _handler.DeleteFile(file);

            act.Should().Throw<UnauthorizedAccessException>();
        }

        // ─── Helper ───────────────────────────────────────────────────────────────

        /// <summary>Minimal IWtmFile implementation for testing path validation.</summary>
        private sealed class TraversalTestFile : IWtmFile
        {
            public string? Path { get; set; }
            public string FileName { get; set; } = string.Empty;
            public string FileExt { get; set; } = string.Empty;
            public long Length { get; set; }
            public DateTime UploadTime { get; set; }
            public string? SaveMode { get; set; }
            public string? ExtraInfo { get; set; }
            public string? HandlerInfo { get; set; }
            public Stream? DataStream { get; set; }

            public TraversalTestFile(string? path) => Path = path;

            public string GetID() => Guid.NewGuid().ToString();
        }
    }
}
