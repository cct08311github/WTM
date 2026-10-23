using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;
using WalkingTec.Mvvm.Mvc.Imaging;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>The picture guard of the framework (WalkingTec.Mvvm.Mvc.Imaging): limits, configuration and the thumbnail helper.</summary>
    [TestClass]
    public class FrameworkImageDecodeGuardTest
    {
        private sealed class ForwardOnlyStream : MemoryStream
        {
            public ForwardOnlyStream(byte[] data) : base(data) { }
            public override bool CanSeek => false;
        }

        private static byte[] Make(int w, int h, bool tiff = false)
        {
            using var ms = new MemoryStream();
            using (var img = new Image<Rgba32>(w, h))
            {
                if (tiff) img.Save(ms, new TiffEncoder()); else img.Save(ms, new PngEncoder());
            }
            return ms.ToArray();
        }

        private static IConfiguration Config(Dictionary<string, string> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        [TestMethod]
        public void Limits_DefaultsAreTheDocumentedOnes()
        {
            var limits = new ImageDecodeLimits();
            Assert.AreEqual(10L * 1024 * 1024, limits.MaxBytes);
            Assert.AreEqual(8192, limits.MaxWidth);
            Assert.AreEqual(8192, limits.MaxHeight);
            Assert.AreEqual(25_000_000L, limits.MaxPixels);
            Assert.AreEqual(1, limits.MaxFrames);
            Assert.AreEqual(4096, limits.MaxResizeWidth);
            Assert.AreEqual(4096, limits.MaxResizeHeight);
            var fromNothing = ImageDecodeLimits.FromConfiguration(null);
            Assert.AreEqual(limits.MaxPixels, fromNothing.MaxPixels);
        }

        [TestMethod]
        public void Limits_AreReadFromTheImageUploadLimitsSection_AndBadValuesKeepTheDefault()
        {
            var limits = ImageDecodeLimits.FromConfiguration(Config(new Dictionary<string, string>
            {
                { "ImageUploadLimits:MaxBytes", "1000" },
                { "ImageUploadLimits:MaxWidth", "100" },
                { "ImageUploadLimits:MaxHeight", "-1" },
                { "ImageUploadLimits:MaxPixels", "abc" },
                { "ImageUploadLimits:MaxFrames", "3" },
                { "ImageUploadLimits:MaxResizeWidth", "500" },
                { "ImageUploadLimits:MaxResizeHeight", "0" },
            }));
            Assert.AreEqual(1000L, limits.MaxBytes);
            Assert.AreEqual(100, limits.MaxWidth);
            Assert.AreEqual(ImageDecodeLimits.DefaultMaxHeight, limits.MaxHeight);
            Assert.AreEqual(ImageDecodeLimits.DefaultMaxPixels, limits.MaxPixels);
            Assert.AreEqual(3, limits.MaxFrames);
            Assert.AreEqual(500, limits.MaxResizeWidth);
            Assert.AreEqual(ImageDecodeLimits.DefaultMaxResizeHeight, limits.MaxResizeHeight);
        }

        [TestMethod]
        [DataRow("MaxBytes")]
        [DataRow("MaxWidth")]
        [DataRow("MaxHeight")]
        [DataRow("MaxPixels")]
        [DataRow("MaxFrames")]
        [DataRow("MaxResizeWidth")]
        [DataRow("MaxResizeHeight")]
        public void Limits_ZeroNegativeAndNonNumericValues_KeepTheDefault_ForEachKey(string key)
        {
            var defaults = new ImageDecodeLimits();
            var property = typeof(ImageDecodeLimits).GetProperty(key);
            foreach (var bad in new[] { "0", "-1", "-9999", "abc", "", " ", "1.5", "12abc", "99999999999999999999999" })
            {
                var limits = ImageDecodeLimits.FromConfiguration(Config(new Dictionary<string, string> { { "ImageUploadLimits:" + key, bad } }));
                Assert.AreEqual(property.GetValue(defaults), property.GetValue(limits), key + " = '" + bad + "'");
                foreach (var other in typeof(ImageDecodeLimits).GetProperties().Where(x => x.Name != key))
                {
                    Assert.AreEqual(other.GetValue(defaults), other.GetValue(limits), other.Name + " changed by " + key);
                }
            }
            var good = ImageDecodeLimits.FromConfiguration(Config(new Dictionary<string, string> { { "ImageUploadLimits:" + key, "7" } }));
            Assert.AreEqual(System.Convert.ChangeType(7, property.PropertyType), property.GetValue(good), key);
        }

        private static byte[] MakeAnimatedGif()
        {
            using var ms = new MemoryStream();
            using (var img = new Image<Rgba32>(8, 8, new Rgba32(255, 0, 0, 255)))
            {
                foreach (var color in new[] { new Rgba32(0, 0, 255, 255), new Rgba32(0, 255, 0, 255) })
                {
                    using var frame = new Image<Rgba32>(8, 8, color);
                    img.Frames.AddFrame(frame.Frames.RootFrame);
                }
                img.Save(ms, new GifEncoder());
            }
            return ms.ToArray();
        }

        [TestMethod]
        public void MaxFrames_BoundsTheFramesThatAreDecoded()
        {
            var data = MakeAnimatedGif();
            foreach (var (frames, expected) in new[] { (1, 1), (2, 2), (10, 3) })
            {
                using var source = new MemoryStream(data);
                Assert.IsTrue(ImageDecodeGuard.TryLoad(source, new ImageDecodeLimits { MaxFrames = frames }, out var image, out var error), error);
                using (image)
                {
                    Assert.AreEqual(expected, image.Frames.Count, "MaxFrames = " + frames);
                }
            }
        }

        [TestMethod]
        public void Resize_OfAnAnimatedGif_MakesOneFrame()
        {
            using var source = new MemoryStream(MakeAnimatedGif());
            var result = ImageDecodeGuard.Resize(source, 4, null, new ImageDecodeLimits());

            Assert.IsNotNull(result.Resized);
            using (result.Resized)
            using (var thumbnail = Image.Load<Rgba32>(result.Resized))
            {
                Assert.AreEqual(1, thumbnail.Frames.Count);
                Assert.IsTrue(thumbnail[1, 1].R > 200, "the first frame");
            }
        }

        [TestMethod]
        public void Resize_MakesAJpegThumbnail_KeepingTheAspectRatio()
        {
            using var source = new MemoryStream(Make(40, 20));
            var result = ImageDecodeGuard.Resize(source, 10, null, new ImageDecodeLimits());

            Assert.IsNotNull(result.Resized);
            Assert.IsNull(result.Buffered);
            Assert.IsNull(result.Error);
            using (result.Resized)
            {
                var info = Image.Identify(result.Resized);
                Assert.AreEqual(10, info.Width);
                Assert.AreEqual(5, info.Height);
                Assert.AreEqual("JPEG", info.Metadata.DecodedImageFormat.Name);
            }
        }

        [TestMethod]
        public void Resize_OutsideTheLimits_DoesNotDecode_AndHandsTheBytesBack()
        {
            var data = Make(40, 20);
            using var source = new ForwardOnlyStream(data);
            var result = ImageDecodeGuard.Resize(source, 10, null, new ImageDecodeLimits { MaxPixels = 100 });

            Assert.IsNull(result.Resized);
            Assert.AreEqual("Image dimensions too large", result.Error);
            Assert.IsNotNull(result.Buffered, "a source that cannot seek has been read: the copy is what can still be served");
            using (result.Buffered)
            {
                CollectionAssert.AreEqual(data, ((MemoryStream)result.Buffered).ToArray());
            }
        }

        [TestMethod]
        public void Resize_TooLargeToBuffer_LeavesTheSourceUntouched()
        {
            var data = Make(40, 20);
            using var source = new MemoryStream(data);
            var result = ImageDecodeGuard.Resize(source, 10, null, new ImageDecodeLimits { MaxBytes = 10 });

            Assert.IsNull(result.Resized);
            Assert.IsNull(result.Buffered);
            Assert.AreEqual("Image file too large", result.Error);
            Assert.AreEqual(0, source.Position);
        }

        [TestMethod]
        public void Resize_NeverThrows_ForABadPictureOrASizeOutOfRange()
        {
            foreach (var data in new[] { new byte[0], new byte[] { 1, 2, 3 }, Make(40, 20, tiff: true) })
            {
                using var source = new MemoryStream(data);
                var result = ImageDecodeGuard.Resize(source, 10, null, new ImageDecodeLimits());
                Assert.IsNull(result.Resized);
                Assert.IsNotNull(result.Error);
                result.Buffered?.Dispose();
            }
            foreach (var size in new (int?, int?)[] { (0, null), (-1, null), (5000, null), (null, 5000), (10, -3) })
            {
                using var source = new MemoryStream(Make(40, 20));
                var result = ImageDecodeGuard.Resize(source, size.Item1, size.Item2, new ImageDecodeLimits());
                Assert.IsNull(result.Resized, size.ToString());
                Assert.AreEqual("Requested size out of range", result.Error);
                result.Buffered?.Dispose();
            }
        }

        [TestMethod]
        public void CreateResizer_UsesTheLimitsOfEachCall()
        {
            var limits = new ImageDecodeLimits();
            var resizer = ImageDecodeGuard.CreateResizer(() => limits);

            using (var source = new MemoryStream(Make(40, 20)))
            {
                var ok = resizer(source, 10, null);
                Assert.IsNotNull(ok.Resized);
                ok.Resized.Dispose();
            }
            limits.MaxPixels = 100;
            using (var source = new MemoryStream(Make(40, 20)))
            {
                var refused = resizer(source, 10, null);
                Assert.IsNull(refused.Resized);
                refused.Buffered?.Dispose();
            }
        }
    }
}
