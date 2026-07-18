#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

[TestClass]
public class ColorPickerTagHelperTests
{
    private sealed class DummyModel
    {
        public string? ColorField { get; set; }
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
        var propertyInfo = typeof(DummyModel).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(DummyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext()
        => new("wt:colorpicker", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    // Issue #578: same ambient "formid" context item FormTagHelper publishes
    // for descendant tag helpers — simulates rendering inside a <wt:form>.
    private static TagHelperContext MakeContextInsideForm(string formId)
        => new("wt:colorpicker", new TagHelperAttributeList(),
               new Dictionary<object, object> { ["formid"] = formId }, "test-id");

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

    // ── Callback-free path: eval-free JSON island ────────────────────────────

    [TestMethod]
    public void Process_NoCallback_EmitsJsonIslandNotInlineScript()
    {
        SetupLocalizer();
        var helper = new ColorPickerTagHelper { Field = MakeField("ColorField"), Id = "cp_test1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"",
            "Must emit the wtm-dialog-init JSON island");
        StringAssert.Contains(postHtml, "\"type\":\"colorpicker\"",
            "Island action type must be 'colorpicker'");
        Assert.IsFalse(postHtml.Contains("layui.use('colorpicker'"),
            "Must not emit the legacy inline layui.use('colorpicker' call when there is no callback");
        Assert.IsFalse(postHtml.Contains("colorpicker.render("),
            "Must not emit a raw colorpicker.render( call — that lives in the island's JSON now");
    }

    [TestMethod]
    public void Process_NoCallback_JsonIslandContainsExpectedOpts()
    {
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField", "#123456"),
            Id = "cp_test2",
            EnableAlpha = true,
            PredefinedColors = "#fff,#000,#f00"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");

        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("colorpicker", root.GetProperty("type").GetString());
        var opts = root.GetProperty("opts");
        Assert.AreEqual("#cp_cp_test2", opts.GetProperty("elem").GetString());
        Assert.AreEqual("#123456", opts.GetProperty("color").GetString());
        Assert.IsTrue(opts.GetProperty("alpha").GetBoolean());
        Assert.AreEqual("rgb", opts.GetProperty("format").GetString());
        Assert.IsTrue(opts.GetProperty("predefine").GetBoolean());
        var colors = opts.GetProperty("colors");
        Assert.AreEqual(3, colors.GetArrayLength());
        Assert.AreEqual("#fff", colors[0].GetString());
        Assert.AreEqual("#000", colors[1].GetString());
        Assert.AreEqual("#f00", colors[2].GetString());

        Assert.AreEqual("cp_test2", root.GetProperty("valueFieldId").GetString());

        // No callback options ever appear in the island.
        Assert.IsFalse(opts.TryGetProperty("done", out _));
    }

    [TestMethod]
    public void Process_NoCallback_NoPredefinedColors_FormatIsHexAndPredefineFalse()
    {
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField", "#abcdef"),
            Id = "cp_test3"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.AreEqual("hex", opts.GetProperty("format").GetString());
        Assert.IsFalse(opts.GetProperty("predefine").GetBoolean());
        Assert.IsFalse(opts.TryGetProperty("colors", out _), "colors must be omitted when no PredefinedColors");
    }

    [TestMethod]
    public void Process_HiddenInputIdMatchesValueFieldId()
    {
        SetupLocalizer();
        var helper = new ColorPickerTagHelper { Field = MakeField("ColorField"), Id = "cp_hidden" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "id='cp_hidden'",
            "Hidden input id must match the valueFieldId referenced by the island");
    }

    // Issue #552 adversarial-review fix (P0, pre-existing XSS): replaces the
    // old Process_NoCallback_ColorWithCloseScript_CannotBreakOut test, which
    // asserted the WRONG boundary — that an XSS payload "round-tripped" into
    // opts.color unchanged. JSON/JS-string escaping only protects the wire
    // format; layui.colorpicker.render still splices the DECODED value,
    // unescaped, into an HTML string it builds internally, so a value that
    // survives JSON-escaping can still carry a stored DOM-XSS payload. The
    // real fix is BaseFieldTag.IsSafeColorToken validating the decoded value
    // itself and OMITTING it entirely when unsafe — asserted below on both
    // the island and the legacy inline-<script> emission paths.
    [TestMethod]
    public void Process_NoCallback_UnsafeColorValue_IsOmittedFromIslandNotRoundTripped()
    {
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField", "</script><script>alert(1)</script>"),
            Id = "cp_xss"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        Assert.IsFalse(json!.Contains("</script>"),
            "JSON must Unicode-escape < and > to prevent script injection");

        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.IsFalse(opts.TryGetProperty("color", out _),
            "An unsafe color value must be OMITTED from the island opts entirely — never merely escaped");
    }

    [TestMethod]
    public void Process_WithNonIdentifierChangeFunc_UnsafeColorValue_IsOmittedFromLegacyScript()
    {
        // Same neutralization must hold on the legacy inline-<script> path
        // (non-identifier ChangeFunc, still forcing the legacy fallback post
        // Slice I) — the pre-existing vulnerability was reachable via BOTH
        // emission paths.
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField", "</script><script>alert(1)</script>"),
            Id = "cp_xss_legacy",
            ChangeFunc = "obj.myColorChanged"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        // Scope the "no unescaped payload" check to the JS-executable <script>
        // block itself — the hidden <input>'s value attribute legitimately
        // contains the HTML-ENCODED (safe, non-executable) representation of
        // the same raw value, which would otherwise make this assertion
        // falsely fail on a harmless attribute-context encoding.
        var scriptStart = postHtml.IndexOf("<script>");
        Assert.IsTrue(scriptStart >= 0, "Legacy inline <script> must be present");
        var scriptContent = postHtml[scriptStart..];
        Assert.IsFalse(scriptContent.Contains("alert(1)"),
            "Unsafe color value must never reach the legacy inline <script> in any executable form");
        Assert.IsFalse(scriptContent.Contains(",color:"),
            "Unsafe color value must be OMITTED from the legacy inline <script> entirely — the 'color:' key must not be emitted");
    }

    [TestMethod]
    public void Process_NoCallback_LegitimateColors_PassThroughIslandUnchanged()
    {
        // Compat red line: the allowlist grammar must accept every legitimate
        // stored color format — hex, rgba() functional notation, and CSS
        // named colors — unchanged.
        SetupLocalizer();
        foreach (var color in new[] { "#1a2b3c", "rgba(0,0,0,0.5)", "rebeccapurple" })
        {
            var helper = new ColorPickerTagHelper
            {
                Field = MakeField("ColorField", color),
                Id = "cp_legit_" + System.Math.Abs(color.GetHashCode())
            };
            var output = MakeOutput();
            helper.Process(MakeContext(), output);
            var postHtml = output.PostElement.GetContent();
            var json = ExtractJsonFromIsland(postHtml);
            Assert.IsNotNull(json, $"Island must be present for legitimate color '{color}'");
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var opts = doc.RootElement.GetProperty("opts");
            Assert.AreEqual(color, opts.GetProperty("color").GetString(),
                $"Legitimate color '{color}' must pass through the island unchanged");
        }
    }

    [TestMethod]
    public void Process_WithNonIdentifierChangeFunc_LegitimateColors_PassThroughLegacyScriptUnchanged()
    {
        SetupLocalizer();
        foreach (var color in new[] { "#1a2b3c", "rgba(0,0,0,0.5)", "rebeccapurple" })
        {
            var helper = new ColorPickerTagHelper
            {
                Field = MakeField("ColorField", color),
                Id = "cp_legit_legacy_" + System.Math.Abs(color.GetHashCode()),
                ChangeFunc = "obj.myColorChanged"
            };
            var output = MakeOutput();
            helper.Process(MakeContext(), output);
            var postHtml = output.PostElement.GetContent();
            StringAssert.Contains(postHtml, $",color:'{color}'",
                $"Legitimate color '{color}' must pass through the legacy inline script unchanged");
        }
    }

    [TestMethod]
    public void Process_WithIdentifierChangeFunc_LegitimateColors_PassThroughIslandUnchanged()
    {
        // Issue #470 Slice I: a plain-identifier ChangeFunc now migrates to the
        // island — color must still pass through unchanged there too.
        SetupLocalizer();
        foreach (var color in new[] { "#1a2b3c", "rgba(0,0,0,0.5)", "rebeccapurple" })
        {
            var helper = new ColorPickerTagHelper
            {
                Field = MakeField("ColorField", color),
                Id = "cp_legit_island_" + System.Math.Abs(color.GetHashCode()),
                ChangeFunc = "myColorChanged"
            };
            var output = MakeOutput();
            helper.Process(MakeContext(), output);
            var postHtml = output.PostElement.GetContent();
            var json = ExtractJsonFromIsland(postHtml);
            Assert.IsNotNull(json, $"Island must be present for legitimate color '{color}'");
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var opts = doc.RootElement.GetProperty("opts");
            Assert.AreEqual(color, opts.GetProperty("color").GetString(),
                $"Legitimate color '{color}' must pass through the island unchanged");
        }
    }

    [TestMethod]
    public void Process_NoCallback_PredefinedColors_UnsafeTokenDroppedValidTokensKept()
    {
        // Each PredefinedColors token is validated independently — an unsafe
        // token must be dropped without poisoning the rest of the list.
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField"),
            Id = "cp_predef_mixed",
            PredefinedColors = "#fff,'});alert(1);({',#000"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        var colors = opts.GetProperty("colors").EnumerateArray().Select(c => c.GetString()).ToList();
        CollectionAssert.Contains(colors, "#fff");
        CollectionAssert.Contains(colors, "#000");
        Assert.IsFalse(colors.Any(v => v != null && v.Contains("alert")),
            "Unsafe predefined-color token must be dropped");
        Assert.AreEqual(2, colors.Count, "Only the two safe tokens should survive");
    }

    [TestMethod]
    public void Process_WithNonIdentifierChangeFunc_PredefinedColors_UnsafeTokenDroppedInLegacyScript()
    {
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField"),
            Id = "cp_predef_mixed_legacy",
            ChangeFunc = "obj.cb",
            PredefinedColors = "#fff,'});alert(1);({',#000"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("alert(1)"),
            "Unsafe predefined-color token must never reach the legacy inline script");
        StringAssert.Contains(postHtml, "'#fff'");
        StringAssert.Contains(postHtml, "'#000'");
    }

    // ── Issue #578: owning-form id plumbed into the island for the client-side
    //    write-back containment gate ────────────────────────────────────────

    [TestMethod]
    public void Process_InsideForm_NoCallback_JsonIslandContainsFormId()
    {
        SetupLocalizer();
        var helper = new ColorPickerTagHelper { Field = MakeField("ColorField"), Id = "cp_formid1" };
        var output = MakeOutput();
        helper.Process(MakeContextInsideForm("wtForm_test1"), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("wtForm_test1", doc.RootElement.GetProperty("formId").GetString());
    }

    [TestMethod]
    public void Process_NotInsideForm_NoCallback_JsonIslandOmitsFormId()
    {
        // No ambient "formid" context item -> the field must be OMITTED
        // entirely (never emitted as null/empty), matching the DTO's
        // WhenWritingNull serializer option and the client's back-compat
        // no-containment-check behavior.
        SetupLocalizer();
        var helper = new ColorPickerTagHelper { Field = MakeField("ColorField"), Id = "cp_noformid" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("formId", out _),
            "formId must be absent from the island entirely when there is no ambient owning form");
    }

    // ── Callback-bearing path: 3-way decision (Issue #470 Slice I) ───────────
    // A ChangeFunc whose bare name (FormatFuncName(ChangeFunc, false)) is a
    // plain identifier migrates to the eval-free JSON island (superseding
    // #552's blanket "any callback keeps the inline script" rule); a
    // non-identifier bare name still keeps the legacy inline <script>
    // fallback, now with a deprecation console.warn.

    [TestMethod]
    public void Process_WithIdentifierChangeFunc_MigratesToJsonIsland()
    {
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField"),
            Id = "cp_changefunc",
            ChangeFunc = "myColorChanged"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"", "Must emit the JSON island");
        Assert.IsFalse(postHtml.Contains("layui.use('colorpicker'"),
            "Must not emit the legacy inline layui.use('colorpicker' call");
        Assert.IsFalse(postHtml.Contains("colorpicker.render("),
            "Must not emit a raw colorpicker.render( call");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("myColorChanged", doc.RootElement.GetProperty("changeFn").GetString());
    }

    [TestMethod]
    public void Process_WithCallArgumentSyntaxChangeFunc_TruncatesToBareNameAndMigratesToJsonIsland()
    {
        // Issue #470 Slice I: the legacy path ALWAYS truncated ChangeFunc at
        // its first "(" via FormatFuncName(ChangeFunc, false) and invoked the
        // bare name with the framework's own "data" argument — so
        // "myColorChanged(ignored)" resolves to the SAME bare identifier
        // "myColorChanged" the plain-identifier case above does, and migrates
        // to the island too (dropping the caller's explicit arg text, exactly
        // as the legacy path already did).
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField"),
            Id = "cp_changefunc_callexpr",
            ChangeFunc = "myColorChanged(ignoredArg)"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("myColorChanged", doc.RootElement.GetProperty("changeFn").GetString());
    }

    [TestMethod]
    public void Process_WithIdentifierChangeFunc_PredefinedColorsStillInIsland()
    {
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField"),
            Id = "cp_changefunc2",
            ChangeFunc = "myColorChanged",
            PredefinedColors = "#fff,#000"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var colors = doc.RootElement.GetProperty("opts").GetProperty("colors")
            .EnumerateArray().Select(c => c.GetString()).ToList();
        CollectionAssert.Contains(colors, "#fff");
        CollectionAssert.Contains(colors, "#000");
    }

    [TestMethod]
    public void Process_WithNonIdentifierChangeFunc_KeepsInlineScriptFallbackAndWarns()
    {
        // Issue #470 Slice I: a non-identifier bare name (dotted/bracketed,
        // which never had a "(" to truncate) can never be safely resolved by
        // ff._resolveGuardedWindowFn, so it keeps the exact legacy inline
        // <script> fallback — never silently dropping the developer's
        // handler — but must surface a loud deprecation console.warn.
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField"),
            Id = "cp_changefunc_dotted",
            ChangeFunc = "obj.myColorChanged"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("layui.use('colorpicker'"), "Must still emit layui.use('colorpicker'");
        Assert.IsTrue(postHtml.Contains("colorpicker.render("), "Must still emit colorpicker.render(");
        StringAssert.Contains(postHtml, "obj.myColorChanged", "Must reference the change callback");
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"),
            "Must not emit the JSON island for a non-identifier callback");
        StringAssert.Contains(postHtml, "console.warn(", "Must emit a deprecation console.warn");
        StringAssert.Contains(postHtml, "ChangeFunc", "Warning must name the offending attribute");
    }

    [TestMethod]
    public void Process_WithNonIdentifierChangeFunc_PredefinedColorsStillInInlineScript()
    {
        SetupLocalizer();
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField"),
            Id = "cp_changefunc2_dotted",
            ChangeFunc = "obj.myColorChanged",
            PredefinedColors = "#fff,#000"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains(",colors: ['#fff','#000']"),
            "Legacy path must still emit the colors array exactly as before");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
