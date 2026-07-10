#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

// Issue #632 (redesigned — data as markup, not script): CheckBoxTagHelper's
// per-widget
//   {Id}defaultvalues = [...];
// inline <script> was replaced by TWO things:
//   1. A data-wtm-defaults="[...]" attribute on the rendered div — the
//      AUTHORITATIVE source ff.ChainChange now reads (see
//      framework_layui_632_fielddefaults_markup.test.js for the JS side).
//   2. A back-compat-only wtm-dialog-init JSON island ({"type":"fieldDefaults",
//      "id":"...","values":[...]}) that publishes window[id+'defaultvalues']
//      for app-authored JS that still reads it directly. The framework itself
//      never reads this island's output.
// The rejected design (island AS the authoritative source) is NOT what this
// locks in — these tests specifically assert the attribute is present
// (authoritative) alongside the island (back-compat), not instead of it.
//
// Issue #646 (Codex adversarial review, pre-10.14.4): the island-only
// back-compat above was INCOMPLETE. On a full page the island is only
// consumed at DOMContentLoaded, so app-authored JS reading
// window[id+'defaultvalues'] from an inline <script> immediately after this
// widget's markup saw `undefined` — a real regression vs. the pre-#632
// synchronous, parse-time publication #632 explicitly promised to keep "for
// back-compat". The fix restores the legacy inline
// <script>{Id}defaultvalues=[...];</script> write, unconditionally, in
// PostElement, alongside the attribute AND the island (see
// CheckBoxTagHelper.cs's Process() comment for the full rationale, including
// why the TagHelper can't conditionally omit it based on the client-only
// #627 kill-switch). These tests now assert all THREE are present — not any
// one instead of the others — and that the inline write is readable
// SYNCHRONOUSLY (see framework_layui_646_sync_defaultvalues.test.js for the
// JS-side parse-time proof).
[TestClass]
public class CheckBoxTagHelperTests
{
    private enum Role
    {
        Admin,
        User,
        Guest
    }

    private sealed class DummyModel
    {
        public List<Role>? Roles { get; set; }

        public bool Flag { get; set; }
    }

