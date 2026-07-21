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

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice N1: opt-in (WtmUIOptions.UseSelectIslandRender, default
/// OFF — the SAME flag #470 Slices J/K/L/M use) eval-free 'renderTreeContainer'
/// JSON island for &lt;wt:treecontainer&gt;. Mirrors
/// RenderTransferIsland470SliceKTests conventions exactly — same helpers, same
/// island-extraction pattern.
///
/// IMPORTANT: WtmUIOptions is process-wide static state
/// (BaseFieldTag.SetUIOptions). Every test that flips UseSelectIslandRender ON
/// must be paired with the [TestCleanup] reset below.
/// </summary>
[TestClass]
public class RenderTreeContainerIsland470SliceN1Tests
{
    private sealed class DummyModel
    {
        public List<TreeSelectListItem>? Items { get; set; }
        public string? IdField { get; set; }
        public int? LevelField { get; set; }
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

    private static ModelExpression MakeField(string propertyName, object? modelValue)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(DummyModel).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(DummyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static ModelExpression MakeItemsField(List<TreeSelectListItem> items)
        => MakeField("Items", items);

    private static TagHelperContext MakeContext()
        => new("wt:treecontainer", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    // Default: empty child content — mirrors "no nested grid/button" scenario.
    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    // Caller-supplied raw child HTML — simulates a nested grid/search-button
    // markup, mirroring SelectorTagHelperDialogInitIsland635Tests'
    // MakeOutputWithChildContent convention.
    private static TagHelperOutput MakeOutputWithChildContent(string rawChildHtml)
        => new("div", new TagHelperAttributeList(),
               (_, __) =>
               {
                   var content = new DefaultTagHelperContent();
                   content.SetHtmlContent(rawChildHtml);
                   return Task.FromResult<TagHelperContent>(content);
               });

    private static List<TreeSelectListItem> OneItem(string value = "v1", string text = "Node 1")
        => [new TreeSelectListItem { Value = value, Text = text }];

    private static string? ExtractJsonFromIsland(string html, string needleType)
    {
        var marker = "\"type\":\"" + needleType + "\"";
        var idx = html.IndexOf(marker, System.StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = html.LastIndexOf('{', idx);
        if (start < 0) return null;
        var depth = 0;
        var end = -1;
        for (var i = start; i < html.Length; i++)
        {
            if (html[i] == '{') depth++;
            else if (html[i] == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }
        if (end < 0) return null;
        return html[start..(end + 1)];
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ── Flag OFF (default) — byte-for-byte legacy inline render ─────────────

    [TestMethod]
    public async Task TreeContainer_FlagOff_EmitsInlineTreeRenderScript_NoIsland()
    {
        SetupLocalizer();
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_1",
            Items = MakeItemsField(OneItem())
        };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        StringAssert.Contains(content, "layui.use(['tree']",
            "Flag OFF (default) must keep emitting the legacy inline tree.render");
        StringAssert.Contains(content, "layui.tree.render(");
        Assert.IsFalse(content.Contains("\"type\":\"renderTreeContainer\""),
            "Flag OFF must never emit a renderTreeContainer island");
        Assert.IsFalse(content.Contains("wtm-dialog-init"),
            "Flag OFF must never emit the JSON island wrapper");
        Assert.IsFalse(content.Contains("console.warn('[WTM] TreeContainerTagHelper"),
            "Flag OFF must never emit the deprecation warn line");
        StringAssert.Contains(content, "var toptc_n1_1selected = {};",
            "Flag OFF must still create the legacy top{Id}selected global inline");
    }

    [TestMethod]
    public async Task TreeContainer_FlagOff_NonIdentifierClickFunc_KeepsLegacyInlineScript_NoWarn()
    {
        // Issue #753 precedent: even with a ClickFunc, flag OFF (default) must
        // stay byte-identical to base — zero warning characters.
        SetupLocalizer();
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_1b",
            Items = MakeItemsField(OneItem()),
            ClickFunc = "some.dotted.expr"
        };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        StringAssert.Contains(content, "layui.use(['tree']");
        Assert.IsFalse(content.Contains("console.warn("),
            "Flag OFF must contribute ZERO warning characters — base never had this warning");
        StringAssert.Contains(content, "some.dotted.expr");
    }

    // ── Flag ON + identifier/absent ClickFunc → island, no inline render ────

    [TestMethod]
    public async Task TreeContainer_FlagOn_NoClickFunc_EmptyContent_EmitsIsland_LoadPageMode()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_2",
            Items = MakeItemsField(OneItem())
        };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        StringAssert.Contains(content, "\"type\":\"renderTreeContainer\"");
        Assert.IsFalse(content.Contains("layui.use(['tree']"),
            "Flag ON + island path must NOT also emit the legacy inline tree.render script");

        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("tc_n1_2", root.GetProperty("id").GetString());
        Assert.AreEqual("divtc_n1_2", root.GetProperty("elemId").GetString());
        Assert.AreEqual("div_tc_n1_2", root.GetProperty("gridDivId").GetString());
        Assert.AreEqual("loadPage", root.GetProperty("clickMode").GetString(),
            "Empty nested content (no button, no grid) must resolve to 'loadPage' mode");
        Assert.IsFalse(root.TryGetProperty("clickFunc", out _));
        Assert.IsFalse(root.TryGetProperty("searchButtonId", out _));
        Assert.IsFalse(root.TryGetProperty("gridId", out _));
    }

    [TestMethod]
    public async Task TreeContainer_FlagOn_IdentifierClickFunc_EmitsIslandWithCustomMode()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_3",
            Items = MakeItemsField(OneItem()),
            ClickFunc = "myPlainClickFunc"
        };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        Assert.IsFalse(content.Contains("layui.use(['tree']"));
        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("custom", doc.RootElement.GetProperty("clickMode").GetString());
        Assert.AreEqual("myPlainClickFunc", doc.RootElement.GetProperty("clickFunc").GetString());
    }

    // ── Flag ON + non-identifier ClickFunc → legacy inline kept + warn ──────

    [TestMethod]
    public async Task TreeContainer_FlagOn_NonIdentifierClickFunc_KeepsInlineRender_EmitsWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_4",
            Items = MakeItemsField(OneItem()),
            ClickFunc = "some.dotted.expr"
        };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        StringAssert.Contains(content, "layui.use(['tree']",
            "Non-identifier ClickFunc must keep the legacy inline render — never silently dropped");
        Assert.IsFalse(content.Contains("\"type\":\"renderTreeContainer\""));
        StringAssert.Contains(content, "console.warn('[WTM] TreeContainerTagHelper",
            "A deprecation warn must fire naming the field/ClickFunc when the flag is ON but the island was skipped");
        StringAssert.Contains(content, "some.dotted.expr");
    }

    // ── Click-mode analysis: searchButton / grid / default ──────────────────

    [TestMethod]
    public async Task TreeContainer_FlagOn_SearchButtonInNestedContent_ResolvesSearchButtonMode()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_5",
            Items = MakeItemsField(OneItem())
        };
        var output = MakeOutputWithChildContent(@"<a id=""mySearchBtn"" IsSearchButton>Search</a>");
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("searchButton", doc.RootElement.GetProperty("clickMode").GetString());
        Assert.AreEqual("mySearchBtn", doc.RootElement.GetProperty("searchButtonId").GetString());
        Assert.IsFalse(doc.RootElement.TryGetProperty("gridId", out _));
    }

    [TestMethod]
    public async Task TreeContainer_FlagOn_NestedGridWithTableRenderVar_ResolvesGridMode_ExtendWhereTrue()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_6",
            Items = MakeItemsField(OneItem())
        };
        // Mirrors what DataTableTagHelper's rendered <script> looks like:
        // "{gridid}option = {" declaration + "{gridvar} = table.render({gridid}option)".
        var childHtml = @"<script>
