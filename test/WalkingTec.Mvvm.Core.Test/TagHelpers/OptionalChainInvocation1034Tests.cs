#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acornima;
using Acornima.Ast;
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
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #1034 — #999 part (A)/(B) and #965 fixed 20 unwrapped-IIFE sites by
/// wrapping every developer-supplied <c>*Func</c> expression in a grouping
/// operator, <c>(expr)(args)</c>. That wrap is valid JS for every shape those
/// three fixes were designed against, EXCEPT one: it ends an optional chain's
/// short-circuit. <c>handlers?.onChange(data)</c> short-circuits to a no-op
/// when <c>handlers</c> is nullish; the wrapped
/// <c>(handlers?.onChange)(data)</c> forces the chain to produce its value
/// (<c>undefined</c>) BEFORE the call, so the call throws <c>TypeError</c> and
/// aborts instead. This regression was disclosed on all six affected
/// CHANGELOG/production-readiness entries by #1040 and is fixed here by a
/// CLOSED-LANGUAGE classifier — <c>BaseElementTag.IsNarrowOptionalChain</c> —
/// that recognizes the narrow, provably-safe set of "ASCII identifier atoms
/// joined by <c>.</c>/<c>?.</c>, at least one <c>?.</c>, non-keyword head"
/// values and emits the call INSIDE the chain for exactly that set,
/// <c>expr(args)</c>. Every other value (including anything that merely
/// CONTAINS <c>?.</c> without being a plain chain, e.g. an arrow body
/// <c>(v)=>a?.b</c>) falls back to the existing, unchanged, byte-identical
/// wrap — fail-closed, never fail-open. See
/// <c>BaseElementTag.FormatFuncInvocation</c>'s own header comment for the
/// full spec argument.
///
/// This file's harness is the SAME harness <c>FormatFuncInvocation999BTests</c>
/// (render the real TagHelper, extract the actual emitted <c>&lt;script&gt;</c>
/// block) plus <c>RawFuncInterpolationParens999Tests</c>/
/// <c>DataTableTagHelperUnwrappedIife965Tests</c>'s per-site fixtures for the
/// 10 sites that were raw string interpolation before #1034 converged them
/// onto <c>FormatFuncInvocation</c> too — redefined locally per this repo's
/// existing convention (each TagHelper test file keeps its own copy of
/// SetupLocalizer/MakeField/MakeContext/MakeOutput/extractors; see
/// <c>DataTableTagHelperUnwrappedIife965Tests</c>'s own header comment for why
/// that duplication is deliberate, not an oversight). ZERO new shipped or
/// test-only dependency: no Jint, no `node` — Acornima (already a test-only
/// dependency since #965/#998) is used only for the belt-and-braces AST checks
/// below (T-ast), and only to confirm the STRUCTURE of already-byte-pinned
/// text, never to execute anything.
///
/// Test groups (naming matches the cross-vendor-reviewed design doc's
/// vocabulary so a reviewer can cross-reference 1:1):
/// <list type="bullet">
/// <item><b>T-chain</b> (one test per each of the 20 emission sites): the
/// callback is <c>window.handlers?.onChange</c> — assert the emitted text
/// contains the DIRECT form, <c>window.handlers?.onChange(&lt;site args&gt;)</c>,
/// verbatim, and does NOT contain the wrapped form,
/// <c>(window.handlers?.onChange)</c>.</item>
/// <item><b>T-byte</b> (one test per site with a plain-identifier value, plus
/// two exemplar sites × {dotted-member, factory-call}): the callback does NOT
/// contain <c>?.</c> — assert the emitted text is byte-identical to today's
/// (unfixed) wrap form, <c>(value)(&lt;site args&gt;)</c>. Function-literal
/// byte/parse coverage at all 20 sites is deliberately NOT repeated here — it
/// already exists, untouched, in <c>FormatFuncInvocation999BTests</c> /
/// <c>RawFuncInterpolationParens999Tests</c> /
/// <c>DataTableTagHelperUnwrappedIife965Tests</c>, and (see each test's own
/// comment below) those same fixtures independently catch the
/// "IsNarrowOptionalChain always true" mutant this file's T-byte tests target,
/// because that mutant reintroduces the exact unwrapped-IIFE SyntaxError #999/
/// #965 fixed for a function-literal value.</item>
/// <item><b>T-cls</b>: a 36-entry (6 true + 30 false) classification
/// regression matrix run ONLY against the .NET implementation, via the
/// PUBLIC <c>FormatFuncInvocation</c> surface (no reflection into the private
/// classifier needed — its branch selection is fully observable from the
/// public method's return value). This is a regression matrix for THIS
/// regex/denylist, not a proof of equivalence with any JS-engine regex.</item>
/// <item><b>T-cond</b>: the two <c>if (X != false)</c> expression-position
/// sites (Tree/ComboBox) — folded into their own T-chain tests below (sites
/// 04 and 06), since the assertion IS the same "assert exact site text"
/// check; called out by name here so a reviewer looking for "T-cond" finds
/// it.</item>
/// <item><b>T-ast</b> (belt-and-braces): parses REAL emitted output with
/// Acornima and walks the AST, asserting the direct-form call site's root is
/// a <c>ChainExpression</c> wrapping a non-optional <c>CallExpression</c> —
/// this distinguishes the correct output from the REJECTED alternative design
/// <c>expr?.(args)</c> (which would also contain <c>?.</c> in its text but
/// parses to a ChainExpression whose CallExpression has <c>Optional == true</c>),
/// and from the wrap form (whose root is a CallExpression, not a
/// ChainExpression).</item>
/// <item>Island-fallback (design's "named change #5"): proves a chain-bearing
/// value stays on the LEGACY render path even when
/// <c>WtmUIOptions.UseSelectIslandRender</c> is ON, by testing the actual
/// island/legacy DECISION points directly (<c>TreeTagHelper.cs</c>'s and
/// <c>ComboBoxTagHelper.cs</c>'s <c>useSelectIsland</c> computation), not just
/// inferring it from a flag-OFF golden.</item>
/// </list>
///
/// Anti-vacuous positive control: every render helper's result is asserted
/// non-null/non-empty before any content assertion (<c>AssertNotVacuous</c>
/// below) — the mutant this guards against is deleting the
/// <c>helper.Process(...)</c>/<c>ProcessAsync(...)</c> call itself (which
/// still COMPILES — <c>output</c> is constructed either way — but leaves
/// <c>output.PostElement</c>/<c>output.Content</c> empty), not deleting the
/// extractor call (which would fail to compile and therefore proves nothing
/// about production code — see the repo's own "ask which line deletion turns
/// this red" rule, `.claude/rules` canon).
/// </summary>
[TestClass]
public class OptionalChainInvocation1034Tests
{
    private sealed class DummyModel
    {
        public string? StringField { get; set; }
        public int IntField { get; set; }
        public string? DateField { get; set; }
        public string? ColorField { get; set; }
        public List<string>? Roles { get; set; }
        public List<string>? SelectedIds { get; set; }
        public List<TreeSelectListItem>? Items { get; set; }
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

