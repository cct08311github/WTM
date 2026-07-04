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

    [TestMethod]
    public void Process_NoCallback_ColorWithCloseScript_CannotBreakOut()
    {
        // System.Text.Json's default encoder Unicode-escapes '<' and '>', so a
        // color value containing "</script>" cannot terminate the surrounding
        // <script type="application/json"> block early.
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
        Assert.AreEqual("</script><script>alert(1)</script>", opts.GetProperty("color").GetString(),
            "Decoded color value must round-trip to the original string");
    }

    // ── Callback-bearing path: legacy inline <script> fallback preserved ─────

    [TestMethod]
    public void Process_WithChangeFunc_KeepsInlineScriptFallback()
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
        Assert.IsTrue(postHtml.Contains("layui.use('colorpicker'"), "Must still emit layui.use('colorpicker'");
        Assert.IsTrue(postHtml.Contains("colorpicker.render("), "Must still emit colorpicker.render(");
        StringAssert.Contains(postHtml, "myColorChanged", "Must reference the change callback");
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"),
            "Must not emit the JSON island when a callback is present");
    }

    [TestMethod]
    public void Process_WithChangeFunc_PredefinedColorsStillInInlineScript()
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
