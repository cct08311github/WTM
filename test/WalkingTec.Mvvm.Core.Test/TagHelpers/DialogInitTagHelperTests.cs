using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 — DialogInitTagHelper unit tests.
///
/// Verifies:
///  1. The emitted &lt;script&gt; island has the correct type/class attributes.
///  2. The JSON payload contains an initForm action with the expected filter.
///  3. The JSON is script-safe: characters that could break a &lt;script&gt; block
///     are Unicode-escaped by System.Text.Json's default encoder.
///  4. Optional FormType and Dates properties round-trip correctly.
///  5. Null / empty optional properties are omitted from the JSON payload.
/// </summary>
[TestClass]
public class DialogInitTagHelperTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static string RenderTagHelper(DialogInitTagHelper helper)
    {
        var context = new TagHelperContext(
            tagName: "wt:dialog-init",
            allAttributes: new TagHelperAttributeList(),
            items: new Dictionary<object, object>(),
            uniqueId: "test-id");

        var output = new TagHelperOutput(
            tagName: "wt:dialog-init",
            attributes: new TagHelperAttributeList(),
            getChildContentAsync: (_, _) =>
                System.Threading.Tasks.Task.FromResult<TagHelperContent>(
                    new DefaultTagHelperContent()));

        helper.Process(context, output);

        using var writer = new StringWriter();
        output.WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }

    // ── TC-01: emitted element shape ─────────────────────────────────────────

    [TestMethod]
    public void Emits_ScriptElement_WithCorrectTypeAndClass()
    {
        // The island must be picked up by ff.OpenDialog's DOMParser query:
        // querySelector('script[type="application/json"].wtm-dialog-init')
        var helper = new DialogInitTagHelper { FormFilter = "myFilter" };
        var html = RenderTagHelper(helper);

        StringAssert.Contains(html, "type=\"application/json\"",
            "Island script must have type=application/json");
        StringAssert.Contains(html, "class=\"wtm-dialog-init\"",
            "Island script must have class=wtm-dialog-init");
    }

    [TestMethod]
    public void Emits_ClosingScriptTag()
    {
        var helper = new DialogInitTagHelper { FormFilter = "f" };
        var html = RenderTagHelper(helper);

        StringAssert.Contains(html, "</script>",
            "Island must have a closing </script> tag");
    }

    // ── TC-02: JSON payload structure ─────────────────────────────────────────

    [TestMethod]
    public void Payload_ContainsInitFormAction_WithCorrectFilter()
    {
        var helper = new DialogInitTagHelper { FormFilter = "orderForm" };
        var html = RenderTagHelper(helper);

        // Extract the JSON from the script element
        var json = ExtractJsonFromIsland(html);
        Assert.IsNotNull(json, "Island must contain parseable JSON");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.IsTrue(root.TryGetProperty("actions", out var actions),
            "Payload must have 'actions' array");
        Assert.AreEqual(1, actions.GetArrayLength(),
            "Payload must have exactly one action");

        var action = actions[0];
        Assert.AreEqual("initForm", action.GetProperty("type").GetString(),
            "Action type must be 'initForm'");
        Assert.AreEqual("orderForm", action.GetProperty("filter").GetString(),
            "Action filter must match FormFilter property");
    }

    [TestMethod]
    public void Payload_WithFormType_IncludesFormTypeField()
    {
        var helper = new DialogInitTagHelper { FormFilter = "f", FormType = "select" };
        var html = RenderTagHelper(helper);

        var json = ExtractJsonFromIsland(html);
        using var doc = JsonDocument.Parse(json!);
        var action = doc.RootElement.GetProperty("actions")[0];

        Assert.AreEqual("select", action.GetProperty("formType").GetString(),
            "Action formType must match FormType property");
    }

    [TestMethod]
    public void Payload_WithoutFormType_OmitsFormTypeField()
    {
        var helper = new DialogInitTagHelper { FormFilter = "f" };
        var html = RenderTagHelper(helper);

        var json = ExtractJsonFromIsland(html);
        using var doc = JsonDocument.Parse(json!);
        var action = doc.RootElement.GetProperty("actions")[0];

        Assert.IsFalse(action.TryGetProperty("formType", out _),
            "formType must be omitted when not set (WriteIfNotNull)");
    }

    [TestMethod]
    public void Payload_WithDates_IncludesDatesArray()
    {
        var helper = new DialogInitTagHelper
        {
            FormFilter = "f",
            Dates = new List<DialogDateConfig>
            {
                new DialogDateConfig { Elem = "#BirthDate", Type = "date", Format = "yyyy-MM-dd" }
            }
        };
        var html = RenderTagHelper(helper);

        var json = ExtractJsonFromIsland(html);
        using var doc = JsonDocument.Parse(json!);
        var action = doc.RootElement.GetProperty("actions")[0];

        Assert.IsTrue(action.TryGetProperty("dates", out var dates),
            "dates array must be present when Dates property is set");
        Assert.AreEqual(1, dates.GetArrayLength());

        var d = dates[0];
        Assert.AreEqual("#BirthDate", d.GetProperty("elem").GetString());
        Assert.AreEqual("date", d.GetProperty("type").GetString());
        Assert.AreEqual("yyyy-MM-dd", d.GetProperty("format").GetString());
    }

    [TestMethod]
    public void Payload_WithoutDates_OmitsDatesField()
    {
        var helper = new DialogInitTagHelper { FormFilter = "f" };
        var html = RenderTagHelper(helper);

        var json = ExtractJsonFromIsland(html);
        using var doc = JsonDocument.Parse(json!);
        var action = doc.RootElement.GetProperty("actions")[0];

        Assert.IsFalse(action.TryGetProperty("dates", out _),
            "dates must be omitted when Dates is null/empty");
    }

    // ── TC-02b: null/empty FormFilter omits filter key ───────────────────────

    [TestMethod]
    public void NoFormFilter_EmitsIslandWithoutFilterKey()
    {
        // Arrange: FormFilter is left at its default (empty string — not set by caller)
        var helper = new DialogInitTagHelper();
        // Do NOT set FormFilter; the TagHelper default is string.Empty

        // Act
        var html = RenderTagHelper(helper);

        // Assert: island is still emitted
        StringAssert.Contains(html, "type=\"application/json\"",
            "Island script must still be emitted even when FormFilter is not set");

        var json = ExtractJsonFromIsland(html);
        Assert.IsNotNull(json, "Island must contain parseable JSON");

        using var doc = JsonDocument.Parse(json!);
        var action = doc.RootElement.GetProperty("actions")[0];

        // type must still be present
        Assert.AreEqual("initForm", action.GetProperty("type").GetString(),
            "Action type must be 'initForm' even without a filter");

        // filter must be omitted (null, so JsonIgnoreCondition.WhenWritingNull drops it)
        Assert.IsFalse(action.TryGetProperty("filter", out _),
            "filter key must be omitted from JSON when FormFilter is null/empty");
    }

    // ── TC-03: script-injection safety ───────────────────────────────────────

    [TestMethod]
    public void Json_FilterWithCloseScript_IsUnicodeEscaped()
    {
        // System.Text.Json's default encoder (JavaScriptEncoder.Default) encodes
        // '<', '>' and '&' as <, >, & respectively.
        // This prevents a FormFilter value like "</script>" from breaking the
        // surrounding <script> block and creating a script-injection vector.
        var helper = new DialogInitTagHelper { FormFilter = "</script><script>alert(1)</script>" };
        var html = RenderTagHelper(helper);

        Assert.IsFalse(html.Contains("</script><script>"),
            "Raw </script> inside the filter must not appear in the island output");

        // The JSON content portion (between script tags) must not contain the raw payload
        var json = ExtractJsonFromIsland(html);
        Assert.IsNotNull(json);
        Assert.IsFalse(json.Contains("</script>"),
            "System.Text.Json must Unicode-escape < and > to prevent script injection");

        // Confirm it is still parseable (the escaping is valid JSON)
        using var doc = JsonDocument.Parse(json!);
        var action = doc.RootElement.GetProperty("actions")[0];
        // The decoded value must round-trip back to the original string
        Assert.AreEqual("</script><script>alert(1)</script>",
            action.GetProperty("filter").GetString(),
            "Decoded filter value must round-trip to the original string");
    }

    [TestMethod]
    public void Json_AmpersandInFilter_IsUnicodeEscaped()
    {
        var helper = new DialogInitTagHelper { FormFilter = "a&b" };
        var html = RenderTagHelper(helper);

        var json = ExtractJsonFromIsland(html);
        Assert.IsNotNull(json);
        // System.Text.Json default encoder produces & for &
        Assert.IsFalse(json.Contains("\"a&b\""),
            "Raw & must be Unicode-escaped in the JSON to prevent HTML injection");

        using var doc = JsonDocument.Parse(json!);
        var action = doc.RootElement.GetProperty("actions")[0];
        Assert.AreEqual("a&b", action.GetProperty("filter").GetString(),
            "Decoded filter with & must round-trip correctly");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string? ExtractJsonFromIsland(string html)
    {
        const string open = "\"wtm-dialog-init\">";
        const string close = "</script>";
        var start = html.IndexOf(open);
        if (start < 0) return null;
        start += open.Length;
        var end = html.IndexOf(close, start);
        if (end < 0) return null;
        return html[start..end];
    }
}
