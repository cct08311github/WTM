#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice N2 — opt-in (WtmUIOptions.UseSelectIslandRender, default
/// OFF — the SAME flag Slices J-O1 use) eval-free delegated click/myclick
/// wiring for &lt;wt:searchpanel&gt;/SearchPanelTagHelper. Design authority:
/// internal infrastructure issue #470 comment 18118 §5 ("SearchPanel N2 co-design").
///
/// Pattern: <c>IOptionsMonitor&lt;Configs&gt;</c> mock per
/// DateTimeTagHelperTests.cs; localizer mock + [TestCleanup]
/// BaseFieldTag.SetUIOptions reset convention per
/// RenderGridIsland470SliceO1Tests.cs / RenderTreeContainerIsland470SliceN1
/// tests. SearchPanelTagHelper is bound to a bare <see cref="BaseSearcher"/>
/// (no ListVM) for most tests — GridId/ChartId/Id are all pinned via public
/// setters, so no DataContext/MockWtmContext machinery is needed for those.
/// One integration-style test at the bottom binds a real
/// <c>PlainColumnsListVM</c> (shared fixture from
/// DataTableByteIdentityTests.Vm.cs, same namespace) to prove GridId
/// auto-derivation and the "Searcher" fieldPre prefix still work end to end.
///
/// tempSearchTitleId (Guid.NewGuid().ToNoSplitString(), SearchPanelTagHelper.cs)
/// is the one non-deterministic component with no public seam to pin — it is
/// extracted from the rendered PreContent via <see cref="ExtractTitleId"/>
/// and either substituted into a hand-built expected fixture (TC-01/TC-02) or
/// normalized away for a self-comparison byte-identity check (TC-03/TC-04),
/// mirroring DataTableByteIdentityTests.Normalize's documented last-resort
/// pattern.
/// </summary>
[TestClass]
public class SearchPanelIsland470SliceN2Tests
{
    private sealed class TestSearcher : BaseSearcher
    {
    }

    // ── helpers ──────────────────────────────────────────────────────────

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

    private static ModelExpression MakeVm(object model)
    {
        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(model.GetType());
        var modelExplorer = new ModelExplorer(provider, metadata, model);
        return new ModelExpression(string.Empty, modelExplorer);
    }

    private static TagHelperContext MakeContext()
        => new("wt:searchpanel", new TagHelperAttributeList(), new Dictionary<object, object>(), "test-id");

    private static TagHelperContext MakeSelectorContext()
    {
        var items = new Dictionary<object, object> { ["inselector"] = true };
        return new("wt:searchpanel", new TagHelperAttributeList(), items, "test-id");
    }

    private static TagHelperOutput MakeOutput()
        => new("form", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static SearchPanelTagHelper CreateHelper(object vmModel, string id)
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new SearchPanelTagHelper(monitor.Object)
        {
            Vm = MakeVm(vmModel),
            Id = id
        };
    }

    private static async Task<(string pre, string post, string postElement)> Render(
        SearchPanelTagHelper helper, TagHelperContext? context = null)
    {
        context ??= MakeContext();
        var output = MakeOutput();
        await helper.ProcessAsync(context, output);
        return (output.PreContent.GetContent(), output.PostContent.GetContent(), output.PostElement.GetContent());
    }

    private static string ExtractTitleId(string pre)
    {
        var m = Regex.Match(pre, "lay-filter=\"([0-9a-f]{32})x\"");
        Assert.IsTrue(m.Success, "Could not extract tempSearchTitleId from PreContent: " + pre);
        return m.Groups[1].Value;
    }

