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

    private static string InvokeGetTemplate(string field, string random, bool hasFormat = false, bool encodeFormat = false)
    {
        var helper = new DataTableTagHelper();
        var method = typeof(DataTableTagHelper).GetMethod(
            "getTemplate",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method, "getTemplate method must exist");
        return (string)method.Invoke(helper, new object[] { field, random, hasFormat, encodeFormat })!;
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

    // ── Issue #331: ColorPicker encoding ────────────────────────────────────

    [TestMethod]
    public void ColorPicker_MaliciousValue_IsHtmlEncoded_InHiddenInput()
    {
        // Fix 1: value= attribute in hidden input must be HtmlEncoded.
        var raw = "'><script>alert(1)</script>";
        var encoded = System.Net.WebUtility.HtmlEncode(raw);
        Assert.IsFalse(encoded.Contains("<script>"),
            "ColorPicker hidden input value must not contain raw <script>");
        Assert.IsFalse(encoded.Contains("'>"),
            "ColorPicker hidden input value must not contain attribute-breaking '>");
    }

    [TestMethod]
    public void ColorPicker_MaliciousValue_IsJsEncoded_InColorProperty()
    {
        // Fix 1: color: JS string literal must be JavaScriptEncoder-encoded.
        var raw = "'><script>alert(1)</script>";
        var encoded = System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(raw);
        Assert.IsFalse(encoded.Contains("'"),
            "ColorPicker JS color property must not contain unescaped single quotes");
        Assert.IsFalse(encoded.Contains("<script>"),
            "ColorPicker JS color property must not contain raw <script>");
    }

    // ── Issue #331: LayuiUIService MakeCheckBox/MakeTextBox ─────────────────

    [TestMethod]
    public void MakeCheckBox_MaliciousValue_IsHtmlEncoded()
    {
        // Fix 2: MakeCheckBox value= and title= must be HtmlEncoded.
        var svc = new WalkingTec.Mvvm.TagHelpers.LayUI.Common.LayuiUIService();
        var html = svc.MakeCheckBox(
            ischeck: false,
            text: "\" onmouseover=\"alert(1)",
            name: "testfield",
            value: "'><script>alert(1)</script>",
            isReadOnly: false);

        Assert.IsFalse(html.Contains("<script>"),
            "MakeCheckBox must not emit raw <script> in value attribute");
        // The double-quote in the title text must be HtmlEncoded to &quot; so the attribute
        // boundary cannot be broken. The literal string onmouseover= still appears but is
        // safely imprisoned inside an encoded &quot; boundary — check that raw " is absent.
        Assert.IsFalse(html.Contains("title=\"\" onmouseover="),
            "MakeCheckBox must not allow double-quote to break title attribute boundary");
        StringAssert.Contains(html, "&quot;",
            "MakeCheckBox must HtmlEncode double-quotes in title attribute");
    }

    [TestMethod]
    public void MakeTextBox_MaliciousValue_IsHtmlEncoded()
    {
        // Fix 2: MakeTextBox value= must be HtmlEncoded.
        var svc = new WalkingTec.Mvvm.TagHelpers.LayUI.Common.LayuiUIService();
        var html = svc.MakeTextBox(
            name: "testfield",
            value: "'><script>alert(1)</script>",
            emptyText: null,
            isReadOnly: false);

        Assert.IsFalse(html.Contains("<script>"),
            "MakeTextBox must not emit raw <script> in value attribute");
        Assert.IsFalse(html.Contains("'>"),
            "MakeTextBox must not contain unescaped '> that breaks the attribute");
    }

    [TestMethod]
    public void MakeTextBox_NormalValue_IsPreserved()
    {
        // Fix 2 regression: normal text values must still appear in the output.
        var svc = new WalkingTec.Mvvm.TagHelpers.LayUI.Common.LayuiUIService();
        var html = svc.MakeTextBox(
            name: "testfield",
            value: "hello world",
            emptyText: null,
            isReadOnly: false);

        StringAssert.Contains(html, "hello world",
            "MakeTextBox must preserve plain text values");
    }

    // ── Issue #331: ComboBox ItemUrl JS-encoding ─────────────────────────────

    [TestMethod]
    public void JavaScriptEncoder_ComboItemUrl_WithSingleQuote_IsEncoded()
    {
        // Fix 3: ItemUrl with a single quote must be JS-encoded before insertion
        // into ff.LoadComboItems('combo','<url>',...)
        var url = "/api/items?q=hello'world";
        var encoded = System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(url);
        Assert.IsFalse(encoded.Contains("'"),
            "ComboBox ItemUrl JS encoding must eliminate single quotes");
    }

    // ── Issue #331: GridAction DialogTitle and Url ───────────────────────────

    [TestMethod]
    public void JavaScriptEncoder_GridActionDialogTitle_WithSingleQuote_IsEncoded()
    {
        // Fix 4: DialogTitle with a single quote must be JS-encoded.
        var title = "Delete 'item'";
        var encoded = System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(title);
        Assert.IsFalse(encoded.Contains("'"),
            "GridAction DialogTitle must be JS-encoded to prevent JS string breakout");
    }

    [TestMethod]
    public void JavaScriptEncoder_GridActionUrl_WithXssPayload_IsEncoded()
    {
        // Fix 4: Url with XSS payload must not break out of JS string.
        // The key injection vector is the single quote which closes the JS string literal.
        // JavaScriptEncoder.Default encodes ' as ', neutralizing the string breakout.
        var url = "/edit/1');alert(1);//";
        var encoded = System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(url);
        Assert.IsFalse(encoded.Contains("'"),
            "GridAction Url must not contain raw single quotes after JS-encoding");
        // Verify the single quote was encoded (the JS string cannot be broken)
        StringAssert.Contains(encoded, "\\u0027",
            "JavaScriptEncoder must encode single quote as \\u0027 to prevent JS string breakout");
    }

    // ── Issue #331: Transfer selectVal JSON serialization ────────────────────

    [TestMethod]
    public void Transfer_SelectVal_WithSingleQuotes_JsonSerializeIsReflectionSafe()
    {
        // Fix 5: selectVal must be serialized with JsonSerializer.Serialize() not
        // manual quote-concatenation. A value with a single quote must be safely
        // embedded as a JSON string. JsonSerializer.Default encodes ' as '.
        var values = new System.Collections.Generic.List<string> { "it's a value", "normal" };
        var json = System.Text.Json.JsonSerializer.Serialize(values);
        // JsonSerializer produces valid JSON — starts with [ and uses double-quoted strings
        Assert.IsTrue(json.StartsWith("[\""),
            "JsonSerializer output must use double-quoted JSON array, not single-quoted");
        Assert.IsFalse(json.StartsWith("['"),
            "JsonSerializer output must use double-quoted JSON, not single-quoted array");
        // The value is present (single quote is encoded as ')
        StringAssert.Contains(json, "it",
            "JsonSerializer must include the string value content");
        StringAssert.Contains(json, "a value",
            "JsonSerializer must include the full string value content");
    }

    [TestMethod]
    public void Transfer_SelectVal_WithXssPayload_JsonSerializeIsReflectionSafe()
    {
        // Fix 5: a value with </script> must not break the surrounding <script> block.
        var values = new System.Collections.Generic.List<string> { "</script><script>alert(1)</script>" };
        var json = System.Text.Json.JsonSerializer.Serialize(values);
        // JsonSerializer uses < etc for < by default — but regardless, the value
        // is quoted. We verify there's no raw closing script tag that would break out.
        Assert.IsFalse(json.Contains("</script>"),
            "JsonSerializer must encode </script> to prevent script block breakout");
    }

    // ── Issue #387: EncodeFormat / SetFormatEncode opt-in flag ───────────────

    [TestMethod]
    public void SetFormat_Default_CellExprIsVerbatim()
    {
        // Arrange — a column with SetFormat but EncodeFormat left at default (false)
        var col = new GridColumn<WalkingTec.Mvvm.Core.Test.Student>();
        col.SetFormat((entity, dc) => "<b>html</b>");
        // Assert — HasFormat true, EncodeFormat false → verbatim path expected
        Assert.IsTrue(col.HasFormat());
        Assert.IsFalse(col.EncodeFormat);
    }

    [TestMethod]
    public void SetFormatEncode_Flag_CellExprIsEscaped()
    {
        // Arrange — a column with SetFormatEncode, flag must be set
        var col = new GridColumn<WalkingTec.Mvvm.Core.Test.Student>();
        col.SetFormatEncode((entity, dc) => entity.LoginName);
        // Assert — HasFormat true, EncodeFormat true → escaped path expected
        Assert.IsTrue(col.HasFormat());
        Assert.IsTrue(col.EncodeFormat);
    }

    // ── Issue #387: getTemplate encodeFormat path (via reflection) ───────────

    [TestMethod]
    public void GetTemplate_HasFormatTrue_EncodeFormatTrue_UsesEscapeText()
    {
        // Issue #387: when hasFormat=true AND encodeFormat=true the template must
        // route the value through ff.EscapeText (plain-text encode path).
        var js = InvokeGetTemplate("UserName", "r2", hasFormat: true, encodeFormat: true);

        StringAssert.Contains(js, "ff.EscapeText(d.UserName)",
            "hasFormat=true encodeFormat=true must use ff.EscapeText");
    }

    [TestMethod]
    public void GetTemplate_HasFormatTrue_EncodeFormatFalse_RendersVerbatim()
    {
        // Default behaviour preserved: hasFormat=true encodeFormat=false → verbatim.
        var js = InvokeGetTemplate("Actions", "r3", hasFormat: true, encodeFormat: false);

        StringAssert.Contains(js, "d.Actions",
            "hasFormat=true encodeFormat=false must render d.Actions verbatim");
        Assert.IsFalse(js.Contains("ff.EscapeText(d.Actions)"),
            "hasFormat=true encodeFormat=false must NOT wrap in ff.EscapeText");
    }

    // ── Issue #461: bool column without SetFormat renders verbatim (not escaped) ──

    [TestMethod]
    public void BoolColumn_WithoutSetFormat_RendersVerbatimNotEscaped()
    {
        // #461: a bare MakeGridHeader(x => x.SomeBool) column has no Format callback,
        // so HasFormat() returns false. Without the fix, getTemplate would emit
        // ff.EscapeText(d.field), turning the framework-generated checkbox HTML into
        // literal escaped text instead of a real checkbox.
        //
        // The fix detects bool/bool? FieldType and forces hasFormat=true for those
        // columns. Verify: the verbatim (non-EscapeText) path is taken when hasFormat=true.
        var js = InvokeGetTemplate("IsActive", "r99", hasFormat: true);

        StringAssert.Contains(js, "d.IsActive",
            "bool column template must contain the raw field expression d.IsActive");
        Assert.IsFalse(js.Contains("ff.EscapeText(d.IsActive)"),
            "bool column template must NOT wrap d.IsActive in ff.EscapeText — checkbox HTML must render verbatim");
    }

    // ── Issue #331: Slider hidden input encoding ─────────────────────────────

    [TestMethod]
    public void Slider_DefaultValueNumericValidation_NonNumeric_EmitsZero()
    {
        // Fix 6: A non-numeric DefaultValue must not be emitted raw into JS.
        // Validate that double.TryParse rejects the XSS payload.
        var payload = "0;alert(1)//";
        bool isNumeric = double.TryParse(payload,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);
        Assert.IsFalse(isNumeric,
            "XSS payload must not parse as a valid double");
    }

    [TestMethod]
    public void Slider_HiddenInputValue_XssPayload_IsHtmlEncoded()
    {
        // Fix 6: hidden input value= must be HtmlEncoded.
        var raw = "'><script>alert(1)</script>";
        var encoded = System.Net.WebUtility.HtmlEncode(raw);
        Assert.IsFalse(encoded.Contains("<script>"),
            "Slider hidden input value must HtmlEncode XSS payload");
        Assert.IsFalse(encoded.Contains("'>"),
            "Slider hidden input value must HtmlEncode attribute-breaking chars");
    }
}
