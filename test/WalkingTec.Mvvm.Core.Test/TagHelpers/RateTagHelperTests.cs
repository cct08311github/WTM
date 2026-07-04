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

        // Issue #584: Chinese identifiers are legal C# property names, and
        // Utils.GetIdByName only replaces '.', '[', ']', '-' — it does not
        // strip non-ASCII characters — so a DOM id derived from a field like
        // this can legitimately contain non-BasicLatin characters.
        public int 评分 { get; set; }
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

    // Issue #578: same ambient "formid" context item FormTagHelper publishes
    // for descendant tag helpers — simulates rendering inside a <wt:form>.
    private static TagHelperContext MakeContextInsideForm(string formId)
        => new("wt:rate", new TagHelperAttributeList(),
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

    // ── Issue #578: owning-form id plumbed into the island for the client-side
    //    write-back containment gate ────────────────────────────────────────

    [TestMethod]
    public void Process_InsideForm_JsonIslandContainsFormId()
    {
        SetupLocalizer();
        var helper = new RateTagHelper { Field = MakeField("IntField"), Id = "rate_formid1" };
        var output = MakeOutput();
        helper.Process(MakeContextInsideForm("wtForm_test1"), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("wtForm_test1", doc.RootElement.GetProperty("formId").GetString());
    }

    [TestMethod]
    public void Process_NotInsideForm_JsonIslandOmitsFormId()
    {
        // No ambient "formid" context item -> the field must be OMITTED
        // entirely (never emitted as null/empty), matching the DTO's
        // WhenWritingNull serializer option and the client's back-compat
        // no-containment-check behavior.
        SetupLocalizer();
        var helper = new RateTagHelper { Field = MakeField("IntField"), Id = "rate_noformid" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("formId", out _),
            "formId must be absent from the island entirely when there is no ambient owning form");
    }

    // ── Issue #584: island 'elem' selector must be the raw DOM id ──────────

    [TestMethod]
    public void Process_NonAsciiFieldId_IslandSelectorMatchesRawDomId()
    {
        SetupLocalizer();
        // Id mirrors exactly what BaseFieldTag's Id getter would derive for
        // this field via Utils.GetIdByName(ModelType.Name + "." + Field.Name)
        // — that helper only replaces '.', '[', ']', '-' and leaves non-ASCII
        // characters untouched, so a real Chinese-named field produces this
        // same shape of id in production.
        var helper = new RateTagHelper { Field = MakeField("评分"), Id = "DummyModel_评分" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        var domId = output.Attributes["id"].Value?.ToString();
        Assert.IsFalse(string.IsNullOrEmpty(domId), "DOM id must be set");
        Assert.IsTrue(domId!.Contains('评'),
            "Sanity check: the derived id must actually contain the non-ASCII character under test");

        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");

        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        var elemSelector = opts.GetProperty("elem").GetString();

        // Issue #584: the selector must be the RAW "#<id>". JSON transport
        // escaping (System.Text.Json, applied once during serialization) is
        // what makes this safe to embed — a manual JavaScriptEncoder
        // pre-escape double-escapes non-BasicLatin characters, so after
        // JSON.parse on the client the selector no longer matches the raw
        // DOM id set above, and layui.rate.render silently targets nothing.
        Assert.AreEqual("#" + domId, elemSelector,
            "Island 'elem' selector must exactly match the raw DOM id, or layui.rate.render silently renders nothing for non-ASCII field ids");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