    private static string ExtractJsonFromIsland(string html, string needleType)
    {
        var marker = "\"type\":\"" + needleType + "\"";
        var idx = html.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.IsTrue(idx >= 0, $"Could not find a '{needleType}' island in: {html}");
        var start = html.LastIndexOf('{', idx);
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
        Assert.IsTrue(end >= 0, "Malformed island JSON");
        return html[start..(end + 1)];
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ── TC-01: flag OFF — legacy fragments present, byte-identity pins ─────

    [TestMethod]
    public async Task FlagOff_Default_EmitsLegacyMarkupVerbatim_NoIslandContent()
    {
        SetupLocalizer();
        var helper = CreateHelper(new TestSearcher(), "wtForm_N2A");
        helper.GridId = "wtTable_N2A";
        helper.ChartId = "wtChart_N2A";
        helper.ChartPrefix = "MyPrefix";

        var (pre, post, postElement) = await Render(helper);
        var titleId = ExtractTitleId(pre);

        // The search button carries IsSearchButton and NOTHING island-only.
        StringAssert.Contains(pre,
            $"<a href=\"javascript:void(0)\" class=\"layui-btn layui-btn-sm\" id=\"{helper.SearchBtnId}\" IsSearchButton><i class=\"layui-icon\">&#xe615;</i>Sys.Search</a>",
            "Flag OFF must render the search button with no data-wtm-* attributes at all");
        Assert.IsFalse(pre.Contains("data-wtm-search"), "Flag OFF must never emit data-wtm-search");

        // refreshgridjs / refreshchartjs pieces, verbatim (SearchPanelTagHelper.cs's
        // refreshgridjs/refreshchartjs string builders, unchanged by N2).
        StringAssert.Contains(postElement, "var tempwherewtTable_N2A = {};");
        StringAssert.Contains(postElement, "$.extend(tempwherewtTable_N2A,wtTable_N2Adefaultfilter.where);");
        StringAssert.Contains(postElement, "var pagewtTable_N2A = wtTable_N2Afilterback.page;");
        StringAssert.Contains(postElement, "if(keeppage ==null){ pagewtTable_N2A.curr = 1}");
        StringAssert.Contains(postElement,
            "table.reload('wtTable_N2A',{page: pagewtTable_N2A,url:wtTable_N2Aurl,where: $.extend(tempwherewtTable_N2A,ff.GetSearchFormData('wtForm_N2A',''))});");
        StringAssert.Contains(postElement, "ff.RefreshChart('wtChart_N2A','MyPrefix');");

        // Collapse / reset / IsExpanded pieces, verbatim.
        StringAssert.Contains(postElement, $"$('#{titleId} .layui-btn').on('click',function(e){{e.stopPropagation();}})");
        StringAssert.Contains(postElement, $"$('#{helper.ResetBtnId}').on('click', function (btn) {{ff.resetForm(this.form.id);}});");
        // UIOptions.SearchPanelOptions.DefaultExpand defaults to true
        // (DefaultConfigConsts.DEFAULT_SEARCHPANEL_DEFAULT_EXPAND) and neither
        // helper here sets Expanded/SearcherVM.IsExpanded, so `show` resolves
        // to that default.
        StringAssert.Contains(postElement, "value='true' />");
        StringAssert.Contains(postElement, $"layui.element.on('collapse({titleId}x)', function(data){{");
        StringAssert.Contains(postElement, $"layui.element.on('collapse({titleId})', function(data){{ff.triggerResize()}});");

        // Click/myclick bindings, verbatim.
        StringAssert.Contains(postElement, $"$('#{helper.SearchBtnId}').on('click', function () {{");
        StringAssert.Contains(postElement, $"$('#{helper.SearchBtnId}').bind('myclick', function () {{");

        // Note: FormTagHelper.Process (called via base.ProcessAsync) ALWAYS
        // emits its own unconditional 'initForm' wtm-dialog-init island
        // (Issue #561/#564 — unrelated to the #470 N2 flag), so
        // "wtm-dialog-init" itself legitimately appears here; the guard is
        // that no 'searchPanelInit' TYPE island is among them.
        Assert.IsFalse(postElement.Contains("\"type\":\"searchPanelInit\""), "Flag OFF must never emit a searchPanelInit JSON island");
    }

    // ── TC-02: flag ON, non-OldPost, non-selector — island path ────────────

    [TestMethod]
    public async Task FlagOn_Default_EmitsDataWtmSearchAttrsAndSearchPanelInitIsland_NoLegacyScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new TestSearcher(), "wtForm_N2B");
        helper.GridId = "wtTable_N2B";
        helper.ChartId = "wtChart_N2B";
        helper.ChartPrefix = "MyPrefix";

        var (pre, _, postElement) = await Render(helper);
        var titleId = ExtractTitleId(pre);

        StringAssert.Contains(pre, "data-wtm-search-grids=\"wtTable_N2B\"");
        StringAssert.Contains(pre, "data-wtm-search-charts=\"wtChart_N2B\"");
        StringAssert.Contains(pre, "data-wtm-chart-prefix=\"MyPrefix\"");
        StringAssert.Contains(pre, "data-wtm-form=\"wtForm_N2B\"");
        StringAssert.Contains(pre, "data-wtm-fieldpre=\"\"");
        // Marker attribute present as a standalone token (not folded into another attr name).
        StringAssert.Contains(pre, "IsSearchButton data-wtm-search ");

        // No legacy click/myclick/table.reload/collapse script survives.
        Assert.IsFalse(postElement.Contains("table.reload("), "Island path must not emit the legacy table.reload( call");
        Assert.IsFalse(postElement.Contains(".on('click', function ()"), "Island path must not emit the legacy click handler");
        Assert.IsFalse(postElement.Contains(".bind('myclick'"), "Island path must not emit the legacy myclick handler");
        Assert.IsFalse(postElement.Contains("layui.element.on('collapse("), "Island path must not emit the legacy inline collapse handlers");
        Assert.IsFalse(postElement.Contains("layui.use(['table','element']"), "Island path must not emit the legacy layui.use wrapper");

