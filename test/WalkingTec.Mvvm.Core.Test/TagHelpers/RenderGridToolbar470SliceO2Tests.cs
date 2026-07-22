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
/// Issue #470 Slice O2: opt-in (WtmUIOptions.UseSelectIslandRender, default OFF —
/// the SAME flag Slices J-O1 use) eval-free grid toolbar/row-button descriptor +
/// tool dispatch, completing the toolbar/row-button islandification O1 deferred
/// (invariant 7). Design authority: internal infrastructure issue #470 comment 18118 ("Slice O
/// design brief", §2 O2).
///
/// Reuses the SAME <c>CreateHelper</c>/<c>MakeContext</c>/<c>MakeOutput</c>/
/// <c>SetupLocalizer</c> shapes as <see cref="DataTableByteIdentityTests"/> (O1
/// stage 1) and <see cref="RenderGridIsland470SliceO1Tests"/> (O1 stage 2), and
/// the SAME public ListVM fixtures those files define (<see cref="PlainColumnsListVM"/>,
/// <see cref="ActionsMatrixListVM"/>) — same namespace, no extra using needed.
/// Never touches the DataTableByteIdentityTests fixtures or its flag-OFF tests.
/// </summary>
[TestClass]
public class RenderGridToolbar470SliceO2Tests
{
    // A GridAction whose OnClickFunc is a non-identifier expression — forces the
    // whole grid to the legacy path (mirrors RenderGridIsland470SliceO1Tests's
    // NonIdentifierOnClickListVM; redefined locally to keep this file
    // self-contained).
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
                    Area = "ActBadO2",
                    Name = "BadO2",
                    ParameterType = GridActionParameterTypesEnum.SingleId,
                    OnClickFunc = "myObj.notAnIdentifierO2()",
                },
            };
        }
    }

    // Exercises the ShowInRow==true + BindVisiableColName combination —
    // ActionsMatrixListVM's own BindVisiableColName-carrying action (ActEdit)
    // has ShowInRow==false, so BindVisiableColName is inert there in BOTH the
    // legacy (AddSubButton nests the whole visibility conditional inside
    // `if (item.ShowInRow)`) and island paths — this fixture exercises the
    // combination where it actually matters.
    public class RowVisibilityActionListVM : BasePagedListVM<Student, BaseSearcher>
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
                    Area = "ActRowVisible",
                    Name = "RowVisible",
                    ParameterType = GridActionParameterTypesEnum.SingleId,
                    ShowInRow = true,
                    HideOnToolBar = true,
                    BindVisiableColName = "IsValid",
                    ButtonClass = "layui-btn-normal",
                },
            };
        }
    }

    // Exercises whereStr + a whereStr-driven SingleId action, distinct from
    // ActionsMatrixListVM (which never sets whereStr on any action).
    public class WhereStrActionListVM : BasePagedListVM<Student, BaseSearcher>
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
                    Area = "ActWhereStr",
                    Name = "WhereStrAction",
                    ParameterType = GridActionParameterTypesEnum.SingleId,
                    whereStr = new[] { "LoginName", "Email" },
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
        string id = "wtTable_O2")
        where TModel : TopBasePoco
        where TSearcher : BaseSearcher
    {
        listVm.Wtm = wtm;
        listVm.ViewDivId = "FixedViewDivO2";
        return new DataTableTagHelper
        {
            Vm = MakeVm(listVm),
            Id = id,
            SearchPanelId = "wtForm_O2",
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
    /// Brace-matching JSON extractor — same pattern as RenderGridIsland470SliceO1Tests.
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

    // ── Flag OFF sanity — O2 code is completely dormant ─────────────────────

    [TestMethod]
    public void ActionsMatrix_FlagOff_NoActionsArrayNoDataWtmClick()
    {
        SetupLocalizer();
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"gridActions\":["));
        Assert.IsFalse(post.Contains("data-wtm-click"));
        Assert.IsFalse(post.Contains("\"tpl\":\"actionCol\""));
        StringAssert.Contains(post, "function wtToolBarFunc_wtTable_O2(obj)");
    }

    // ── Island grids: actions[] + actionCol tpl + data-wtm-click, no legacy ──

    [TestMethod]
    public void ActionsMatrix_FlagOn_NoInlineToolBarFuncOrLaytplConditional()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        Assert.IsFalse(post.Contains("wtToolBarFunc_"), "island grids must not emit the legacy inline dispatcher");
        Assert.IsFalse(post.Contains("{{#"), "island grids must not emit the laytpl {{# if }} conditional");
        Assert.IsFalse(post.Contains("<script type=\"text / html\""), "island grids must not emit the toolbar laytpl <script>");
        Assert.IsFalse(post.Contains("<script type=\"text/html\""), "island grids must not emit the row-button laytpl <script>");
        StringAssert.Contains(post, "\"gridActions\":[");
        StringAssert.Contains(post, "\"tpl\":\"actionCol\"");
        StringAssert.Contains(post, "data-wtm-click=\\u0022toolbarButton\\u0022");
    }

    /// <summary>
    /// CRITICAL FIX regression pin (review-caught, real-browser confirmed): the
    /// renderGrid island's descriptor-array JSON key MUST be <c>gridActions</c>,
    /// never <c>actions</c>. A field literally named <c>actions</c> collides
    /// with ff._normalizeIslandPayload's <c>Array.isArray(parsed.actions)</c>
    /// batch-shape probe (framework_layui.js) — added to recognize the
    /// pre-existing BATCH island shape {"actions":[{type},...]} emitted by
    /// DialogInitTagHelper/FormTagHelper — so a renderGrid payload carrying its
    /// OWN `actions` field is silently misclassified as already-batched and
    /// `case 'renderGrid'` in ff.DispatchAction is NEVER reached: the grid
    /// never renders, with no exception and nothing in the console/logs. This
    /// pins the C#/JS contract on the SERVER side; the JS regression test
    /// exercising the real ff._normalizeIslandPayload -> ff.DispatchAction
    /// chain lives in framework_layui_470_sliceO2_grid_toolbar.test.js (see
    /// the "real page-ready entry point" describe block there).
    /// </summary>
    [TestMethod]
    public void ActionsMatrix_FlagOn_IslandJsonUsesGridActionsKeyNotActions_ContractPin()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);

        Assert.IsTrue(doc.RootElement.TryGetProperty("gridActions", out var gridActions),
            "renderGrid island JSON must carry its descriptor array under the 'gridActions' key");
        Assert.IsTrue(gridActions.GetArrayLength() > 0);
        Assert.IsFalse(doc.RootElement.TryGetProperty("actions", out _),
            "renderGrid island JSON must NEVER carry a top-level 'actions' key — it collides with " +
            "ff._normalizeIslandPayload's batch-shape probe and silently kills grid rendering (see class doc)");
    }

    [TestMethod]
    public void ActionsMatrix_FlagOn_ActionColumnCarriesActionColTempletNoToolbarSelector()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var cols = doc.RootElement.GetProperty("cols")[0];
        var actionCol = cols[cols.GetArrayLength() - 1];
        Assert.AreEqual("actionCol", actionCol.GetProperty("templet").GetProperty("tpl").GetString());
        Assert.IsFalse(actionCol.TryGetProperty("toolbar", out _), "actionCol must not also carry the legacy `toolbar` selector field");
    }

    [TestMethod]
    public void ActionsMatrix_FlagOn_AllSevenParameterTypesRepresentedInActions()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var actions = doc.RootElement.GetProperty("gridActions");
        var paramTypes = actions.EnumerateArray().Select(a => a.GetProperty("paramType").GetString()).ToHashSet();

        CollectionAssert.Contains(paramTypes.ToList(), "noId");
        CollectionAssert.Contains(paramTypes.ToList(), "singleId");
        CollectionAssert.Contains(paramTypes.ToList(), "multiIds");
        CollectionAssert.Contains(paramTypes.ToList(), "singleIdWithNull");
        CollectionAssert.Contains(paramTypes.ToList(), "multiIdWithNull");
        CollectionAssert.Contains(paramTypes.ToList(), "addRow");
        CollectionAssert.Contains(paramTypes.ToList(), "removeRow");
    }

    [TestMethod]
    public void ActionsMatrix_FlagOn_ShowDialogActionCarriesDialogFieldsAndGuid()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        var edit = doc.RootElement.GetProperty("gridActions").EnumerateArray().First(a => a.GetProperty("event").GetString() == "ActEdit");

        Assert.IsTrue(edit.GetProperty("showDialog").GetBoolean());
        Assert.AreEqual(700, edit.GetProperty("dialogWidth").GetInt32());
        Assert.AreEqual(500, edit.GetProperty("dialogHeight").GetInt32());
        Assert.AreEqual("Edit Item", edit.GetProperty("dialogTitle").GetString());
        Assert.IsTrue(edit.TryGetProperty("dialogGuid", out var guid));
        StringAssert.Matches(guid.GetString(), new System.Text.RegularExpressions.Regex("^[0-9a-f]{32}$"));
        // ActEdit's BindVisiableColName is inert (ShowInRow == false on this
        // fixture — see RowVisibilityAction_FlagOn_VisibleFieldAndShowInRowCarried
        // for the combination where visibleField/showInRow actually populate).
        Assert.IsFalse(edit.GetProperty("showInRow").GetBoolean());
    }

    [TestMethod]
    public void RowVisibilityAction_FlagOn_VisibleFieldAndShowInRowCarried()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new RowVisibilityActionListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        var action = doc.RootElement.GetProperty("gridActions")[0];
        Assert.IsTrue(action.GetProperty("showInRow").GetBoolean());
        Assert.AreEqual("IsValid", action.GetProperty("visibleField").GetString());
        Assert.AreEqual("layui-btn-normal", action.GetProperty("class").GetString());
        Assert.AreEqual("RowVisible", action.GetProperty("name").GetString());
        Assert.IsFalse(action.GetProperty("removeRow").GetBoolean());
        // HideOnToolBar == true on this fixture — no toolbar markup at all.
        Assert.IsFalse(post.Contains("data-wtm-click=\\u0022toolbarButton\\u0022"));
    }

    [TestMethod]
    public void ActionsMatrix_FlagOn_PromptMessageAndDownloadCarried()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        var batchDelete = doc.RootElement.GetProperty("gridActions").EnumerateArray().First(a => a.GetProperty("event").GetString() == "ActBatchDelete");

        Assert.AreEqual("Are you sure you want to delete these rows?", batchDelete.GetProperty("prompt").GetString());
        Assert.IsTrue(batchDelete.GetProperty("download").GetBoolean());
        Assert.AreEqual("multiIds", batchDelete.GetProperty("paramType").GetString());
    }

    [TestMethod]
    public void ActionsMatrix_FlagOn_RedirectAndExportCarried()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        var actions = doc.RootElement.GetProperty("gridActions");

        var details = actions.EnumerateArray().First(a => a.GetProperty("event").GetString() == "ActDetails");
        Assert.IsTrue(details.GetProperty("redirect").GetBoolean());

        var export = actions.EnumerateArray().First(a => a.GetProperty("event").GetString() == "ActExport");
        Assert.IsTrue(export.GetProperty("export").GetBoolean());

        var forcePost = actions.EnumerateArray().First(a => a.GetProperty("event").GetString() == "ActForcePost");
        Assert.IsTrue(forcePost.GetProperty("forcePost").GetBoolean());
    }

    [TestMethod]
    public void ActionsMatrix_FlagOn_OnClickFuncCarriedAsGuardedIdentifier()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        var onClick = doc.RootElement.GetProperty("gridActions").EnumerateArray().First(a => a.GetProperty("event").GetString() == "ActOnClick");
        Assert.AreEqual("myGridOnClickHandler", onClick.GetProperty("onClickFn").GetString());
    }

    [TestMethod]
    public void ActionsMatrix_FlagOn_AddRowCarriesRowJsonObject()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        var addRow = doc.RootElement.GetProperty("gridActions").EnumerateArray().First(a => a.GetProperty("event").GetString() == "ActAddRow");
        Assert.AreEqual("addRow", addRow.GetProperty("paramType").GetString());
        var addRowJson = addRow.GetProperty("addRowJson");
        Assert.AreEqual(JsonValueKind.Object, addRowJson.ValueKind);
        Assert.IsTrue(addRowJson.TryGetProperty("ID", out _));
        Assert.IsTrue(addRowJson.TryGetProperty("LoginName", out _));
    }

    [TestMethod]
    public void ActionsMatrix_FlagOn_RemoveRowIsShowInRowAndHiddenFromToolbar()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        var removeRow = doc.RootElement.GetProperty("gridActions").EnumerateArray().First(a => a.GetProperty("event").GetString() == "ActRemoveRow");
        Assert.IsTrue(removeRow.GetProperty("removeRow").GetBoolean());
        Assert.IsTrue(removeRow.GetProperty("showInRow").GetBoolean());
        Assert.AreEqual("removeRow", removeRow.GetProperty("paramType").GetString());

        // HideOnToolBar == true on this fixture's RemoveRow action, so its
        // event must never appear in a data-wtm-event toolbar attribute.
        Assert.IsFalse(post.Contains("data-wtm-event=\\u0022ActRemoveRow\\u0022"));
    }

    [TestMethod]
    public void ActionsMatrix_FlagOn_GroupSubActionsPresentGroupContainerHasNoOwnDispatchEntry()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        var events = doc.RootElement.GetProperty("gridActions").EnumerateArray()
            .Select(a => a.GetProperty("event").GetString()).ToList();

        CollectionAssert.Contains(events, "GroupSub1");
        CollectionAssert.Contains(events, "GroupSub2");
        // "Batch Group" (the ActionsGroup container itself) has no ControllerName/
        // ActionName/Area combination named after it — it never gets an event of
        // its own; the group markup lives only in toolbarHtml (data-wtm-btngroup).
        StringAssert.Contains(post, "data-wtm-btngroup=\\u00221\\u0022");
        StringAssert.Contains(post, "id=\\u0022btn_fixed-group-btn-01\\u0022");
    }

    [TestMethod]
    public void WhereStrAction_FlagOn_WhereStrArrayCarried()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new WhereStrActionListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        var action = doc.RootElement.GetProperty("gridActions")[0];
        var whereStr = action.GetProperty("whereStr");
        Assert.AreEqual(2, whereStr.GetArrayLength());
        Assert.AreEqual("LoginName", whereStr[0].GetString());
        Assert.AreEqual("Email", whereStr[1].GetString());
    }

    // ── actionMsgs (localized selection-guard/prompt strings), hoisted once ──

    [TestMethod]
    public void ActionsMatrix_FlagOn_ActionMsgsPresentWhenActionsExist()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        var msgs = doc.RootElement.GetProperty("actionMsgs");
        Assert.AreEqual("Sys.SelectOneRow", msgs.GetProperty("selectOneRow").GetString());
        Assert.AreEqual("Sys.SelectOneRowMax", msgs.GetProperty("selectOneRowMax").GetString());
        Assert.AreEqual("Sys.SelectOneRowMin", msgs.GetProperty("selectOneRowMin").GetString());
        Assert.AreEqual("Sys.Info", msgs.GetProperty("infoTitle").GetString());
    }

    [TestMethod]
    public void PlainColumns_FlagOn_NoActionsNoActionMsgsNoToolbarHtml()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new PlainColumnsListVM(), NewWtm());
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        using var doc = JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("gridActions", out _));
        Assert.IsFalse(doc.RootElement.TryGetProperty("actionMsgs", out _));
        Assert.IsFalse(doc.RootElement.TryGetProperty("toolbarHtml", out _));
    }

    // ── Legacy fallback grids keep the FULL legacy toolbar (invariant 2) ─────

    [TestMethod]
    public void ActionsMatrix_IsInSelector_FlagOn_KeepsFullLegacyToolbar()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        helper.IsInSelector = true;
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""));
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"));
        StringAssert.Contains(post, "function wtToolBarFunc_wtTable_O2(obj)");
        StringAssert.Contains(post, "id=\"wtToolBar_wtTable_O22\"");
        StringAssert.Contains(post, "onclick=\"wtToolBarFunc_wtTable_O2({event:'ActImport'}");
        Assert.IsFalse(post.Contains("data-wtm-click"));
    }

    // Historical note: through O1/O2, UseLocalData forced the whole grid
    // (toolbar included) to legacy — this test asserted that. Slice O3 (comment
    // 18118 §2 O3 point 1) lifts the UseLocalData containment entirely, so an
    // ActionsMatrixListVM grid with UseLocalData=true now gets BOTH the O2
    // island toolbar AND the O3 localData island field. See
    // RenderGridLocalData470SliceO3Tests.cs for the dedicated O3 coverage; the
    // assertion below replaces the retired one in place (same rationale as the
    // other ActionsMatrix_FlagOn_* rewrites in this file/RenderGridIsland470SliceO1Tests.cs).
    [TestMethod]
    public void ActionsMatrix_UseLocalData_FlagOn_GetsIslandToolbarAndLocalData()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        helper.UseLocalData = true;
        var (_, attrs, post) = Render(helper);

        Assert.IsTrue(post.Contains("\"type\":\"renderGrid\""));
        StringAssert.Contains(attrs, "data-wtm-grid-id=wtTable_O2");
        Assert.IsFalse(post.Contains("function wtToolBarFunc_wtTable_O2(obj)"));
        StringAssert.Contains(post, "\"gridActions\":[");
        StringAssert.Contains(post, "\"toolbarHtml\":");
        StringAssert.Contains(post, "\"localData\":[");
    }

    [TestMethod]
    public void ActionsMatrix_EnableAnalysis_FlagOn_KeepsFullLegacyToolbarWithAnalysisButton()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new ActionsMatrixListVM(), NewWtm());
        helper.EnableAnalysis = true;
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""));
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"));
        StringAssert.Contains(post, "function wtToolBarFunc_wtTable_O2(obj)");
        StringAssert.Contains(post, "wtmAnalysis.toggle('wtTable_O2',");
        Assert.IsFalse(post.Contains("data-wtm-click"));
    }

    [TestMethod]
    public void NonIdentifierOnClickFunc_FlagOn_KeepsFullLegacyToolbar()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper(new NonIdentifierOnClickListVM(), NewWtm());
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""));
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"));
        StringAssert.Contains(post, "function wtToolBarFunc_wtTable_O2(obj)");
        StringAssert.Contains(post, "myObj.notAnIdentifierO2()(ids,ff.GetSelectionData('wtTable_O2'));");
        Assert.IsFalse(post.Contains("data-wtm-click"));
    }

    // ── XOR: island-toolbar XOR legacy-toolbar per grid ──────────────────────

    [TestMethod]
    public void XorEmission_IslandToolbarXorLegacyToolbar_AcrossConfigMatrix()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });

        void AssertXor(DataTableTagHelper helper, string label)
        {
            var (_, _, post) = Render(helper);
            var hasIslandToolbar = post.Contains("\"gridActions\":[") || post.Contains("\"toolbarHtml\":");
            var hasLegacyToolbar = post.Contains($"function wtToolBarFunc_{helper.Id}(obj)");
            Assert.AreNotEqual(hasIslandToolbar, hasLegacyToolbar, $"{label}: island-toolbar XOR legacy-toolbar must hold (exactly one true)");
        }

        AssertXor(CreateHelper(new ActionsMatrixListVM(), NewWtm(), "wtTable_O2Xor1"), "eligible-actionsmatrix");

        var selectorHelper = CreateHelper(new ActionsMatrixListVM(), NewWtm(), "wtTable_O2Xor2");
        selectorHelper.IsInSelector = true;
        AssertXor(selectorHelper, "fallback-IsInSelector");

        // #470 Slice O3: UseLocalData now gets the island toolbar too (see
        // RenderGridLocalData470SliceO3Tests.cs) — XOR must still hold either
        // way, so only the label is updated for accuracy.
        var localDataHelper = CreateHelper(new ActionsMatrixListVM(), NewWtm(), "wtTable_O2Xor3");
        localDataHelper.UseLocalData = true;
        AssertXor(localDataHelper, "island-UseLocalData");

        var analysisHelper = CreateHelper(new ActionsMatrixListVM(), NewWtm(), "wtTable_O2Xor4");
        analysisHelper.EnableAnalysis = true;
        AssertXor(analysisHelper, "fallback-EnableAnalysis");

        AssertXor(CreateHelper(new NonIdentifierOnClickListVM(), NewWtm(), "wtTable_O2Xor5"), "fallback-NonIdentifierOnClickFunc");
    }
}
