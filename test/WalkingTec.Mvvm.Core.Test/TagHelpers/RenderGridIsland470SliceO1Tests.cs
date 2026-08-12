#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice O1 stage 2: opt-in (WtmUIOptions.UseSelectIslandRender,
/// default OFF — the SAME flag Slices J/K/L/M/N1 use) eval-free 'renderGrid'
/// JSON island for &lt;wt:grid&gt;/DataTableTagHelper. Design authority: internal infrastructure
/// issue #470 comment 18118 ("Slice O design brief").
///
/// Reuses the SAME <c>CreateHelper</c>/<c>MakeContext</c>/<c>MakeOutput</c>/
/// <c>SetupLocalizer</c> shapes as <see cref="DataTableByteIdentityTests"/>
/// (stage 1) and the SAME public ListVM fixtures it defines
/// (<see cref="PlainColumnsListVM"/>, <see cref="ActionsMatrixListVM"/>,
/// <see cref="AggregateColumnsListVM"/>, <see cref="RichColumnsListVM"/>,
/// <see cref="BoolColumnListVM"/>) — same namespace, so no extra using is
/// needed. This class never touches the stage 1 byte-identity fixtures file
/// or its tests; it only ever flips <c>UseSelectIslandRender</c> ON, always
/// paired with the [TestCleanup] reset below (Slice J/N1 convention).
/// </summary>
[TestClass]
public class RenderGridIsland470SliceO1Tests
{
    // A GridAction whose OnClickFunc is a non-identifier expression — used by
    // the OnClickFunc fallback-matrix test. Deliberately minimal (unlike
    // ActionsMatrixListVM's full 9-action matrix) so the fallback reason is
    // unambiguous.
    public class NonIdentifierOnClickListVM : BasePagedListVM<Student, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
        {
            return new List<GridColumn<Student>>
            {
                this.MakeGridHeader(x => x.LoginName),
                this.MakeGridHeaderAction(),
            };
        }

        protected override List<GridAction> InitGridAction()
        {
            return new List<GridAction>
            {
                new GridAction
                {
                    Area = "ActBad",
                    Name = "Bad",
                    ParameterType = GridActionParameterTypesEnum.SingleId,
                    OnClickFunc = "myObj.notAnIdentifier()",
                },
            };
        }
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

    private static ModelExpression MakeVm(object model)
    {
        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(model.GetType());
        var modelExplorer = new ModelExplorer(provider, metadata, model);
        return new ModelExpression(string.Empty, modelExplorer);
    }

    private static TagHelperContext MakeContext()
        => new("wt:grid", new TagHelperAttributeList(), new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("table", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static WTMContext NewWtm()
        => MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));

    private static DataTableTagHelper CreateHelper<TModel, TSearcher>(
        BasePagedListVM<TModel, TSearcher> listVm,
        WTMContext wtm,
        string id = "wtTable_O1")
        where TModel : TopBasePoco
        where TSearcher : BaseSearcher
    {
        listVm.Wtm = wtm;
        listVm.ViewDivId = "FixedViewDivO1";
        return new DataTableTagHelper
        {
            Vm = MakeVm(listVm),
            Id = id,
            SearchPanelId = "wtForm_O1",
        };
    }

    private static (TagHelperOutput output, string attrs, string post) Render(DataTableTagHelper helper)
    {
        var context = MakeContext();
        var output = MakeOutput();
        helper.Process(context, output);
        var attrs = string.Join(";", output.Attributes.Select(a => a.Name + "=" + a.Value));
        return (output, attrs, output.PostElement.GetContent());
    }

    /// <summary>
    /// Brace-matching JSON extractor — same pattern as
    /// RenderTreeContainerIsland470SliceN1Tests/RenderSelectIsland470SliceJTests.
    /// </summary>
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

    // ── Flag OFF sanity — the island code path is completely dormant ───────