        var json = ExtractJsonFromIsland(postElement, "searchPanelInit");
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("searchPanelInit", doc.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(titleId, doc.RootElement.GetProperty("titleId").GetString());
        Assert.AreEqual(helper.ResetBtnId, doc.RootElement.GetProperty("resetBtnId").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("show").GetBoolean(), "DefaultConfigConsts.DEFAULT_SEARCHPANEL_DEFAULT_EXPAND defaults to true");
    }

    [TestMethod]
    public async Task FlagOn_NoGridNoChart_StillEmitsMarkerAndFormFieldpre_ButOmitsGridChartAttrs()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new TestSearcher(), "wtForm_N2C");
        // GridId/ChartId intentionally left unset (no ListVM => GridId is also null).

        var (pre, _, postElement) = await Render(helper);

        StringAssert.Contains(pre, "IsSearchButton data-wtm-search ");
        Assert.IsFalse(pre.Contains("data-wtm-search-grids"), "No GridId => no data-wtm-search-grids attribute");
        Assert.IsFalse(pre.Contains("data-wtm-search-charts"), "No ChartId => no data-wtm-search-charts attribute");
        Assert.IsFalse(pre.Contains("data-wtm-chart-prefix"), "No ChartId => no data-wtm-chart-prefix attribute either");
        StringAssert.Contains(pre, "data-wtm-form=\"wtForm_N2C\"");

        Assert.IsTrue(postElement.Contains("wtm-dialog-init"), "searchPanelInit island must still be emitted even with no grids/charts");
    }

    [TestMethod]
    public async Task FlagOn_ChartWithoutPrefix_OmitsChartPrefixAttribute()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new TestSearcher(), "wtForm_N2D");
        helper.ChartId = "wtChart_N2D";
        // ChartPrefix intentionally left unset.

        var (pre, _, _) = await Render(helper);

        StringAssert.Contains(pre, "data-wtm-search-charts=\"wtChart_N2D\"");
        Assert.IsFalse(pre.Contains("data-wtm-chart-prefix"), "Empty ChartPrefix must omit the attribute entirely (JS reads getAttribute -> null, same falsy branch as legacy's 'undefined')");
    }

    // ── TC-03: OldPost stays legacy even when the flag is ON (invariant 3) ─

    [TestMethod]
    public async Task FlagOn_OldPost_IsByteIdenticalToFlagOff_NoIslandAttrsNoIsland()
    {
        SetupLocalizer();

        // Same TestSearcher instance shared by both helpers: ResetBtnId is
        // derived from SearcherVM.UniqueId (a lazily-generated Guid with no
        // setter — see SearchPanelTagHelper.cs), so two SEPARATE instances
        // would make the two runs differ for a reason that has nothing to do
        // with the flag, defeating the byte-identity comparison below.
        var searcher = new TestSearcher();

        var helperOff = CreateHelper(searcher, "wtForm_N2E");
        helperOff.GridId = "wtTable_N2E";
        helperOff.OldPost = true;
        var (preOff, postOff, postElementOff) = await Render(helperOff);
        var titleOff = ExtractTitleId(preOff);

        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });

        var helperOn = CreateHelper(searcher, "wtForm_N2E");
        helperOn.GridId = "wtTable_N2E";
        helperOn.OldPost = true;
        var (preOn, postOn, postElementOn) = await Render(helperOn);
        var titleOn = ExtractTitleId(preOn);

        Assert.AreEqual(preOff.Replace(titleOff, "<<T>>"), preOn.Replace(titleOn, "<<T>>"),
            "OldPost PreContent must be byte-identical regardless of the flag (modulo the random titleId)");
        Assert.AreEqual(postOff, postOn, "OldPost PostContent has no titleId in it and must match exactly");
        Assert.AreEqual(postElementOff.Replace(titleOff, "<<T>>"), postElementOn.Replace(titleOn, "<<T>>"),
            "OldPost PostElement must be byte-identical regardless of the flag (modulo the random titleId)");

        Assert.IsFalse(preOn.Contains("data-wtm-search"), "OldPost must never emit data-wtm-search even when flag is ON");
        // Note: FormTagHelper's own unconditional 'initForm' island (#561/#564,
        // unrelated to the N2 flag) always contributes a wtm-dialog-init
        // script, so the guard here is specifically no 'searchPanelInit' action.
        Assert.IsFalse(postElementOn.Contains("\"type\":\"searchPanelInit\""), "OldPost must never emit the searchPanelInit island even when flag is ON");
        // OldPost's legacy SearchPanelTagHelper click/myclick refresh binding
        // is ALSO absent in both runs (native submit path) — locks invariant
        // 3 explicitly. Note: FormTagHelper's OWN OldPost submit-binding
        // script (".on('click', function () {" + ff.PostForm(...)) is a
        // SEPARATE, always-present feature unrelated to N2 — checking for
        // 'table.reload(' instead unambiguously targets ONLY
        // SearchPanelTagHelper's own refreshgridjs-driven handler.
        Assert.IsFalse(postElementOn.Contains("table.reload("));
        Assert.IsFalse(postElementOn.Contains(".bind('myclick'"));
    }

    // ── TC-04: selector-hosted stays legacy even when the flag is ON (invariant 4) ─

    [TestMethod]
    public async Task FlagOn_SelectorHosted_IsByteIdenticalToFlagOff_NoIslandAttrsNoIsland()
    {
        SetupLocalizer();

        // Same TestSearcher instance shared by both helpers — see the
        // OldPost test above for why (ResetBtnId stability).
        var searcher = new TestSearcher();

        var helperOff = CreateHelper(searcher, "wtForm_N2F");
        helperOff.GridId = "wtTable_N2F";
        var (preOff, postOff, postElementOff) = await Render(helperOff, MakeSelectorContext());
        var titleOff = ExtractTitleId(preOff);

        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });

        var helperOn = CreateHelper(searcher, "wtForm_N2F");
        helperOn.GridId = "wtTable_N2F";
        var (preOn, postOn, postElementOn) = await Render(helperOn, MakeSelectorContext());
        var titleOn = ExtractTitleId(preOn);

        Assert.AreEqual(preOff.Replace(titleOff, "<<T>>"), preOn.Replace(titleOn, "<<T>>"),
            "Selector-hosted PreContent must be byte-identical regardless of the flag (modulo the random titleId)");
        Assert.AreEqual(postOff, postOn);
        Assert.AreEqual(postElementOff.Replace(titleOff, "<<T>>"), postElementOn.Replace(titleOn, "<<T>>"),
            "Selector-hosted PostElement must be byte-identical regardless of the flag (modulo the random titleId)");

        Assert.IsFalse(preOn.Contains("data-wtm-search"), "Selector-hosted panels must never emit data-wtm-search even when flag is ON");
        // Note: FormTagHelper's own unconditional 'initForm' island (#561/#564,
        // unrelated to the N2 flag) always contributes a wtm-dialog-init
        // script, so the guard here is specifically no 'searchPanelInit' action.
        Assert.IsFalse(postElementOn.Contains("\"type\":\"searchPanelInit\""), "Selector-hosted panels must never emit the searchPanelInit island even when flag is ON");
        // The legacy click/myclick binding IS present (selector panels are not OldPost) — locks that the FULL legacy path is kept, not just a subset.
        StringAssert.Contains(postElementOn, $"$('#{helperOn.SearchBtnId}').on('click', function () {{");
        StringAssert.Contains(postElementOn, $"$('#{helperOn.SearchBtnId}').bind('myclick', function () {{");
    }

    // ── TC-05: ResetBtn present — always bound regardless of flag ──────────

    [TestMethod]
    public async Task FlagOn_ResetBtnTrue_SearchPanelInitCarriesResetBtnId_ButtonRendered()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new TestSearcher(), "wtForm_N2G");
        helper.ResetBtn = true;

        var (pre, _, postElement) = await Render(helper);

        StringAssert.Contains(pre, $"id=\"{helper.ResetBtnId}\"");
        var json = ExtractJsonFromIsland(postElement, "searchPanelInit");
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual(helper.ResetBtnId, doc.RootElement.GetProperty("resetBtnId").GetString());
    }

    // ── TC-06: multiple comma-joined grid ids preserved raw on the attribute ─

    [TestMethod]
    public async Task FlagOn_MultipleGridIds_CommaJoinedAttributePreserved()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new TestSearcher(), "wtForm_N2H");
        helper.GridId = "wtTable_A,wtTable_B";

        var (pre, _, _) = await Render(helper);

        StringAssert.Contains(pre, "data-wtm-search-grids=\"wtTable_A,wtTable_B\"");
    }

    // ── TC-07: real ListVM binding — GridId auto-derivation + "Searcher" fieldPre ─

    [TestMethod]
    public async Task FlagOn_RealListVm_GridIdAutoDerivedAndFieldPreIsSearcher()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var listVm = new PlainColumnsListVM
        {
            Wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory)),
            ViewDivId = "FixedViewDivN2"
        };
        var helper = CreateHelper(listVm, "wtForm_N2I");

        var (pre, _, _) = await Render(helper);

        var expectedGridId = DataTableTagHelper.TABLE_ID_PREFIX + listVm.UniqueId;
        StringAssert.Contains(pre, $"data-wtm-search-grids=\"{expectedGridId}\"");
        StringAssert.Contains(pre, "data-wtm-fieldpre=\"Searcher\"");
    }
}
