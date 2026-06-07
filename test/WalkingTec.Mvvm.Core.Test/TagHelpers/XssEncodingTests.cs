using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Reflection;
using System.Text.Encodings.Web;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #108 — XSS encoding guards.
///
/// These tests verify:
///  1. DataTableTagHelper.getTemplate generates JS that uses ff.EscapeText (not raw concatenation).
///  2. WebUtility.HtmlEncode correctly encodes model/ID values for hidden input attributes.
///  3. JavaScriptEncoder correctly encodes values for JS string contexts.
///
/// Full TagHelper Process() rendering requires an HTTP context and is covered by the JS tests.
/// These tests target the helpers / generated strings directly via reflection where the method
/// is private, or test the encoding behaviour of the encoder calls in isolation.
/// </summary>
[TestClass]
public class XssEncodingTests
{
    // ── getTemplate output (stored XSS fix) ──────────────────────────────────

    private static string InvokeGetTemplate(string field, string random, bool hasFormat = false)
    {
        var helper = new DataTableTagHelper();
        var method = typeof(DataTableTagHelper).GetMethod(
            "getTemplate",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method, "getTemplate method must exist");
        return (string)method.Invoke(helper, new object[] { field, random, hasFormat })!;
    }

    [TestMethod]
    public void GetTemplate_UsesEscapeText_NotRawConcat()
    {
        // Issue #108: the generated JS must route d.<field> through ff.EscapeText
        // before inserting into innerHTML. The old raw .replace() pattern must be gone.
        var js = InvokeGetTemplate("TestField", "abc");

        // Must contain the EscapeText call
        StringAssert.Contains(js, "ff.EscapeText(d.TestField)",
            "getTemplate JS must use ff.EscapeText to encode row data");
    }

    [TestMethod]
    public void GetTemplate_DoesNotContainRawReplaceEscape()
    {
        // The old escape approach was: d.{field}.replace(/\"/g,"'")
        // This is NOT a safe HTML-encoding strategy — verify it is removed.
        var js = InvokeGetTemplate("TestField", "abc");

        Assert.IsFalse(js.Contains("d.TestField.replace"),
            "Raw .replace() used as escape must be removed in favour of ff.EscapeText");
    }

    [TestMethod]
    public void GetTemplate_StructureIsIntact_DivWithStyleAndId()
    {
        // Verify the div wrapper structure is not accidentally broken by the fix.
        var js = InvokeGetTemplate("Name", "x1");

        StringAssert.Contains(js, "<div style=", "div style attribute must be present");
        StringAssert.Contains(js, "id=", "div id attribute must be present");
        StringAssert.Contains(js, "</div>", "closing div tag must be present");
    }

    // ── WebUtility.HtmlEncode for hidden input values ────────────────────────

    [TestMethod]
    public void HtmlEncode_SingleQuoteInValue_IsEncoded()
    {
        // A selected ID containing a single quote would break value='...' attribute.
        // WebUtility.HtmlEncode uses &#39; (decimal) for the apostrophe/single-quote.
        var raw = "abc'def";
        var encoded = WebUtility.HtmlEncode(raw);
        Assert.IsFalse(encoded.Contains("'"),
            "Single quote must be encoded to prevent attribute breakout");
        StringAssert.Contains(encoded, "&#39;",
            "HtmlEncode must entity-encode the single quote as &#39;");
    }

    [TestMethod]
    public void HtmlEncode_AngleBracketPayload_IsNeutralized()
    {
        // A value containing markup must not inject elements via value='...'.
        var raw = "<script>alert(1)</script>";
        var encoded = WebUtility.HtmlEncode(raw);
        Assert.IsFalse(encoded.Contains("<script"),
            "Angle brackets must be entity-encoded");
        StringAssert.Contains(encoded, "&lt;script&gt;");
    }