grid1option = { };
grid1table = table.render(grid1option);
</script>";
        var output = MakeOutputWithChildContent(childHtml);
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("grid", doc.RootElement.GetProperty("clickMode").GetString());
        Assert.AreEqual("grid1", doc.RootElement.GetProperty("gridId").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("gridExtendWhere").GetBoolean());
    }

    [TestMethod]
    public async Task TreeContainer_FlagOn_NestedGridWithoutTableRenderVar_ResolvesGridMode_ExtendWhereFalse()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_7",
            Items = MakeItemsField(OneItem())
        };
        // "{gridid}option = {" present, but NO "= table.render(grid1option)" line.
        var childHtml = @"<script>
grid1option = { };
</script>";
        var output = MakeOutputWithChildContent(childHtml);
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("grid", doc.RootElement.GetProperty("clickMode").GetString());
        Assert.AreEqual("grid1", doc.RootElement.GetProperty("gridId").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("gridExtendWhere").GetBoolean());
    }

    [TestMethod]
    public async Task TreeContainer_FlagOn_NonEmptyUnmatchedContent_ResolvesDefaultMode()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_8",
            Items = MakeItemsField(OneItem())
        };
        var output = MakeOutputWithChildContent("<div>plain nested content, no grid, no button</div>");
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("default", doc.RootElement.GetProperty("clickMode").GetString());
    }

    // ── ShowLine / AutoLoadUrl / SelectedItem round-trip ─────────────────────

    [TestMethod]
    public async Task TreeContainer_FlagOn_ShowLineFalse_IslandCarriesShowLineFalse()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_9",
            Items = MakeItemsField(OneItem()),
            ShowLine = false
        };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.GetProperty("showLine").GetBoolean());
    }

    [TestMethod]
    public async Task TreeContainer_FlagOn_AutoLoadUrl_NoSelection_IslandCarriesAutoLoadUrl()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_10",
            Items = MakeItemsField(OneItem()),
            AutoLoadUrl = "/Home/FirstNode"
        };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("/Home/FirstNode", doc.RootElement.GetProperty("autoLoadUrl").GetString());
    }

    [TestMethod]
    public async Task TreeContainer_FlagOn_SelectedItemPresent_AutoLoadUrlOmitted()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_11",
            Items = MakeItemsField(OneItem("v1", "Node 1")),
            AutoLoadUrl = "/Home/FirstNode",
            IdField = MakeField("IdField", "v1")
        };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("autoLoadUrl", out _),
            "AutoLoadUrl must be omitted when a node is already selected — mirrors the legacy inline gate exactly");
        var selectedItem = doc.RootElement.GetProperty("selectedItem");
        Assert.AreEqual("v1", selectedItem.GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task TreeContainer_FlagOn_IdLevelFieldNames_StripSearcherPrefix()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_12",
            Items = MakeItemsField(OneItem())
        };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("notsetid", doc.RootElement.GetProperty("idFieldName").GetString());
        Assert.AreEqual("notsetlevel", doc.RootElement.GetProperty("levelFieldName").GetString());
    }

    // ── Markup (div structure) is preserved regardless of flag ──────────────

    [TestMethod]
    public async Task TreeContainer_FlagOn_MarkupStructure_PreservesNestedDivs()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_n1_13",
            Items = MakeItemsField(OneItem())
        };
        var output = MakeOutputWithChildContent("<div id='nested'>nested grid markup</div>");
        await helper.ProcessAsync(MakeContext(), output);
        var content = output.Content.GetContent();

        StringAssert.Contains(content, @"id=""divtc_n1_13outer""");
        StringAssert.Contains(content, @"id=""divtc_n1_13""");
        StringAssert.Contains(content, @"id=""div_tc_n1_13""");
        StringAssert.Contains(content, "nested grid markup");
    }
}
