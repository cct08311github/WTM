#nullable enable
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
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
/// Issue #470 Slice O1 stage 1 — the first-ever end-to-end
/// <see cref="DataTableTagHelper.Process"/> byte-identity test harness. This
/// class runs entirely against the UNMODIFIED (pre-island) code: it is the
/// mechanical flag-off guard the O1 island work (stage 2) must not disturb.
/// <see cref="DataTableTagHelperAnalysisTests"/> explicitly deferred exactly
/// this coverage ("Full integration test coverage is deferred to Task 9").
///
/// Pattern: <c>MockWtmContext.CreateWtmContext(new DataContext(seed,
/// DBTypeEnum.Memory))</c> + a real ListVM, per SelectorTagHelperTests.cs;
/// localizer mock + [TestCleanup] convention per
/// RenderSelectIsland470SliceJTests.cs. <c>Id</c>/<c>SearchPanelId</c> are
/// pinned via the TagHelper's public setters (which also pin the derived
/// ToolBarId/TableJSVar — see DataTableTagHelper.cs's Id/ToolBarId/
/// TableJSVar getters), and <see cref="IBaseVM.ViewDivId"/> is pinned via its
/// public setter, so <c>UniqueId</c> never leaks into any fixture.
///
/// Three components have NO public seam to pin (documented last-resort
/// regex normalization, per the #470 comment-18118 design brief §6):
///   A. The per-(fixed-group) `random` suffix
///      (<c>Guid.NewGuid().ToString().Replace("-","")</c>,
///      DataTableTagHelper.cs's generateColHeaderCore) embedded by
///      getTemplate() as <c>var did = '{field}{random}_'+d.LAY_INDEX;</c>.
///   B. The per-dialog-action instance id
///      (<c>Guid.NewGuid().ToNoSplitString()</c>, DataTableTagHelper.cs's
///      AddSubButton ShowDialog branch) passed as the second argument to
///      <c>ff.OpenDialog(tempUrl,'{guid}',...)</c>.
///   C. <c>BaseSearcher.UniqueId</c> (also a lazily-generated Guid with no
///      setter) is not excluded by DataTableTagHelper.cs's
///      BuildWhereFilter's <c>_excludeParams</c>/<c>_excludeTypes</c> lists,
///      so it is copied by reflection into the emitted <c>where:</c> JSON as
///      <c>"UniqueId":"{guid}"</c> (or <c>"Searcher.UniqueId":"{guid}"</c>
///      when IsInSelector) whenever <c>where</c> is non-empty (every config
///      except UseLocalData, which short-circuits <c>where</c> to empty).
/// All three are anchored to their exact known emission sites (see
/// <see cref="Normalize"/>) so the regex cannot silently swallow an
/// unrelated future change elsewhere in the emitted script.
/// </summary>
[TestClass]
public class DataTableByteIdentityTests
{
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

    /// <summary>
    /// Builds a DataTableTagHelper wired to <paramref name="listVm"/> with
    /// Id/SearchPanelId pinned to fixed strings (also pins the derived
    /// ToolBarId/TableJSVar — see DataTableTagHelper.Id's setter, which sets
    /// the private _gridIdUserSet field that ToolBarId/TableJSVar consult).
    /// ViewDivId is pinned separately since it is a BaseVM-level property,
    /// not a TagHelper attribute.
    /// </summary>
    private static DataTableTagHelper CreateHelper<TModel, TSearcher>(
        BasePagedListVM<TModel, TSearcher> listVm,
        WTMContext wtm)
        where TModel : TopBasePoco
        where TSearcher : BaseSearcher
    {
        listVm.Wtm = wtm;
        listVm.ViewDivId = "FixedViewDiv470O1";
        return new DataTableTagHelper
        {
            Vm = MakeVm(listVm),
            Id = "wtTable_Fixed470O1",
            SearchPanelId = "wtForm_Fixed470O1",
        };
    }

    /// <summary>
    /// Anchored, documented normalization for the three Guid-based
    /// components that have no public seam to pin (see class doc). Every
    /// regex is anchored to its exact known emission site so an unrelated
    /// future change cannot be silently swallowed by an over-broad match.
    /// </summary>
    private static string Normalize(string raw)
    {
        // Component A: DataTableTagHelper.cs's getTemplate() —
        //   var did = '{field}{random}_'+d.LAY_INDEX;
        // `random` is Guid.NewGuid().ToString().Replace("-","") — always
        // exactly 32 lowercase hex chars — generated once per
        // generateColHeaderCore call (i.e. shared by every plain/bool column
        // in the same fixed-group at the same header depth).
        raw = Regex.Replace(raw, "[0-9a-f]{32}_'\\+d\\.LAY_INDEX", "<<RANDOM>>_'+d.LAY_INDEX");

        // Component B: DataTableTagHelper.cs's AddSubButton ShowDialog
        // (non-redirect, no OnClickFunc) branch —
        //   ff.OpenDialog(tempUrl,'{guid}','{title}',...)
        // guid is Guid.NewGuid().ToNoSplitString() — 32 lowercase hex chars.
        raw = Regex.Replace(raw, "ff\\.OpenDialog\\(tempUrl,'[0-9a-f]{32}',", "ff.OpenDialog(tempUrl,'<<DIALOG_GUID>>',");

        // Component C: BaseSearcher.UniqueId — a lazily-generated Guid with
        // no setter, not excluded by BuildWhereFilter's
        // _excludeParams/_excludeTypes, so it is reflection-copied into the
        // emitted `where:` JSON as "UniqueId":"{guid}" (or
        // "Searcher.UniqueId":"{guid}" when IsInSelector).
        raw = Regex.Replace(raw, "\"(Searcher\\.)?UniqueId\":\"[0-9a-f]{32}\"", "\"$1UniqueId\":\"<<SEARCHER_UNIQUEID>>\"");

        return raw;
    }