    [TestMethod]
    public void Default_FlagOff_NoIslandNoAttribute_LegacyRenderPresent()
    {
        SetupLocalizer();
        var helper = CreateHelper(new PlainColumnsListVM(), NewWtm());
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("wtm-dialog-init"), "Flag OFF must never emit a JSON island");
        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""));
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"), "Flag OFF must never emit the island-only attribute");
        StringAssert.Contains(post, "wtVar_wtTable_O1 = table.render(wtTable_O1option);");
    }

    // ── Flag ON, island-eligible → renderGrid island, XOR legacy ────────────

    [TestMethod]
    public void Default_FlagOn_EmitsRenderGridIsland_XorLegacyRender()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new PlainColumnsListVM(), NewWtm());
        var (_, attrs, post) = Render(helper);

        var hasIsland = post.Contains("\"type\":\"renderGrid\"");
        var hasLegacyRenderCall = post.Contains("table.render(wtTable_O1option)");
        Assert.IsTrue(hasIsland, "flag ON + island-eligible grid must emit the renderGrid island");
        Assert.IsFalse(hasLegacyRenderCall, "island XOR legacy — a grid must never emit both (invariant 6)");
        Assert.IsFalse(post.Contains("console.warn('[WTM] DataTableTagHelper"),
            "an island-eligible grid must not emit the fallback console.warn");
        StringAssert.Contains(attrs, "data-wtm-grid-id=wtTable_O1");

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("wtTable_O1", root.GetProperty("gridId").GetString());
        Assert.AreEqual("wtVar_wtTable_O1", root.GetProperty("tableJsVar").GetString());
        Assert.AreEqual("#wtTable_O1", root.GetProperty("elem").GetString());
        Assert.AreEqual("wtTable_O1", root.GetProperty("id").GetString());
        Assert.AreEqual("post", root.GetProperty("method").GetString());
        Assert.IsTrue(root.TryGetProperty("request", out _), "non-selector grids carry the request pageName/limitName shim");
        Assert.IsFalse(root.GetProperty("isInSelector").GetBoolean());
        Assert.IsTrue(root.TryGetProperty("done", out var done));
        Assert.IsFalse(done.TryGetProperty("doneFn", out _));
        Assert.IsFalse(done.TryGetProperty("checkedFn", out _));

        var cols = root.GetProperty("cols")[0];
        Assert.AreEqual(4, cols.GetArrayLength(), "checkbox + numbers + LoginName + Name");
        Assert.AreEqual("checkbox", cols[0].GetProperty("type").GetString());
        Assert.AreEqual("numbers", cols[1].GetProperty("type").GetString());
        Assert.AreEqual("LoginName", cols[2].GetProperty("field").GetString());
        Assert.AreEqual("plain", cols[2].GetProperty("templet").GetProperty("tpl").GetString());
        Assert.AreEqual("Name", cols[3].GetProperty("field").GetString());
    }

    // ── Toolbar/laytpl chunk retired for island grids (Slice O2, brief §2 O2) ──
    // Historical note: under O1, this class's precursor tests
    // (ActionsMatrix_FlagOn_IslandPlusLegacyToolbarChunk_BothPresent /
    // ActionsMatrix_FlagOn_RowAndToolbarTemplatesEmitted) asserted that the
    // legacy wtToolBarFunc_{Id} dispatcher + both laytpl
    // <script type="text/html"> templates were STILL emitted verbatim even for
    // island-eligible grids (O1's accepted interim, invariant 7). Slice O2
    // completes the toolbar/row-button islandification those tests were
    // documenting as deferred — island grids now emit an `actions[]` descriptor
    // array + `toolbarHtml` + a `templet:{tpl:'actionCol'}` row-action column
    // instead, and NEITHER legacy artifact is emitted anymore. See
    // RenderGridToolbar470SliceO2Tests.cs for the full O2 coverage; the two
    // assertions below replace the retired ones in place (same test names would
    // now assert the opposite of what O2 ships, so they are rewritten rather
    // than duplicated).

    [TestMethod]
    public void ActionsMatrix_FlagOn_EmitsIslandActionsInsteadOfLegacyToolbarChunk()
    {
        // ActionsMatrixListVM's one OnClickFunc ("myGridOnClickHandler") is a bare
        // identifier, so the grid itself is island-eligible. Slice O2: the legacy
        // toolbar dispatcher + laytpl templates are retired for island grids —
        // toolbar/row-button dispatch is now carried entirely by the island JSON.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, attrs, post) = Render(helper);

        StringAssert.Contains(attrs, "data-wtm-grid-id=wtTable_O1");
        StringAssert.Contains(post, "\"type\":\"renderGrid\"");
        Assert.IsFalse(post.Contains("table.render(wtTable_O1option)"));

        Assert.IsFalse(post.Contains("function wtToolBarFunc_wtTable_O1(obj)"), "Slice O2 retires the legacy toolbar dispatcher for island grids");
        Assert.IsFalse(post.Contains("id=\"wtToolBar_wtTable_O12\""), "Slice O2 retires the legacy toolbar laytpl <script> for island grids");
        StringAssert.Contains(post, "\"gridActions\":[");
        StringAssert.Contains(post, "\"toolbarHtml\":");
        StringAssert.Contains(post, "data-wtm-click=\\u0022toolbarButton\\u0022");
    }

    [TestMethod]
    public void ActionsMatrix_FlagOn_NoLaytplScriptBlocksEmitted()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        Assert.IsFalse(post.Contains("<script type=\"text / html\" id=\"wtToolBar_wtTable_O12\" >"), "Slice O2 retires the toolbar laytpl <script> for island grids");
        Assert.IsFalse(post.Contains("<script type=\"text/html\" id=\"wtToolBar_wtTable_O1\">"), "Slice O2 retires the row-button laytpl <script> for island grids");
        Assert.IsFalse(post.Contains("{{#"), "Slice O2 retires the laytpl {{# if }} row-visibility conditional for island grids");
        StringAssert.Contains(post, "\"tpl\":\"actionCol\"");
    }

    // ── Rich/aggregate/bool templet descriptor parity ───────────────────────

    [TestMethod]
    public void RichColumns_FlagOn_TempletDescriptorsCoverAllFiveVariants()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new RichColumnsListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var cols = doc.RootElement.GetProperty("cols")[0];
        // index 0/1 = checkbox/numbers, then the 5 rich columns in declaration order.
        var progress = cols[2].GetProperty("templet");
        Assert.AreEqual("progress", progress.GetProperty("tpl").GetString());
        Assert.AreEqual("LoginName", progress.GetProperty("field").GetString());

        var tag = cols[3].GetProperty("templet");
        Assert.AreEqual("tag", tag.GetProperty("tpl").GetString());
        Assert.AreEqual("blue", tag.GetProperty("tagColor").GetString());

        var image = cols[4].GetProperty("templet");
        Assert.AreEqual("image", image.GetProperty("tpl").GetString());
        Assert.AreEqual(48, image.GetProperty("imageSize").GetInt32());

        var currency = cols[5].GetProperty("templet");
        Assert.AreEqual("currency", currency.GetProperty("tpl").GetString());
        Assert.AreEqual("0.00", currency.GetProperty("currencyFormat").GetString());

        var currencyRow = cols[6].GetProperty("templet");
        Assert.AreEqual("currencyRow", currencyRow.GetProperty("tpl").GetString());
        Assert.AreEqual("Address", currencyRow.GetProperty("currencyCodeField").GetString());
    }

    [TestMethod]
    public void BoolColumn_FlagOn_TempletDescriptorIsBoolWithHasFormatTrue()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new BoolColumnListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var cols = doc.RootElement.GetProperty("cols")[0];
        var boolTemplet = cols[3].GetProperty("templet"); // checkbox,numbers,LoginName,IsValid
        Assert.AreEqual("bool", boolTemplet.GetProperty("tpl").GetString());
        Assert.IsTrue(boolTemplet.GetProperty("hasFormat").GetBoolean());
    }

    [TestMethod]
    public void AggregateColumns_FlagOn_DoneCarriesAggregateFieldsAndTotalRowTrue()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new AggregateColumnsListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.IsTrue(root.GetProperty("totalRow").GetBoolean());
        var aggFields = root.GetProperty("done").GetProperty("aggregateFields");
        Assert.AreEqual(1, aggFields.GetArrayLength());
        Assert.AreEqual("Price", aggFields[0].GetString());
    }

    // ── DoneFunc/CheckedFunc identifiers → carried into island (guarded names) ─

    [TestMethod]
    public void IdentifierDoneFuncAndCheckedFunc_FlagOn_CarriedIntoIslandAsGuardedNames()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new PlainColumnsListVM(), NewWtm());
        helper.DoneFunc = "myDoneHandler";
        helper.CheckedFunc = "myCheckedHandler";
        var (_, _, post) = Render(helper);

        StringAssert.Contains(post, "\"type\":\"renderGrid\"");
        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var done = doc.RootElement.GetProperty("done");
        Assert.AreEqual("myDoneHandler", done.GetProperty("doneFn").GetString());
        Assert.AreEqual("myCheckedHandler", done.GetProperty("checkedFn").GetString());
    }

    // ── Fallback-trigger matrix — each containment condition forces legacy ──

    [TestMethod]
    public void IsInSelector_FlagOn_FallsBackToLegacy_WithWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new PlainColumnsListVM(), NewWtm());
        helper.IsInSelector = true;
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""), "IsInSelector must fall back to legacy in O1");
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"));
        StringAssert.Contains(post, "table.render(wtTable_O1option)");
        StringAssert.Contains(post, "console.warn('[WTM] DataTableTagHelper #wtTable_O1:");
        StringAssert.Contains(post, "IsInSelector");
    }

    // Historical note: through O1/O2, UseLocalData was in the fallback-trigger
    // matrix below (this test asserted the SAME legacy-fallback-with-warn shape
    // as IsInSelector/EnableAnalysis/etc.). Slice O3 (comment 18118 §2 O3 point
    // 1) LIFTS that containment — a flag-ON UseLocalData grid now islandifies
    // via the `localData` island field instead. See
    // RenderGridLocalData470SliceO3Tests.cs for the full O3 coverage; the
    // assertion below replaces the retired one in place (same rationale as the
    // O2 ActionsMatrix_FlagOn_* rewrite above — a same-named test now asserting
    // the opposite of what ships would be confusing kept side-by-side).
    [TestMethod]
    public void UseLocalData_FlagOn_NoLongerFallsBackToLegacy_Islandifies()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new PlainColumnsListVM(), NewWtm());
        helper.UseLocalData = true;
        var (_, attrs, post) = Render(helper);

        Assert.IsTrue(post.Contains("\"type\":\"renderGrid\""), "#470 Slice O3: UseLocalData must islandify, not fall back");
        StringAssert.Contains(attrs, "data-wtm-grid-id=wtTable_O1");
        Assert.IsFalse(post.Contains("ff.LoadLocalData("), "the inline ff.LoadLocalData(...) call text must not be emitted on the island path");
        Assert.IsFalse(post.Contains("console.warn('[WTM] DataTableTagHelper #wtTable_O1:"), "an island-eligible grid must not emit the fallback console.warn");
        StringAssert.Contains(post, "\"localData\":[");
    }

    [TestMethod]
    public void EnableAnalysis_FlagOn_FallsBackToLegacy_WithWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new PlainColumnsListVM(), NewWtm());
        helper.EnableAnalysis = true;
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""));
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"));
        StringAssert.Contains(post, "wtmAnalysis.toggle(");
        StringAssert.Contains(post, "console.warn('[WTM] DataTableTagHelper #wtTable_O1:");
        StringAssert.Contains(post, "EnableAnalysis");
    }

    [TestMethod]
    public void NonIdentifierDoneFunc_FlagOn_FallsBackToLegacy_WithWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new PlainColumnsListVM(), NewWtm());
        helper.DoneFunc = "myObj.notAnIdentifier";
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""));
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"));
        // Issue #999 part (A): DoneFunc is now paren-wrapped — (DoneFunc)(res,curr,count)
        // — so a function-literal DoneFunc can't produce an unwrapped-IIFE SyntaxError
        // that would kill the whole enclosing <script> block. A bare identifier/dotted
        // expression like this fixture's still calls through fine: (myObj.notAnIdentifier)(...)
        // is exactly equivalent to myObj.notAnIdentifier(...).
        StringAssert.Contains(post, "(myObj.notAnIdentifier)(res,curr,count)");
        StringAssert.Contains(post, "console.warn('[WTM] DataTableTagHelper #wtTable_O1:");
        StringAssert.Contains(post, "DoneFunc");
    }

    [TestMethod]
    public void NonIdentifierCheckedFunc_FlagOn_FallsBackToLegacy_WithWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new PlainColumnsListVM(), NewWtm());
        helper.CheckedFunc = "myObj.notAnIdentifier()";
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""));
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"));
        StringAssert.Contains(post, "table.on('checkbox(wtTable_O1)',myObj.notAnIdentifier());");
        StringAssert.Contains(post, "console.warn('[WTM] DataTableTagHelper #wtTable_O1:");
        StringAssert.Contains(post, "CheckedFunc");
    }

    [TestMethod]
    public void NonIdentifierOnClickFunc_FlagOn_FallsBackToLegacy_WithWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new NonIdentifierOnClickListVM(), NewWtm());
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""));
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"));
        StringAssert.Contains(post, "table.render(wtTable_O1option)");
        StringAssert.Contains(post, "console.warn('[WTM] DataTableTagHelper #wtTable_O1:");
        StringAssert.Contains(post, "OnClickFunc");
        StringAssert.Contains(post, "myObj.notAnIdentifier()");
    }

    // ── XOR emission — dedicated cross-config assertion ──────────────────────

    [TestMethod]
    public void XorEmission_NeverBothIslandAndLegacyRender_AcrossConfigMatrix()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });

        void AssertXor(DataTableTagHelper helper, string label)
        {
            var (_, _, post) = Render(helper);
            var hasIsland = post.Contains("\"type\":\"renderGrid\"");
            var hasLegacy = post.Contains($"table.render({helper.Id}option)");
            Assert.AreNotEqual(hasIsland, hasLegacy, $"{label}: island XOR legacy must hold (exactly one true)");
        }

        AssertXor(CreateHelper(new PlainColumnsListVM(), NewWtm(), "wtTable_Xor1"), "eligible-default");
        AssertXor(CreateHelper(new RichColumnsListVM(), NewWtm(), "wtTable_Xor2"), "eligible-rich");

        var selectorHelper = CreateHelper(new PlainColumnsListVM(), NewWtm(), "wtTable_Xor3");
        selectorHelper.IsInSelector = true;
        AssertXor(selectorHelper, "fallback-IsInSelector");

        // #470 Slice O3: UseLocalData now islandifies (see
        // RenderGridLocalData470SliceO3Tests.cs) — XOR must still hold either
        // way (this helper doesn't assert WHICH branch is taken, only that
        // exactly one is), so the label is updated for accuracy but the shared
        // AssertXor body is unchanged.
        var localDataHelper = CreateHelper(new PlainColumnsListVM(), NewWtm(), "wtTable_Xor4");
        localDataHelper.UseLocalData = true;
        AssertXor(localDataHelper, "island-UseLocalData");
    }

    // ── TreeContainer island-aware probe (invariant/brief §2 point 3) ────────
    // Exercises TreeContainerTagHelper's new island-then-legacy probe (see
    // TreeContainerTagHelper.cs / LayUiRegexes.IslandGridIdRegex) directly,
    // simulating what a nested island-rendered DataTableTagHelper's <table>
    // markup looks like: `data-wtm-grid-id="wtTable_Foo"` present, and NO
    // legacy "{gridid}option = {" text at all. Only TreeContainer's OWN
    // island-branch clickMode analysis gets the probe (not its legacy
    // branch) — see the comment in TreeContainerTagHelper.cs for why a
    // mirrored probe in the legacy branch would be unreachable dead code
    // (ClickFunc-driven legacy fallback always takes the custom-click path
    // before ever reaching the grid-detection code).

    private static TagHelperOutput MakeTreeContainerOutputWithChildContent(string rawChildHtml)
        => new("div", new TagHelperAttributeList(),
               (_, __) =>
               {
                   var content = new DefaultTagHelperContent();
                   content.SetHtmlContent(rawChildHtml);
                   return Task.FromResult<TagHelperContent>(content);
               });

    private static ModelExpression MakeItemsField(List<TreeSelectListItem> items)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(TreeContainerDummyModel).GetProperty(nameof(TreeContainerDummyModel.Items))!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(TreeContainerDummyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, items);
        return new ModelExpression(nameof(TreeContainerDummyModel.Items), modelExplorer);
    }

    private sealed class TreeContainerDummyModel
    {
        public List<TreeSelectListItem>? Items { get; set; }
    }

    [TestMethod]
    public async Task TreeContainer_FlagOn_IslandGrid_IslandBranch_ResolvesGridModeInJson()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TreeContainerTagHelper
        {
            Id = "tc_o1_2",
            // No ClickFunc — TreeContainer itself is island-eligible too.
            Items = MakeItemsField([new TreeSelectListItem { Value = "v1", Text = "Node 1" }]),
        };
        var childHtml = @"<table id=""wtTable_Nested2"" lay-filter=""wtTable_Nested2"" data-wtm-grid-id=""wtTable_Nested2""></table>";
        var output = MakeTreeContainerOutputWithChildContent(childHtml);
        await helper.ProcessAsync(new TagHelperContext("wt:treecontainer", new TagHelperAttributeList(), new Dictionary<object, object>(), "test-id"), output);
        var content = output.Content.GetContent();

        var json = ExtractJsonFromIsland(content, "renderTreeContainer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("grid", doc.RootElement.GetProperty("clickMode").GetString());
        Assert.AreEqual("wtTable_Nested2", doc.RootElement.GetProperty("gridId").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("gridExtendWhere").GetBoolean());
    }
}
