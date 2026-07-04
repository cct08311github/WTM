using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// Regression tests for #563: the #530 MIME-sniffing hardening added to
    /// <see cref="_FrameworkController.GetFile"/> (Content-Type pinned via
    /// <see cref="_FrameworkController.GetSafeStreamContentType"/> + <c>X-Content-Type-Options:
    /// nosniff</c>) was never ported to the demo/_Admin scaffolding's
    /// <see cref="FileApiController.GetFile"/> — the app-owned controller generated into every
    /// `dotnet new wtm` project. That copy is <c>[Public]</c> (unauthenticated) and, before this
    /// fix, streamed the raw file bytes to <c>Response.Body</c> with no Content-Type header at
    /// all, letting the browser MIME-sniff an uploaded file (e.g. an .svg/.html upload) as active
    /// content in the app's origin — a stored XSS reachable by anyone who can guess/enumerate a
    /// file GUID.
    ///
    /// These tests exercise the real <see cref="FileApiController.GetFile"/> action end-to-end
    /// (via <see cref="WtmFileProvider"/> backed by an in-memory <see cref="Demo.DataContext"/>,
    /// which falls back to <c>WtmDataBaseFileHandler</c> since no file-handler assembly scan runs
    /// in the test process) and assert the actual <c>Content-Type</c> / <c>X-Content-Type-Options</c>
    /// response headers — not merely that <c>GetSafeStreamContentType</c> was invoked.
    ///
    /// <see cref="SingleConnectionWtmContext"/> overrides only <see cref="WTMContext.CreateDC"/>
    /// (virtual) to return the already-seeded in-memory <see cref="IDataContext"/> directly,
    /// bypassing the connection-string/tenant routing that requires full config not available
    /// under <c>MockController</c> (see known-quirks.md "TestFrameworkContext" — the same
    /// limitation the original #530 fix's tests called out as the reason GetFile itself could not
    /// be tested end-to-end). Everything else in the request path — the controller action, the
    /// real <see cref="WtmFileProvider"/>, and the real Content-Type/nosniff header writes — runs
    /// unmodified.
    /// </summary>
    [TestClass]
    public class FileApiControllerGetFileNosniffTests
    {
        private string _seed = null!;

        [TestInitialize]
        public void Setup()
        {
            _seed = Guid.NewGuid().ToString();
        }

        private sealed class SingleConnectionWtmContext : WTMContext
        {
            private readonly IDataContext _dc;

            public SingleConnectionWtmContext(IDataContext dc)
                : base(null, new GlobalData(), null, new DefaultUIService(), null, dc, null)
            {
                _dc = dc;
            }

            public override IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true) => _dc;
        }

        private static FileApiController CreateController(IDataContext dc, out MemoryStream responseBody)
        {
            var wtm = new SingleConnectionWtmContext(dc)
            {
                LoginUserInfo = new LoginUserInfo { ITCode = "testuser" }
            };
            var controller = new FileApiController { Wtm = wtm };
            var httpContext = new DefaultHttpContext();
            responseBody = new MemoryStream();
            httpContext.Response.Body = responseBody;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        /// <summary>
        /// An uploaded .svg is renderable (can embed &lt;script&gt;) — GetFile must never send
        /// it back as image/svg+xml or with no Content-Type at all, and must set nosniff so the
        /// browser cannot second-guess the declared type.
        /// </summary>
        [TestMethod]
        public async Task GetFile_SvgUpload_SetsOctetStreamAndNosniff()
        {
            using var dc = new Demo.DataContext(_seed, DBTypeEnum.Memory);
            var wtm = new SingleConnectionWtmContext(dc)
            {
                LoginUserInfo = new LoginUserInfo { ITCode = "testuser" }
            };
            var fp = new WtmFileProvider(wtm);

            var payload = Encoding.UTF8.GetBytes("<svg onload=\"alert(document.domain)\"></svg>");
            var uploaded = fp.Upload("evil.svg", payload.Length, new MemoryStream(payload), dc: dc);
            Assert.IsNotNull(uploaded, "seed upload must succeed");

            var controller = CreateController(dc, out var responseBody);
            controller.Wtm = wtm;

            var result = await controller.GetFile(fp, ((IWtmFile)uploaded!).GetID());

            Assert.IsInstanceOfType(result, typeof(EmptyResult),
                "the non-mp4 stream branch writes directly to Response.Body and returns EmptyResult");
            Assert.AreEqual("application/octet-stream", controller.Response.ContentType,
                "an .svg upload must never be served with a renderable Content-Type");
            Assert.AreNotEqual("image/svg+xml", controller.Response.ContentType,
                "image/svg+xml would let the browser render the uploaded file as active content (stored XSS)");
            Assert.AreEqual("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString(),
                "nosniff must pin the browser to the declared Content-Type");

            responseBody.Position = 0;
            var written = new byte[responseBody.Length];
            _ = responseBody.Read(written, 0, written.Length);
            CollectionAssert.AreEqual(payload, written, "the original file bytes must still be streamed unmodified");
        }

        /// <summary>
        /// An uploaded .html is renderable and must also be forced to application/octet-stream —
        /// same MIME-sniffing stored-XSS vector as the #530 fix, ported here to the demo
        /// scaffolding's [Public] endpoint.
        /// </summary>
        [TestMethod]
        public async Task GetFile_HtmlUpload_SetsOctetStreamAndNosniff()
        {
            using var dc = new Demo.DataContext(_seed, DBTypeEnum.Memory);
            var wtm = new SingleConnectionWtmContext(dc)
            {
                LoginUserInfo = new LoginUserInfo { ITCode = "testuser" }
            };
            var fp = new WtmFileProvider(wtm);

            var payload = Encoding.UTF8.GetBytes("<script>alert(document.domain)</script>");
            var uploaded = fp.Upload("evil.html", payload.Length, new MemoryStream(payload), dc: dc);
            Assert.IsNotNull(uploaded, "seed upload must succeed");

            var controller = CreateController(dc, out _);
            controller.Wtm = wtm;

            await controller.GetFile(fp, ((IWtmFile)uploaded!).GetID());

            Assert.AreEqual("application/octet-stream", controller.Response.ContentType);
            Assert.AreEqual("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        }

        /// <summary>
        /// Whitelisted image extensions must still be served with their native image/* type —
        /// the hardening must not regress normal inline image preview (e.g. avatar/thumbnail use
        /// via GetUserPhoto, which delegates straight to GetFile).
        /// </summary>
        [TestMethod]
        public async Task GetFile_PngUpload_KeepsNativeImageContentTypeWithNosniff()
        {
            using var dc = new Demo.DataContext(_seed, DBTypeEnum.Memory);
            var wtm = new SingleConnectionWtmContext(dc)
            {
                LoginUserInfo = new LoginUserInfo { ITCode = "testuser" }
            };
            var fp = new WtmFileProvider(wtm);

            // Minimal valid 1x1 PNG signature bytes are not required — GetFile's non-resize
            // branch never decodes the image, it only inspects the file extension.
            var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            var uploaded = fp.Upload("photo.png", payload.Length, new MemoryStream(payload), dc: dc);
            Assert.IsNotNull(uploaded, "seed upload must succeed");

            var controller = CreateController(dc, out _);
            controller.Wtm = wtm;

            await controller.GetFile(fp, ((IWtmFile)uploaded!).GetID());

            Assert.AreEqual("image/png", controller.Response.ContentType,
                "whitelisted image extensions must keep serving their native content type");
            Assert.AreEqual("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        }
    }
}
