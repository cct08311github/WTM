#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice G: UEditorTagHelper's render script migrates off the
/// inline &lt;script&gt;layui.use(['ueditorconfig'], function(){...})&lt;/script&gt;
/// call onto an eval-free 'ueditor' wtm-dialog-init JSON island, replayed by
/// the new 'ueditor' DispatchAction case (framework_layui.js).
/// </summary>
[TestClass]
public class UEditorTagHelperTests
{
    private sealed class DummyModel
    {
        public string? RichField { get; set; }
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

    // UEditorTagHelper reads context.Items["model"] via the plain indexer
    // (unlike RichTextBoxTagHelper's ContainsKey-guarded read) — the "model"
    // key must be present (null is fine; it just falls back to the default
    // "UploadForLayUIUEditor" url), or the indexer throws KeyNotFoundException.
    private static TagHelperContext MakeContext()
        => new("wt:ueditor", new TagHelperAttributeList(),
               new Dictionary<object, object> { ["model"] = null! }, "test-id");

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
    public void Process_EmitsJsonIslandNotInlineScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new UEditorTagHelper { Field = MakeField("RichField", "Hello <b>World</b>"), Id = "ueditor_1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"",
            "Must emit the wtm-dialog-init JSON island");
        Assert.IsFalse(postHtml.Contains("layui.use(['ueditorconfig']"),
            "Must not emit the legacy inline layui.use(['ueditorconfig'], ...) call text anywhere");
        Assert.IsFalse(postHtml.Contains("loadEditor("),
            "The legacy loadEditor(...).ready(...) call text must not appear inline");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("ueditor", root.GetProperty("type").GetString());
        Assert.AreEqual("ueditor_1", root.GetProperty("id").GetString());
        Assert.AreEqual("Hello <b>World</b>", root.GetProperty("content").GetString(),
            "The decoded content value must round-trip exactly — only the WIRE encoding changes");
    }

    [TestMethod]
    public void Process_NullModel_FallsBackToDefaultValue()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new UEditorTagHelper
        {
            Field = MakeField("RichField", null),
            Id = "ueditor_2",
            DefaultValue = "placeholder text"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("placeholder text", doc.RootElement.GetProperty("content").GetString());
    }

    [TestMethod]
    public void Process_EmptyModelAndDefaultValue_ContentIsEmptyString()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new UEditorTagHelper { Field = MakeField("RichField", null), Id = "ueditor_3" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual(string.Empty, doc.RootElement.GetProperty("content").GetString());
    }

    [TestMethod]
    public void Process_IsrichAttributeIsSet()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new UEditorTagHelper { Field = MakeField("RichField"), Id = "ueditor_4" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        Assert.IsTrue(output.Attributes.ContainsName("isrich"));
        Assert.AreEqual("1", output.Attributes["isrich"].Value?.ToString());
    }

    [TestMethod]
    public void Process_ContentWithScriptBreakoutPayload_CannotEscapeIsland()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new UEditorTagHelper
        {
            Field = MakeField("RichField", "</script><script>alert(1)</script>"),
            Id = "ueditor_5"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("</script><script>alert(1)"),
            "Raw </script><script> must never appear — JSON escaping must neutralize it");
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        Assert.IsFalse(json!.Contains("</script>"),
            "JSON must Unicode-escape < and > to prevent script injection");
    }

    // ── Issue #753: flag-OFF byte-identical-to-base regression coverage ─────
    // WtmUIOptions is process-wide static state (BaseFieldTag.SetUIOptions) —
    // every test above that flips UseSelectIslandRender ON is paired with the
    // [TestCleanup] reset below, so a later test class in the same run never
    // inherits it.

    [TestMethod]
    public void Process_FlagOff_EmitsLegacyInlineScript_NoIsland()
    {
        // Issue #753: Slice G shipped BEFORE UseSelectIslandRender existed and
        // migrated UNCONDITIONALLY (no legacy fallback branch at all). With
        // the flag OFF (default), it must fall back to the exact pre-Slice-G
        // inline <script> — byte-identical to base 947ecbc9.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new UEditorTagHelper { Field = MakeField("RichField", "Hello <b>World</b>"), Id = "ueditor_flagoff" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"), "Flag OFF must never emit the JSON island");
        StringAssert.Contains(postHtml, "layui.use(['ueditorconfig']",
            "Flag OFF must emit the legacy inline layui.use(['ueditorconfig'], ...) call");
        StringAssert.Contains(postHtml, "loadEditor('ueditor_flagoff').ready(",
            "Flag OFF must emit the legacy loadEditor(...).ready(...) call");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }
}
