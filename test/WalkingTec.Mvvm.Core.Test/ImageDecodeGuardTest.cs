using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using WalkingTec.Mvvm.Admin.Api;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class ImageDecodeGuardTest
    {
        private static MemoryStream Make(int w, int h, string format)
        {
            var ms = new MemoryStream();
            using (var img = new Image<Rgba32>(w, h))
            {
                switch (format)
                {
                    case "png": img.Save(ms, new PngEncoder()); break;
                    case "jpeg": img.Save(ms, new JpegEncoder()); break;
                    case "gif": img.Save(ms, new GifEncoder()); break;
                    case "bmp": img.Save(ms, new BmpEncoder()); break;
                    case "webp": img.Save(ms, new WebpEncoder()); break;
                    case "tiff": img.Save(ms, new TiffEncoder()); break;
                }
            }
            ms.Position = 0;
            return ms;
        }

        [DataTestMethod]
        [DataRow("png")]
        [DataRow("jpeg")]
        [DataRow("gif")]
        [DataRow("bmp")]
        [DataRow("webp")]
        public void AllowedFormatsPass(string format)
        {
            using var ms = Make(16, 8, format);
            var ok = ImageDecodeGuard.TryLoad(ms, new ImageDecodeLimits(), out var image, out var error);
            using (image)
            {
                Assert.IsTrue(ok, error);
                Assert.IsNotNull(image);
                Assert.AreEqual(16, image.Width);
                Assert.AreEqual(8, image.Height);
            }
        }

        [TestMethod]
        public void TiffIsRejected()
        {
            using var ms = Make(16, 8, "tiff");
            var ok = ImageDecodeGuard.TryLoad(ms, new ImageDecodeLimits(), out var image, out var error);
            Assert.IsFalse(ok);
            Assert.IsNull(image);
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void OversizedWidthIsRejected()
        {
            using var ms = Make(ImageDecodeLimits.DefaultMaxWidth + 1, 1, "png");
            Assert.IsFalse(ImageDecodeGuard.TryLoad(ms, new ImageDecodeLimits(), out var image, out _));
            Assert.IsNull(image);
        }

        [TestMethod]
        public void OversizedPixelCountIsRejected()
        {
            // each side within limits, total pixels above MaxPixels
            using var ms = Make(2000, 2000, "png");
            var limits = new ImageDecodeLimits { MaxPixels = 1_000_000 };
            Assert.IsFalse(ImageDecodeGuard.TryLoad(ms, limits, out var image, out _));
            Assert.IsNull(image);
        }

        [TestMethod]
        public void OversizedBytesAreRejected()
        {
            using var ms = Make(64, 64, "bmp");
            var limits = new ImageDecodeLimits { MaxBytes = 100 };
            Assert.IsTrue(ms.Length > 100);
            Assert.IsFalse(ImageDecodeGuard.TryLoad(ms, limits, out var image, out _));
            Assert.IsNull(image);
        }

        [TestMethod]
        public void OversizedBytesAreRejectedForNonSeekableStream()
        {
            using var inner = Make(64, 64, "bmp");
            using var ns = new NonSeekableStream(inner);
            var limits = new ImageDecodeLimits { MaxBytes = 100 };
            Assert.IsFalse(ImageDecodeGuard.TryLoad(ns, limits, out var image, out _));
            Assert.IsNull(image);
        }

        [TestMethod]
        public void NonImageIsRejected()
        {
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"));
            Assert.IsFalse(ImageDecodeGuard.TryLoad(ms, new ImageDecodeLimits(), out var image, out _));
            Assert.IsNull(image);
        }

        [TestMethod]
        public void EmptyStreamIsRejected()
        {
            using var ms = new MemoryStream();
            Assert.IsFalse(ImageDecodeGuard.TryLoad(ms, new ImageDecodeLimits(), out var image, out _));
            Assert.IsNull(image);
        }

        private class NonSeekableStream : Stream
        {
            private readonly Stream _s;
            public NonSeekableStream(Stream s) { _s = s; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new System.NotSupportedException();
            public override long Position { get => throw new System.NotSupportedException(); set => throw new System.NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _s.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new System.NotSupportedException();
            public override void SetLength(long value) => throw new System.NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();
        }
    }
}