    private static void SetupLocalizer()
    {
        var localizerMock = new Mock<Microsoft.Extensions.Localization.IStringLocalizer>();
        localizerMock.Setup(x => x[It.IsAny<string>()])
            .Returns((string s) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        localizerMock.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
            .Returns((string s, object[] _) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        THProgram._localizer = localizerMock.Object;
        CoreProgram._localizer = localizerMock.Object;
    }

    private static ModelExpression MakeField(string propertyName, object? modelValue = null)
    {
        var provider = new EmptyModelMetadataProvider();
        // Deliberately uses the (containerType, propertyName) overload, NOT
        // the (PropertyInfo, containerType) overload most sibling *TagHelperTests
        // files use — that overload returns metadata whose ModelType is the
        // CONTAINER type (DummyModel), not the property's own type, for a
        // collection-typed property. CheckBoxTagHelper (and RadioTagHelper)
        // branch heavily on Field.Metadata.ModelType.IsList() /
        // .IsBoolOrNullableBool(), so this fixture needs the correct
        // property-level ModelType.
        var metadata = provider.GetMetadataForProperty(typeof(DummyModel), propertyName);
        var modelExplorer = new ModelExplorer(provider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext()
        => new("wt:checkbox", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

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

    // Issue #646: extracts the JSON array literal out of the restored
    // "{Id}defaultvalues = [...];" bare inline <script> write — mirrors
    // ExtractJsonFromIsland's shape for the island's JSON.
    private static string? ExtractJsonFromInlineDefaultsScript(string html, string id)
    {
        var marker = id + "defaultvalues = ";
        var start = html.IndexOf(marker, System.StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = html.IndexOf(';', start);
        if (end < 0) return null;
        return html[start..end];
    }

    [TestMethod]
    public void Process_EmitsDataWtmDefaultsAttribute_AsAuthoritativeSource()
    {
        SetupLocalizer();
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<Role> { Role.Admin, Role.User }),
            Id = "chk_test1"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-defaults"),
            "Must emit a data-wtm-defaults attribute on the rendered div");
        var attrValue = output.Attributes["data-wtm-defaults"].Value?.ToString();
        Assert.IsNotNull(attrValue);

        using var doc = System.Text.Json.JsonDocument.Parse(attrValue!);
        Assert.AreEqual(2, doc.RootElement.GetArrayLength());
        Assert.AreEqual("Admin", doc.RootElement[0].GetString());
        Assert.AreEqual("User", doc.RootElement[1].GetString());
    }

    [TestMethod]
    public void Process_StillEmitsFieldDefaultsIsland_ForBackCompat()
    {
        SetupLocalizer();
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<Role> { Role.Admin, Role.User }),
            Id = "chk_test2"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"",
            "Must still emit the wtm-dialog-init back-compat island");
        StringAssert.Contains(postHtml, "\"type\":\"fieldDefaults\"",
            "Island action type must be 'fieldDefaults'");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("chk_test2", doc.RootElement.GetProperty("id").GetString());
        var values = doc.RootElement.GetProperty("values");
        Assert.AreEqual(2, values.GetArrayLength());
        Assert.AreEqual("Admin", values[0].GetString());
        Assert.AreEqual("User", values[1].GetString());
    }

    [TestMethod]
    public void Process_EmitsLegacyInlineDefaultsScript_ForSynchronousBackCompat()
    {
        // Issue #646: the inline <script>{Id}defaultvalues=[...];</script> write
        // MUST be present, unconditionally, alongside the data-wtm-defaults
        // attribute and the fieldDefaults island — this is what makes
        // window[id+'defaultvalues'] readable SYNCHRONOUSLY, at HTML-parse
        // time, for app-authored JS that runs immediately after this widget's
        // markup. The island alone only publishes at DOMContentLoaded, which
        // was the #646 regression.
        SetupLocalizer();
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<Role> { Role.Admin }),
            Id = "chk_legacy"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-defaults"),
            "Attribute must still be present alongside the restored inline script");
        Assert.IsTrue(postHtml.Contains(helper.Id + "defaultvalues ="),
            "Must emit the restored legacy raw '{Id}defaultvalues =' bare inline script");
        // CRLF-tolerant: CheckBoxTagHelper.cs's raw string literal carries the
        // source file's own line endings verbatim into the rendered markup.
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(postHtml, "<script>\r?\n"),
            "Must emit a bare (non-application/json) <script> block for the legacy global");
        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"",
            "fieldDefaults island must still be emitted alongside the restored inline script");

        var json = ExtractJsonFromInlineDefaultsScript(postHtml, helper.Id);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual(1, doc.RootElement.GetArrayLength());
        Assert.AreEqual("Admin", doc.RootElement[0].GetString());
    }

    [TestMethod]
    public void Process_NoModelValue_FallsBackToDefaultValueSplit()
    {
        SetupLocalizer();
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles"),
            Id = "chk_defaults",
            DefaultValue = "Admin,Guest"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var attrValue = output.Attributes["data-wtm-defaults"].Value?.ToString();
        Assert.IsNotNull(attrValue);

        using var doc = System.Text.Json.JsonDocument.Parse(attrValue!);
        Assert.AreEqual(2, doc.RootElement.GetArrayLength());
        Assert.AreEqual("Admin", doc.RootElement[0].GetString());
        Assert.AreEqual("Guest", doc.RootElement[1].GetString());
    }

    [TestMethod]
    public void Process_WithItemUrl_StillEmitsAttributeAndIslandUnconditionally()
    {
        // CheckBoxTagHelper appends the ff.LoadComboItems island AND the
        // data-wtm-defaults attribute / restored inline defaultvalues script
        // (#646) / fieldDefaults island unconditionally, regardless of ItemUrl
        // — matching the legacy behavior where {Id}defaultvalues was always
        // emitted after the ItemUrl if/else, never inside it.
        SetupLocalizer();
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<Role> { Role.Admin }),
            Id = "chk_itemurl",
            ItemUrl = "/api/roles"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-defaults"),
            "ItemUrl branch must still emit the data-wtm-defaults attribute");
        StringAssert.Contains(postHtml, "\"type\":\"loadComboItems\"", "ItemUrl branch must still emit the combo-load island");
        StringAssert.Contains(postHtml, helper.Id + "defaultvalues =",
            "ItemUrl branch must still emit the restored inline defaultvalues script (#646)");
        StringAssert.Contains(postHtml, "\"type\":\"fieldDefaults\"", "fieldDefaults island must still be emitted alongside ItemUrl");
    }

    [TestMethod]
    public void Process_ValueWithHostileCharacters_CannotBreakOutOfAttributeIslandOrInlineScript()
    {
        // System.Text.Json's default encoder Unicode-escapes '<'/'>'/'&' inside
        // the JSON payload (safe for the <script type="application/json"> island
        // AND the restored bare <script>{Id}defaultvalues=...} write — #646),
        // and ASP.NET Core's TagHelperOutput.Attributes pipeline HTML-attribute-
        // encodes plain string attribute values (safe for data-wtm-defaults,
        // including a literal '"' which JSON array syntax itself introduces).
        SetupLocalizer();
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles"),
            Id = "chk_xss",
            DefaultValue = "</script><script>alert(1)</script>"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("</script><script>alert(1)"),
            "Raw </script><script> must not appear anywhere in the emitted markup");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        Assert.IsFalse(json!.Contains("</script>"),
            "Island JSON must Unicode-escape < and > to prevent script injection");

        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var values = doc.RootElement.GetProperty("values");
        Assert.AreEqual("</script><script>alert(1)</script>", values[0].GetString(),
            "Decoded island value must round-trip to the original string");

        // Issue #646: the restored inline "{Id}defaultvalues = [...];" write
        // must use the same default (HTML/JS-safe) JsonSerializer encoder — a
        // hostile value cannot break out of the bare <script> block either.
        var inlineJson = ExtractJsonFromInlineDefaultsScript(postHtml, helper.Id);
        Assert.IsNotNull(inlineJson);
        Assert.IsFalse(inlineJson!.Contains("</script>"),
            "Inline defaultvalues script JSON must Unicode-escape < and > to prevent script injection");
        using var inlineDoc = System.Text.Json.JsonDocument.Parse(inlineJson!);
        Assert.AreEqual("</script><script>alert(1)</script>", inlineDoc.RootElement[0].GetString(),
            "Decoded inline-script value must round-trip to the original string");

        // The attribute's raw TagHelperAttribute.Value (pre-render) is the same
        // JSON text; the actual HTML-attribute quote-escaping ('"' -> &quot;,
        // '<' already Unicode-escaped by the JSON encoder) happens when
        // TagHelperOutput is rendered by the framework, not by this TagHelper —
        // assert the value round-trips through JSON (i.e. is well-formed,
        // parseable JSON that a browser's JSON.parse can consume once the HTML
        // attribute decoder reverses the encoding).
        var attrValue = output.Attributes["data-wtm-defaults"].Value?.ToString();
        Assert.IsNotNull(attrValue);
        using var attrDoc = System.Text.Json.JsonDocument.Parse(attrValue!);
        Assert.AreEqual("</script><script>alert(1)</script>", attrDoc.RootElement[0].GetString());
    }

    [TestMethod]
    public void Process_DonotuseHiddenInputStillPresent()
    {
        // Regression: the markup migration must not disturb the pre-existing
        // _DONOTUSE_{Field.Name} hidden input emitted right before the island.
        SetupLocalizer();
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<Role> { Role.Admin }),
            Id = "chk_hidden"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "name=\"_DONOTUSE_Roles\"");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
