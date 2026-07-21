#nullable enable
using System.Text.Json;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Chart;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice N1: opt-in (WtmUIOptions.UseSelectIslandRender, default
/// OFF — the SAME flag #470 Slices J/K/L/M/N1's renderTreeContainer use)
/// eval-free 'renderChart' JSON island for &lt;wt:chart&gt;. Mirrors
/// RenderTransferIsland470SliceKTests / RenderUploadIsland470SliceLTests
/// conventions exactly — same island-extraction pattern.
///
/// IMPORTANT: WtmUIOptions is process-wide static state
/// (BaseFieldTag.SetUIOptions). Every test that flips UseSelectIslandRender ON
/// must be paired with the [TestCleanup] reset below.
/// </summary>
[TestClass]
public class RenderChartIsland470SliceN1Tests
{
    private static System.Collections.Generic.Dictionary<object, object> Items()
        => new();

    private static TagHelperContext MakeContext()
        => new("wt:chart", new TagHelperAttributeList(), Items(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => System.Threading.Tasks.Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

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

    [TestCleanup]
    public void Cleanup()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ── Flag OFF (default) — byte-for-byte legacy inline render ─────────────

    [TestMethod]
    public void Chart_FlagOff_EmitsInlineEchartsInitScript_NoIsland()
    {
        var helper = new ChartTagHelper
        {
            Id = "chart_n1_1",
            Type = ChartTypeEnum.Bar
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "echarts.init(document.getElementById('chart_n1_1')",
            "Flag OFF (default) must keep emitting the legacy inline echarts.init(...)");
        StringAssert.Contains(postHtml, "chart_n1_1Chart = echarts.init(");
        Assert.IsFalse(postHtml.Contains("\"type\":\"renderChart\""),
            "Flag OFF must never emit a renderChart island");
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"),
            "Flag OFF must never emit the JSON island wrapper");
    }

    [TestMethod]
    public void Chart_FlagOff_MarkupCarriesIscharttMarker_RegardlessOfFlag()
    {
        // Issue #470 Slice N1: the ischart="1" marker is emitted as MARKUP
        // (output.Attributes), not inside the flag-branched <script> — so it
        // must be present unconditionally (ff.ResizeChart's div[ischart='1']
        // scan + OpenDialog resize wiring depend on it).
        var helper = new ChartTagHelper { Id = "chart_n1_1c", Type = ChartTypeEnum.Bar };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("ischart"));
        Assert.AreEqual("1", output.Attributes["ischart"].Value);
    }

    // ── Flag ON — island, no inline render, ischart marker unaffected ───────

    [TestMethod]
    public void Chart_FlagOn_EmitsRenderChartIsland_NoInlineRender()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper
        {
            Id = "chart_n1_2",
            Type = ChartTypeEnum.Bar
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"renderChart\"");
        Assert.IsFalse(postHtml.Contains("echarts.init("),
            "Flag ON + island path must NOT also emit the legacy inline echarts.init(...) script");

        Assert.IsTrue(output.Attributes.ContainsName("ischart"),
            "ischart marker must still be present under the island path (it's markup, not script)");
        Assert.AreEqual("1", output.Attributes["ischart"].Value);

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("renderChart", root.GetProperty("type").GetString());
        Assert.AreEqual("chart_n1_2", root.GetProperty("id").GetString());
        Assert.AreEqual("\"type\":\"bar\"", root.GetProperty("chartType").GetString());
        Assert.AreEqual("bar", root.GetProperty("chartTypeName").GetString());
    }

    [TestMethod]
    public void Chart_FlagOn_NoTheme_IslandOmitsTheme()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper { Id = "chart_n1_3", Type = ChartTypeEnum.Line };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("theme", out _),
            "Theme omitted (WhenWritingNull) when not set — renderer defaults to 'default'");
    }

    [TestMethod]
    public void Chart_FlagOn_ThemeSet_IslandCarriesLowercaseThemeName()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper { Id = "chart_n1_4", Type = ChartTypeEnum.Bar, Theme = ChartThemeEnum.dark };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("dark", doc.RootElement.GetProperty("theme").GetString());
    }

    [TestMethod]
    public void Chart_FlagOn_LineType_ChartTypeIncludesSmoothFlag()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper { Id = "chart_n1_5", Type = ChartTypeEnum.Line, OpenSmooth = true };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("\"type\":\"line\",\"smooth\": true", doc.RootElement.GetProperty("chartType").GetString());
    }

    [TestMethod]
    public void Chart_FlagOn_PieHollowType_ChartTypeUsesPieWithRadius()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper { Id = "chart_n1_6", Type = ChartTypeEnum.PieHollow };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("\"type\":\"pie\",\"radius\": [\"40%\", \"70%\"]", doc.RootElement.GetProperty("chartType").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("noCartesianAxes").GetBoolean());
    }

    [TestMethod]
    public void Chart_FlagOn_ScatterType_NoCartesianAxesFalse_NamesCarried()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper
        {
            Id = "chart_n1_7",
            Type = ChartTypeEnum.Scatter,
            NameX = "X Axis",
            NameY = "Y Axis",
            NameAddition = "Addl",
            NameCategory = "Cat"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.GetProperty("noCartesianAxes").GetBoolean());
        Assert.AreEqual("X Axis", doc.RootElement.GetProperty("nameX").GetString());
        Assert.AreEqual("Y Axis", doc.RootElement.GetProperty("nameY").GetString());
        Assert.AreEqual("Addl", doc.RootElement.GetProperty("nameAddition").GetString());
        Assert.AreEqual("Cat", doc.RootElement.GetProperty("nameCategory").GetString());
    }

    [TestMethod]
    public void Chart_FlagOn_GaugeType_NoCartesianAxesTrue()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper { Id = "chart_n1_8", Type = ChartTypeEnum.Gauge };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsTrue(doc.RootElement.GetProperty("noCartesianAxes").GetBoolean());
    }

    [TestMethod]
    public void Chart_FlagOn_ShowLegendDefaultsTrue_IslandCarriesLegendTrue()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper { Id = "chart_n1_9", Type = ChartTypeEnum.Bar };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsTrue(doc.RootElement.GetProperty("legend").GetBoolean());
        Assert.IsTrue(doc.RootElement.GetProperty("showTooltip").GetBoolean());
    }

    [TestMethod]
    public void Chart_FlagOn_ShowTooltipFalse_IslandCarriesShowTooltipFalse()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper { Id = "chart_n1_10", Type = ChartTypeEnum.Bar, ShowTooltip = false };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.GetProperty("showTooltip").GetBoolean());
    }

    [TestMethod]
    public void Chart_FlagOn_TriggerUrl_IslandCarriesUrl()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper { Id = "chart_n1_11", Type = ChartTypeEnum.Bar, TriggerUrl = "/Home/GetChartData" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("/Home/GetChartData", doc.RootElement.GetProperty("url").GetString());
    }

    [TestMethod]
    public void Chart_FlagOn_Title_IslandCarriesTitle_OmittedWhenEmpty()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ChartTagHelper { Id = "chart_n1_12", Type = ChartTypeEnum.Bar, Title = "Sales" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("Sales", doc.RootElement.GetProperty("title").GetString());

        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper2 = new ChartTagHelper { Id = "chart_n1_12b", Type = ChartTypeEnum.Bar };
        var output2 = MakeOutput();
        helper2.Process(MakeContext(), output2);
        var postHtml2 = output2.PostElement.GetContent();
        var json2 = ExtractJsonFromIsland(postHtml2);
        Assert.IsNotNull(json2);
        using var doc2 = JsonDocument.Parse(json2!);
        Assert.IsFalse(doc2.RootElement.TryGetProperty("title", out _));
    }
}
