#nullable enable
using System;
using System.IO;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Models;

namespace WalkingTec.Mvvm.Core.Test.Models
{
    [TestClass]
    public class FileAttachmentTests
    {
        // ─── Property defaults ─────────────────────────────────────────────────

        [TestMethod]
        public void DefaultConstructor_FileNameIsEmpty()
        {
            var f = new FileAttachment();
            f.FileName.Should().Be("");
        }

        [TestMethod]
        public void DefaultConstructor_FileExtIsEmpty()
        {
            var f = new FileAttachment();
            f.FileExt.Should().Be("");
        }

        [TestMethod]
        public void DefaultConstructor_NullablesAreNull()
        {
            var f = new FileAttachment();
            f.Path.Should().BeNull();
            f.SaveMode.Should().BeNull();
            f.FileData.Should().BeNull();
            f.ExtraInfo.Should().BeNull();
            f.HandlerInfo.Should().BeNull();
            f.TenantCode.Should().BeNull();
            f.DataStream.Should().BeNull();
        }

        [TestMethod]
        public void DefaultConstructor_LengthIsZero()
        {
            var f = new FileAttachment();
            f.Length.Should().Be(0L);
        }

        // ─── Property set/get ─────────────────────────────────────────────────

        [TestMethod]
        public void FileName_CanBeSetAndRetrieved()
        {
            var f = new FileAttachment { FileName = "document.pdf" };
            f.FileName.Should().Be("document.pdf");
        }

        [TestMethod]
        public void FileExt_CanBeSetAndRetrieved()
        {
            var f = new FileAttachment { FileExt = "pdf" };
            f.FileExt.Should().Be("pdf");
        }

        [TestMethod]
        public void Path_CanBeSetAndRetrieved()
        {
            var f = new FileAttachment { Path = "/uploads/doc.pdf" };
            f.Path.Should().Be("/uploads/doc.pdf");
        }

        [TestMethod]
        public void Length_CanBeSetAndRetrieved()
        {
            var f = new FileAttachment { Length = 1024L };
            f.Length.Should().Be(1024L);
        }

        [TestMethod]
        public void UploadTime_CanBeSetAndRetrieved()
        {
            var now = DateTime.UtcNow;
            var f = new FileAttachment { UploadTime = now };
            f.UploadTime.Should().Be(now);
        }

        [TestMethod]
        public void SaveMode_CanBeSetAndRetrieved()
        {
            var f = new FileAttachment { SaveMode = "Local" };
            f.SaveMode.Should().Be("Local");
        }

        [TestMethod]
        public void FileData_CanBeSetAndRetrieved()
        {
            var data = new byte[] { 1, 2, 3 };
            var f = new FileAttachment { FileData = data };
            f.FileData.Should().BeEquivalentTo(data);
        }

        [TestMethod]
        public void ExtraInfo_CanBeSetAndRetrieved()
        {
            var f = new FileAttachment { ExtraInfo = "{\"key\":\"val\"}" };
            f.ExtraInfo.Should().Be("{\"key\":\"val\"}");
        }

        [TestMethod]
        public void HandlerInfo_CanBeSetAndRetrieved()
        {
            var f = new FileAttachment { HandlerInfo = "S3Handler" };
            f.HandlerInfo.Should().Be("S3Handler");
        }

        [TestMethod]
        public void TenantCode_CanBeSetAndRetrieved()
        {
            var f = new FileAttachment { TenantCode = "TENANT01" };
            f.TenantCode.Should().Be("TENANT01");
        }

        [TestMethod]
        public void DataStream_CanBeSetAndRetrieved()
        {
            using var ms = new MemoryStream(new byte[] { 10, 20 });
            var f = new FileAttachment { DataStream = ms };
            f.DataStream.Should().BeSameAs(ms);
        }

        // ─── TopBasePoco inheritance ───────────────────────────────────────────

        [TestMethod]
        public void ID_IsInheritedFromTopBasePoco()
        {
            var id = Guid.NewGuid();
            var f = new FileAttachment { ID = id };
            f.ID.Should().Be(id);
        }

        [TestMethod]
        public void HasID_WhenIdSet_ReturnsTrue()
        {
            var f = new FileAttachment { ID = Guid.NewGuid() };
            f.HasID().Should().BeTrue();
        }

        [TestMethod]
        public void HasID_WhenIdEmpty_ReturnsFalse()
        {
            var f = new FileAttachment { ID = Guid.Empty };
            f.HasID().Should().BeFalse();
        }

        // ─── IWtmFile.GetID ───────────────────────────────────────────────────

        [TestMethod]
        public void IWtmFile_GetID_ReturnsIdAsString()
        {
            var id = Guid.NewGuid();
            IWtmFile file = new FileAttachment { ID = id };
            file.GetID().Should().Be(id.ToString());
        }

        // ─── Dispose ──────────────────────────────────────────────────────────

        [TestMethod]
        public void Dispose_WithNoStream_DoesNotThrow()
        {
            var f = new FileAttachment();
            Action act = () => f.Dispose();
            act.Should().NotThrow();
        }

        [TestMethod]
        public void Dispose_WithStream_DisposesStream()
        {
            var ms = new MemoryStream(new byte[] { 1, 2, 3 });
            var f = new FileAttachment { DataStream = ms };
            f.Dispose();
            ms.Disposed().Should().BeTrue();
        }

        [TestMethod]
        public void Dispose_ViaUsing_DisposesStream()
        {
            var ms = new MemoryStream(new byte[] { 42 });
            using (var f = new FileAttachment { DataStream = ms })
            {
                // use f
                f.FileName.Should().Be("");
            }
            // ms should be disposed after using block
            ms.Disposed().Should().BeTrue();
        }

        // ─── ITenant interface ────────────────────────────────────────────────

        [TestMethod]
        public void TenantCode_IsReadableViaClass()
        {
            var f = new FileAttachment { TenantCode = "T001" };
            f.TenantCode.Should().Be("T001");
        }
    }

    /// <summary>Helper extension to detect MemoryStream disposal via CanRead.</summary>
    internal static class MemoryStreamExtensions
    {
        public static bool Disposed(this MemoryStream ms)
        {
            try
            {
                _ = ms.Position;
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
}