    /// <summary>
    /// Renders <paramref name="helper"/> and captures everything
    /// DataTableTagHelper.Process() can touch on the TagHelperOutput: tag
    /// name, tag mode, attributes (id/lay-filter/subpro), PreElement (always
    /// empty today — captured anyway so a future silent addition fails loud)
    /// and PostElement (the monolithic &lt;script&gt; block + laytpl
    /// templates + trailing blocks). Normalized per <see cref="Normalize"/>.
    /// </summary>
    private static string RenderNormalized(DataTableTagHelper helper)
    {
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var sb = new StringBuilder();
        sb.Append("TAG:").Append(output.TagName).Append('|');
        sb.Append("MODE:").Append(output.TagMode).Append('|');
        sb.Append("ATTRS:");
        foreach (var attr in output.Attributes)
        {
            sb.Append(attr.Name).Append('=').Append(attr.Value).Append(';');
        }
        sb.Append("|PRE:").Append(output.PreElement.GetContent());
        sb.Append("|POST:").Append(output.PostElement.GetContent());

        return Normalize(sb.ToString());
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        // Defensive: DataTableTagHelper does not read WtmUIOptions yet (the
        // O1 island render — stage 2 of this slice — is the first consumer,
        // reusing WtmUIOptions.UseSelectIslandRender per the #470
        // comment-18118 brief §2). Resetting here now future-proofs this
        // harness for stage 2 and matches the Slice J
        // ([TestCleanup]-resets-process-wide-static-state) convention.
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ── Matrix configs (>=12 per the #470 comment-18118 design brief §6) ──

    [TestMethod]
    public void Default_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new PlainColumnsListVM();
        var helper = CreateHelper(listVm, wtm);

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.Default, actual);
    }

    [TestMethod]
    public void UseLocalData_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new PlainColumnsListVM();
        var helper = CreateHelper(listVm, wtm);
        helper.UseLocalData = true;

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.UseLocalData, actual);
    }

    [TestMethod]
    public void EnableHeaderFilter_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new PlainColumnsListVM();
        var helper = CreateHelper(listVm, wtm);
        helper.EnableHeaderFilter = true;

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.EnableHeaderFilter, actual);
    }

    [TestMethod]
    public void EnableAnalysis_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new PlainColumnsListVM();
        var helper = CreateHelper(listVm, wtm);
        helper.EnableAnalysis = true;

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.EnableAnalysis, actual);
    }

    [TestMethod]
    public void ActionsMatrix_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new ActionsMatrixListVM();
        var helper = CreateHelper(listVm, wtm);

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.ActionsMatrix, actual);
    }

    [TestMethod]
    public void AggregateColumns_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new AggregateColumnsListVM();
        var helper = CreateHelper(listVm, wtm);

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.AggregateColumns, actual);
    }

    [TestMethod]
    public void RichColumns_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new RichColumnsListVM();
        var helper = CreateHelper(listVm, wtm);

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.RichColumns, actual);
    }

    [TestMethod]
    public void BoolColumn_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new BoolColumnListVM();
        var helper = CreateHelper(listVm, wtm);

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.BoolColumn, actual);
    }

    [TestMethod]
    public void IsInSelector_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new PlainColumnsListVM();
        var helper = CreateHelper(listVm, wtm);
        helper.IsInSelector = true;

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.IsInSelector, actual);
    }

    [TestMethod]
    public void AutoSearchFalse_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new PlainColumnsListVM();
        var helper = CreateHelper(listVm, wtm);
        helper.AutoSearch = false;

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.AutoSearchFalse, actual);
    }

    [TestMethod]
    public void SearcherExpanded_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new PlainColumnsListVM();
        var helper = CreateHelper(listVm, wtm);
        helper.SearcherExpanded = true;

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.SearcherExpanded, actual);
    }

    [TestMethod]
    public void DetailGridPrix_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new PlainColumnsListVM();
        listVm.DetailGridPrix = "Detail_470O1";
        var helper = CreateHelper(listVm, wtm);

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.DetailGridPrix, actual);
    }

    [TestMethod]
    public void EnableClientExport_FlagOff_ByteIdentical()
    {
        SetupLocalizer();
        var wtm = MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));
        var listVm = new PlainColumnsListVM();
        var helper = CreateHelper(listVm, wtm);
        helper.EnableClientExport = true;

        var actual = RenderNormalized(helper);

        Assert.AreEqual(DataTableByteIdentityFixtures.EnableClientExport, actual);
    }

}
