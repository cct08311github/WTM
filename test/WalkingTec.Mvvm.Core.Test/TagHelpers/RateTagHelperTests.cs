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
public class RateTagHelperTests
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
        => new("wt:rate", new TagHelperAttributeList(),
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

    // Issue #552 (#470-E): RateTagHelper has no developer-facing callback
    // attribute at all, so it always emits the JSON island — there is no
    // legacy-fallback branch (unlike Slider/DateTime/ColorPicker).

    [TestMethod]
    public void Process_AlwaysEmitsJsonIslandNeverInlineScript()
    {
        SetupLocalizer();
        var helper = new RateTagHelper { Field = MakeField("IntField"), Id = "rate_test1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"",
            "Must emit the wtm-dialog-init JSON island");
        StringAssert.Contains(postHtml, "\"type\":\"rate\"",
            "Island action type must be 'rate'");
        Assert.IsFalse(postHtml.Contains("layui.use(['rate']"),
            "Must not emit the legacy inline layui.use(['rate'] call");
        Assert.IsFalse(postHtml.Contains("rate.render("),
            "Must not emit a raw rate.render( call — that lives in the island's JSON now");
    }

    [TestMethod]
    public void Process_JsonIslandContainsExpectedOpts()
    {
        SetupLocalizer();
        var helper = new RateTagHelper
        {
            Field = MakeField("IntField", 3),
            Id = "rate_test2",
            Length = 7,
            Half = true,
            ReadOnly = true,
            Text = "Great"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");

        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("rate", root.GetProperty("type").GetString());
        var opts = root.GetProperty("opts");
        Assert.AreEqual("#rate_test2", opts.GetProperty("elem").GetString());
        Assert.AreEqual(3, opts.GetProperty("value").GetInt32());
        Assert.AreEqual(7, opts.GetProperty("length").GetInt32());
        Assert.IsTrue(opts.GetProperty("half").GetBoolean());
        Assert.IsTrue(opts.GetProperty("readonly").GetBoolean());
        Assert.AreEqual(1, opts.GetProperty("text").GetArrayLength());
        Assert.AreEqual("Great", opts.GetProperty("text")[0].GetString());

        Assert.AreEqual("rate_test2_val", root.GetProperty("valueFieldId").GetString());

        // No callback options ever appear in the island.
        Assert.IsFalse(opts.TryGetProperty("choose", out _));
    }

    [TestMethod]
    public void Process_DefaultLengthAndNoOptionalFlags_OmitsHalfReadonlyText()
    {
        SetupLocalizer();
        var helper = new RateTagHelper { Field = MakeField("IntField"), Id = "rate_defaults" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.AreEqual(5, opts.GetProperty("length").GetInt32(), "Length must default to 5");
        Assert.IsFalse(opts.TryGetProperty("half", out _), "half must be omitted when false");
        Assert.IsFalse(opts.TryGetProperty("readonly", out _), "readonly must be omitted when false");
        Assert.IsFalse(opts.TryGetProperty("text", out _), "text must be omitted when not set");
    }

    [TestMethod]
    public void Process_HiddenInputIdMatchesValueFieldId()
    {
        SetupLocalizer();
        var helper = new RateTagHelper { Field = MakeField("IntField"), Id = "rate_hidden" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "id=\"rate_hidden_val\"",
            "Hidden input id must match the valueFieldId referenced by the island");
    }

    [TestMethod]
    public void Process_TextWithCloseScript_CannotBreakOut()
    {
        // System.Text.Json's default encoder Unicode-escapes '<' and '>', so a
        // Text value containing "</script>" cannot terminate the surrounding
        // <script type="application/json"> block early.
        SetupLocalizer();
        var helper = new RateTagHelper
        {
            Field = MakeField("IntField"),
            Id = "rate_xss",
            Text = "</script><script>alert(1)</script>"
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
        Assert.AreEqual("</script><script>alert(1)</script>", opts.GetProperty("text")[0].GetString(),
            "Decoded text value must round-trip to the original string");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
