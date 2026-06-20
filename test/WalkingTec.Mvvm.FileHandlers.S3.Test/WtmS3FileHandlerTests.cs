#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.FileHandlers.S3;

namespace WalkingTec.Mvvm.FileHandlers.S3.Test;

[TestClass]
public class WtmS3FileHandlerTests
{
    // ─── helpers ────────────────────────────────────────────────────────────

    private static IOptions<S3FileHandlerOptions> BuildOptions(
        string bucket = "test-bucket",
        string prefix = "")
    {
        return Options.Create(new S3FileHandlerOptions
        {
            BucketName   = bucket,
            AccessKey    = "test-access",
            SecretKey    = "test-secret",
            KeyPrefix    = prefix,
            ForcePathStyle = true,
        });
    }

    /// <summary>Minimal stub for IWtmFile with a settable Path.</summary>
    private sealed class StubFile : IWtmFile
    {
        public string? Path        { get; set; }
        public string  FileName    { get; set; } = "test.txt";
        public string  FileExt     { get; set; } = "txt";
        public long    Length      { get; set; }
        public DateTime UploadTime { get; set; } = DateTime.UtcNow;
        public string? SaveMode    { get; set; }
        public string? ExtraInfo   { get; set; }
        public string? HandlerInfo { get; set; }
        public Stream? DataStream  { get; set; }
        public string GetID()      => "stub-id";
    }

    // ─── Upload tests ────────────────────────────────────────────────────────

