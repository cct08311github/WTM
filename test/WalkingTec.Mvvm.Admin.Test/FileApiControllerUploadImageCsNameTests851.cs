#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// Regression test for #851: <see cref="FileApiController.UploadImage"/>'s no-resize fast
    /// path (<c>width == null &amp;&amp; height == null</c>) called the sibling <c>Upload</c>
    /// action <b>positionally</b> — <c>Upload(fp, sm, groupName, csName)</c>. <c>Upload</c>'s own
    /// signature is <c>(fp, sm, groupName, subdir, extra, csName)</c>: the 4th slot is
    /// <c>subdir</c>, not <c>csName</c>. The caller's <c>csName</c> therefore landed in
    /// <c>Upload</c>'s <c>subdir</c> parameter, while <c>Upload</c>'s own <c>csName</c> silently
    /// fell back to its default (<c>null</c> — the default connection). A caller who explicitly
    /// asked for a named connection had the upload written to the wrong database with no error.
    /// Three demo <c>FileApiController</c> copies (LayUI/Vue3/Blazor) all had this bug; this
    /// project only boots the LayUI copy (<see cref="FileApiController"/> in
    /// <c>WalkingTec.Mvvm.Admin.Api</c>) — same scope note as
    /// <see cref="FileApiControllerGetFileNosniffTests"/> and <c>FileApiControllerHardeningTests830</c>,
    /// the Vue3/Blazor copies received byte-identical fixes and are covered by that reasoning, not
    /// a second harness.
    /// </summary>
    [TestClass]
    public class FileApiControllerUploadImageCsNameTests851
    {
        // Overrides only CreateDC (virtual) so the test can observe exactly which `cskey` value
        // a call site forwards, without needing full multi-connection-string routing (which
        // MockController-style harnesses cannot provide — see
        // FileApiControllerGetFileNosniffTests.SingleConnectionWtmContext's doc comment for the
        // same limitation). Always returns the same pre-seeded in-memory IDataContext regardless
        // of cskey, so the upload itself succeeds either way — the assertion is purely on what
        // cskey was RECORDED, which is exactly the value #851's bug corrupts.
        private sealed class RecordingCsKeyWtmContext : WTMContext
        {
            private readonly IDataContext _dc;
            public List<string?> CreateDcCsKeys { get; } = new();

            public RecordingCsKeyWtmContext(IOptionsMonitor<Configs> configMonitor, IDataContext dc)
                : base(configMonitor, new GlobalData(), null, new DefaultUIService(), null, dc, null)
            {
                _dc = dc;
            }

            public override IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true)
            {
                CreateDcCsKeys.Add(cskey);
                return _dc;
            }
        }

        private static IFormFile BuildFormFile(byte[] payload, string fileName)
        {
            var stream = new MemoryStream(payload);
            return new FormFile(stream, 0, payload.Length, "file", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg",
            };
        }

        [TestMethod]
        public void UploadImage_NoResizeFastPath_ForwardsCallersCsNameToUpload_NotIntoSubdirSlot()
        {
            // A second, named connection the caller explicitly asks for — IsKnownConnectionKey
            // (checked both by UploadImage itself and, on the buggy code path, by Upload again)
            // must recognise it, or the request would 400 before #851's bug is even reachable.
            var configs = new Configs();
            configs.Connections.Add(new CS { Key = "wtmtest2", Value = "unused-in-this-test" });
            var configMonitor = new Mock<IOptionsMonitor<Configs>>();
            configMonitor.Setup(x => x.CurrentValue).Returns(configs);

            using var dc = new Demo.DataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            var wtm = new RecordingCsKeyWtmContext(configMonitor.Object, dc)
            {
                LoginUserInfo = new LoginUserInfo { ITCode = "testuser" }
            };
            var fp = new WtmFileProvider(wtm);
            var controller = new FileApiController { Wtm = wtm };

            var httpContext = new DefaultHttpContext();
            var payload = Encoding.UTF8.GetBytes("fake-image-bytes-851");
            var formFile = BuildFormFile(payload, "test851.jpg");
            httpContext.Request.Form = new FormCollection(
                new Dictionary<string, StringValues>(),
                new FormFileCollection { formFile });
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = controller.UploadImage(
                fp, width: null, height: null, sm: null, groupName: null, subdir: null, extra: null, csName: "wtmtest2");

            Assert.IsInstanceOfType(result, typeof(OkObjectResult),
                "#851: the no-resize fast path must still succeed once csName is forwarded to " +
                $"the correct parameter. Result was: {result?.GetType().Name}.");
            Assert.IsTrue(wtm.CreateDcCsKeys.Contains("wtmtest2"),
                "#851: UploadImage's no-resize fast path called Upload(fp, sm, groupName, csName) " +
                "POSITIONALLY -- Upload's own 4th parameter is `subdir`, not `csName`, so the " +
                "caller-supplied csName ('wtmtest2') landed in Upload's subdir slot while Upload's " +
                "own csName parameter silently fell back to null (the default connection). " +
                "Wtm.CreateDC(cskey:) must be called with the caller's ORIGINAL csName, not null -- " +
                $"otherwise the upload silently lands on the wrong connection. Recorded CreateDC " +
                $"cskey calls: [{string.Join(", ", wtm.CreateDcCsKeys.Select(k => k ?? "<null>"))}].");
        }

        /// <summary>
        /// Positive control in the same shape: the resize branch (<c>width</c>/<c>height</c> both
        /// non-null, so <c>UploadImage</c> never delegates to <c>Upload</c> at all) already called
        /// <c>Wtm.CreateDC(cskey: csName)</c> directly and was never affected by #851's bug --
        /// pinned here so a future refactor that broke THIS branch instead would not be masked by
        /// only ever asserting on the no-resize path above.
        /// </summary>
        [TestMethod]
        public void UploadImage_ResizeBranch_AlreadyForwardsCsNameToCreateDC()
        {
            var configs = new Configs();
            configs.Connections.Add(new CS { Key = "wtmtest2", Value = "unused-in-this-test" });
            var configMonitor = new Mock<IOptionsMonitor<Configs>>();
            configMonitor.Setup(x => x.CurrentValue).Returns(configs);

            using var dc = new Demo.DataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            var wtm = new RecordingCsKeyWtmContext(configMonitor.Object, dc)
            {
                LoginUserInfo = new LoginUserInfo { ITCode = "testuser" }
            };
            var fp = new WtmFileProvider(wtm);
            var controller = new FileApiController { Wtm = wtm };

            var httpContext = new DefaultHttpContext();
            // A minimal valid 1x1 PNG so Image.Load succeeds in the resize branch.
            var payload = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            var formFile = BuildFormFile(payload, "test851.png");
            httpContext.Request.Form = new FormCollection(
                new Dictionary<string, StringValues>(),
                new FormFileCollection { formFile });
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = controller.UploadImage(
                fp, width: 10, height: 10, sm: null, groupName: null, subdir: null, extra: null, csName: "wtmtest2");

            Assert.IsInstanceOfType(result, typeof(OkObjectResult),
                $"positive control: the resize branch must succeed. Result was: {result?.GetType().Name}.");
            Assert.IsTrue(wtm.CreateDcCsKeys.Contains("wtmtest2"),
                "positive control: the resize branch calls Wtm.CreateDC(cskey: csName) directly " +
                "and was never affected by #851 -- proving the negative assertion in the no-resize " +
                "test above is not vacuous (i.e. this harness can actually observe a correct " +
                $"forward). Recorded CreateDC cskey calls: [{string.Join(", ", wtm.CreateDcCsKeys.Select(k => k ?? "<null>"))}].");
        }
    }
}
