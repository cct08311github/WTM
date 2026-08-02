#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Acornima;
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
/// Issue #965 — <see cref="DataTableTagHelper"/>'s legacy (non-island)
/// AddSubButton branch emitted a raw <c>GridAction.OnClickFunc</c> function
/// LITERAL directly ahead of its invocation parens:
/// <c>function(ids,data){...}(ids,ff.GetSelectionData('...'));</c>.
///
/// That is an unwrapped IIFE — a statement cannot start with the `function`
/// keyword and be call-expression'd without wrapping parens (an anonymous
/// `function` at statement position parses as a FunctionDeclaration, which
/// requires a name; without one it's a hard syntax error). The error is not
/// local to that one action: it kills parsing of the WHOLE enclosing
/// &lt;script&gt; block, so every OTHER action/handler defined in the same
/// block goes dead too. EtlJobListVM's "執行記錄" toolbar action
/// (src/WalkingTec.Mvvm.Etl/ViewModels/EtlJobListVM.cs) reaches this exact
/// shape, breaking /_EtlJob/Index's entire grid via any real navigation route.
///
/// <see cref="DataTableByteIdentityTests"/> exercises this exact code path
/// with a bare-identifier <c>OnClickFunc</c> fixture (never this literal
/// shape) and stayed green throughout — precisely because pinning exact
/// output BYTES says nothing about JavaScript VALIDITY: a golden string is
/// captured from whatever the code currently emits, bugs included, so it
/// would have stayed equally green had its own fixture been the buggy shape.
/// A plain string-comparison test would have the same blind spot and would
/// stop meaning anything the next time the generator's formatting changes
/// (#965's own framing). This test instead renders the real TagHelper output,
/// extracts the actual emitted &lt;script&gt; block, and feeds it to
/// Acornima — a pure-.NET, Test262-complete ECMAScript parser (no
/// interpreter/execution, no external process, no `node` on PATH required —
/// see Directory.Packages.props's Issue #965 comment for why that constraint
/// matters on this repo's .NET test job) — asserting it parses without a
/// syntax error.
///
/// Fixture/harness plumbing (CreateHelper/MakeContext/MakeOutput/
/// SetupLocalizer/NewWtm) mirrors <see cref="RenderGridToolbar470SliceO2Tests"/>,
/// whose own header doc says it "Reuses the SAME ... shapes as
/// <see cref="DataTableByteIdentityTests"/>" — each of those private static
/// methods is nevertheless independently redefined per file, not shared
/// (they are `private`, so C# could not share them across classes even in
/// the same namespace). That file's own comment on its
/// <c>NonIdentifierOnClickListVM</c> fixture states the reason explicitly:
/// "redefined locally to keep this file self-contained" — the same reasoning
/// applies here to the harness methods.
/// </summary>
[TestClass]
public class DataTableTagHelperUnwrappedIife965Tests
{
    // Shaped exactly like EtlJobListVM's real "執行記錄" GridAction
    // (src/WalkingTec.Mvvm.Etl/ViewModels/EtlJobListVM.cs): a raw anonymous
    // function-literal OnClickFunc, not a bare identifier. A non-identifier
    // OnClickFunc is what forces DetermineGridIslandDecision to fall back to
    // the legacy (non-island) rendering path — the ONLY path that ever
    // reaches AddSubButton's `actionScript = $"{item.OnClickFunc}(ids,...)"`
    // line (DataTableTagHelper.cs) — so this fixture exercises the exact
    // branch #965 reports regardless of WtmUIOptions.UseSelectIslandRender.
    public class UnwrappedIifeActionListVM : BasePagedListVM<Student, BaseSearcher>
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
                    Name = "執行記錄",
                    IconCls = "layui-icon layui-icon-list",
                    ShowInRow = true,
                    HideOnToolBar = true,
                    // Verbatim copy of EtlJobListVM's real OnClickFunc string (#540).
                    OnClickFunc = @"function(ids,data){var id=ids&&ids.length>0?ids[0]:'';ff.OpenDialog('/_EtlRunLog/Index?jobId='+id,null,'執行記錄',900,null,undefined,false);}",
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
        => MockWtmContext.CreateWtmContext(new DataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory));

    private static DataTableTagHelper CreateHelper<TModel, TSearcher>(
        BasePagedListVM<TModel, TSearcher> listVm,
        WTMContext wtm,
        string id = "wtTable_965")
        where TModel : TopBasePoco
        where TSearcher : BaseSearcher
    {
        listVm.Wtm = wtm;
        listVm.ViewDivId = "FixedViewDiv965";
        return new DataTableTagHelper
        {
            Vm = MakeVm(listVm),
            Id = id,
            SearchPanelId = "wtForm_965",
        };
    }

    private static string Render(DataTableTagHelper helper)
    {
        var context = MakeContext();
        var output = MakeOutput();
        helper.Process(context, output);
        return output.PostElement.GetContent();
    }

    /// <summary>
    /// Extracts the FIRST bare <c>&lt;script&gt;...&lt;/script&gt;</c> block —
    /// i.e. actual executable JavaScript. Deliberately does NOT match
    /// <c>&lt;script type="text/html" ...&gt;</c> (LayUI template markup,
    /// which is HTML, not JS, and would never parse as a script) or
    /// <c>&lt;script src="..."&gt;</c> (asset includes with no inline body).
    ///
    /// Verified (not assumed) against this fixture's actual rendered output:
    /// the literal substring <c>"&lt;script&gt;"</c> occurs TWICE, not once —
    /// DataTableTagHelper.cs's getTemplate() (called from generateColHeaderCore) embeds a
    /// second, decoy occurrence inside the per-cell background-colour callback
    /// it writes into the <c>cols:</c> JSON (<c>bg = "&lt;script&gt;...");</c>,
    /// itself nested INSIDE the first, real block's own content). Taking the
    /// FIRST occurrence is still correct because that decoy is textually after
    /// the real opening tag, never before it. The matching close is safe for
    /// the same structural reason plus one more: the decoy's own
    /// <c>&lt;/script&gt;</c> is written as the split concatenation
    /// <c>"&lt;/s"+"cript&gt;"</c> specifically so the literal, contiguous
    /// string never appears in the emitted source — so
    /// <c>IndexOf("&lt;/script&gt;", start)</c> cannot land on it and is
    /// guaranteed to find the real closing tag first. (Confirmed by counting
    /// occurrences in the actual rendered `post` string for this exact
    /// fixture: two `"&lt;script&gt;"`, one `"&lt;/script&gt;"` before the
    /// next, attributed `&lt;script type="text/html"&gt;` tag begins.)
    /// </summary>
    private static string? ExtractFirstBareScriptBlock(string html)
    {
        const string openTag = "<script>";
        var start = html.IndexOf(openTag, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += openTag.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }
        return html[start..end];
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        // WtmUIOptionsHolder is process-wide shared state (see BaseFieldTag.SetUIOptions);
        // reset it so this test can never leak UseSelectIslandRender=true into a test that
        // runs after it.
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    [TestMethod]
    public void ToolbarActionScript_WithFunctionLiteralOnClickFunc_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        // Explicit default (island rendering OFF) rather than relying on ambient
        // WtmUIOptionsHolder state left by another test class: this is also the
        // REAL production default, so it exercises the exact code path
        // /_EtlJob/Index hits today (DetermineGridIslandDecision's first check
        // short-circuits to the legacy path whenever this flag is off, before it
        // ever even looks at OnClickFunc's identifier-ness).
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateHelper(new UnwrappedIifeActionListVM(), NewWtm());
        var post = Render(helper);

        var script = ExtractFirstBareScriptBlock(post);
        Assert.IsFalse(string.IsNullOrEmpty(script),
            "Expected a bare <script> block (the wtToolBarFunc_* dispatcher) in the rendered output — none was found, so this test would otherwise pass vacuously.");

        // Guards against the extractor silently grabbing an unrelated/empty
        // block: the fixture's OnClickFunc body must actually be present in
        // whatever we hand to the parser, or a pass here proves nothing about
        // the #965 code path.
        StringAssert.Contains(script, "OpenDialog");
        StringAssert.Contains(script, "執行記錄");

        var parser = new Parser();
        try
        {
            parser.ParseScript(script!);
        }
        catch (ParseErrorException ex)
        {
            Assert.Fail(
                $"DataTableTagHelper emitted a <script> block that is not valid JavaScript " +
                $"(Issue #965 — likely an unwrapped IIFE from a function-literal GridAction.OnClickFunc). " +
                $"Parser error: {ex.Message}\n--- emitted script ---\n{script}");
        }
    }
}
