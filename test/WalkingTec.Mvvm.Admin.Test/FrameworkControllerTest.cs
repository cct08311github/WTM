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

        // ─── SanitizeSelectorJson — #481 stored-XSS regression ───────────────

        /// <summary>
        /// #481: A field value containing &lt;/script&gt; must be unicode-escaped so that it
        /// cannot terminate the enclosing &lt;script&gt; element in Selector.cshtml.
        /// The paired-tag regex that was previously used only matched complete
        /// &lt;script&gt;…&lt;/script&gt; pairs; a bare &lt;/script&gt; survived unmodified.
        ///
        /// We test <see cref="_FrameworkController.SanitizeSelectorJson"/> directly — the helper
        /// is <c>public static</c> so no DB/HTTP plumbing is required. This is the right
        /// granularity: the helper is the exact escape boundary, and any future refactor of
        /// the Selector action that forgets to call it will still be caught by a failing test
        /// on the helper itself plus the absence of a call in the action.
        /// </summary>
        [TestMethod]
        public void SanitizeSelectorJson_ScriptBreakoutPayload_IsEscaped()
        {
            // Arrange: simulate the JSON GetDataJson() might return when a DB field contains
            // an XSS payload that terminates the page's script block.
            const string maliciousJson =
                @"[{""Name"":""</script><img src=x onerror=alert(1)>""}]";

            // Act
            string sanitized = _FrameworkController.SanitizeSelectorJson(maliciousJson);

            // Assert: the dangerous literal must not appear verbatim.
            Assert.IsFalse(sanitized.Contains("</script>"),
                "Raw </script> must not survive SanitizeSelectorJson — it would terminate the page script block");
            Assert.IsFalse(sanitized.Contains("<img"),
                "Raw < must not survive SanitizeSelectorJson — it enables HTML injection");

            // Verify the unicode-escape replacements applied by SanitizeSelectorJson.
            Assert.IsTrue(sanitized.Contains("\\u003c/script\\u003e"),
                "Expected \\u003c and \\u003e to replace < and > in </script>");

            // No raw < or > must remain anywhere.
            Assert.IsFalse(sanitized.Contains("<"),  "< must be unicode-escaped to \\u003c");
            Assert.IsFalse(sanitized.Contains(">"),  "> must be unicode-escaped to \\u003e");
        }

        /// <summary>
        /// Verifies that benign JSON (no HTML metacharacters) passes through unchanged.
        /// </summary>
        [TestMethod]
        public void SanitizeSelectorJson_BenignJson_PassesThroughUnchanged()
        {
            const string benign = @"[{""ID"":""1"",""Name"":""Alice""}]";
            string result = _FrameworkController.SanitizeSelectorJson(benign);
            Assert.AreEqual(benign, result,
                "SanitizeSelectorJson must not alter JSON that contains no HTML metacharacters");
        }

        /// <summary>
        /// Verifies that ampersands are unicode-escaped to prevent double-decode attacks and
        /// that & is replaced before &lt; / &gt; to avoid double-escaping.
        /// </summary>
        [TestMethod]
        public void SanitizeSelectorJson_Ampersand_IsEscaped()
        {
            const string withAmpersand = @"[{""Url"":""https://example.com/a&b=1""}]";
            string result = _FrameworkController.SanitizeSelectorJson(withAmpersand);
            Assert.IsFalse(result.Contains("\"&b"),
                "Raw & must be unicode-escaped; unescaped & enables double-decode attacks");
            Assert.IsTrue(result.Contains("\\u0026b"),
                "& should be replaced with \\u0026");
        }

        /// <summary>
        /// #481 hardening: UPPERCASE &lt;/SCRIPT&gt; must also be blocked.
        /// Some parsers normalise case before processing tag tokens; an uppercase variant
        /// can break out of the inline script block the same way the lowercase one does.
        /// Because <see cref="_FrameworkController.SanitizeSelectorJson"/> operates on the
        /// raw characters (not tag tokens), replacing &lt; and &gt; is inherently
        /// case-independent — this test proves it.
        /// </summary>
        [TestMethod]
        public void SanitizeSelectorJson_UppercaseScriptTag_IsEscaped()
        {
            const string uppercasePayload = @"[{""Name"":""</SCRIPT><img src=x onerror=alert(1)>""}]";

            string sanitized = _FrameworkController.SanitizeSelectorJson(uppercasePayload);

            // No raw < or > must remain anywhere.
            Assert.IsFalse(sanitized.Contains("<"),
                "< must be unicode-escaped regardless of surrounding tag casing");
            Assert.IsFalse(sanitized.Contains(">"),
                "> must be unicode-escaped regardless of surrounding tag casing");

            // The uppercase variant must be escaped the same way as the lowercase one.
            Assert.IsTrue(sanitized.Contains("\\u003c/SCRIPT\\u003e"),
                "Expected \\u003c and \\u003e to replace < and > in </SCRIPT>");
        }

        /// <summary>
        /// #481 hardening: U+2028 (LINE SEPARATOR) and U+2029 (PARAGRAPH SEPARATOR) inside
        /// a JSON string value that is embedded verbatim in an HTML &lt;script&gt; block cause
        /// an implicit newline in certain older JS parsers, breaking the string literal and
        /// potentially creating a parse error or execution boundary.  They must be escaped to
        /// \u2028 / \u2029 (the six-character JS unicode escape sequence).
        /// </summary>
        [TestMethod]
        public void SanitizeSelectorJson_LineSeparators_AreEscaped()
        {
            // Arrange: embed the actual code-points inside a JSON field value.
            string lineSep = "\u2028";
            string paraSep = "\u2029";
            string json = $"[{{\"Name\":\"before{lineSep}middle{paraSep}after\"}}]";

            // Act
            string sanitized = _FrameworkController.SanitizeSelectorJson(json);

            // Assert: neither raw code-point may remain.
            Assert.IsFalse(sanitized.Contains(lineSep),
                "U+2028 LINE SEPARATOR must be escaped to \\u2028 in the output");
            Assert.IsFalse(sanitized.Contains(paraSep),
                "U+2029 PARAGRAPH SEPARATOR must be escaped to \\u2029 in the output");

            // Verify the exact escape sequences.
            Assert.IsTrue(sanitized.Contains("\\u2028"),
                "U+2028 must be replaced with the literal six-character sequence \\u2028");
            Assert.IsTrue(sanitized.Contains("\\u2029"),
                "U+2029 must be replaced with the literal six-character sequence \\u2029");
        }

        // ─── GetSafeStreamContentType — #530 MIME-sniffing stored XSS fix ─────

        /// <summary>
        /// #530: <see cref="_FrameworkController.GetFile"/> streams uploaded files inline
        /// (stream=true) without ever setting Response.ContentType, so a browser would
        /// MIME-sniff an uploaded .html file and render it as active content in the app's
        /// origin (stored XSS) — any authenticated user can reach any file by GUID because
        /// default upload validation allows arbitrary extensions.
        ///
        /// <see cref="_FrameworkController.GetSafeStreamContentType"/> is the extracted,
        /// directly-testable boundary that decides the Content-Type for that response (the
        /// GetFile action itself cannot be unit-tested end-to-end: it calls
        /// <c>Wtm.CreateDC(cskey:)</c>, which requires full connection-string configuration
        /// not available under MockController — see known-quirks.md "TestFrameworkContext").
        /// This test proves an .html upload never gets served as text/html.
        /// </summary>
        [TestMethod]
        public void GetSafeStreamContentType_HtmlExtension_ReturnsOctetStreamNotHtml()
        {
            string result = _FrameworkController.GetSafeStreamContentType("html", "application/octet-stream");

            Assert.AreEqual("application/octet-stream", result,
                "An uploaded .html file must never be served with a renderable Content-Type");
            Assert.AreNotEqual("text/html", result,
                "text/html would let the browser render the uploaded file as active content (stored XSS)");
        }

        /// <summary>
        /// #530: image/svg+xml is renderable (can embed &lt;script&gt;) and must also be
        /// forced to application/octet-stream, even though it is a common inline-preview type.
        /// </summary>
        [TestMethod]
        public void GetSafeStreamContentType_SvgExtension_ReturnsOctetStream()
        {
            string result = _FrameworkController.GetSafeStreamContentType("svg", "image/svg+xml");

            Assert.AreEqual("application/octet-stream", result,
                "SVG can embed <script> and must not be served as image/svg+xml inline");
        }

        /// <summary>
        /// #530: unknown/unrecognised extensions fall through GetFile's contenttype
        /// computation as the default "application/octet-stream" already — this test locks
        /// in that the helper keeps it that way rather than accidentally widening the
        /// whitelist.
        /// </summary>
        [TestMethod]
        public void GetSafeStreamContentType_UnknownExtension_ReturnsOctetStream()
        {
            string result = _FrameworkController.GetSafeStreamContentType("xyz", "application/octet-stream");

            Assert.AreEqual("application/octet-stream", result);
        }

        /// <summary>
        /// #530: the whitelisted image extensions are still served with their native
        /// image/* content type — the fix must not regress normal inline image preview.
        /// </summary>
        [TestMethod]
        [DataRow("png", "image/png")]
        [DataRow("jpg", "image/jpg")]
        [DataRow("jpeg", "image/jpeg")]
        [DataRow("gif", "image/gif")]
        [DataRow("bmp", "image/bmp")]
        [DataRow("tif", "image/tif")]
        public void GetSafeStreamContentType_WhitelistedImageExtension_ReturnsNativeImageType(string ext, string computedContentType)
        {
            string result = _FrameworkController.GetSafeStreamContentType(ext, computedContentType);

            Assert.AreEqual(computedContentType, result,
                $"Whitelisted image extension '{ext}' must keep serving its native content type");
        }

        /// <summary>
        /// #530: extension matching must be case-insensitive, mirroring the ToLower() call
        /// GetFile already applies to file.FileExt before computing contenttype.
        /// </summary>
        [TestMethod]
        public void GetSafeStreamContentType_UppercaseImageExtension_StillMatchesWhitelist()
        {
            string result = _FrameworkController.GetSafeStreamContentType("PNG", "image/png");

            Assert.AreEqual("image/png", result,
                "Extension matching must be case-insensitive");
        }

        /// <summary>
        /// #530: null/empty extension (e.g. a file with no extension) must not slip through
        /// as a renderable type.
        /// </summary>
        [TestMethod]
        public void GetSafeStreamContentType_NullOrEmptyExtension_ReturnsOctetStream()
        {
            Assert.AreEqual("application/octet-stream", _FrameworkController.GetSafeStreamContentType(null, "application/octet-stream"));
            Assert.AreEqual("application/octet-stream", _FrameworkController.GetSafeStreamContentType("", "application/octet-stream"));
        }
    }
}
