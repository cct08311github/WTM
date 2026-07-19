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

[TestClass]
public class RichTextBoxTagHelperTests
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

    // RichTextBoxTagHelper uses context.Items.ContainsKey("model") — safe with empty dict.
    private static TagHelperContext MakeContext()
        => new("wt:richtextbox", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("textarea", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    // Issue #470 Slice G: same island-JSON extraction helper as
    // ComboBoxTagHelperTests/LoadComboItemsIsland633Tests — the layedit
    // island is a bare (non-{actions:[...]}-wrapped) payload.
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

    // Issue #470 Slice G: RichTextBoxTagHelper's layedit render script now
    // migrates off the inline
    // &lt;script&gt;layui.use('layedit', function(){...})&lt;/script&gt; call onto an
    // eval-free 'layedit' wtm-dialog-init JSON island, replayed by the new
    // 'layedit' DispatchAction case (framework_layui.js). These tests replace
    // the pre-#470 assertions that matched the legacy inline script text.
    [TestMethod]
    public void Process_EmitsJsonIslandNotInlineScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new RichTextBoxTagHelper { Field = MakeField("RichField"), Id = "rich_field_1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"",
            "Must emit the wtm-dialog-init JSON island");
        Assert.IsFalse(postHtml.Contains("layedit.build("),
            "Must not emit the legacy inline layedit.build(...) call text anywhere");
        Assert.IsFalse(postHtml.Contains("layui.use('layedit'"),
            "Must not emit the legacy inline layui.use('layedit', ...) call text anywhere");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("layedit", root.GetProperty("type").GetString());
        Assert.AreEqual("rich_field_1", root.GetProperty("id").GetString());
        Assert.IsFalse(root.TryGetProperty("height", out _),
            "height must be omitted entirely when Height is not set");
    }

    [TestMethod]
    public void Process_HeightSet_IslandContainsHeight()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new RichTextBoxTagHelper
        {
            Field = MakeField("RichField"),
            Id = "rich_field_3",
            Height = 300
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual(300, doc.RootElement.GetProperty("height").GetInt32(),
            "Island must contain height:300 when Height is set to 300");
    }

    [TestMethod]
    public void Process_UploadUrl_IslandContainsUploadUrl()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new RichTextBoxTagHelper
        {
            Field = MakeField("RichField"),
            Id = "rich_field_upload",
            UploadUrl = "/Custom/Upload"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        StringAssert.Contains(doc.RootElement.GetProperty("uploadUrl").GetString()!, "/Custom/Upload");
    }

    [TestMethod]
    public void Process_IsrichAttributeIsSet()
    {
        SetupLocalizer();
        var helper = new RichTextBoxTagHelper { Field = MakeField("RichField"), Id = "rich_field_4" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        Assert.IsTrue(output.Attributes.ContainsName("isrich"),
            "isrich attribute must be set on output element");
        Assert.AreEqual("1", output.Attributes["isrich"].Value?.ToString());
    }

    [TestMethod]
    public void Process_ModelWinsOverDefaultValue()
    {
        SetupLocalizer();
        var helper = new RichTextBoxTagHelper
        {
            Field = MakeField("RichField", "saved content"),
            Id = "rich_field_5",
            DefaultValue = "default text"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var contentHtml = output.Content.GetContent();
        Assert.IsTrue(contentHtml.Contains("saved content"),
            "Non-empty model must win over DefaultValue");
        Assert.IsFalse(contentHtml.Contains("default text"),
            "DefaultValue must not appear when model is non-empty");
    }

    [TestMethod]
    public void Process_NullModel_FallsBackToDefaultValue()
    {
        SetupLocalizer();
        var helper = new RichTextBoxTagHelper
        {
            Field = MakeField("RichField", null),
            Id = "rich_field_6",
            DefaultValue = "placeholder"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var contentHtml = output.Content.GetContent();
        Assert.IsTrue(contentHtml.Contains("placeholder"),
            "Null model must fall back to DefaultValue");
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
        var helper = new RichTextBoxTagHelper
        {
            Field = MakeField("RichField"),
            Id = "rich_field_flagoff",
            Height = 300,
            UploadUrl = "/Custom/Upload"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"), "Flag OFF must never emit the JSON island");
        StringAssert.Contains(postHtml, "layui.use('layedit'",
            "Flag OFF must emit the legacy inline layui.use('layedit', ...) call");
        StringAssert.Contains(postHtml, "layedit.build('rich_field_flagoff'",
            "Flag OFF must emit the legacy inline layedit.build(...) call");
        StringAssert.Contains(postHtml, "/Custom/Upload");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }
}
