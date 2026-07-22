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
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice O3 — LAST grid slice: opt-in (WtmUIOptions.UseSelectIslandRender,
/// default OFF — the SAME flag Slices J-O2 use) grid <c>localData</c> island +
/// delegated cell-change + <c>foldPanel</c> island, completing the grid
/// islandification campaign. Design authority: Gitea issue #470 comment 18118
/// ("Slice O design brief", §2 O3).
///
/// Reuses the SAME <c>CreateHelper</c>/<c>MakeContext</c>/<c>MakeOutput</c>/
/// <c>SetupLocalizer</c>/<c>ExtractJsonFromIsland</c> shapes as
/// <see cref="RenderGridIsland470SliceO1Tests"/> / <see cref="RenderGridToolbar470SliceO2Tests"/>,
/// and the SAME public ListVM fixtures those files (and
/// <see cref="DataTableByteIdentityTests"/>) define — same namespace, no extra
/// using needed. Never touches the DataTableByteIdentityTests fixtures/tests —
/// its UseLocalData config pins flag-OFF (legacy EscapeLocalDataJson path),
/// which O3 leaves completely untouched.
/// </summary>
[TestClass]
public class RenderGridLocalData470SliceO3Tests
{
    /// <summary>
    /// One editable-looking column: SetFormat returns an <c>&lt;input&gt;</c>
    /// HTML string (the shape a detail-grid cell-editing column produces),
    /// exercising the localData row-JSON payload with realistic cell markup.
    /// </summary>
    public class LocalDataListVM : BasePagedListVM<Student, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
        {
            return new List<GridColumn<Student>>
            {
                this.MakeGridHeader(x => x.LoginName)
                    .SetFormat((entity, val) => $"<input value='{val}' />"),
            };
        }
    }

    /// <summary>
    /// Same shape as <see cref="LocalDataListVM"/>, but the formatted cell value
    /// embeds a <c>&lt;/script&gt;</c> substring — proves the O3 island path
    /// protects against the SAME script-breakout the legacy path's
    /// EscapeLocalDataJson (#490) protects against, via a different mechanism
    /// (LayuiIslandJson.Serialize's default HTML-safe JsonSerializer encoder,
    /// not a bespoke Unicode-escape helper).
    /// </summary>
    public class LocalDataXssListVM : BasePagedListVM<Student, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
        {
            return new List<GridColumn<Student>>
            {
                this.MakeGridHeader(x => x.LoginName)
                    .SetFormat((entity, val) => "</script><script>alert(1)</script>"),
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

    /// <summary>Fresh, seeded DataContext with exactly one Student row.</summary>
    private static DataContext NewSeededDc()
    {
        var dc = new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        dc.Set<Student>().Add(new Student { LoginName = "o3student", Password = "p", Name = "O3 Student", IsValid = true });
        dc.SaveChanges();
        return dc;
    }

    private static DataTableTagHelper CreateHelper<TModel, TSearcher>(
        BasePagedListVM<TModel, TSearcher> listVm,
        WTMContext wtm,
        string id = "wtTable_O3")
        where TModel : TopBasePoco
        where TSearcher : BaseSearcher
    {
        listVm.Wtm = wtm;
        listVm.ViewDivId = "FixedViewDivO3";
        return new DataTableTagHelper
        {
            Vm = MakeVm(listVm),
            Id = id,
            SearchPanelId = "wtForm_O3",
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
    /// RenderGridIsland470SliceO1Tests/RenderGridToolbar470SliceO2Tests. Finds
    /// the Nth occurrence of the marker (0-based) so callers can disambiguate
    /// between multiple same-typed islands on one page (renderGrid vs foldPanel
    /// are different types, so occurrence 0 is always sufficient here, but the
    /// parameter is kept for symmetry/robustness).
    /// </summary>
    private static string? ExtractJsonFromIsland(string html, string needleType, int occurrence = 0)
    {
        var marker = "\"type\":\"" + needleType + "\"";
        var searchFrom = 0;
        for (var n = 0; n <= occurrence; n++)
        {
            var idx = html.IndexOf(marker, searchFrom, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            if (n == occurrence)
            {
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
            searchFrom = idx + marker.Length;
        }
        return null;
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ── Invariant 1 (byte-identity): UseLocalData flag-OFF is DataTableByteIdentityTests'
    // job (DataTableByteIdentityTests.UseLocalData_FlagOff_ByteIdentical), and that
    // fixture/test class is NEVER touched by this slice. No duplicate test here —
    // this class only ever flips UseSelectIslandRender ON.

    // ── UseLocalData containment LIFTED — islandifies instead of falling back ──

    [TestMethod]
    public void UseLocalData_FlagOn_EmitsRenderGridIslandWithLocalDataArray_NoInlineLoadLocalDataCall()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var wtm = MockWtmContext.CreateWtmContext(NewSeededDc());
        var helper = CreateHelper(new LocalDataListVM(), wtm);
        helper.UseLocalData = true;
        var (_, attrs, post) = Render(helper);

        Assert.IsTrue(post.Contains("\"type\":\"renderGrid\""), "#470 Slice O3: UseLocalData must islandify, not fall back to legacy");
        StringAssert.Contains(attrs, "data-wtm-grid-id=wtTable_O3");
        Assert.IsFalse(post.Contains("ff.LoadLocalData("), "the inline ff.LoadLocalData(...) call text must not be emitted on the island path");
        Assert.IsFalse(post.Contains("console.warn('[WTM] DataTableTagHelper #wtTable_O3:"), "an island-eligible grid must not emit the fallback console.warn");

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.IsTrue(root.TryGetProperty("localData", out var localData), "renderGrid island must carry a localData field when UseLocalData=true");
        Assert.AreEqual(JsonValueKind.Array, localData.ValueKind);
        Assert.AreEqual(1, localData.GetArrayLength(), "seeded with exactly one Student row");
        var row = localData[0];
        StringAssert.Contains(row.GetProperty("LoginName").GetString(), "o3student");
        Assert.AreEqual(1, root.GetProperty("limit").GetInt32(), "limit mirrors the legacy entityCount formula (overwritten to 9999 client-side by ff.LoadLocalData regardless)");
    }

    [TestMethod]
    public void UseLocalData_FlagOn_LocalDataFieldIsNotABareTopLevelArrayOrNamedActions()
    {
        // #470 Slice O2 incident guard (comment 18118 §2 O2/O3 critical fix):
        // ff._normalizeIslandPayload classifies a payload as a BATCH the moment
        // Array.isArray(parsed.actions) is true — the renderGrid payload itself
        // must never be a bare array, and no field may be (re)named `actions`.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var wtm = MockWtmContext.CreateWtmContext(NewSeededDc());
        var helper = CreateHelper(new LocalDataListVM(), wtm);
        helper.UseLocalData = true;
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind, "the whole renderGrid payload must be an OBJECT, never a bare array");
        Assert.IsFalse(doc.RootElement.TryGetProperty("actions", out _), "must never (re)introduce a top-level `actions` field");
        StringAssert.Contains(post, "\"localData\":[");
    }

    [TestMethod]
    public void UseLocalData_FlagOn_ScriptBreakoutValueIsSafelyEncoded_NoLiteralCloseScript()
    {
        // Parity with the legacy path's #490 EscapeLocalDataJson protection —
        // achieved here via LayuiIslandJson.Serialize's default (no Encoder
        // override) JsonSerializer, which HTML-escapes '<'/'>'/'&' automatically,
        // so a </script> substring inside row data can never break out of the
        // island's own <script type="application/json"> element.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var wtm = MockWtmContext.CreateWtmContext(NewSeededDc());
        var helper = CreateHelper(new LocalDataXssListVM(), wtm);
        helper.UseLocalData = true;
        var (_, _, post) = Render(helper);

        Assert.IsFalse(post.Contains("</script><script>alert(1)</script>"), "the raw script-breakout substring must never appear literally in the emitted HTML");

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var row = doc.RootElement.GetProperty("localData")[0];
        Assert.AreEqual("</script><script>alert(1)</script>", row.GetProperty("LoginName").GetString(),
            "round-trips to the exact original value once JSON-decoded — only the WIRE encoding changed, never the value");
    }

    [TestMethod]
    public void UseLocalData_WithDetailGridPrix_FlagOn_CarriesDetailGridPrixOnIsland()
    {
        // The "Major Edit" detail-grid scenario (brief §6): UseLocalData +
        // DetailGridPrix together — ff._renderGridAction derives isNormalTable
        // from action.detailGridPrix (present => NOT a normal table => the
        // grid-cell family's onchange/data-wtm-cellchange injection applies).
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var wtm = MockWtmContext.CreateWtmContext(NewSeededDc());
        var listVm = new LocalDataListVM { DetailGridPrix = "Detail_470O3" };
        var helper = CreateHelper(listVm, wtm);
        helper.UseLocalData = true;
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "renderGrid");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("Detail_470O3", doc.RootElement.GetProperty("detailGridPrix").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("localData", out _));
    }

    // ── UseLocalData no longer forces legacy — but EnableAnalysis/IsInSelector
    // (out of O3 scope) still do ─────────────────────────────────────────────

    [TestMethod]
    public void EnableAnalysis_WithUseLocalData_FlagOn_StillFallsBackToLegacy_WithWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var wtm = MockWtmContext.CreateWtmContext(NewSeededDc());
        var helper = CreateHelper(new LocalDataListVM(), wtm);
        helper.UseLocalData = true;
        helper.EnableAnalysis = true;
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""), "EnableAnalysis stays out of O3 scope and must still force legacy");
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"));
        StringAssert.Contains(post, "ff.LoadLocalData(");
        StringAssert.Contains(post, "console.warn('[WTM] DataTableTagHelper #wtTable_O3:");
        StringAssert.Contains(post, "EnableAnalysis");
    }

    [TestMethod]
    public void IsInSelector_WithUseLocalData_FlagOn_StillFallsBackToLegacy_WithWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var wtm = MockWtmContext.CreateWtmContext(NewSeededDc());
        var helper = CreateHelper(new LocalDataListVM(), wtm);
        helper.UseLocalData = true;
        helper.IsInSelector = true;
        var (_, attrs, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"renderGrid\""), "IsInSelector stays out of O3 scope and must still force legacy");
        Assert.IsFalse(attrs.Contains("data-wtm-grid-id"));
        StringAssert.Contains(post, "ff.LoadLocalData(");
        StringAssert.Contains(post, "console.warn('[WTM] DataTableTagHelper #wtTable_O3:");
        // IsInSelector is checked first in DetermineGridIslandDecision, so that's
        // the reported fallback reason even though UseLocalData is also true.
        StringAssert.Contains(post, "IsInSelector");
    }

    // ── foldPanel island (SearcherExpanded) ─────────────────────────────────

    [TestMethod]
    public void SearcherExpanded_FlagOn_EmitsFoldPanelIsland_NotInlineScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var helper = CreateHelper(new LocalDataListVM(), wtm);
        helper.SearcherExpanded = true;
        var (_, _, post) = Render(helper);

        Assert.IsFalse(post.Contains("layui.use(['element'], function() {"), "the inline fold <script> text must not be emitted on the island path");
        var json = ExtractJsonFromIsland(post, "foldPanel");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("wtForm_O3", doc.RootElement.GetProperty("searchPanelId").GetString());
        // SearcherExpanded=true => expanded => fold:false (mirrors legacy foldBool).
        Assert.IsFalse(doc.RootElement.GetProperty("fold").GetBoolean());
    }

    [TestMethod]
    public void SearcherExpandedFalse_FlagOn_FoldPanelIslandFoldIsTrue()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var helper = CreateHelper(new LocalDataListVM(), wtm);
        helper.SearcherExpanded = false;
        var (_, _, post) = Render(helper);

        var json = ExtractJsonFromIsland(post, "foldPanel");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsTrue(doc.RootElement.GetProperty("fold").GetBoolean());
    }

    [TestMethod]
    public void SearcherExpandedNotSet_FlagOn_NoFoldPanelIslandEmitted()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var helper = CreateHelper(new LocalDataListVM(), wtm);
        var (_, _, post) = Render(helper);

        Assert.IsFalse(post.Contains("\"type\":\"foldPanel\""));
    }

    // ── XOR: renderGrid island XOR legacy render, across the UseLocalData axis ─

    [TestMethod]
    public void XorEmission_RenderGridIslandXorLegacyRender_AcrossUseLocalDataMatrix()
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

        var plain = CreateHelper(new LocalDataListVM(), MockWtmContext.CreateWtmContext(NewSeededDc()), "wtTable_O3Xor1");
        plain.UseLocalData = true;
        AssertXor(plain, "island-UseLocalData");

        var withAnalysis = CreateHelper(new LocalDataListVM(), MockWtmContext.CreateWtmContext(NewSeededDc()), "wtTable_O3Xor2");
        withAnalysis.UseLocalData = true;
        withAnalysis.EnableAnalysis = true;
        AssertXor(withAnalysis, "fallback-EnableAnalysis-with-UseLocalData");

        var withSelector = CreateHelper(new LocalDataListVM(), MockWtmContext.CreateWtmContext(NewSeededDc()), "wtTable_O3Xor3");
        withSelector.UseLocalData = true;
        withSelector.IsInSelector = true;
        AssertXor(withSelector, "fallback-IsInSelector-with-UseLocalData");
    }
}
