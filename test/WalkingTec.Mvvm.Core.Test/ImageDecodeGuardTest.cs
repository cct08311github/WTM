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

        [TestMethod]
        [Timeout(2000)]
        public void CraftedBigTiffHeaderIsRejectedQuickly()
        {
            // BigTIFF header + first IFD offset 16 + IFD entry count 5e9 (GHSA-wmxv-xphr-5c9g): the TIFF parser
            // would spin; the restricted configuration must never route it to a TIFF decoder.
            var bytes = new byte[24];
            bytes[0] = 0x49; bytes[1] = 0x49;           // II
            bytes[2] = 0x2B; bytes[3] = 0x00;           // BigTIFF magic 43
            bytes[4] = 0x08; bytes[5] = 0x00;           // offset size 8
            bytes[8] = 0x10;                            // first IFD at 16
            System.BitConverter.GetBytes(5_000_000_000UL).CopyTo(bytes, 16);
            using var ms = new MemoryStream(bytes);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ok = ImageDecodeGuard.TryLoad(ms, new ImageDecodeLimits(), out var image, out _);
            Assert.IsFalse(ok);
            Assert.IsNull(image);
            Assert.IsTrue(sw.ElapsedMilliseconds < 2000);
        }

        [TestMethod]
        public void AnimatedGifLoadsOnlyFirstFrame()
        {
            using var src = new Image<Rgba32>(8, 8);
            src.Frames.AddFrame(src.Frames.RootFrame);
            var ms = new MemoryStream();
            src.Save(ms, new GifEncoder());
            ms.Position = 0;
            Assert.IsTrue(ImageDecodeGuard.TryLoad(ms, new ImageDecodeLimits(), out var image, out _));
            using (image)
            {
                Assert.AreEqual(1, image.Frames.Count);
            }
        }

        [TestMethod]
        public void RejectedNonSeekableStreamKeepsBufferedCopy()
        {
            var data = System.Text.Encoding.UTF8.GetBytes("not an image");
            using var ns = new NonSeekableStream(new MemoryStream(data));
            Assert.IsFalse(ImageDecodeGuard.TryLoad(ns, new ImageDecodeLimits(), out var image, out var buffer, out _));
            Assert.IsNull(image);
            Assert.IsNotNull(buffer);
            using (buffer)
            {
                Assert.AreEqual(0, buffer.Position);
                CollectionAssert.AreEqual(data, buffer.ToArray());
            }
        }

        [TestMethod]
        public void ResizeTargetIsClampedAndGuardedAgainstZero()
        {
            var l = new ImageDecodeLimits();
            Assert.IsFalse(ImageDecodeGuard.TryResolveSize(30000, 30000, 100, 100, l, out _, out _));
            Assert.IsFalse(ImageDecodeGuard.TryResolveSize(0, null, 100, 100, l, out _, out _));
            Assert.IsFalse(ImageDecodeGuard.TryResolveSize(-5, null, 100, 100, l, out _, out _));
            Assert.IsFalse(ImageDecodeGuard.TryResolveSize(null, null, 100, 100, l, out _, out _));
            // aspect ratio math that would give 0 is raised to 1
            Assert.IsTrue(ImageDecodeGuard.TryResolveSize(1, null, 8000, 1, l, out var w, out var h));
            Assert.AreEqual(1, w); Assert.AreEqual(1, h);
            // derived side above the maximum is rejected
            Assert.IsFalse(ImageDecodeGuard.TryResolveSize(null, 1, 8000, 1, l, out _, out _));
            Assert.IsTrue(ImageDecodeGuard.TryResolveSize(null, 4000, 1, 1, l, out w, out h));
            Assert.AreEqual(4000, w); Assert.AreEqual(4000, h);
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
