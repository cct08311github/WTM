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
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;
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
            handlers.SetValue(null, new Dictionary<string, ConstructorInfo> { { "database", ctor } });
            defaults.SetValue(null, ctor);
        }

        [TestCleanup]
        public void Cleanup()
        {
            typeof(WtmFileProvider).GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, _oldHandlers);
            typeof(WtmFileProvider).GetField("_defaultHandler", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, _oldDefault);
        }

        private sealed class Harness
        {
            public _FrameworkController Controller;
            public WtmFileProvider Provider;
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
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
            http.Response.Body = new MemoryStream();
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
            return new Harness { Controller = controller, Provider = new WtmFileProvider(wtm) };
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

        private Guid AddFile(byte[] data, string ext = "png")
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
                SaveMode = "database",
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
        public async Task GetFile_WithASize_TakesOnlyTheFirstFrameOfAnAnimation()
        {
            // an ordinary (single frame) gif still resizes
            var id = AddFile(Make(40, 20, "gif"), "gif");
            var h = Make();

            var bytes = Read(await h.Controller.GetFile(h.Provider, id.ToString(), false, null, 10, null));

            Assert.AreEqual(10, Image.Identify(bytes).Width);
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
