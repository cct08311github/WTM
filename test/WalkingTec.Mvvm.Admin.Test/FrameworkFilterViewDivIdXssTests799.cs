#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Mvc.Filters;
using WalkingTec.Mvvm.Test.Mock;
using DUWENINK.Captcha;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// Regression tests for Issue #799: <see cref="FrameworkFilter.OnResultExecuted"/>
    /// interpolated the unescaped <see cref="BaseVM.ViewDivId"/> straight into an inline
    /// <c>&lt;script&gt;</c> body it writes to the response.
    ///
    /// <para><b>Reachability chain established before writing this fix</b> (verified against
    /// the code, not assumed from the issue text):</para>
    /// <list type="number">
    /// <item><c>WTMContext.CreateVM</c> (<c>WTMContext.CreateVM.cs</c>) copies every key in
    /// <c>HttpContext.Request.Form</c>/<c>.Query</c> into the new VM's <c>FC</c> dictionary
    /// verbatim — including a caller-supplied <c>"ViewDivId"</c> form field, which nothing
    /// on this path validates or restricts.</item>
    /// <item><c>_FrameworkController.Selector</c> (an <c>[AllRights]</c> — authenticated,
    /// not per-page-privileged — POST endpoint) calls
    /// <c>BaseController.RedoUpdateModel(listVM)</c>, which iterates <c>FC.Keys</c> and
    /// writes each value onto the matching VM property via raw reflection
    /// (<c>PropertyHelper.SetPropertyValue</c>) — bypassing ASP.NET's own model-binder
    /// attributes (<c>ViewDivId</c> carries no <c>[BindNever]</c>; the attribute wouldn't
    /// have helped here anyway since this write never goes through the binder).</item>
    /// <item><c>Selector</c> then <c>return PartialView(listVM)</c>s the tainted VM.</item>
    /// <item><c>FrameworkFilter.OnResultExecuted</c> unwraps the <c>PartialViewResult</c>'s
    /// model and, pre-fix, wrote
    /// <c>$"&lt;script&gt;try{{ff.ResizeChart('{model?.ViewDivId}')}}catch{{}}&lt;/script&gt;"</c>
    /// with no encoding.</item>
    /// </list>
    /// <para>Neither of the two things that might otherwise have mitigated this apply:
    /// <c>grep -rn "ValidateAntiForgeryToken|AutoValidateAntiforgeryToken" src/</c> returns
    /// nothing (no antiforgery layer anywhere in this codebase), and
    /// <c>WtmCspOptions.ScriptSrc</c> defaults to <c>"'self' 'unsafe-inline'"</c>
    /// (<c>src/WalkingTec.Mvvm.Mvc/ConfigOptions/WtmCspOptions.cs</c>), so the shipped
    /// default CSP does not block an injected inline <c>&lt;script&gt;</c> either. So this is
    /// a genuine authenticated-victim XSS, not developer-authored Razor content.</para>
    ///
    /// <para>These tests drive that exact chain rather than calling
    /// <c>FrameworkFilter.OnResultExecuted</c> in isolation with a hand-set
    /// <c>model.ViewDivId</c>: a real <see cref="DefaultHttpContext"/>'s
    /// <c>Request.Form</c> carries the hostile <c>"ViewDivId"</c> field into
    /// <c>WTMContext.CreateVM</c> (via the <c>form</c> parameter added to
    /// <see cref="MockWtmContext.CreateWtmContext"/> for this issue),
    /// <c>_FrameworkController.Selector</c>'s own <c>RedoUpdateModel(listVM)</c> call
    /// reflects it onto the VM exactly as production does, and the resulting
    /// <see cref="PartialViewResult"/> is fed into a real <see cref="FrameworkFilter"/>
    /// instance's <c>OnResultExecuted</c> — the actual sink — so the assertions below are
    /// against the literal bytes the production filter wrote to the response body, not a
    /// proxy for them.</para>
    /// </summary>
    [TestClass]
    public class FrameworkFilterViewDivIdXssTests799
    {
        [TestInitialize]
        public void Init()
        {
            RbacExportListVM.Data = new List<ImportEndpointItem>();
        }

        // ─── Helpers ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a real <see cref="_FrameworkController"/> whose <see cref="WTMContext"/>
        /// is wired to a mock <c>Request.Form</c> containing <paramref name="viewDivIdFormValue"/>
        /// under the key <c>"ViewDivId"</c> — i.e. what an attacker's cross-site auto-submit
        /// POST to <c>/_Framework/Selector</c> would deliver.
        /// </summary>
        private static _FrameworkController CreateControllerWithHostileForm(string viewDivIdFormValue)
        {
            var form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["ViewDivId"] = viewDivIdFormValue,
            });

            var mockSecurityCode = new Mock<ISecurityCodeHelper>();
            var controller = new _FrameworkController(mockSecurityCode.Object);
            controller.Wtm = MockWtmContext.CreateWtmContext(null, "testuser", form);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        /// <summary>
        /// Runs a real <see cref="FrameworkFilter"/> instance's <c>OnResultExecuted</c> over
        /// <paramref name="result"/> (as returned by a real controller action) with the
        /// response body captured to an in-memory stream, and returns the bytes actually
        /// written to it. Mirrors the shape of the real ASP.NET Core pipeline invocation
        /// closely enough for this filter's needs: a real <see cref="ControllerActionDescriptor"/>
        /// (its <c>MethodInfo</c>/<c>ControllerTypeInfo</c> are read unconditionally near the
        /// top of <c>OnResultExecuted</c>) and a working (if empty) <c>RequestServices</c>
        /// provider, since the post-write log-append code resolves <c>ILogger&lt;T&gt;</c>
        /// instances from it.
        /// </summary>
        private static string RunOnResultExecutedAndCaptureBody(IBaseController controller, IActionResult result)
        {
            var httpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().BuildServiceProvider(),
            };
            var responseBody = new MemoryStream();
            httpContext.Response.Body = responseBody;

            var actionDescriptor = new ControllerActionDescriptor
            {
                ControllerName = "_Framework",
                ActionName = "Selector",
                ControllerTypeInfo = typeof(_FrameworkController).GetTypeInfo(),
                MethodInfo = typeof(_FrameworkController).GetMethod(nameof(_FrameworkController.Selector))!,
            };
            var actionContext = new ActionContext(httpContext, new RouteData(), actionDescriptor);
            var resultExecutedContext = new ResultExecutedContext(
                actionContext, new List<IFilterMetadata>(), result, controller);

            new FrameworkFilter().OnResultExecuted(resultExecutedContext);

            responseBody.Position = 0;
            using var reader = new StreamReader(responseBody, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        // ─── #799: hostile ViewDivId must not break out of the JS string literal ─────

        /// <summary>
        /// The live exploit chain from the issue: a <c>ViewDivId</c> crafted to close the
        /// single-quoted JS string literal and append an arbitrary statement. Pre-fix, the
        /// response body would have contained the literal bytes
        /// <c>ff.ResizeChart('x');alert(document.cookie);//')}catch{}&lt;/script&gt;</c> —
        /// i.e. the attacker's <c>alert(document.cookie)</c> call sitting outside any string
        /// literal, executable the instant the browser parses the &lt;script&gt; block.
        ///
        /// Asserts on the actually-escaped form being present (computed via the same
        /// <see cref="JavaScriptEncoder.Default"/> the fix uses — not a hand-copied escape
        /// sequence that could silently drift from the production encoder), not merely on the
        /// absence of the raw payload, and separately proves the raw breakout sequence is
        /// gone. Per this repo's "ask which line deletion turns this red" discipline: deleting
        /// the <c>JavaScriptEncoder.Default.Encode(...)</c> call in
        /// <c>FrameworkFilter.OnResultExecuted</c> (reverting to raw interpolation) is exactly
        /// what turns this assertion red — see the registered mutant,
        /// <c>test/mutants/entries/mvc799-viewdivid-xss-script-encode.json</c>.
        /// </summary>
        [TestMethod]
        public void Selector_HostileViewDivId_ScriptSinkEncodesPayload_NoRawBreakout()
        {
            const string hostilePayload = "x');alert(document.cookie);//";
            var controller = CreateControllerWithHostileForm(hostilePayload);

            var result = controller.Selector(
                typeof(RbacExportListVM).AssemblyQualifiedName!,
                _DONOT_USE_KFIELD: "Code",
                _DONOT_USE_VFIELD: "Code",
                _DONOT_USE_FIELD: "Code",
                _DONOT_USE_MULTI_SEL: false,
                _DONOT_USE_SEL_ID: null!,
                _DONOT_USE_SUBMIT: null!,
                _DONOT_USE_LINK_FIELD: null!,
                _DONOT_USE_TRIGGER_URL: null!,
                _DONOT_USE_CURRENTCS: null!);

            Assert.IsInstanceOfType(result, typeof(PartialViewResult),
                "Selector must have reached PartialView(listVM) for OnResultExecuted's sink to run at all.");
            var partialViewResult = (PartialViewResult)result;
            var model = partialViewResult.Model as BaseVM;
            Assert.IsNotNull(model, "The PartialViewResult's model must be the BaseVM RedoUpdateModel wrote into.");
            Assert.AreEqual(hostilePayload, model!.ViewDivId,
                "Sanity check: RedoUpdateModel must actually have reflected the posted Form's " +
                "\"ViewDivId\" onto the VM before OnResultExecuted ever runs — otherwise this test " +
                "would be exercising a value nobody could have supplied.");

            var body = RunOnResultExecutedAndCaptureBody(controller, result);

            var expectedEncoded = JavaScriptEncoder.Default.Encode(hostilePayload);
            StringAssert.Contains(body, $"ff.ResizeChart('{expectedEncoded}')",
                "The response must contain the ENCODED payload inside the JS string literal — " +
                "proving the fix's JavaScriptEncoder.Default.Encode call actually ran, not just " +
                "that the raw payload is somehow absent.");

            Assert.IsFalse(body.Contains("ResizeChart('x');alert"),
                "The raw, unescaped breakout sequence — closing the string literal and appending " +
                "an executable statement — must not appear in the response.");
            Assert.IsFalse(body.Contains(hostilePayload),
                "The raw payload — including its literal, unescaped closing-quote breakout " +
                "character — must not appear verbatim anywhere in the response. (Everything " +
                "after that quote in the payload is plain text with nothing else needing " +
                "escaping, so this is the one assertion that actually distinguishes 'encoded' " +
                "from 'not encoded' for this specific payload — a check for the payload's tail " +
                "substring alone would pass whether or not the fix is present, since that tail " +
                "legitimately reappears verbatim, unescaped, inside the correctly-escaped output too.)");
        }

        // ─── #799 positive control: a normal ViewDivId still renders, unchanged ──────

        /// <summary>
        /// Positive control for the fix above: an ordinary, non-hostile <c>ViewDivId</c> (the
        /// shape <see cref="BaseVM.ViewDivId"/>'s own getter generates by default —
        /// <c>"ViewDiv" + UniqueId</c>, alphanumeric) must render byte-identically before and
        /// after the fix. <see cref="JavaScriptEncoder.Default"/> only rewrites characters
        /// outside its safe set; a plain alphanumeric id contains none of them, so this proves
        /// the fix does not corrupt or double-encode the overwhelmingly common case — the
        /// CHANGELOG's "byte-identical for default/ordinary ViewDivId values" claim rests on
        /// this assertion, not on inspection alone.
        /// </summary>
        [TestMethod]
        public void Selector_NormalViewDivId_ScriptSinkRendersUnchanged()
        {
            const string normalViewDivId = "ViewDiv1234567890";
            var controller = CreateControllerWithHostileForm(normalViewDivId);

            var result = controller.Selector(
                typeof(RbacExportListVM).AssemblyQualifiedName!,
                _DONOT_USE_KFIELD: "Code",
                _DONOT_USE_VFIELD: "Code",
                _DONOT_USE_FIELD: "Code",
                _DONOT_USE_MULTI_SEL: false,
                _DONOT_USE_SEL_ID: null!,
                _DONOT_USE_SUBMIT: null!,
                _DONOT_USE_LINK_FIELD: null!,
                _DONOT_USE_TRIGGER_URL: null!,
                _DONOT_USE_CURRENTCS: null!);

            Assert.IsInstanceOfType(result, typeof(PartialViewResult));
            var model = ((PartialViewResult)result).Model as BaseVM;
            Assert.AreEqual(normalViewDivId, model!.ViewDivId,
                "Sanity check: the ordinary ViewDivId must have round-tripped through " +
                "RedoUpdateModel unchanged before the rendering assertion below means anything.");

            var body = RunOnResultExecutedAndCaptureBody(controller, result);

            StringAssert.Contains(body, $"ff.ResizeChart('{normalViewDivId}')",
                "A non-hostile, alphanumeric ViewDivId must render exactly as before the fix — " +
                "JavaScriptEncoder must not alter characters that never needed escaping.");
        }
    }
}
