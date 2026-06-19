#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Tests for Issue #407 — opt-in upload validation seam.
    ///
    /// Covers:
    ///   – No-op validator (default behaviour): every file is accepted
    ///   – ExtensionContentTypeUploadValidator: extension allowlist enforcement
    ///   – ExtensionContentTypeUploadValidator: content-type allowlist enforcement
    ///   – ExtensionContentTypeUploadValidator: MaxUploadBytes cap enforcement
    ///   – Custom IUploadValidator is invoked and its result is honoured
    /// </summary>
    [TestClass]
    public class UploadValidationTests
    {
        // ─── NoOpUploadValidator ──────────────────────────────────────────────────

        [TestMethod]
        public async Task NoOpValidator_AlwaysReturnsValid()
        {
            var validator = new NoOpUploadValidator();
            var ctx = BuildContext("virus.exe", ".exe", "application/x-msdownload", length: 5_000_000);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeTrue("no-op validator must accept everything, including .exe files");
            result.Error.Should().BeNull();
        }

        // ─── ExtensionContentTypeUploadValidator — extension checks ───────────────

        [TestMethod]
        public async Task ExtensionValidator_AllowedExtension_ReturnsValid()
        {
            var opts = new FileUploadOptions
            {
                AllowedExtensions = [".png", ".jpg", ".pdf"]
            };
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("photo.PNG", ".PNG", "image/png", 1024);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeTrue("'.PNG' should match '.png' (case-insensitive)");
        }

        [TestMethod]
        public async Task ExtensionValidator_AllowedExtensionWithoutLeadingDot_ReturnsValid()
        {
            // Extensions configured without leading dot must still match
            var opts = new FileUploadOptions
            {
                AllowedExtensions = ["png", "pdf"]
            };
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("photo.png", ".png", "image/png", 1024);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeTrue("'png' in allowlist should match '.png' extension");
        }

        [TestMethod]
        public async Task ExtensionValidator_ForbiddenExtension_ReturnsInvalid()
        {
            var opts = new FileUploadOptions
            {
                AllowedExtensions = [".png", ".jpg"]
            };
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("malware.exe", ".exe", "application/octet-stream", 1024);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeFalse("'.exe' is not in the allowlist");
            result.Error.Should().Contain(".exe", "rejection message should include the blocked extension");
        }

        [TestMethod]
        public async Task ExtensionValidator_EmptyAllowedExtensions_AcceptsAnything()
        {
            var opts = new FileUploadOptions
            {
                AllowedExtensions = []   // empty = allow all
            };
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("any.exe", ".exe", "application/octet-stream", 1024);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeTrue("empty AllowedExtensions means allow all");
        }

        // ─── ExtensionContentTypeUploadValidator — content-type checks ────────────

        [TestMethod]
        public async Task ContentTypeValidator_AllowedContentType_ReturnsValid()
        {
            var opts = new FileUploadOptions
            {
                AllowedContentTypes = ["image/png", "application/pdf"]
            };
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("doc.pdf", ".pdf", "application/pdf; charset=utf-8", 2048);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeTrue("'application/pdf; charset=utf-8' should match 'application/pdf' (params stripped)");
        }

        [TestMethod]
        public async Task ContentTypeValidator_ForbiddenContentType_ReturnsInvalid()
        {
            var opts = new FileUploadOptions
            {
                AllowedContentTypes = ["image/png", "image/jpeg"]
            };
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("script.sh", ".sh", "application/x-sh", 512);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeFalse("'application/x-sh' is not in the allowlist");
            result.Error.Should().Contain("application/x-sh");
        }

        [TestMethod]
        public async Task ContentTypeValidator_EmptyAllowedContentTypes_AcceptsAnything()
        {
            var opts = new FileUploadOptions
            {
                AllowedContentTypes = []   // empty = allow all
            };
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("strange.bin", ".bin", "application/octet-stream", 512);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeTrue("empty AllowedContentTypes means allow all");
        }

        // ─── ExtensionContentTypeUploadValidator — size checks ────────────────────

        [TestMethod]
        public async Task SizeValidator_FileBelowMaxUploadBytes_ReturnsValid()
        {
            var opts = new FileUploadOptions { MaxUploadBytes = 1_048_576 }; // 1 MB
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("small.png", ".png", "image/png", length: 512_000);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeTrue("512 KB is below the 1 MB cap");
        }

        [TestMethod]
        public async Task SizeValidator_FileExactlyAtMaxUploadBytes_ReturnsValid()
        {
            var opts = new FileUploadOptions { MaxUploadBytes = 1_000 };
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("exact.png", ".png", "image/png", length: 1_000);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeTrue("file exactly at MaxUploadBytes is allowed");
        }

        [TestMethod]
        public async Task SizeValidator_FileExceedsMaxUploadBytes_ReturnsInvalid()
        {
            var opts = new FileUploadOptions { MaxUploadBytes = 500 };
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("big.png", ".png", "image/png", length: 501);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeFalse("501 bytes exceeds 500-byte cap");
            result.Error.Should().Contain("size", "rejection message should mention the size exceeded");
            result.Error.Should().Contain("maximum", "rejection message should mention the maximum allowed");
        }

        [TestMethod]
        public async Task SizeValidator_MaxUploadBytesZero_AcceptsAnySize()
        {
            var opts = new FileUploadOptions { MaxUploadBytes = 0 }; // 0 = unlimited
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("huge.bin", ".bin", "application/octet-stream", length: long.MaxValue);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeTrue("MaxUploadBytes = 0 means unlimited");
        }

        // ─── Combined checks ──────────────────────────────────────────────────────

        [TestMethod]
        public async Task CombinedValidator_SizeCheckedBeforeExtension()
        {
            // Extension is allowed but size exceeds cap — size error should fire first.
            var opts = new FileUploadOptions
            {
                AllowedExtensions = [".png"],
                MaxUploadBytes = 100
            };
            var validator = new ExtensionContentTypeUploadValidator(opts);
            var ctx = BuildContext("img.png", ".png", "image/png", length: 9_999_999);

            var result = await validator.ValidateAsync(ctx);

            result.IsValid.Should().BeFalse();
            // The message contains the size in N0 format ("9,999,999" or locale-equivalent)
            // Just confirm it is the size error (not extension error):
            result.Error.Should().Contain("size", because: "size check should fire before extension check");
            result.Error.Should().NotContain("extension", because: "extension check must not trigger when size already fails");
        }

        // ─── Custom IUploadValidator seam ─────────────────────────────────────────

        [TestMethod]
        public async Task CustomValidator_IsInvokedAndRejectResultHonoured()
        {
            var invoked = false;
            var customValidator = new LambdaUploadValidator(ctx =>
            {
                invoked = true;
                // Reject everything that starts with "bad_"
                if (ctx.FileName.StartsWith("bad_", StringComparison.Ordinal))
                    return UploadValidationResult.Reject("Custom: file name starts with bad_");
                return UploadValidationResult.Valid;
            });

            // Rejected file
            var rejectedCtx = BuildContext("bad_malware.png", ".png", "image/png", 1024);
            var rejected = await customValidator.ValidateAsync(rejectedCtx);

            invoked.Should().BeTrue("the custom validator must be called");
            rejected.IsValid.Should().BeFalse();
            rejected.Error.Should().Be("Custom: file name starts with bad_");

            // Accepted file
            invoked = false;
            var acceptedCtx = BuildContext("good_photo.png", ".png", "image/png", 1024);
            var accepted = await customValidator.ValidateAsync(acceptedCtx);

            invoked.Should().BeTrue("validator must be called for the second file too");
            accepted.IsValid.Should().BeTrue();
        }

        [TestMethod]
        public async Task CustomValidator_CanReadStreamFromContext()
        {
            // Validate that OpenReadStream is passed through so validators can inspect bytes
            byte[]? captured = null;
            var customValidator = new LambdaUploadValidator(ctx =>
            {
                if (ctx.OpenReadStream != null)
                {
                    using var stream = ctx.OpenReadStream();
                    captured = new byte[stream.Length];
                    _ = stream.Read(captured, 0, captured.Length);
                }
                return UploadValidationResult.Valid;
            });

            var content = "hello world"u8.ToArray();
            var ctxWithStream = new UploadValidationContext
            {
                FileName = "test.txt",
                Extension = ".txt",
                ContentType = "text/plain",
                Length = content.Length,
                OpenReadStream = () => new MemoryStream(content)
            };

            await customValidator.ValidateAsync(ctxWithStream);

            captured.Should().BeEquivalentTo(content, "the stream opened from context should contain the file bytes");
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private static UploadValidationContext BuildContext(
            string fileName, string extension, string contentType, long length)
        {
            return new UploadValidationContext
            {
                FileName = fileName,
                Extension = extension,
                ContentType = contentType,
                Length = length,
                OpenReadStream = () => new MemoryStream(Encoding.UTF8.GetBytes("fake content")),
            };
        }

        /// <summary>Minimal test double that wraps a synchronous decision lambda.</summary>
        private sealed class LambdaUploadValidator : IUploadValidator
        {
            private readonly Func<UploadValidationContext, UploadValidationResult> _fn;
            public LambdaUploadValidator(Func<UploadValidationContext, UploadValidationResult> fn) => _fn = fn;

            public Task<UploadValidationResult> ValidateAsync(UploadValidationContext context, CancellationToken ct = default)
                => Task.FromResult(_fn(context));
        }
    }
}