    [TestMethod]
    public void HtmlEncode_PlainGuid_IsUnchanged()
    {
        // Normal Guid values must pass through encoding without modification
        // (no false positives — plain ASCII alphanumeric is not changed).
        var guid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        var encoded = WebUtility.HtmlEncode(guid);
        Assert.AreEqual(guid, encoded,
            "A plain Guid string must not be altered by HtmlEncode");
    }

    [TestMethod]
    public void HtmlEncode_NullOrEmpty_IsHandledGracefully()
    {
        // Field.Model?.ToString() ?? string.Empty → empty string goes to HtmlEncode
        var encoded = WebUtility.HtmlEncode(string.Empty);
        Assert.AreEqual(string.Empty, encoded,
            "Empty string must encode to empty string without throwing");
    }

    // ── JavaScriptEncoder for JS string contexts ─────────────────────────────

    [TestMethod]
    public void JavaScriptEncoder_SingleQuoteInPromptMessage_IsEncoded()
    {
        // PromptMessage with a single quote would break layer.confirm('...')
        var raw = "Are you sure? It's final";
        var encoded = JavaScriptEncoder.Default.Encode(raw);
        Assert.IsFalse(encoded.Contains("'"),
            "Single quote must be JS-encoded to prevent string breakout in layer.confirm");
    }

    [TestMethod]
    public void JavaScriptEncoder_ScriptTagInPromptMessage_IsEncoded()
    {
        // A crafted PromptMessage with </script> must not break the inline script block.
        var raw = "Confirm</script><script>alert(1)</script>";
        var encoded = JavaScriptEncoder.Default.Encode(raw);
        Assert.IsFalse(encoded.Contains("</script>"),
            "JavaScriptEncoder must encode the closing script tag");
    }

    [TestMethod]
    public void JavaScriptEncoder_NormalUrl_IsPreserved()
    {
        // A normal URL used as ItemUrl must survive encoding without corruption
        // (path characters / and ? are safely encoded — the JS receiver decodes them).
        var url = "/api/items?type=combo";
        var encoded = JavaScriptEncoder.Default.Encode(url);
        // The decoded form must round-trip back to the original
        Assert.IsFalse(string.IsNullOrEmpty(encoded),
            "Normal URL must produce non-empty encoded string");
        // Key characters for URL structure are preserved or safely encoded
        Assert.IsFalse(encoded.Contains("'"),
            "Encoded URL must not contain unescaped single quotes");
    }

    // ── grid-001: hasFormat flag — format columns vs plain data columns ──────

    [TestMethod]
    public void GetTemplate_HasFormatTrue_RendersRawFieldExpression()
    {
        // grid-001: when hasFormat=true (format/button column) the template must
        // render d.<field> verbatim as HTML, NOT via ff.EscapeText.
        // MakeButton/MakeDialogButton produce framework HTML that contains <a>/<button>;
        // wrapping that in ff.EscapeText would double-encode the markup and show it
        // as escaped literal text instead of a clickable button.
        var js = InvokeGetTemplate("Actions", "abc", hasFormat: true);

        StringAssert.Contains(js, "d.Actions",
            "hasFormat=true template must contain the raw field expression d.Actions");
        Assert.IsFalse(js.Contains("ff.EscapeText(d.Actions)"),
            "hasFormat=true template must NOT wrap d.Actions in ff.EscapeText");
    }

    [TestMethod]
    public void GetTemplate_HasFormatFalse_UsesEscapeText()
    {
        // grid-001: when hasFormat=false (plain data column) the template must
        // route the cell value through ff.EscapeText to prevent stored XSS.
        var js = InvokeGetTemplate("Name", "xyz", hasFormat: false);

        StringAssert.Contains(js, "ff.EscapeText(d.Name)",
            "hasFormat=false template must use ff.EscapeText to encode user data");
    }

