#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice G (the #633 miss): TreeTagHelper's ItemUrl branch migrates
/// off the inline &lt;script&gt;ff.LoadComboItems('tree',...)&lt;/script&gt; call onto
/// the SAME eval-free wtm-dialog-init JSON island its four #633 siblings
/// (ComboBoxTagHelper/CheckBoxTagHelper/RadioTagHelper/TransferTagHelper)
/// already emit — riding the 'loadComboItems' DispatchAction case, which
/// already handled controlType 'tree' (ff.LoadComboItems's own tree branch),
/// just never had a server emitter wired up. Mirrors
/// LoadComboItemsIsland633Tests' conventions exactly.
/// </summary>
[TestClass]
public class TreeTagHelperLoadComboItemsIsland470Tests
{
    private sealed class DummyModel
    {
        public string? StringField { get; set; }
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
        => new("wt:tree", new TagHelperAttributeList(),
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

    private static TreeTagHelper CreateTreeHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new TreeTagHelper(monitor.Object);
    }

    [TestMethod]
    public void Tree_ItemUrl_EmitsJsonIslandNotInlineScript()
    {
        SetupLocalizer();
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "1");
        helper.Id = "tree_470_1";
        helper.ItemUrl = "/Home/GetTreeItems";
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"",
            "ItemUrl mode must emit the wtm-dialog-init JSON island");
        Assert.IsFalse(postHtml.Contains("ff.LoadComboItems("),
            "Must not emit the legacy inline ff.LoadComboItems(...) call text anywhere");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("loadComboItems", root.GetProperty("type").GetString());
        Assert.AreEqual("tree", root.GetProperty("controlType").GetString());
        Assert.AreEqual("/Home/GetTreeItems", root.GetProperty("url").GetString());
        Assert.AreEqual("tree_470_1", root.GetProperty("id").GetString());
        Assert.AreEqual("StringField", root.GetProperty("field").GetString());
        var selectVal = root.GetProperty("selectVal");
        Assert.AreEqual(JsonValueKind.Array, selectVal.ValueKind);
        Assert.AreEqual(1, selectVal.GetArrayLength());
        Assert.AreEqual("1", selectVal[0].GetString());
        // TreeTagHelper's legacy call never passed a 7th (disabled) arg — the
        // island must match: 'disabled' entirely absent.
        Assert.IsFalse(root.TryGetProperty("disabled", out _),
            "TreeTagHelper never sets Disabled on the DTO — must be omitted entirely");
    }

    [TestMethod]
    public void Tree_ItemUrl_StillEmitsUnconditionalXmSelectRenderScript()
    {
        // Red line: this slice must not touch the always-unconditional xmSelect
        // render script — zero behaviour change for it.
        SetupLocalizer();
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "1");
        helper.Id = "tree_470_2";
        helper.ItemUrl = "/Home/GetTreeItems";
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "xmSelect.render(",
            "The unconditional xmSelect render script must still be emitted inline, untouched by this slice");
    }

    [TestMethod]
    public void Tree_NoItemUrl_DoesNotEmitLoadComboItemsIsland()
    {
        SetupLocalizer();
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "1");
        helper.Id = "tree_470_3";
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("\"type\":\"loadComboItems\""),
            "Without ItemUrl, no loadComboItems island should be emitted");
    }

    [TestMethod]
    public void Tree_ItemUrlWithScriptBreakoutPayload_CannotEscapeIsland()
    {
        SetupLocalizer();
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "1");
        helper.Id = "tree_470_4";
        helper.ItemUrl = "/Home/Get?x=</script><script>alert(1)</script>";
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

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