    private static TagHelperContext MakeContext(string tagName)
        => new(tagName, new TagHelperAttributeList(), new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput(string wrapperTag)
        => new(wrapperTag, new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static List<TreeSelectListItem> OneItem(string value = "v1", string text = "Node 1")
        => [new TreeSelectListItem { Value = value, Text = text }];

    private static WTMContext NewWtm()
        => MockWtmContext.CreateWtmContext(new DataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory));

    private static TreeTagHelper CreateTreeHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new TreeTagHelper(monitor.Object);
    }

    private static ComboBoxTagHelper CreateComboBoxHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new ComboBoxTagHelper(monitor.Object, null!);
    }

    private static DateTimeTagHelper CreateDateTimeHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new DateTimeTagHelper(monitor.Object) { Field = MakeField("DateField") };
    }

    /// <summary>
    /// Mirrors FormatFuncInvocation999BTests/RawFuncInterpolationParens999Tests'
    /// identical helper — extracts the FIRST bare &lt;script&gt;...&lt;/script&gt;
    /// block (real executable JS, not a JSON island or text/html template).
    /// </summary>
    private static string? ExtractFirstBareScriptBlock(string html)
    {
        const string openTag = "<script>";
        var start = html.IndexOf(openTag, StringComparison.Ordinal);
        if (start < 0) return null;
        start += openTag.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (end < 0) return null;
        return html[start..end];
    }

    /// <summary>
    /// Mirrors FormatFuncInvocation999BTests' identical helper — extracts the
    /// bare &lt;script&gt; block whose CONTENT contains <paramref name="marker"/>,
    /// needed for TagHelpers that emit more than one bare &lt;script&gt; block
    /// (e.g. CheckBoxTagHelper: a data-defaults script first, then the
    /// ChangeFunc wiring script).
    /// </summary>
    private static string? ExtractBareScriptBlockContaining(string html, string marker)
    {
        var searchFrom = 0;
        while (true)
        {
            var start = html.IndexOf("<script>", searchFrom, StringComparison.Ordinal);
            if (start < 0) return null;
            start += "<script>".Length;
            var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
            if (end < 0) return null;
            var block = html[start..end];
            if (block.Contains(marker, StringComparison.Ordinal)) return block;
            searchFrom = end + "</script>".Length;
        }
    }

    /// <summary>
    /// Anti-vacuous positive control shared by every site test below — see
    /// this class's own header comment for the mutant this guards against
    /// (deleting the compiling <c>Process</c>/<c>ProcessAsync</c> call, not
    /// the extractor, which would fail to compile).
    /// </summary>
    private static void AssertNotVacuous(string? script, string siteLabel)
    {
        Assert.IsFalse(string.IsNullOrEmpty(script),
            $"Expected a non-empty emitted <script> block for {siteLabel} — none was found, " +
            "so this test would otherwise pass vacuously (see AssertNotVacuous's own doc comment " +
            "for the specific mutant — deleting the Process/ProcessAsync call — this guards against).");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Per-site render helpers (site numbers match the design doc's 20-row
    // table 1:1 — comment on each names the exact file:line the emission
    // lives at, so a reviewer can jump straight there).
    // ═══════════════════════════════════════════════════════════════════════

    // Site 01: BaseElementTag.cs's EmitFormChangeWiring — checkbox/switch/
    // radio ChangeFunc -> layui.form.on(...), statement position. Exercised
    // via CheckBoxTagHelper (matches FormatFuncInvocation999BTests' own
    // statement-position exemplar). args = "data".
    private static string RenderSite01_CheckBoxFormChange(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<string> { "Admin" }),
            Id = "s01_1034",
            ChangeFunc = funcValue,
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:checkbox"), output);
        return ExtractBareScriptBlockContaining(output.PostElement.GetContent(), "layui.use(['form']")!;
    }

    // Site 02: BaseElementTag.cs's EmitAutocompleteWiring, WITH TriggerUrl —
    // TextBoxTagHelper's onselect: wiring, statement position. args = "data".
    private static string RenderSite02_TextBoxAutocompleteWithTrigger(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("StringField", "foo"),
            Id = "s02_1034",
            SearchUrl = "/api/search1034",
            TriggerUrl = "/api/trigger1034",
            ChangeFunc = funcValue,
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:textbox"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // Site 03: BaseElementTag.cs's EmitAutocompleteWiring, WITHOUT TriggerUrl.
    // args = "data".
    private static string RenderSite03_TextBoxAutocompleteNoTrigger(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("StringField", "foo"),
            Id = "s03_1034",
            SearchUrl = "/api/search1034",
            ChangeFunc = funcValue,
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:textbox"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // Site 04: TreeTagHelper.cs:368 — ChangeFunc, LinkId SET -> expression
    // position, `if (X != false)` link-chain gate. args = "data". Also T-cond #1.
    private static string RenderSite04_TreeWithLink(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "s04_1034";
        helper.LinkId = "link1034";
        helper.ChangeFunc = funcValue;
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:tree"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // Site 05: TreeTagHelper.cs:377 — ChangeFunc, LinkId NOT set -> statement
    // position, `on:function(data){X}`. args = "data".
    private static string RenderSite05_TreeNoLink(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "s05_1034";
        helper.ChangeFunc = funcValue;
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:tree"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // Site 06: ComboBoxTagHelper.cs:458 — ChangeFunc, LinkId SET -> expression
    // position `if (X != false)`. args = "data". Also T-cond #2.
    private static string RenderSite06_ComboBoxWithLink(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "s06_1034";
        helper.LinkId = "link1034";
        helper.ChangeFunc = funcValue;
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:combobox"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // Site 07: ComboBoxTagHelper.cs:467 — ChangeFunc, LinkId NOT set ->
    // statement position. args = "data".
    private static string RenderSite07_ComboBoxNoLink(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "s07_1034";
        helper.ChangeFunc = funcValue;
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:combobox"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // Site 08: TreeContainerTagHelper.cs:311 — ClickFunc -> click:
    // function(data){...}, statement position. args = "data".
    private static async Task<string> RenderSite08_TreeContainerClick(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new TreeContainerTagHelper
        {
            Id = "s08_1034",
            Items = MakeField("Items", OneItem()),
            ClickFunc = funcValue,
        };
        var output = MakeOutput("div");
        await helper.ProcessAsync(MakeContext("wt:treecontainer"), output);
        return ExtractFirstBareScriptBlock(output.Content.GetContent())!;
    }

    // Site 09: Form/ColorPicker.cs:291 — ChangeFunc -> done:
    // function(data){...}, statement position. args = "data".
    private static string RenderSite09_ColorPicker(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField"),
            Id = "s09_1034",
            ChangeFunc = funcValue,
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:colorpicker"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    private sealed class Site10ListVM : BasePagedListVM<Student, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
            => new List<GridColumn<Student>> { this.MakeGridHeader(x => x.LoginName) };
    }

    // Site 10: Form/SelectorTagHelper.cs:478 — BeforeOnpenDialogFunc ->
    // "var data={};" + X + ";", statement position, synthetic `data`.
    // args = "data" (default).
    private static async Task<string> RenderSite10_Selector(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var listVm = new Site10ListVM { Wtm = NewWtm() };
        var provider = new EmptyModelMetadataProvider();
        var vmMetadata = provider.GetMetadataForType(listVm.GetType());
        var vmExplorer = new ModelExplorer(provider, vmMetadata, listVm);
        var textBindProperty = typeof(Student).GetProperty("LoginName")!;
        var textBindMetadata = provider.GetMetadataForProperty(textBindProperty, typeof(Student));

        var helper = new SelectorTagHelper
        {
            Field = MakeField("SelectedIds"),
            ListVM = new ModelExpression(string.Empty, vmExplorer),
            TextBind = new ModelExpression("LoginName", new ModelExplorer(provider, textBindMetadata, null)),
            Id = "s10_1034",
            BeforeOnpenDialogFunc = funcValue,
        };
        var output = MakeOutput("input");
        await helper.ProcessAsync(MakeContext("wt:selector"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    private sealed class Site11ListVM : BasePagedListVM<Student, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
            => new List<GridColumn<Student>> { this.MakeGridHeader(x => x.LoginName) };
    }

    // Site 11: DataTableTagHelper.cs:809 — DoneFunc -> done:
    // function(res,curr,count){...}, statement position. args = "res,curr,count".
    private static string RenderSite11_DataTableDoneFunc(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var listVm = new Site11ListVM { Wtm = NewWtm(), ViewDivId = "s11div1034" };
        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(listVm.GetType());
        var modelExplorer = new ModelExplorer(provider, metadata, listVm);
        var helper = new DataTableTagHelper
        {
            Vm = new ModelExpression(string.Empty, modelExplorer),
            Id = "s11_1034",
            SearchPanelId = "s11form1034",
            DoneFunc = funcValue,
        };
        var output = MakeOutput("table");
        helper.Process(MakeContext("wt:grid"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    private sealed class Site12ListVM : BasePagedListVM<Student, BaseSearcher>
    {
        public string OnClickFuncValue = "";

        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
            => new List<GridColumn<Student>>
            {
                this.MakeGridHeader(x => x.LoginName),
                this.MakeGridHeaderAction(),
            };

        protected override List<GridAction> InitGridAction()
            => new List<GridAction>
            {
                new GridAction
                {
                    Name = "s12action1034",
                    IconCls = "layui-icon layui-icon-list",
                    ShowInRow = true,
                    HideOnToolBar = true,
                    OnClickFunc = OnClickFuncValue,
                },
            };
    }

    // Site 12: DataTableTagHelper.cs:1300 — GridAction.OnClickFunc (#965),
    // statement position. args = "ids,ff.GetSelectionData('s12_1034')".
    private static string RenderSite12_DataTableOnClickFunc(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var listVm = new Site12ListVM { OnClickFuncValue = funcValue, Wtm = NewWtm(), ViewDivId = "s12div1034" };
        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(listVm.GetType());
        var modelExplorer = new ModelExplorer(provider, metadata, listVm);
        var helper = new DataTableTagHelper
        {
            Vm = new ModelExpression(string.Empty, modelExplorer),
            Id = "s12_1034",
            SearchPanelId = "s12form1034",
        };
        var output = MakeOutput("table");
        helper.Process(MakeContext("wt:grid"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // Site 13: Form/SliderTagHelper.cs:469 — ChangeFunc, statement position.
    // args = "value,sliderIns".
    private static string RenderSite13_Slider(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "s13_1034",
            ChangeFunc = funcValue,
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:slider"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // Site 14: Form/TransferTagHelper.cs:341 — ChangeFunc, statement position.
    // args = "data, index,transferIns" (note the space after "data," — must be
    // preserved verbatim).
    private static string RenderSite14_Transfer(string funcValue)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField"),
            Id = "s14_1034",
            ChangeFunc = funcValue,
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:transfer"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // Sites 15-17: Form/DateTimeTagHelper.cs:477/478/479 — single-field
    // (non-IsRange) Ready/Change/Done. args = "value,dateIns" (Ready) /
    // "value,date,endDate,dateIns" (Change/Done).
    private static string RenderDateTimeSingle(string idSuffix, string? readyFunc, string? changeFunc, string? doneFunc)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateDateTimeHelper();
        helper.Id = "s" + idSuffix + "_1034";
        helper.IsRange = false;
        helper.ReadyFunc = readyFunc;
        helper.ChangeFunc = changeFunc;
        helper.DoneFunc = doneFunc;
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:datetime"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // Sites 18-20: Form/DateTimeTagHelper.cs:577/578/582 — two-hidden-input
    // IsRange path Ready/Change/Done. Same args shape as 15-17.
    private static string RenderDateTimeRange(string idSuffix, string? readyFunc, string? changeFunc, string? doneFunc)
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateDateTimeHelper();
        helper.Id = "s" + idSuffix + "_1034";
        helper.IsRange = true;
        helper.RangeStartName = "RangeStart" + idSuffix + "1034";
        helper.RangeEndName = "RangeEnd" + idSuffix + "1034";
        helper.ReadyFunc = readyFunc;
        helper.ChangeFunc = changeFunc;
        helper.DoneFunc = doneFunc;
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:datetime"), output);
        return ExtractFirstBareScriptBlock(output.PostElement.GetContent())!;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // T-chain ×20 — narrow optional chain, direct form, short-circuit restored
    // ═══════════════════════════════════════════════════════════════════════

    private const string Chain = "window.handlers?.onChange";

    [TestMethod]
    public void TChain_Site01_CheckBoxFormChange_DirectFormPreservesShortCircuit()
    {
        var script = RenderSite01_CheckBoxFormChange(Chain);
        AssertNotVacuous(script, "site 01 (CheckBox form.on wiring)");
        StringAssert.Contains(script, "window.handlers?.onChange(data);");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"), "must not fall back to the wrap that ends the chain's short-circuit");
    }

    [TestMethod]
    public void TChain_Site02_TextBoxAutocompleteWithTrigger_DirectFormPreservesShortCircuit()
    {
        var script = RenderSite02_TextBoxAutocompleteWithTrigger(Chain);
        AssertNotVacuous(script, "site 02 (TextBox onselect, WITH TriggerUrl)");
        StringAssert.Contains(script, "window.handlers?.onChange(data);");
        StringAssert.Contains(script, "ff.ChainChange(", "the TriggerUrl branch must still wire ChainChange after the invocation");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site03_TextBoxAutocompleteNoTrigger_DirectFormPreservesShortCircuit()
    {
        var script = RenderSite03_TextBoxAutocompleteNoTrigger(Chain);
        AssertNotVacuous(script, "site 03 (TextBox onselect, NO TriggerUrl)");
        StringAssert.Contains(script, "window.handlers?.onChange(data);");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    // Also T-cond #1: the `if (X != false)` expression-position exemplar.
    [TestMethod]
    public void TChain_Site04_TreeWithLink_DirectFormInsideIfCondition()
    {
        var script = RenderSite04_TreeWithLink(Chain);
        AssertNotVacuous(script, "site 04 (Tree if(X != false) link-chain gate)");
        StringAssert.Contains(script, "if (window.handlers?.onChange(data) != false)");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site05_TreeNoLink_DirectFormPreservesShortCircuit()
    {
        var script = RenderSite05_TreeNoLink(Chain);
        AssertNotVacuous(script, "site 05 (Tree on:function(data){X}, no link)");
        StringAssert.Contains(script, "window.handlers?.onChange(data)");
        Assert.IsFalse(script!.Contains("!= false"), "must be exercising the NO-link branch, not the if-gate branch");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    // Also T-cond #2.
    [TestMethod]
    public void TChain_Site06_ComboBoxWithLink_DirectFormInsideIfCondition()
    {
        var script = RenderSite06_ComboBoxWithLink(Chain);
        AssertNotVacuous(script, "site 06 (ComboBox if(X != false) link-chain gate)");
        StringAssert.Contains(script, "if (window.handlers?.onChange(data) != false)");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site07_ComboBoxNoLink_DirectFormPreservesShortCircuit()
    {
        var script = RenderSite07_ComboBoxNoLink(Chain);
        AssertNotVacuous(script, "site 07 (ComboBox on:function(data){X}, no link)");
        StringAssert.Contains(script, "window.handlers?.onChange(data)");
        Assert.IsFalse(script!.Contains("!= false"));
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public async Task TChain_Site08_TreeContainerClick_DirectFormPreservesShortCircuit()
    {
        var script = await RenderSite08_TreeContainerClick(Chain);
        AssertNotVacuous(script, "site 08 (TreeContainer click: callback)");
        StringAssert.Contains(script, "window.handlers?.onChange(data);");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site09_ColorPicker_DirectFormPreservesShortCircuit()
    {
        var script = RenderSite09_ColorPicker(Chain);
        AssertNotVacuous(script, "site 09 (ColorPicker done: callback)");
        StringAssert.Contains(script, "window.handlers?.onChange(data);");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public async Task TChain_Site10_Selector_DirectFormPreservesShortCircuit()
    {
        var script = await RenderSite10_Selector(Chain);
        AssertNotVacuous(script, "site 10 (Selector BeforeOnpenDialogFunc)");
        StringAssert.Contains(script, "var data={};window.handlers?.onChange(data);");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site11_DataTableDoneFunc_DirectFormPreservesShortCircuit()
    {
        var script = RenderSite11_DataTableDoneFunc(Chain);
        AssertNotVacuous(script, "site 11 (DataTable done: callback, DoneFunc)");
        StringAssert.Contains(script, "window.handlers?.onChange(res,curr,count)");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site12_DataTableOnClickFunc_DirectFormPreservesShortCircuit()
    {
        var script = RenderSite12_DataTableOnClickFunc(Chain);
        AssertNotVacuous(script, "site 12 (DataTable GridAction.OnClickFunc, #965)");
        // args here call layui's checkStatus (ff.GetSelectionData) — this is
        // the site where #1034's §2.4 correction matters most: the direct
        // form does not evaluate this call-argument at all when `handlers`
        // is nullish, whereas the wrapped form (today's 10.22.0 behaviour)
        // evaluates it first and THEN throws.
        StringAssert.Contains(script, "window.handlers?.onChange(ids,ff.GetSelectionData('s12_1034'));");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site13_Slider_DirectFormPreservesShortCircuit()
    {
        var script = RenderSite13_Slider(Chain);
        AssertNotVacuous(script, "site 13 (Slider change: callback)");
        StringAssert.Contains(script, "window.handlers?.onChange(value,sliderIns)");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site14_Transfer_DirectFormPreservesShortCircuit()
    {
        var script = RenderSite14_Transfer(Chain);
        AssertNotVacuous(script, "site 14 (Transfer onchange: callback)");
        StringAssert.Contains(script, "window.handlers?.onChange(data, index,transferIns);");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site15_DateTimeSingleReadyFunc_DirectFormPreservesShortCircuit()
    {
        var script = RenderDateTimeSingle("15", readyFunc: Chain, changeFunc: null, doneFunc: null);
        AssertNotVacuous(script, "site 15 (DateTime single-field ready:)");
        StringAssert.Contains(script, ",ready: function(value){window.handlers?.onChange(value,dateIns)}");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site16_DateTimeSingleChangeFunc_DirectFormPreservesShortCircuit()
    {
        var script = RenderDateTimeSingle("16", readyFunc: null, changeFunc: Chain, doneFunc: null);
        AssertNotVacuous(script, "site 16 (DateTime single-field change:)");
        StringAssert.Contains(script, ",change: function(value,date,endDate){window.handlers?.onChange(value,date,endDate,dateIns)}");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site17_DateTimeSingleDoneFunc_DirectFormPreservesShortCircuit()
    {
        var script = RenderDateTimeSingle("17", readyFunc: null, changeFunc: null, doneFunc: Chain);
        AssertNotVacuous(script, "site 17 (DateTime single-field done:)");
        StringAssert.Contains(script, ",done: function(value,date,endDate){window.handlers?.onChange(value,date,endDate,dateIns)}");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site18_DateTimeRangeReadyFunc_DirectFormPreservesShortCircuit()
    {
        var script = RenderDateTimeRange("18", readyFunc: Chain, changeFunc: null, doneFunc: null);
        AssertNotVacuous(script, "site 18 (DateTime IsRange ready:)");
        StringAssert.Contains(script, "range: true", "must actually be exercising the IsRange path");
        StringAssert.Contains(script, ",ready: function(value){window.handlers?.onChange(value,dateIns)}");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site19_DateTimeRangeChangeFunc_DirectFormPreservesShortCircuit()
    {
        var script = RenderDateTimeRange("19", readyFunc: null, changeFunc: Chain, doneFunc: null);
        AssertNotVacuous(script, "site 19 (DateTime IsRange change:)");
        StringAssert.Contains(script, "range: true");
        StringAssert.Contains(script, ",change: function(value,date,endDate){window.handlers?.onChange(value,date,endDate,dateIns)}");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void TChain_Site20_DateTimeRangeDoneFunc_DirectFormPreservesShortCircuit()
    {
        var script = RenderDateTimeRange("20", readyFunc: null, changeFunc: null, doneFunc: Chain);
        AssertNotVacuous(script, "site 20 (DateTime IsRange done:, shared with the built-in split)");
        StringAssert.Contains(script, "getElementById('RangeStart201034')", "must be the IsRange done: body, after the built-in split statements");
        StringAssert.Contains(script, "window.handlers?.onChange(value,date,endDate,dateIns);");
        Assert.IsFalse(script!.Contains("(window.handlers?.onChange)"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // T-byte — plain-identifier value at all 20 sites: byte-identical to
    // today's (unfixed) wrap form. This is the direct, per-site kill for the
    // "IsNarrowOptionalChain always returns true" compile-preserving mutant
    // (BaseElementTag.cs's classifier body replaced with `return true;`) —
    // under that mutant EVERY one of these 20 assertions goes red, because
    // "myHandler1034" (no "?.") would incorrectly take the direct-call branch
    // and lose its wrapping parens.
    // ═══════════════════════════════════════════════════════════════════════

    private const string Identifier = "myHandler1034";

    [TestMethod]
    public void TByte_Site01_CheckBoxFormChange_IdentifierStaysWrapped()
    {
        var script = RenderSite01_CheckBoxFormChange(Identifier);
        AssertNotVacuous(script, "site 01 identifier");
        StringAssert.Contains(script, "(myHandler1034)(data);");
    }

    [TestMethod]
    public void TByte_Site02_TextBoxAutocompleteWithTrigger_IdentifierStaysWrapped()
    {
        var script = RenderSite02_TextBoxAutocompleteWithTrigger(Identifier);
        AssertNotVacuous(script, "site 02 identifier");
        StringAssert.Contains(script, "(myHandler1034)(data);");
    }

    [TestMethod]
    public void TByte_Site03_TextBoxAutocompleteNoTrigger_IdentifierStaysWrapped()
    {
        var script = RenderSite03_TextBoxAutocompleteNoTrigger(Identifier);
        AssertNotVacuous(script, "site 03 identifier");
        StringAssert.Contains(script, "(myHandler1034)(data);");
    }

    [TestMethod]
    public void TByte_Site04_TreeWithLink_IdentifierStaysWrapped()
    {
        var script = RenderSite04_TreeWithLink(Identifier);
        AssertNotVacuous(script, "site 04 identifier");
        StringAssert.Contains(script, "if ((myHandler1034)(data) != false)");
    }

    [TestMethod]
    public void TByte_Site05_TreeNoLink_IdentifierStaysWrapped()
    {
        var script = RenderSite05_TreeNoLink(Identifier);
        AssertNotVacuous(script, "site 05 identifier");
        StringAssert.Contains(script, "(myHandler1034)(data)");
    }

    [TestMethod]
    public void TByte_Site06_ComboBoxWithLink_IdentifierStaysWrapped()
    {
        var script = RenderSite06_ComboBoxWithLink(Identifier);
        AssertNotVacuous(script, "site 06 identifier");
        StringAssert.Contains(script, "if ((myHandler1034)(data) != false)");
    }

    [TestMethod]
    public void TByte_Site07_ComboBoxNoLink_IdentifierStaysWrapped()
    {
        var script = RenderSite07_ComboBoxNoLink(Identifier);
        AssertNotVacuous(script, "site 07 identifier");
        StringAssert.Contains(script, "(myHandler1034)(data)");
    }

    [TestMethod]
    public async Task TByte_Site08_TreeContainerClick_IdentifierStaysWrapped()
    {
        var script = await RenderSite08_TreeContainerClick(Identifier);
        AssertNotVacuous(script, "site 08 identifier");
        StringAssert.Contains(script, "(myHandler1034)(data);");
    }

    [TestMethod]
    public void TByte_Site09_ColorPicker_IdentifierStaysWrapped()
    {
        var script = RenderSite09_ColorPicker(Identifier);
        AssertNotVacuous(script, "site 09 identifier");
        StringAssert.Contains(script, "(myHandler1034)(data);");
    }

    [TestMethod]
    public async Task TByte_Site10_Selector_IdentifierStaysWrapped()
    {
        var script = await RenderSite10_Selector(Identifier);
        AssertNotVacuous(script, "site 10 identifier");
        StringAssert.Contains(script, "var data={};(myHandler1034)(data);");
    }

    [TestMethod]
    public void TByte_Site11_DataTableDoneFunc_IdentifierStaysWrapped()
    {
        var script = RenderSite11_DataTableDoneFunc(Identifier);
        AssertNotVacuous(script, "site 11 identifier");
        StringAssert.Contains(script, "(myHandler1034)(res,curr,count)");
    }

    [TestMethod]
    public void TByte_Site12_DataTableOnClickFunc_IdentifierStaysWrapped()
    {
        var script = RenderSite12_DataTableOnClickFunc(Identifier);
        AssertNotVacuous(script, "site 12 identifier");
        StringAssert.Contains(script, "(myHandler1034)(ids,ff.GetSelectionData('s12_1034'));");
    }

    [TestMethod]
    public void TByte_Site13_Slider_IdentifierStaysWrapped()
    {
        var script = RenderSite13_Slider(Identifier);
        AssertNotVacuous(script, "site 13 identifier");
        StringAssert.Contains(script, "(myHandler1034)(value,sliderIns)");
    }

    [TestMethod]
    public void TByte_Site14_Transfer_IdentifierStaysWrapped()
    {
        var script = RenderSite14_Transfer(Identifier);
        AssertNotVacuous(script, "site 14 identifier");
        StringAssert.Contains(script, "(myHandler1034)(data, index,transferIns);");
    }

    [TestMethod]
    public void TByte_Site15_DateTimeSingleReadyFunc_IdentifierStaysWrapped()
    {
        var script = RenderDateTimeSingle("15b", readyFunc: Identifier, changeFunc: null, doneFunc: null);
        AssertNotVacuous(script, "site 15 identifier");
        StringAssert.Contains(script, ",ready: function(value){(myHandler1034)(value,dateIns)}");
    }

    [TestMethod]
    public void TByte_Site16_DateTimeSingleChangeFunc_IdentifierStaysWrapped()
    {
        var script = RenderDateTimeSingle("16b", readyFunc: null, changeFunc: Identifier, doneFunc: null);
        AssertNotVacuous(script, "site 16 identifier");
        StringAssert.Contains(script, ",change: function(value,date,endDate){(myHandler1034)(value,date,endDate,dateIns)}");
    }

    [TestMethod]
    public void TByte_Site17_DateTimeSingleDoneFunc_IdentifierStaysWrapped()
    {
        var script = RenderDateTimeSingle("17b", readyFunc: null, changeFunc: null, doneFunc: Identifier);
        AssertNotVacuous(script, "site 17 identifier");
        StringAssert.Contains(script, ",done: function(value,date,endDate){(myHandler1034)(value,date,endDate,dateIns)}");
    }

    [TestMethod]
    public void TByte_Site18_DateTimeRangeReadyFunc_IdentifierStaysWrapped()
    {
        var script = RenderDateTimeRange("18b", readyFunc: Identifier, changeFunc: null, doneFunc: null);
        AssertNotVacuous(script, "site 18 identifier");
        StringAssert.Contains(script, ",ready: function(value){(myHandler1034)(value,dateIns)}");
    }

    [TestMethod]
    public void TByte_Site19_DateTimeRangeChangeFunc_IdentifierStaysWrapped()
    {
        var script = RenderDateTimeRange("19b", readyFunc: null, changeFunc: Identifier, doneFunc: null);
        AssertNotVacuous(script, "site 19 identifier");
        StringAssert.Contains(script, ",change: function(value,date,endDate){(myHandler1034)(value,date,endDate,dateIns)}");
    }

    [TestMethod]
    public void TByte_Site20_DateTimeRangeDoneFunc_IdentifierStaysWrapped()
    {
        var script = RenderDateTimeRange("20b", readyFunc: null, changeFunc: null, doneFunc: Identifier);
        AssertNotVacuous(script, "site 20 identifier");
        StringAssert.Contains(script, "(myHandler1034)(value,date,endDate,dateIns);");
    }

    // ── T-byte, extra shapes at two exemplar sites (one BaseElementTag-shared-
    //    helper family site, one raw-converged-to-FormatFuncInvocation site):
    //    dotted-member and factory-call, both non-narrow (no "?."), both must
    //    stay wrapped byte-identically. Function-literal coverage for all 20
    //    sites already exists, untouched, in FormatFuncInvocation999BTests /
    //    RawFuncInterpolationParens999Tests / DataTableTagHelperUnwrappedIife965Tests
    //    (see this file's header comment) — not repeated here.

    [TestMethod]
    public void TByte_Site01_CheckBoxFormChange_DottedMember_StaysWrapped_PreservesThisBinding()
    {
        var script = RenderSite01_CheckBoxFormChange("ns1034.obj.doThing");
        AssertNotVacuous(script, "site 01 dotted");
        StringAssert.Contains(script, "(ns1034.obj.doThing)(data);",
            "the whole dotted expression must stay inside the parens, call args outside — that shape keeps `this` bound to ns1034.obj");
    }

    [TestMethod]
    public void TByte_Site01_CheckBoxFormChange_FactoryCall_StaysWrapped()
    {
        var script = RenderSite01_CheckBoxFormChange("mk1034()");
        AssertNotVacuous(script, "site 01 factory");
        StringAssert.Contains(script, "(mk1034())(data);");
    }

    [TestMethod]
    public void TByte_Site11_DataTableDoneFunc_DottedMember_StaysWrapped_PreservesThisBinding()
    {
        var script = RenderSite11_DataTableDoneFunc("ns1034.obj.doThing");
        AssertNotVacuous(script, "site 11 dotted");
        StringAssert.Contains(script, "(ns1034.obj.doThing)(res,curr,count)");
    }

    [TestMethod]
    public void TByte_Site11_DataTableDoneFunc_FactoryCall_StaysWrapped()
    {
        var script = RenderSite11_DataTableDoneFunc("mk1034()");
        AssertNotVacuous(script, "site 11 factory");
        StringAssert.Contains(script, "(mk1034())(res,curr,count)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // T-cls — 36-entry (6 true + 30 false) classification regression matrix,
    // via the PUBLIC FormatFuncInvocation surface (its branch selection fully
    // and deterministically reveals the private classifier's decision — no
    // reflection into BaseElementTag needed). Run ONLY against this .NET
    // implementation: this pins the regex/denylist's OWN behaviour and is a
    // regression matrix, not proof of equivalence with any JS-engine regex
    // (the underlying node cross-check that originally validated this table
    // lives in the design doc / PR notes, not re-executed here).
    //
    // Mutant-kill story per row group (each is a compile-preserving edit to
    // BaseElementTag.IsNarrowOptionalChain, not a deletion that would fail to
    // compile):
    //   - the fast-reject `if (!s.Contains("?.")) return false;` line is a
    //     pure perf shortcut with no behavioural effect on its own (the regex
    //     below it still requires "?." and would reject the same values) —
    //     it has no dedicated row here; TCls_FalseSet already exercises every
    //     value that reaches the regex either way.
    //   - `\z` reverted to `$` -> "a?.b\n" flips from false to true (that row
    //     exists specifically to catch this mutation).
    //   - deleting the reserved-head denylist check (`return
    //     !_jsReservedHeads.Contains(...)` replaced with `return true;`) ->
    //     "this?.h" / "new?.target" / "function?.call" flip from false to true.
    //   - the whole classifier body replaced with `return true;` -> every row
    //     in TCls_FalseSet flips from false to true (this is also the mutant
    //     TByte's 20 per-site tests target — see that region's own comment).
    //   - the whole classifier body replaced with `return false;` -> every
    //     row in TCls_TrueSet flips from true to false (this is also the
    //     mutant TChain's 20 per-site tests target).
    // ═══════════════════════════════════════════════════════════════════════

    [DataTestMethod]
    [DataRow("window.handlers?.onChange")]
    [DataRow("a?.b?.c")]
    [DataRow("a.b?.c")]
    [DataRow("$?._x")]
    [DataRow("_ns?.h1.on2")]
    [DataRow("a?.function")]
    public void TCls_TrueSet_NarrowOptionalChain_EmitsDirectForm(string value)
    {
        var result = BaseElementTag.FormatFuncInvocation(value, "data");
        Assert.AreEqual($"{value}(data)", result,
            $"'{value}' is a provably-safe narrow optional chain and must emit the DIRECT form.");
    }

    [DataTestMethod]
    [DataRow("a.b.c")]
    [DataRow("myFunc")]
    [DataRow("obj[\"x\"]?.m")]
    [DataRow("get()?.m")]
    [DataRow("cond?a?.b:c")]
    [DataRow("mk(\"?.\")")]
    [DataRow("a?.b //c")]
    [DataRow("function(){}()?.m")]
    [DataRow("function")]
    [DataRow("function?.call")]
    [DataRow("function(v){return v;}")]
    [DataRow("(v)=>v+1")]
    [DataRow("(v)=>{return v;}")]
    [DataRow("(v)=>a?.b")]
    [DataRow("a ?. b")]
    [DataRow(" a?.b")]
    [DataRow("a?.b ")]
    [DataRow("a?.b\n")]
    [DataRow("a??.b")]
    [DataRow("a?..b")]
    [DataRow("?.a")]
    [DataRow("a?.")]
    [DataRow("a?.5")]
    [DataRow("中?.b")]
    [DataRow("a中?.b")]
    [DataRow("this?.h")]
    [DataRow("new?.target")]
    [DataRow("a?.b(x)")]
    [DataRow("a?.b()")]
    [DataRow("a?.b;c")]
    public void TCls_FalseSet_NotNarrowOptionalChain_EmitsWrappedFallback(string value)
    {
        var result = BaseElementTag.FormatFuncInvocation(value, "data");
        Assert.AreEqual($"({value})(data)", result,
            $"'{value}' must fall back to the existing, byte-identical WRAPPED form — " +
            "it is not a provably-safe narrow optional chain (it either lacks a chain, " +
            "is a keyword-headed chain, or is a chain embedded in a larger, non-chain expression).");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // T-ast — belt-and-braces: parse REAL emitted output with Acornima and
    // walk the AST. Distinguishes the correct direct form from the REJECTED
    // design alternative `expr?.(args)` (same "?." substring, but its
    // CallExpression.Optional == true), and from the wrap form (root is a
    // CallExpression, not a ChainExpression).
    //
    // Compile-preserving mutant this specifically targets: replacing the
    // narrow branch's `return $"{funcExpression}({args})";` with the
    // REJECTED `return $"{funcExpression}?.({args})";` — the byte-text
    // assertions above would NOT catch this (both contain "window.handlers
    // ?.onChange" and neither contains the wrap's "(window.handlers?.onChange)"
    // substring), but this mutation flips CallExpression.Optional from false
    // to true, which these tests assert against directly.
    // ═══════════════════════════════════════════════════════════════════════

    private static List<ChainExpression> FindChainExpressions(Program program)
    {
        var found = new List<ChainExpression>();
        void Walk(Node n)
        {
            if (n is ChainExpression ce) found.Add(ce);
            foreach (var child in n.ChildNodes)
            {
                if (child != null) Walk(child);
            }
        }
        foreach (var stmt in program.Body) Walk(stmt);
        return found;
    }

    [TestMethod]
    public void TAst_Site01_StatementPosition_ChainExpressionWrapsNonOptionalCall()
    {
        var script = RenderSite01_CheckBoxFormChange(Chain);
        AssertNotVacuous(script, "T-ast site 01");

        var parser = new Parser();
        var program = parser.ParseScript(script!);
        var chains = FindChainExpressions(program);

        Assert.AreEqual(1, chains.Count,
            "expected exactly one ChainExpression node in this site's emitted script (the direct-form ChangeFunc call)");
        Assert.IsInstanceOfType(chains[0].Expression, typeof(CallExpression),
            "the ChainExpression must wrap a CallExpression — i.e. the call is INSIDE the chain, so short-circuit propagates through it");
        var call = (CallExpression)chains[0].Expression;
        Assert.IsFalse(call.Optional,
            "the call itself must NOT be an optional call (`?.(`) — that would be the REJECTED `expr?.(args)` design, which turns a mistyped identifier from a loud throw into a silent no-op");
    }

    [TestMethod]
    public void TAst_Site04_ExpressionPosition_IfConditionChainExpressionWrapsNonOptionalCall()
    {
        var script = RenderSite04_TreeWithLink(Chain);
        AssertNotVacuous(script, "T-ast site 04");

        var parser = new Parser();
        var program = parser.ParseScript(script!);
        var chains = FindChainExpressions(program);

        Assert.AreEqual(1, chains.Count,
            "expected exactly one ChainExpression node (the direct-form ChangeFunc call inside `if (X != false)`)");
        Assert.IsInstanceOfType(chains[0].Expression, typeof(CallExpression));
        var call = (CallExpression)chains[0].Expression;
        Assert.IsFalse(call.Optional);
    }

    [TestMethod]
    public void TAst_WrapForm_PositiveControl_RootIsCallExpressionNotChainExpression()
    {
        // Standalone grammar-shape sanity check (not tied to any TagHelper
        // render): this is the AST-level distinguisher the two tests above
        // rely on — a WRAPPED call's root is a CallExpression whose Callee is
        // the ChainExpression, never a ChainExpression at the statement root.
        var parser = new Parser();
        var program = parser.ParseScript("(myFunc)(data);");
        Assert.AreEqual(1, program.Body.Count);
        var stmt = (ExpressionStatement)program.Body[0];
        Assert.IsNotInstanceOfType(stmt.Expression, typeof(ChainExpression),
            "a wrapped, non-chain call's root expression must be a CallExpression, not a ChainExpression");
        Assert.IsInstanceOfType(stmt.Expression, typeof(CallExpression));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Island-fallback (design's "named change #5") — a chain-bearing
    // ChangeFunc must stay on the LEGACY render path even with
    // UseSelectIslandRender ON, by testing the actual decision points
    // directly: TreeTagHelper.cs's and ComboBoxTagHelper.cs's `useSelectIsland`
    // computation (both gated on `_identifierRegex` — a bare-identifier-only
    // regex that a value containing "?." can never match, same reasoning as
    // every other #470 slice's 3-way decision). Mirrors
    // RenderSelectIsland470SliceJTests' *_FlagOn_NonIdentifierChangeFunc_*
    // pattern exactly, with an optional-chain value instead of a dotted one,
    // plus the #1034-specific assertion that the kept legacy inline script
    // itself uses the DIRECT (not wrapped) form.
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void IslandFallback_Tree_OptionalChainChangeFunc_FlagOn_StaysOnLegacyPath_DirectForm()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree_island_1034";
        helper.ChangeFunc = Chain;
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "xmSelect.render(",
            "an optional-chain ChangeFunc can never be a plain identifier — must keep the legacy inline render, flag notwithstanding");
        Assert.IsFalse(postHtml.Contains("\"type\":\"renderSelect\""),
            "must NOT emit the renderSelect island for this field — the ChangeFunc decision point (TreeTagHelper.cs's useSelectIsland computation) must route to legacy");
        StringAssert.Contains(postHtml, "console.warn('[WTM] TreeTagHelper",
            "flag ON + island skipped for a non-identifier ChangeFunc must still fire the deprecation warn");
        StringAssert.Contains(postHtml, "window.handlers?.onChange(data)",
            "the #1034 fix must still apply within the kept legacy path");
        Assert.IsFalse(postHtml.Contains("(window.handlers?.onChange)"));
    }

    [TestMethod]
    public void IslandFallback_ComboBox_OptionalChainChangeFunc_FlagOn_StaysOnLegacyPath_DirectForm()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_island_1034";
        helper.ChangeFunc = Chain;
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "xmSelect.render(",
            "an optional-chain ChangeFunc can never be a plain identifier — must keep the legacy inline render, flag notwithstanding");
        Assert.IsFalse(postHtml.Contains("\"type\":\"renderSelect\""),
            "must NOT emit the renderSelect island for this field — the ChangeFunc decision point (ComboBoxTagHelper.cs's useSelectIsland computation) must route to legacy");
        StringAssert.Contains(postHtml, "console.warn('[WTM] ComboBoxTagHelper",
            "flag ON + island skipped for a non-identifier ChangeFunc must still fire the deprecation warn");
        StringAssert.Contains(postHtml, "window.handlers?.onChange(data)",
            "the #1034 fix must still apply within the kept legacy path");
        Assert.IsFalse(postHtml.Contains("(window.handlers?.onChange)"));
    }
}
