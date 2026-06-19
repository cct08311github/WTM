using System;
using System.Collections.Generic;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;
using DUWENINK.Captcha;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// Regression tests for null-guard fixes in _FrameworkController
    /// (MVC-011 / MVC-012, Issue #379).
    /// </summary>
    [TestClass]
    public class FrameworkControllerTest
    {
        // ─── helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a <see cref="_FrameworkController"/> with a real WtmContext and an
        /// HttpContext whose Features collection can be configured per-test.
        /// </summary>
        private static (_FrameworkController controller, DefaultHttpContext httpContext)
            CreateController()
        {
            var mockSecurityCode = new Mock<ISecurityCodeHelper>();
            var controller = new _FrameworkController(mockSecurityCode.Object);
            controller.Wtm = MockWtmContext.CreateWtmContext();

            var httpContext = new DefaultHttpContext();

            // Wire up a minimal IServiceProvider so GetRequiredService<ILogger<ActionLog>>
            // succeeds when the Error() method runs past the early-return guard.
            var mockLogger = new Mock<ILogger<ActionLog>>();
            var mockServiceProvider = new Mock<IServiceProvider>();
            mockServiceProvider
                .Setup(sp => sp.GetService(typeof(ILogger<ActionLog>)))
                .Returns(mockLogger.Object);
            httpContext.RequestServices = mockServiceProvider.Object;

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            return (controller, httpContext);
        }

        // ─── Error() — null IExceptionHandlerPathFeature ──────────────────────

        /// <summary>
        /// When /error is requested directly (no exception in the pipeline) the
        /// IExceptionHandlerPathFeature is null. Error() must return 400 without
        /// throwing a NullReferenceException.
        /// </summary>
        [TestMethod]
        public void Error_WithNoExceptionFeature_Returns400()
        {
            var (controller, httpContext) = CreateController();

            // Deliberately do NOT register IExceptionHandlerPathFeature — it stays null.
            Assert.IsNull(httpContext.Features.Get<IExceptionHandlerPathFeature>(),
                "Precondition: feature must be absent to exercise the null guard");

            IActionResult result = controller.Error();

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "Error() should return 400 BadRequest when there is no exception context");
        }

        /// <summary>
        /// Ensures no NullReferenceException escapes when the feature is absent.
        /// This is the primary regression guard for MVC-012.
        /// </summary>
        [TestMethod]
        public void Error_WithNoExceptionFeature_DoesNotThrow()
        {
            var (controller, _) = CreateController();

            Exception thrown = null;
            try
            {
                controller.Error();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            Assert.IsNull(thrown,
                $"Error() must not throw when IExceptionHandlerPathFeature is null, but threw: {thrown}");
        }

        // ─── GetExportExcel — null listVM guard (MVC-013) ─────────────────────

        /// <summary>
        /// When an unresolvable VM name is passed, <see cref="_FrameworkController.GetExportExcel"/>
        /// must return 400 BadRequest — not throw a NullReferenceException.
        /// Regression test for MVC-013 (Issue #379 R2).
        /// </summary>
        [TestMethod]
        public void GetExportExcel_UnresolvableVmName_Returns400WithoutNre()
        {
            var (controller, httpContext) = CreateController();

            // GetExportExcel iterates Request.Form, which requires a Content-Type header.
            // Set it to application/x-www-form-urlencoded with an empty body so that
            // Request.Form returns an empty collection without throwing.
            httpContext.Request.QueryString = QueryString.Empty;
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.Body = System.IO.Stream.Null;

            IActionResult result = null;
            Exception thrown = null;
            try
            {
                result = controller.GetExportExcel("NoSuchVm__DoesNotExist", null);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            Assert.IsNull(thrown,
                $"GetExportExcel must not throw for an unresolvable VM name, but threw: {thrown}");
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "GetExportExcel should return 400 BadRequest for an unresolvable VM name");
        }
    }
}
