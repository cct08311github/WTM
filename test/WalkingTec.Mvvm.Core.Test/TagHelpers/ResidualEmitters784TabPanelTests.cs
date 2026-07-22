#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #784 (#470 residual, GROUP 3): gates TabTagHelper's and
/// PanelTagHelper's fixed-shape inline &lt;script&gt; (tab-selection +
/// chart-resize; collapse-resize) behind WtmUIOptions.UseSelectIslandRender
/// (default OFF) via a 'tabInit'/'panelInit' JSON island. No developer
/// callback is involved in either emitter, so there is no non-identifier
/// fallback branch to test — flag ON always migrates to the island.
///
/// IMPORTANT: WtmUIOptions is process-wide static state — every test that
/// flips UseSelectIslandRender ON is paired with the [TestCleanup] reset.
/// </summary>
[TestClass]
public class ResidualEmitters784TabPanelTests
{
    private static TagHelperContext MakeContext(string tagName)
        => new(tagName, new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput(string tagName = "div")
        => new(tagName, new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    [TestCleanup]
    public void Cleanup()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ═══════════════════════ TabTagHelper ═════════════════════════════════

    [TestMethod]
    public void Tab_FlagOff_EmitsExactLegacyScript_NoIsland()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new TabTagHelper { SelectedIndex = 2 };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tab"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"), "Flag OFF must never emit the JSON island");
        StringAssert.Contains(postHtml, $"$('#{helper.Id} ul li').eq(2).addClass('layui-this');");
        StringAssert.Contains(postHtml, $"$('#{helper.Id} .layui-tab-item').eq(2).addClass('layui-show');");
        StringAssert.Contains(postHtml, $"layui.element.on('tab({helper.Id}filter)', function(data){{");
        StringAssert.Contains(postHtml, "var _chart = window[$(this).attr('id') + 'Chart'];");
    }

    [TestMethod]
    public void Tab_FlagOn_EmitsTabInitIsland_NoLegacyScript()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TabTagHelper { SelectedIndex = 1 };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tab"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"tabInit\"");
        StringAssert.Contains(postHtml, $"\"id\":\"{helper.Id}\"");
        StringAssert.Contains(postHtml, $"\"filter\":\"{helper.Id}filter\"");
        StringAssert.Contains(postHtml, "\"selectedIndex\":1");
        Assert.IsFalse(postHtml.Contains("layui.element.on('tab("),
            "Flag ON must not emit the legacy inline tab-selection script");
        Assert.IsFalse(postHtml.Contains("console.warn("));
    }

    [TestMethod]
    public void Tab_FlagOn_DefaultSelectedIndex_OmittedFromJson_WhenZero()
    {
        // int is not nullable, so 0 is always serialized (WhenWritingNull only
        // suppresses actual nulls) — this pins that SelectedIndex=0 (the
        // common default) still round-trips correctly through the island.
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TabTagHelper();
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tab"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"selectedIndex\":0");
    }

    // ═══════════════════════ PanelTagHelper ═══════════════════════════════

    [TestMethod]
    public async Task Panel_FlagOff_EmitsExactLegacyScript_NoIsland()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new PanelTagHelper { PanelType = PanelType.Collapse, Title = "T" };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext("wt:panel"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"), "Flag OFF must never emit the JSON island");
        StringAssert.Contains(postHtml, "layui.use(['element'],function(){");
        StringAssert.Contains(postHtml, "element.init();");
        StringAssert.Contains(postHtml, "window.dispatchEvent(new Event('resize'));");
    }

    [TestMethod]
    public async Task Panel_FlagOn_EmitsPanelInitIsland_NoLegacyScript()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new PanelTagHelper { PanelType = PanelType.Collapse, Title = "T" };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext("wt:panel"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"panelInit\"");
        var filter = output.Attributes["lay-filter"].Value!.ToString();
        StringAssert.Contains(postHtml, $"\"filter\":\"{filter}\"");
        Assert.IsFalse(postHtml.Contains("layui.use(['element'],function(){"),
            "Flag ON must not emit the legacy inline collapse-resize script");
        Assert.IsFalse(postHtml.Contains("console.warn("));
    }

    [TestMethod]
    public async Task Panel_FlagOn_CardType_StillEmitsPanelInitIsland()
    {
        // PanelType.Card also carries the same fixed collapse-resize wiring
        // (used defensively — Card panels aren't collapsible in layui, but
        // the legacy script was unconditional regardless of PanelType, so
        // the island must mirror that exactly).
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new PanelTagHelper { PanelType = PanelType.Card, Title = "T" };
        var output = MakeOutput();
        await helper.ProcessAsync(MakeContext("wt:panel"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"panelInit\"");
    }
}