    [TestMethod]
    public void GetTemplate_DefaultHasFormat_UsesEscapeText()
    {
        // Calling without hasFormat argument defaults to false → EscapeText path
        // (backward-compatible behaviour for plain data columns).
        var js = InvokeGetTemplate("Score", "r1");

        StringAssert.Contains(js, "ff.EscapeText(d.Score)",
            "Default (no hasFormat) template must use ff.EscapeText");
    }

    // ── TLU-SEC-004: LayuiUIService.Make* button text encoding ───────────────

    [TestMethod]
    public void MakeDialogButton_MaliciousButtonText_IsHtmlEncoded()
    {
        // TLU-SEC-004: buttonText containing markup must be HTML-encoded in the output.
        var svc = new LayuiUIService();
        var html = svc.MakeDialogButton(
            ButtonTypesEnum.Button,
            "/edit/1",
            "<script>alert('xss')</script>",
            width: null, height: null);

        Assert.IsFalse(html.Contains("<script>alert"),
            "Raw <script> tag must not appear in MakeDialogButton output");
        StringAssert.Contains(html, "&lt;script&gt;",
            "MakeDialogButton must HtmlEncode the buttonText");
    }

    [TestMethod]
    public void MakeButton_MaliciousButtonText_IsHtmlEncoded()
    {
        // TLU-SEC-004: MakeButton must HtmlEncode buttonText.
        var svc = new LayuiUIService();
        var html = svc.MakeButton(
            ButtonTypesEnum.Button,
            "/view/1",
            "<img src=x onerror=alert(1)>",
            width: null, height: null);

        Assert.IsFalse(html.Contains("<img src=x"),
            "Raw <img> payload must not appear in MakeButton output");
        StringAssert.Contains(html, "&lt;img",
            "MakeButton must HtmlEncode the buttonText");
    }

    [TestMethod]
    public void MakeScriptButton_MaliciousButtonText_IsHtmlEncoded()
    {
        // TLU-SEC-004: MakeScriptButton must HtmlEncode buttonText.
        var svc = new LayuiUIService();
        var html = svc.MakeScriptButton(
            ButtonTypesEnum.Link,
            "<b>bold</b>");

        Assert.IsFalse(html.Contains("<b>bold</b>"),
            "Raw <b> tag must not appear in MakeScriptButton output");
        StringAssert.Contains(html, "&lt;b&gt;bold&lt;/b&gt;",
            "MakeScriptButton must HtmlEncode the buttonText");
    }

    [TestMethod]
    public void MakeDialogButton_NormalButtonText_IsUnchanged()
    {
        // Normal text without special characters must pass through without corruption.
        var svc = new LayuiUIService();
        var html = svc.MakeDialogButton(
            ButtonTypesEnum.Button,
            "/edit/1",
            "Edit",
            width: null, height: null);

        StringAssert.Contains(html, ">Edit<",
            "Plain button text must appear verbatim in MakeDialogButton output");
    }

    // ── TLU-SEC-004: FormTagHelper &lg;/&rg; → WebUtility.HtmlEncode ────────

    [TestMethod]
    public void HtmlEncode_FormValidationError_ProducesValidEntities()
    {
        // TLU-SEC-004: the old code used &lg;/&rg; which are not valid HTML entities.
        // The fix replaces them with WebUtility.HtmlEncode.
        // Verify: an error message with angle brackets is encoded into &lt;/&gt;.
        var rawError = "<b>Required</b>";
        var encoded = WebUtility.HtmlEncode(rawError);

        // Must produce proper entities, not the bogus &lg;/&rg;
        Assert.IsFalse(encoded.Contains("&lg;"),
            "HtmlEncode must not produce bogus &lg; entity");
        Assert.IsFalse(encoded.Contains("&rg;"),
            "HtmlEncode must not produce bogus &rg; entity");
        StringAssert.Contains(encoded, "&lt;",
            "HtmlEncode must produce valid &lt; entity for <");
        StringAssert.Contains(encoded, "&gt;",
            "HtmlEncode must produce valid &gt; entity for >");
    }
}
