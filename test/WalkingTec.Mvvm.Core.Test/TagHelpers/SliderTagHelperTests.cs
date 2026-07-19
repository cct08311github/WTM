#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

[TestClass]
public class SliderTagHelperTests
{
    private sealed class DummyModel
    {
        public int IntField { get; set; }
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
        => new("wt:slider", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    // Issue #578: same ambient "formid" context item FormTagHelper publishes
    // for descendant tag helpers (already consumed by LinkButtonTagHelper /
    // SubmitButtonTagHelper / BaseButton) — simulates rendering inside a
    // <wt:form>.
    private static TagHelperContext MakeContextInsideForm(string formId)
        => new("wt:slider", new TagHelperAttributeList(),
               new Dictionary<object, object> { ["formid"] = formId }, "test-id");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    // Issue #552 (#470-E): extracts the JSON payload from a
    // <script type="application/json" class="wtm-dialog-init">...</script>
    // island, mirroring DateTimeTagHelperTests.ExtractJsonFromIsland (#556).
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
    public void Process_BasicRender_NoCallback_EmitsJsonIslandNotInlineScript()
    {
        // Issue #552 (#470-E): a callback-free slider migrates off the inline
        // <script> onto the eval-free JSON island.
        SetupLocalizer();
        var helper = new SliderTagHelper { Field = MakeField("IntField"), Id = "slider_test1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"",
            "Must emit the wtm-dialog-init JSON island");
        StringAssert.Contains(postHtml, "\"type\":\"slider\"",
            "Island action type must be 'slider'");
        Assert.IsFalse(postHtml.Contains("layui.use(['slider']"),
            "Must not emit the legacy inline layui.use(['slider'] call when there is no callback");
        Assert.IsFalse(postHtml.Contains("slider.render("),
            "Must not emit a raw slider.render( call — that lives in the island's JSON now");
    }

    [TestMethod]
    public void Process_HiddenInputContainsFieldName()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper { Field = MakeField("IntField"), Id = "slider_test3" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("IntField"),
            "PostElement must contain the field name in hidden input");
    }

    [TestMethod]
    public void Process_MinSet_NoCallback_JsonIslandContainsMin()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_min",
            Min = 5
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.AreEqual(5, opts.GetProperty("min").GetInt32());
    }

