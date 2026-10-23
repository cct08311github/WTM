using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// /_Framework/UploadImage and /_Framework/GetFile decode pictures only within the limits of <c>ImageUploadLimits</c>, only when a size is asked for,
    /// and serve (or refuse) the original when the picture is outside them; ordinary pictures behave as before.
    /// </summary>
    [TestClass]
    public class FrameworkImageEndpointsTest
    {
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
            handlers.SetValue(null, new Dictionary<string, ConstructorInfo>
            {
                { "database", ctor },
                { "forward", typeof(ForwardOnlyHandler).GetConstructor(new[] { typeof(WTMContext) }) }
            });
            defaults.SetValue(null, ctor);
        }

        [TestCleanup]
        public void Cleanup()
        {
            typeof(WtmFileProvider).GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, _oldHandlers);
            typeof(WtmFileProvider).GetField("_defaultHandler", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, _oldDefault);
        }

        /// <summary>A file handler whose streams cannot seek (like a remote store).</summary>
        public sealed class ForwardOnlyHandler : WtmDataBaseFileHandler
        {
            public ForwardOnlyHandler(WTMContext wtm) : base(wtm) { }
            public override Stream GetFileData(IWtmFile file) => new ForwardOnlyStream(((MemoryStream)base.GetFileData(file)).ToArray());
        }

        private sealed class ForwardOnlyStream : MemoryStream
        {
            public ForwardOnlyStream(byte[] data) : base(data) { }
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
        }

        private sealed class Harness
        {
            public _FrameworkController Controller;
            public WtmFileProvider Provider;
            public CapturingLoggerFactory Logs;
            public MemoryStream Body;
        }

        private Harness Make(Dictionary<string, string> limits = null, IEnumerable<IFormFile> files = null)
        {
            var seed = _seed;
            var configs = new Configs();
            configs.Connections = new List<CS> { new CS { Key = "default", Value = "x", DbType = DBTypeEnum.Memory } };
            var wtm = MockWtmContext.CreateWtmContext(new LoginTestContext(seed), "alice", configs: configs, createDc: key => new LoginTestContext(seed));
            wtm.GlobaInfo.CustomUserType = typeof(LoginTestUser);
            var services = new ServiceCollection();
            var values = new Dictionary<string, string>();
            if (limits != null)
            {
                foreach (var item in limits)
                {
                    values["ImageUploadLimits:" + item.Key] = item.Value;
                }
            }
            var logs = new CapturingLoggerFactory();
            services.AddSingleton<ILoggerFactory>(logs);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
            var body = new MemoryStream();
            http.Response.Body = body;
            if (files != null)
            {
                var form = new FormFileCollection();
                foreach (var f in files)
                {
                    form.Add(f);
                }
                http.Request.ContentType = "multipart/form-data; boundary=x";
                http.Request.Form = new FormCollection(new Dictionary<string, StringValues>(), form);
            }
            var controller = new _FrameworkController(null) { Wtm = wtm, ControllerContext = new ControllerContext { HttpContext = http } };
            return new Harness { Controller = controller, Provider = new WtmFileProvider(wtm), Logs = logs, Body = body };
        }

        private static byte[] Make(int w, int h, string format)
        {
            using var ms = new MemoryStream();
            using (var img = new Image<Rgba32>(w, h))
            {
                switch (format)
                {
                    case "png": img.Save(ms, new PngEncoder()); break;
                    case "jpeg": img.Save(ms, new JpegEncoder()); break;
                    case "gif": img.Save(ms, new GifEncoder()); break;
                    case "tiff": img.Save(ms, new TiffEncoder()); break;
                }
            }
            return ms.ToArray();
        }

        private static byte[] MakeAnimatedGif(int w, int h)
        {
            using var ms = new MemoryStream();
            using (var img = new Image<Rgba32>(w, h, new Rgba32(255, 0, 0, 255)))
            {
                foreach (var color in new[] { new Rgba32(0, 0, 255, 255), new Rgba32(0, 255, 0, 255) })
                {
                    using var frame = new Image<Rgba32>(w, h, color);
                    img.Frames.AddFrame(frame.Frames.RootFrame);
                }
                Assert.AreEqual(3, img.Frames.Count);
                img.Save(ms, new GifEncoder());
            }
            return ms.ToArray();
        }

        // a 60x30 progressive JPEG (SOF2): red left half, blue right half; ImageSharp 3.1 cannot write one
        private const string ProgressiveJpegBase64 =
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wgARCAAeADwDASIAAhEBAxEB/8QAGAABAQEBAQAAAAAAAAAAAAAAAAIDBAb/xAAZAQACAwEAAAAAAAAAAAAAAAAABgMEBwX/2gAMAwEAAhADEAAAAfPjnOC4uaruGzNQDlCbqi4uaruGzNQD/8QAFxAAAwEAAAAAAAAAAAAAAAAAAiAxEP/aAAgBAQABBQLRrjXGuNT/xAAcEQAABgMAAAAAAAAAAAAAAAABAwQ0crEAECD/2gAIAQMBAT8Bxc2MiNcLmxkRrf8A/8QAGxEAAQQDAAAAAAAAAAAAAAAAAQIEM3EAECD/2gAIAQIBAT8BxvMmxw3mTY3/AP/EABQQAQAAAAAAAAAAAAAAAAAAAFD/2gAIAQEABj8CA//EABcQAQEBAQAAAAAAAAAAAAAAADEwECD/2gAIAQEAAT8h04HA4Hz/AP/aAAwDAQACAAMAAAAQ/q//AP6v/wD/xAAYEQACAwAAAAAAAAAAAAAAAABR8AAQIP/aAAgBAwEBPxCPSw9K/wD/xAAZEQACAwEAAAAAAAAAAAAAAAABUQAQ8CD/2gAIAQIBAT8QmoxxqMX/AP/EABcQAQEBAQAAAAAAAAAAAAAAALEwECD/2gAIAQEAAT8Q1RBRBRBRz//Z";

        private static byte[] MakeProgressiveJpeg()
        {
            var data = Convert.FromBase64String(ProgressiveJpegBase64);
            Assert.IsTrue(data.AsSpan().IndexOf(new byte[] { 0xFF, 0xC2 }) > 0, "the fixture is progressive");
            return data;
        }

        /// <summary>A PNG whose header says another size than the data has (the checksum of the header is kept valid).</summary>
        private static byte[] WithDeclaredSize(byte[] png, uint width, uint height)
        {
            var b = (byte[])png.Clone();
            void Put(int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
            Put(16, width);
            Put(20, height);
            uint crc = 0xFFFFFFFF;
            for (var i = 12; i < 29; i++)
            {
                crc ^= b[i];
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            Put(29, ~crc);
            return b;
        }

        private Guid AddFile(byte[] data, string ext = "png", string saveMode = "database")
        {
            using var dc = new LoginTestContext(_seed);
            var id = Guid.NewGuid();
            dc.Set<FileAttachment>().Add(new FileAttachment
            {
                ID = id,
                FileName = "f." + ext,
                FileExt = ext,
                Length = data.Length,
                UploadTime = new DateTime(2026, 1, 1),
                SaveMode = saveMode,
                FileData = data
            });
            dc.SaveChanges();
            return id;
        }

        private int FileCount()
        {
            using var dc = new LoginTestContext(_seed);
            return dc.Set<FileAttachment>().Count();
        }

        private static byte[] Read(IActionResult result)
        {
            Assert.IsInstanceOfType(result, typeof(FileStreamResult));
            using var ms = new MemoryStream();
            ((FileStreamResult)result).FileStream.CopyTo(ms);
            return ms.ToArray();
        }

        private static IFormFile Form(string name, byte[] data) => new FormFile(new MemoryStream(data), 0, data.Length, "file", name);

        private static (int Code, string Id) UploadAnswer(IActionResult result)
        {
            var ok = (OkObjectResult)result;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var code = doc.RootElement.GetProperty("Code").GetInt32();
            var id = doc.RootElement.GetProperty("Data").GetProperty("Id").GetString();
            return (code, id);
        }

        // ---- GetFile -----------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task GetFile_WithASize_ResizesAnOrdinaryPicture()
        {
            var id = AddFile(Make(40, 20, "png"));
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, null));

            var info = Image.Identify(bytes);
            Assert.AreEqual(10, info.Width);
            Assert.AreEqual(5, info.Height);
        }

        [TestMethod]
        public async Task GetFile_WithoutASize_ServesTheStoredBytes()
        {
            var original = Make(40, 20, "png");
            var id = AddFile(original);
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, null, null));

            CollectionAssert.AreEqual(original, bytes);
        }

        [TestMethod]
        public async Task GetFile_WithoutASize_DoesNotLookAtTheContent_EvenWhenItIsOutsideTheLimits()
        {
            var original = Make(300, 300, "png");
            var id = AddFile(original);
            var h = Make(new Dictionary<string, string> { { "MaxPixels", "100" } });

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, null, null));

            CollectionAssert.AreEqual(original, bytes);
        }

        [TestMethod]
        public async Task GetFile_WithASize_ServesThePictureAsItIs_WhenItHasTooManyPixels()
        {
            var original = Make(300, 300, "png");
            var id = AddFile(original);
            var h = Make(new Dictionary<string, string> { { "MaxPixels", "10000" } });

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, 10));

            CollectionAssert.AreEqual(original, bytes);
        }

        [TestMethod]
        public async Task GetFile_WithASize_ServesThePictureAsItIs_WhenItIsLargerThanMaxBytes()
        {
            var original = Make(300, 300, "png");
            var id = AddFile(original);
            var h = Make(new Dictionary<string, string> { { "MaxBytes", "50" } });

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, 10));

            CollectionAssert.AreEqual(original, bytes);
        }

        [TestMethod]
        public async Task GetFile_WithASize_ServesTheFileAsItIs_WhenTheFormatIsNotOnTheAllowList()
        {
            var original = Make(40, 20, "tiff");
            var id = AddFile(original, "tif");
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, 10));

            CollectionAssert.AreEqual(original, bytes);
        }

        [TestMethod]
        public async Task GetFile_WithASize_ServesTheFileAsItIs_WhenItIsNotAPicture()
        {
            var original = System.Text.Encoding.UTF8.GetBytes("not a picture at all");
            var id = AddFile(original, "png");
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, 10));

            CollectionAssert.AreEqual(original, bytes);
        }

        [TestMethod]
        public async Task GetFile_WithASizeAboveTheMaximum_ServesThePictureAsItIs()
        {
            var original = Make(40, 20, "png");
            var id = AddFile(original);
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 5000, null));

            CollectionAssert.AreEqual(original, bytes);
        }

        [TestMethod]
        public async Task GetFile_WithTheMaximumRaised_ResizesWhatTheDefaultRefused()
        {
            var id = AddFile(Make(40, 20, "png"));
            var h = Make(new Dictionary<string, string> { { "MaxResizeWidth", "6000" }, { "MaxResizeHeight", "6000" } });

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 5000, null));

            Assert.AreEqual(5000, Image.Identify(bytes).Width);
        }

        [TestMethod]
        public async Task GetFile_WithASize_MakesOneFrameFromTheFirstFrameOfAnAnimatedGif()
        {
            var animated = MakeAnimatedGif(40, 20);
            var id = AddFile(animated, "gif");
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, null));

            using var thumbnail = Image.Load<Rgba32>(bytes);
            Assert.AreEqual(1, thumbnail.Frames.Count);
            Assert.AreEqual(10, thumbnail.Width);
            var pixel = thumbnail[5, 2];
            Assert.IsTrue(pixel.R > 200 && pixel.B < 60 && pixel.G < 60, "the first frame (red) is the thumbnail: " + pixel);
        }

        [TestMethod]
        public async Task GetFile_WithASize_ResizesAProgressiveJpeg()
        {
            var id = AddFile(MakeProgressiveJpeg(), "jpg");
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 20, null));

            var info = Image.Identify(bytes);
            Assert.AreEqual(20, info.Width);
            Assert.AreEqual(10, info.Height);
            using var thumbnail = Image.Load<Rgba32>(bytes);
            Assert.IsTrue(thumbnail[3, 5].R > 150 && thumbnail[3, 5].B < 100, "left half is red");
            Assert.IsTrue(thumbnail[16, 5].B > 150 && thumbnail[16, 5].R < 100, "right half is blue");
        }

        [TestMethod]
        public async Task GetFile_WithASize_ServesTheFileAsItIs_WhenTheHeaderSaysMoreThanTheLimitsAllow()
        {
            var original = WithDeclaredSize(Make(40, 20, "png"), 50000, 50000);
            var id = AddFile(original);
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, null));

            CollectionAssert.AreEqual(original, bytes);
        }

        [TestMethod]
        public async Task GetFile_WithASize_DoesNotFail_WhenTheHeaderSaysMoreThanTheDataHas()
        {
            // a size inside the limits, but the data of such a picture is not there: the decoder may give up (the file is served as it is)
            // or fill in what is missing (a thumbnail of the requested size); either way the request is answered
            var original = WithDeclaredSize(Make(40, 20, "png"), 3000, 3000);
            var id = AddFile(original);
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, null));

            if (bytes.Length == original.Length && bytes.SequenceEqual(original))
            {
                return;
            }
            Assert.AreEqual(10, Image.Identify(bytes).Width);
        }

        // ---- fallbacks: what is served, and how it is logged ------------------------------------------------------------------------------

        [TestMethod]
        public async Task GetFile_Fallback_IsLoggedAsInformation_NotAsAWarning()
        {
            var id = AddFile(Make(300, 300, "png"));
            var h = Make(new Dictionary<string, string> { { "MaxPixels", "10000" } });

            await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, 10);

            Assert.IsTrue(h.Logs.Entries.Any(x => x.Level == LogLevel.Information && x.Message.Contains("GetFile")), "logged");
            Assert.IsFalse(h.Logs.Entries.Any(x => x.Level >= LogLevel.Warning), "a fallback is not a warning");
        }

        [TestMethod]
        public void UploadImage_Rejection_IsLoggedAsAWarning_WithoutFileContent()
        {
            var h = Make(new Dictionary<string, string> { { "MaxPixels", "100" } }, new[] { Form("secret-name.png", Make(40, 20, "png")) });

            h.Controller.UploadImage(h.Provider, width: 10);

            var warning = h.Logs.Entries.Single(x => x.Level == LogLevel.Warning);
            StringAssert.Contains(warning.Message, "UploadImage");
            Assert.IsFalse(warning.Message.Contains("secret-name"));
        }

        [TestMethod]
        public async Task GetFile_OfASvgWithASize_StaysAnAttachmentWithNosniff()
        {
            var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><script>1</script></svg>");
            var id = AddFile(svg, "svg");
            var h = Make();

            var result = await h.Controller.GetFile(h.Provider, id.ToString(), true, null, 10, 10);

            Assert.IsInstanceOfType(result, typeof(EmptyResult));
            var response = h.Controller.Response;
            Assert.AreEqual("nosniff", response.Headers["X-Content-Type-Options"].ToString());
            StringAssert.StartsWith(response.Headers["Content-Disposition"].ToString(), "attachment;");
            CollectionAssert.AreEqual(svg, h.Body.ToArray());
        }

        [TestMethod]
        public async Task GetFile_OfAPictureWithASize_StaysInlineWithNosniff()
        {
            var id = AddFile(Make(40, 20, "png"));
            var h = Make();

            await h.Controller.GetFile(h.Provider, id.ToString(), true, null, 10, null);

            var response = h.Controller.Response;
            Assert.AreEqual("nosniff", response.Headers["X-Content-Type-Options"].ToString());
            StringAssert.StartsWith(response.Headers["Content-Disposition"].ToString(), "inline;");
            Assert.AreEqual(10, Image.Identify(h.Body.ToArray()).Width);
        }

        // ---- streams that cannot seek ----------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task GetFile_WithASize_ServesTheWholeFile_WhenTheStreamCannotSeekAndIsLargerThanMaxBytes()
        {
            var original = Make(300, 300, "png");
            var id = AddFile(original, "png", "forward");
            var h = Make(new Dictionary<string, string> { { "MaxBytes", "50" } });

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, 10));

            CollectionAssert.AreEqual(original, bytes, "the part that was read to find out the size is not lost");
        }

        [TestMethod]
        public async Task GetFile_WithASize_ServesTheWholeFile_WhenTheStreamCannotSeekAndIsNotAPicture()
        {
            var original = System.Text.Encoding.UTF8.GetBytes(new string('x', 5000));
            var id = AddFile(original, "png", "forward");
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, 10));

            CollectionAssert.AreEqual(original, bytes);
        }

        [TestMethod]
        public async Task GetFile_WithASize_ResizesAPicture_WhenTheStreamCannotSeek()
        {
            var id = AddFile(Make(40, 20, "png"), "png", "forward");
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, null));

            Assert.AreEqual(10, Image.Identify(bytes).Width);
        }

        [TestMethod]
        public async Task GetFile_Inline_ServesTheWholeFile_WhenTheStreamCannotSeek()
        {
            var original = Make(40, 20, "png");
            var id = AddFile(original, "png", "forward");
            var h = Make();

            var result = await h.Controller.GetFile(h.Provider, id.ToString(), true, null, null, null);

            Assert.IsInstanceOfType(result, typeof(EmptyResult));
            CollectionAssert.AreEqual(original, h.Body.ToArray());
            StringAssert.StartsWith(h.Controller.Response.Headers["Content-Disposition"].ToString(), "inline;");
        }

        // ---- UploadImage -------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void UploadImage_WithASize_StoresAThumbnailOfAnOrdinaryPicture()
        {
            var h = Make(files: new[] { Form("a.png", Make(40, 20, "png")) });

            var (code, id) = UploadAnswer(h.Controller.UploadImage(h.Provider, width: 10));

            Assert.AreEqual(200, code);
            using var dc = new LoginTestContext(_seed);
            var stored = dc.Set<FileAttachment>().Single(x => x.ID == Guid.Parse(id));
            var info = Image.Identify(stored.FileData);
            Assert.AreEqual(10, info.Width);
            Assert.AreEqual(5, info.Height);
        }

        [TestMethod]
        public void UploadImage_WithoutASize_StoresTheFileAsItIs_WhateverItIs()
        {
            var original = System.Text.Encoding.UTF8.GetBytes("not a picture at all");
            var h = Make(files: new[] { Form("a.txt", original) });

            var (code, id) = UploadAnswer(h.Controller.UploadImage(h.Provider));

            Assert.AreEqual(200, code);
            using var dc = new LoginTestContext(_seed);
            CollectionAssert.AreEqual(original, dc.Set<FileAttachment>().Single(x => x.ID == Guid.Parse(id)).FileData);
        }

        [TestMethod]
        [DataRow("garbage")]
        [DataRow("tiff")]
        public void UploadImage_WithASize_RefusesWhatIsNotAnAllowedPicture_AndStoresNothing(string kind)
        {
            var data = kind == "tiff" ? Make(40, 20, "tiff") : System.Text.Encoding.UTF8.GetBytes("not a picture at all");
            var h = Make(files: new[] { Form("a.png", data) });

            var (code, id) = UploadAnswer(h.Controller.UploadImage(h.Provider, width: 10));

            Assert.AreEqual(404, code);
            Assert.AreEqual(string.Empty, id);
            Assert.AreEqual(0, FileCount());
        }

        [TestMethod]
        public void UploadImage_WithASize_RefusesAPictureOutsideTheLimits_AndStoresNothing()
        {
            var h = Make(new Dictionary<string, string> { { "MaxPixels", "100" } }, new[] { Form("a.png", Make(40, 20, "png")) });

            var (code, _) = UploadAnswer(h.Controller.UploadImage(h.Provider, width: 10));

            Assert.AreEqual(404, code);
            Assert.AreEqual(0, FileCount());
        }

        [TestMethod]
        [DataRow(-5, 0)]
        [DataRow(5000, 0)]
        [DataRow(0, 5000)]
        public void UploadImage_WithASizeOutOfRange_Refuses_AndStoresNothing(int width, int height)
        {
            var h = Make(files: new[] { Form("a.png", Make(40, 20, "png")) });

            var (code, _) = UploadAnswer(h.Controller.UploadImage(h.Provider, width: width == 0 ? (int?)null : width, height: height == 0 ? (int?)null : height));

            Assert.AreEqual(404, code);
            Assert.AreEqual(0, FileCount());
        }

        [TestMethod]
        public void UploadImage_WithBothSizes_StoresThatSize()
        {
            var h = Make(files: new[] { Form("a.png", Make(40, 20, "png")) });

            var (code, id) = UploadAnswer(h.Controller.UploadImage(h.Provider, width: 8, height: 8));

            Assert.AreEqual(200, code);
            using var dc = new LoginTestContext(_seed);
            var info = Image.Identify(dc.Set<FileAttachment>().Single(x => x.ID == Guid.Parse(id)).FileData);
            Assert.AreEqual(8, info.Width);
            Assert.AreEqual(8, info.Height);
        }
    }
}