    [TestMethod]
    public void Upload_CallsPutObject_WithCorrectBucketAndContent()
    {
        // Arrange
        var s3Mock = new Mock<IAmazonS3>();
        s3Mock
            .Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = System.Net.HttpStatusCode.OK });

        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions());
        var data    = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        // Act
        var (path, handlerInfo) = handler.Upload("file.txt", 5, data);

        // Assert
        path.Should().NotBeNullOrEmpty("a successful upload must return an object key");
        handlerInfo.Should().Be("s3");

        s3Mock.Verify(c => c.PutObjectAsync(
            It.Is<PutObjectRequest>(r =>
                r.BucketName  == "test-bucket" &&
                r.InputStream == data),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public void Upload_UsesKeyPrefix_WhenConfigured()
    {
        // Arrange
        var s3Mock = new Mock<IAmazonS3>();
        s3Mock
            .Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = System.Net.HttpStatusCode.OK });

        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions(prefix: "uploads"));
        var data    = new MemoryStream(Encoding.UTF8.GetBytes("data"));

        // Act
        var (path, _) = handler.Upload("photo.jpg", 4, data);

        // Assert
        path.Should().StartWith("uploads/", "KeyPrefix must be prepended to the object key");
        path.Should().EndWith(".jpg", "file extension must be preserved in the key");
    }

    [TestMethod]
    public void Upload_IncludesSubdir_InKey()
    {
        var s3Mock = new Mock<IAmazonS3>();
        s3Mock
            .Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = System.Net.HttpStatusCode.OK });

        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions());
        var data    = new MemoryStream(Encoding.UTF8.GetBytes("x"));

        var (path, _) = handler.Upload("doc.pdf", 1, data, subdir: "reports");

        path.Should().StartWith("reports/");
        path.Should().EndWith(".pdf");
    }

    [TestMethod]
    public void Upload_ReturnsNullPath_WhenS3Throws()
    {
        var s3Mock = new Mock<IAmazonS3>();
        s3Mock
            .Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("bucket not found"));

        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions());
        var data    = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        var (path, handlerInfo) = handler.Upload("file.txt", 5, data);

        path.Should().BeNull("failure must be signalled by a null path");
        handlerInfo.Should().BeNull();
    }

    // ─── GetFileData tests ───────────────────────────────────────────────────

    [TestMethod]
    public void GetFileData_CallsGetObject_WithCorrectBucketAndKey()
    {
        // Arrange
        const string key     = "uploads/abc123.txt";
        const string content = "file content";
        var contentStream    = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var response = new GetObjectResponse
        {
            ResponseStream = contentStream,
            HttpStatusCode = System.Net.HttpStatusCode.OK,
        };

        var s3Mock = new Mock<IAmazonS3>();
        s3Mock
            .Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions());
        var file    = new StubFile { Path = key };

        // Act
        var result = handler.GetFileData(file);

        // Assert
        result.Should().NotBeNull();
        result!.Position = 0;
        var text = new StreamReader(result).ReadToEnd();
        text.Should().Be(content);

        s3Mock.Verify(c => c.GetObjectAsync(
            It.Is<GetObjectRequest>(r =>
                r.BucketName == "test-bucket" && r.Key == key),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public void GetFileData_ReturnsNull_WhenPathIsEmpty()
    {
        var s3Mock  = new Mock<IAmazonS3>();
        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions());
        var file    = new StubFile { Path = null };

        var result = handler.GetFileData(file);

        result.Should().BeNull();
        s3Mock.Verify(c => c.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public void GetFileData_ReturnsNull_WhenS3Throws()
    {
        var s3Mock = new Mock<IAmazonS3>();
        s3Mock
            .Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("key not found"));

        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions());
        var file    = new StubFile { Path = "missing/key.txt" };

        var result = handler.GetFileData(file);

        result.Should().BeNull();
    }

    // ─── DeleteFile tests ────────────────────────────────────────────────────

    [TestMethod]
    public void DeleteFile_CallsDeleteObject_WithCorrectBucketAndKey()
    {
        // Arrange
        const string key = "uploads/file.txt";

        var s3Mock = new Mock<IAmazonS3>();
        s3Mock
            .Setup(c => c.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());

        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions());
        var file    = new StubFile { Path = key };

        // Act
        handler.DeleteFile(file);

        // Assert
        s3Mock.Verify(c => c.DeleteObjectAsync(
            It.Is<DeleteObjectRequest>(r =>
                r.BucketName == "test-bucket" && r.Key == key),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public void DeleteFile_DoesNotThrow_WhenS3Throws()
    {
        var s3Mock = new Mock<IAmazonS3>();
        s3Mock
            .Setup(c => c.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("access denied"));

        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions());
        var file    = new StubFile { Path = "some/key.txt" };

        // Should log and swallow — not propagate.
        var act = () => handler.DeleteFile(file);
        act.Should().NotThrow();
    }

    [TestMethod]
    public void DeleteFile_Noop_WhenPathIsEmpty()
    {
        var s3Mock  = new Mock<IAmazonS3>();
        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions());
        var file    = new StubFile { Path = string.Empty };

        handler.DeleteFile(file);

        s3Mock.Verify(c => c.DeleteObjectAsync(
            It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Round-trip metadata test ────────────────────────────────────────────

    [TestMethod]
    public void Upload_ThenGetFileData_RoundTripByKey()
    {
        // Simulates: upload returns a key, which is later used to retrieve the object.
        const string content = "round-trip content";
        var uploadBody       = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var downloadBody     = new MemoryStream(Encoding.UTF8.GetBytes(content));

        string? capturedKey  = null;

        var s3Mock = new Mock<IAmazonS3>();

        // Capture the key used in PutObject
        s3Mock
            .Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((r, _) => capturedKey = r.Key)
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = System.Net.HttpStatusCode.OK });

        // Return content stream when GetObject is called with the captured key
        s3Mock
            .Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse { ResponseStream = downloadBody });

        var handler = new WtmS3FileHandler(s3Mock.Object, BuildOptions(prefix: "files"));

        // Upload
        var (path, handlerInfo) = handler.Upload("doc.txt", content.Length, uploadBody);
        path.Should().NotBeNull();
        handlerInfo.Should().Be("s3");
        capturedKey.Should().Be(path, "the key captured by PutObject must equal the returned path");

        // Retrieve using the returned key
        var file   = new StubFile { Path = path, HandlerInfo = "s3" };
        var result = handler.GetFileData(file);

        result.Should().NotBeNull();
        result!.Position = 0;
        var text = new StreamReader(result).ReadToEnd();
        text.Should().Be(content);

        s3Mock.Verify(c => c.GetObjectAsync(
            It.Is<GetObjectRequest>(r => r.Key == path),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