    [TestMethod]
    public void Process_MaxSet_NoCallback_JsonIslandContainsMax()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_max",
            Max = 95
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.AreEqual(95, opts.GetProperty("max").GetInt32());
    }

    [TestMethod]
    public void Process_RangeDefaultValue_NoCallback_JsonIslandContainsValueArray()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_range",
            DefaultValue = "[10,50]"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        var valueArr = opts.GetProperty("value");
        Assert.AreEqual(System.Text.Json.JsonValueKind.Array, valueArr.ValueKind);
        Assert.AreEqual(2, valueArr.GetArrayLength());
        Assert.AreEqual(10, valueArr[0].GetDouble());
        Assert.AreEqual(50, valueArr[1].GetDouble());
    }

    [TestMethod]
    public void Process_NoCallback_JsonIslandContainsExpectedStaticOpts()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_full",
            SliderType = SliderTypeEnum.Vertical,
            Min = 0,
            Max = 100,
            Step = 5,
            ShowStep = true,
            Tips = false,
            Theme = "#ff0000",
            SliderHeight = 300
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");

        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("slider", root.GetProperty("type").GetString());
        var opts = root.GetProperty("opts");
        Assert.AreEqual("#_sliderslider_full", opts.GetProperty("elem").GetString());
        Assert.AreEqual("vertical", opts.GetProperty("type").GetString());
        Assert.AreEqual(0, opts.GetProperty("min").GetInt32());
        Assert.AreEqual(100, opts.GetProperty("max").GetInt32());
        Assert.AreEqual(5, opts.GetProperty("step").GetInt32());
        Assert.IsTrue(opts.GetProperty("showstep").GetBoolean());
        Assert.IsFalse(opts.GetProperty("tips").GetBoolean());
        Assert.AreEqual("#ff0000", opts.GetProperty("theme").GetString());
        Assert.AreEqual(300, opts.GetProperty("height").GetInt32());

        // No callback options ever appear in the island.
        Assert.IsFalse(opts.TryGetProperty("change", out _));
        Assert.IsFalse(opts.TryGetProperty("setTips", out _));
    }

    [TestMethod]
    public void Process_RangeSlider_NoCallback_JsonIslandHasBothFieldIds()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Field1 = MakeField("IntField"),
            Id = "slider_range2"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.IsTrue(root.GetProperty("opts").GetProperty("range").GetBoolean());
        Assert.IsTrue(root.TryGetProperty("fieldId0", out var f0) && !string.IsNullOrEmpty(f0.GetString()));
        Assert.IsTrue(root.TryGetProperty("fieldId1", out var f1) && !string.IsNullOrEmpty(f1.GetString()));
        // The hidden inputs referenced by fieldId0/fieldId1 must actually carry those ids.
        StringAssert.Contains(postHtml, $"id='{f0.GetString()}'");
        StringAssert.Contains(postHtml, $"id='{f1.GetString()}'");
    }

    // Issue #552 adversarial-review fix (P0, pre-existing XSS): replaces the
    // old Process_NoCallback_ThemeWithCloseScript_CannotBreakOut test, which
    // asserted the WRONG boundary — that an XSS payload "round-tripped" into
    // opts.theme unchanged. JSON/JS-string escaping only protects the wire
    // format; layui.slider.render still splices the DECODED theme value,
    // unescaped, into an HTML string it builds internally
    // (style="border:2px solid '+theme+'"), so a value that survives
    // JSON-escaping can still carry a stored DOM-XSS payload. The real fix is
    // BaseFieldTag.IsSafeColorToken validating the decoded value itself and
    // OMITTING it entirely when unsafe — asserted below on both the island
    // and the legacy inline-<script> emission paths (the legacy path
    // previously had NO encoding at all on Theme, unlike ColorPicker's
    // JavaScriptEncoder-wrapped color).
    [TestMethod]
    public void Process_NoCallback_UnsafeThemeValue_IsOmittedFromIslandNotRoundTripped()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_xss",
            Theme = "</script><script>alert(1)</script>"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("</script><script>alert(1)"),
            "Raw </script><script> must not appear in the island output");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        Assert.IsFalse(json!.Contains("</script>"),
            "JSON must Unicode-escape < and > to prevent script injection");

        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.IsFalse(opts.TryGetProperty("theme", out _),
            "An unsafe theme value must be OMITTED from the island opts entirely — never merely escaped");
    }

    [TestMethod]
    public void Process_WithNonIdentifierChangeFunc_UnsafeThemeValue_IsOmittedFromLegacyScript()
    {
        // Same neutralization must hold on the legacy inline-<script> path
        // (non-identifier ChangeFunc, still forcing the legacy fallback post
        // Slice I) — the pre-existing vulnerability was reachable via BOTH
        // emission paths, and the legacy path previously emitted Theme with
        // NO escaping at all (,theme: '{Theme}').
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_xss_legacy",
            ChangeFunc = "obj.myChangeCallback",
            Theme = "</script><script>alert(1)</script>"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("alert(1)"),
            "Unsafe theme value must never reach the legacy inline <script> in any form");
        Assert.IsFalse(postHtml.Contains(",theme:"),
            "Unsafe theme value must be OMITTED from the legacy inline <script> entirely — the 'theme:' key must not be emitted");
    }

    [TestMethod]
    public void Process_NoCallback_LegitimateThemes_PassThroughIslandUnchanged()
    {
        // Compat red line: the allowlist grammar must accept every legitimate
        // stored color format — hex, rgba() functional notation, and CSS
        // named colors — unchanged.
        SetupLocalizer();
        foreach (var theme in new[] { "#1a2b3c", "rgba(0,0,0,0.5)", "rebeccapurple" })
        {
            var helper = new SliderTagHelper
            {
                Field = MakeField("IntField"),
                Id = "slider_legit_" + System.Math.Abs(theme.GetHashCode()),
                Theme = theme
            };
            var output = MakeOutput();
            helper.Process(MakeContext(), output);
            var postHtml = output.PostElement.GetContent();
            var json = ExtractJsonFromIsland(postHtml);
            Assert.IsNotNull(json, $"Island must be present for legitimate theme '{theme}'");
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var opts = doc.RootElement.GetProperty("opts");
            Assert.AreEqual(theme, opts.GetProperty("theme").GetString(),
                $"Legitimate theme '{theme}' must pass through the island unchanged");
        }
    }

    [TestMethod]
    public void Process_WithNonIdentifierChangeFunc_LegitimateThemes_PassThroughLegacyScriptUnchanged()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        foreach (var theme in new[] { "#1a2b3c", "rgba(0,0,0,0.5)", "rebeccapurple" })
        {
            var helper = new SliderTagHelper
            {
                Field = MakeField("IntField"),
                Id = "slider_legit_legacy_" + System.Math.Abs(theme.GetHashCode()),
                ChangeFunc = "obj.myChangeCallback",
                Theme = theme
            };
            var output = MakeOutput();
            helper.Process(MakeContext(), output);
            var postHtml = output.PostElement.GetContent();
            StringAssert.Contains(postHtml, $",theme: '{theme}'",
                $"Legitimate theme '{theme}' must pass through the legacy inline script unchanged");
        }
    }

    [TestMethod]
    public void Process_WithIdentifierChangeFunc_LegitimateThemes_PassThroughIslandUnchanged()
    {
        // Issue #470 Slice I: a plain-identifier ChangeFunc now migrates to the
        // island — Theme must still pass through unchanged there too.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        foreach (var theme in new[] { "#1a2b3c", "rgba(0,0,0,0.5)", "rebeccapurple" })
        {
            var helper = new SliderTagHelper
            {
                Field = MakeField("IntField"),
                Id = "slider_legit_island_" + System.Math.Abs(theme.GetHashCode()),
                ChangeFunc = "myChangeCallback",
                Theme = theme
            };
            var output = MakeOutput();
            helper.Process(MakeContext(), output);
            var postHtml = output.PostElement.GetContent();
            var json = ExtractJsonFromIsland(postHtml);
            Assert.IsNotNull(json, $"Island must be present for legitimate theme '{theme}'");
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var opts = doc.RootElement.GetProperty("opts");
            Assert.AreEqual(theme, opts.GetProperty("theme").GetString(),
                $"Legitimate theme '{theme}' must pass through the island unchanged");
        }
    }

    // ── Issue #578: owning-form id plumbed into the island for the client-side
    //    write-back containment gate ────────────────────────────────────────

    [TestMethod]
    public void Process_InsideForm_NoCallback_JsonIslandContainsFormId()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper { Field = MakeField("IntField"), Id = "slider_formid1" };
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
        // No ambient "formid" context item (e.g. rendered outside <wt:form>)
        // -> the field must be OMITTED entirely (never emitted as null/empty),
        // matching the DTO's WhenWritingNull serializer option and the
        // client's back-compat no-containment-check behavior.
        SetupLocalizer();
        var helper = new SliderTagHelper { Field = MakeField("IntField"), Id = "slider_noformid" };
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
    // A plain-identifier ChangeFunc/OnTipsFunc migrates to the eval-free JSON
    // island (superseding #552's blanket "any callback keeps the inline
    // script" rule); a non-identifier expression still keeps the legacy
    // inline <script> fallback, now with a deprecation console.warn.

    [TestMethod]
    public void Process_WithIdentifierChangeFunc_MigratesToJsonIsland()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_changefunc",
            ChangeFunc = "myChangeCallback"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"", "Must emit the JSON island");
        Assert.IsFalse(postHtml.Contains("layui.use(['slider']"),
            "Must not emit the legacy inline layui.use(['slider'] call");
        Assert.IsFalse(postHtml.Contains("slider.render("),
            "Must not emit a raw slider.render( call");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("myChangeCallback", doc.RootElement.GetProperty("changeFn").GetString());
        Assert.IsFalse(doc.RootElement.TryGetProperty("onTipsFn", out _));
    }

    [TestMethod]
    public void Process_WithIdentifierOnTipsFunc_MigratesToJsonIsland()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_tipsfunc",
            OnTipsFunc = "myTipsCallback"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"", "Must emit the JSON island");
        Assert.IsFalse(postHtml.Contains("slider.render("),
            "Must not emit a raw slider.render( call");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("myTipsCallback", doc.RootElement.GetProperty("onTipsFn").GetString());
        Assert.IsFalse(doc.RootElement.TryGetProperty("changeFn", out _));
    }

    [TestMethod]
    public void Process_WithBothIdentifierCallbacks_MigratesToJsonIslandWithBoth()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_bothcb",
            ChangeFunc = "c1",
            OnTipsFunc = "t1"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("c1", doc.RootElement.GetProperty("changeFn").GetString());
        Assert.AreEqual("t1", doc.RootElement.GetProperty("onTipsFn").GetString());
    }

    [TestMethod]
    public void Process_WithNonIdentifierChangeFunc_KeepsInlineScriptFallbackAndWarns()
    {
        // Issue #470 Slice I: a non-identifier expression (dotted/call-syntax)
        // can never be safely resolved by ff._resolveGuardedWindowFn, so it
        // keeps the exact legacy inline <script> fallback — never silently
        // dropping the developer's handler — but must surface a loud
        // deprecation console.warn.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_changefunc_dotted",
            ChangeFunc = "obj.myChangeCallback"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("layui.use(['slider']"), "Must still emit layui.use(['slider']");
        Assert.IsTrue(postHtml.Contains("slider.render("), "Must still emit slider.render(");
        StringAssert.Contains(postHtml, "obj.myChangeCallback", "Must reference the change callback");
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"),
            "Must not emit the JSON island for a non-identifier callback");
        StringAssert.Contains(postHtml, "console.warn(", "Must emit a deprecation console.warn");
        StringAssert.Contains(postHtml, "ChangeFunc", "Warning must name the offending attribute");
    }

    [TestMethod]
    public void Process_WithNonIdentifierOnTipsFunc_KeepsInlineScriptFallbackAndWarns()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_tipsfunc_callexpr",
            OnTipsFunc = "myTipsCallback()"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("slider.render("), "Must still emit slider.render(");
        StringAssert.Contains(postHtml, "myTipsCallback", "Must reference the setTips callback");
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"),
            "Must not emit the JSON island for a non-identifier callback");
        StringAssert.Contains(postHtml, "console.warn(", "Must emit a deprecation console.warn");
        StringAssert.Contains(postHtml, "OnTipsFunc", "Warning must name the offending attribute");
    }

    [TestMethod]
    public void Process_MixedIdentifierAndNonIdentifierCallbacks_WholeFieldFallsBackAndWarns()
    {
        // Issue #470 Slice I: the 3-way decision is PER-FIELD, not per-callback
        // — one non-identifier callback forces the ENTIRE field back to the
        // inline <script>, even though the other callback is a valid identifier.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_mixed",
            ChangeFunc = "validIdentifier",
            OnTipsFunc = "obj.notAnIdentifier"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"),
            "A single non-identifier callback must force the whole field back to inline");
        StringAssert.Contains(postHtml, "validIdentifier");
        StringAssert.Contains(postHtml, "obj.notAnIdentifier");
        StringAssert.Contains(postHtml, "console.warn(");
    }

    [TestMethod]
    public void Process_MinSet_WithNonIdentifierCallback_InlineScriptContainsMin()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_min_cb",
            Min = 5,
            ChangeFunc = "obj.cb"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains(",min:5"),
            "PostElement must contain ,min:5 when Min is set to 5 (legacy path)");
    }

    [TestMethod]
    public void Process_MinSet_WithIdentifierCallback_JsonIslandContainsMin()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_min_cb_island",
            Min = 5,
            ChangeFunc = "cb"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.AreEqual(5, opts.GetProperty("min").GetInt32());
    }

    // ── Pure logic tests (no DI / taghelper needed) ──────────────────────────

    [TestMethod]
    public void PureLogic_NonNumericDefaultValue_TryParseRejects()
    {
        var payload = "0;alert(1)//";
        bool isNumeric = double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        Assert.IsFalse(isNumeric, "XSS payload must not parse as a valid double");
    }

    [TestMethod]
    public void PureLogic_ValidNumericDefaultValue_TryParseAccepts()
    {
        var value = "42";
        bool isNumeric = double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        Assert.IsTrue(isNumeric, "Valid numeric string must parse as double");
    }

    [TestMethod]
    public void PureLogic_RangeBracketBothParts_BothParseAsDouble()
    {
        var inner = "10,20";
        var parts = inner.Split(',');
        bool part0Ok = double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        bool part1Ok = double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        Assert.IsTrue(part0Ok, "First range part '10' must parse as double");
        Assert.IsTrue(part1Ok, "Second range part '20' must parse as double");
    }

    [TestMethod]
    public void PureLogic_RangeBracketNonNumericPart_TryParseRejects()
    {
        var inner = "0,abc";
        var parts = inner.Split(',');
        bool part0Ok = double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        bool part1Ok = double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        Assert.IsTrue(part0Ok, "First part '0' must parse as double");
        Assert.IsFalse(part1Ok, "'abc' must not parse as double");
    }

    // ── Issue #753: flag-OFF byte-identical-to-base regression coverage ─────
    // WtmUIOptions is process-wide static state (BaseFieldTag.SetUIOptions) —
    // every test above that flips UseSelectIslandRender ON is paired with the
    // [TestCleanup] reset below, so a later test class in the same run never
    // inherits it.

    [TestMethod]
    public void Process_FlagOff_WithIdentifierChangeFunc_KeepsLegacyInlineScript_NoWarnNoIsland()
    {
        // Issue #753: Slice I shipped BEFORE UseSelectIslandRender existed and
        // migrated whenever ChangeFunc/OnTipsFunc were plain identifiers
        // regardless of the flag. With the flag OFF (default), even a fully-
        // migratable identifier callback must fall through to the exact
        // legacy inline <script> — byte-identical to base 947ecbc9 — with
        // ZERO warning text.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_flagoff_changefunc",
            ChangeFunc = "myChangeCallback"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"),
            "Flag OFF must never emit the JSON island, even for a migratable identifier callback");
        Assert.IsTrue(postHtml.Contains("slider.render("), "Flag OFF must emit the legacy slider.render(");
        Assert.IsFalse(postHtml.Contains("console.warn("),
            "Flag OFF must contribute ZERO warning characters — base never had this warning");
        StringAssert.Contains(postHtml, "myChangeCallback");
    }

    [TestMethod]
    public void Process_FlagOff_WithNonIdentifierChangeFunc_KeepsLegacyInlineScript_NoWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_flagoff_dotted",
            ChangeFunc = "obj.myChangeCallback"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"), "Flag OFF must never emit the JSON island");
        Assert.IsTrue(postHtml.Contains("slider.render("), "Flag OFF must emit the legacy slider.render(");
        Assert.IsFalse(postHtml.Contains("console.warn("),
            "Flag OFF must contribute ZERO warning characters — base never had this warning");
        StringAssert.Contains(postHtml, "obj.myChangeCallback");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }
}
