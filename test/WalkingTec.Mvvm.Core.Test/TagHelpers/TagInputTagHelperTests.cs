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

// Issue #571: TagInputTagHelper is reimplemented as a native, dependency-free
// tag/chip input. The previous implementation targeted a layui.tagInput
// module that has never shipped in any bundled layui tree (see
// test/manual/regression/README.md #14, verified during #566), so
// layui.use(['tagInput'], cb) never resolved and the widget silently
// rendered nothing. These tests lock in the new emitted markup: a hidden
// bound input + a wtm-dialog-init JSON island carrying only whitelisted
// plain-data opts (separator/placeholder/max/readonly/disabled) — no
// developer JS callback path ever existed on the old TagHelper (verified by
// reading the pre-#571 implementation), so — like RateTagHelper (#552) —
// this field unconditionally emits the island; there is no legacy-fallback
// inline <script> branch to preserve.
[TestClass]
public class TagInputTagHelperTests
{
    private sealed class DummyModel
    {
        public string? TagField { get; set; }
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
        => new("wt:taginput", new TagHelperAttributeList(),
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

    [TestMethod]
    public void Process_AlwaysEmitsJsonIslandNeverLegacyLayuiUse()
    {
        SetupLocalizer();
        var helper = new TagInputTagHelper { Field = MakeField("TagField"), Id = "tag_test1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"",
            "Must emit the wtm-dialog-init JSON island");
        StringAssert.Contains(postHtml, "\"type\":\"tagInput\"",
            "Island action type must be 'tagInput'");
        Assert.IsFalse(postHtml.Contains("layui.use(['tagInput']"),
            "Must not emit the legacy inline layui.use(['tagInput'] call — that module never shipped");
        Assert.IsFalse(postHtml.Contains("tagInput.render("),
            "Must not reference a layui tagInput.render( call anywhere");
    }

    [TestMethod]
    public void Process_HiddenInputEmitsBoundValueHtmlEncoded()
    {
        SetupLocalizer();
        var helper = new TagInputTagHelper
        {
            Field = MakeField("TagField", "red,green"),
            Id = "tag_test2"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "id=\"tag_test2_val\"",
            "Hidden input id must match the valueFieldId referenced by the island");
        StringAssert.Contains(postHtml, "value=\"red,green\"",
            "Hidden input must carry the current bound value");
    }

    [TestMethod]
    public void Process_HiddenInputValueIsHtmlEncoded_NotRawInjectable()
    {
        SetupLocalizer();
        var helper = new TagInputTagHelper
        {
            Field = MakeField("TagField", "\"><script>alert(1)</script>"),
            Id = "tag_xss_hidden"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("\"><script>alert(1)</script>"),
            "Raw unescaped payload must never appear in the hidden input's value attribute");
        StringAssert.Contains(postHtml, "&quot;&gt;&lt;script&gt;", "Value must be HTML-encoded");
    }

    [TestMethod]
    public void Process_JsonIslandContainsWhitelistedOptsOnly()
    {
        SetupLocalizer();
        var helper = new TagInputTagHelper
        {
            Field = MakeField("TagField"),
            Id = "tag_test3",
            Delimiter = ";",
            EmptyText = "Add a tag",
            Max = 5,
            ReadOnly = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");

        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("tagInput", root.GetProperty("type").GetString());
        var opts = root.GetProperty("opts");
        Assert.AreEqual("#tag_test3", opts.GetProperty("elem").GetString());
        Assert.AreEqual(";", opts.GetProperty("separator").GetString());
        Assert.AreEqual("Add a tag", opts.GetProperty("placeholder").GetString());
        Assert.AreEqual(5, opts.GetProperty("max").GetInt32());
        Assert.IsTrue(opts.GetProperty("readonly").GetBoolean());

        Assert.AreEqual("tag_test3_val", root.GetProperty("valueFieldId").GetString());

        // Opts must be a plain-data whitelist — no arbitrary/callback keys.
        var enumerated = new List<string>();
        foreach (var prop in opts.EnumerateObject()) { enumerated.Add(prop.Name); }
        CollectionAssert.AreEquivalent(
            new[] { "elem", "separator", "placeholder", "max", "readonly" },
            enumerated,
            "opts must contain exactly the whitelisted plain-data keys — nothing else");
    }

    [TestMethod]
    public void Process_DefaultsOmitOptionalOpts()
    {
        SetupLocalizer();
        var helper = new TagInputTagHelper { Field = MakeField("TagField"), Id = "tag_defaults" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.AreEqual(",", opts.GetProperty("separator").GetString(), "Separator must default to ','");
        Assert.IsFalse(opts.TryGetProperty("placeholder", out _), "placeholder must be omitted when EmptyText is not set");
        Assert.IsFalse(opts.TryGetProperty("max", out _), "max must be omitted when not set");
        Assert.IsFalse(opts.TryGetProperty("readonly", out _), "readonly must be omitted when false");
        Assert.IsFalse(opts.TryGetProperty("disabled", out _), "disabled must be omitted when false");
    }

    [TestMethod]
    public void Process_DisabledEmitsDisabledOpt()
    {
        SetupLocalizer();
        var helper = new TagInputTagHelper
        {
            Field = MakeField("TagField"),
            Id = "tag_disabled",
            Disabled = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.IsTrue(opts.GetProperty("disabled").GetBoolean());
    }

    [TestMethod]
    public void Process_EmptyTextWithScriptCloseTag_CannotBreakOutOfIsland()
    {
        // System.Text.Json's default encoder Unicode-escapes '<' and '>', so a
        // placeholder value containing "</script>" cannot terminate the
        // surrounding <script type="application/json"> block early.
        SetupLocalizer();
        var helper = new TagInputTagHelper
        {
            Field = MakeField("TagField"),
            Id = "tag_island_xss",
            EmptyText = "</script><script>alert(1)</script>"
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
        Assert.AreEqual("</script><script>alert(1)</script>", opts.GetProperty("placeholder").GetString(),
            "Decoded placeholder value must round-trip to the original string");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
