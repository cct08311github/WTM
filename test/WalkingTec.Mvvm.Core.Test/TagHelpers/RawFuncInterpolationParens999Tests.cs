#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acornima;
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
/// Issue #999 part (A) — an exhaustive enumeration of every site where WTM's
/// LayUI TagHelpers interpolate a developer-supplied <c>*Func</c> callback
/// STRING directly into an emitted &lt;script&gt; block found 47 raw/legacy
/// interpolation sites total. #965 (PR #998) fixed the first discovered
/// instance (<c>DataTableTagHelper</c>'s <c>GridAction.OnClickFunc</c>); this
/// file covers the remaining 9 sites where the developer's raw string reaches
/// the emitted JS VERBATIM at JS statement-start position:
///
/// <list type="bullet">
/// <item>DataTableTagHelper.cs — <c>done: function(res,curr,count){{ DoneFunc(...) }}</c></item>
/// <item>TransferTagHelper.cs — <c>onchange: function(data,index){{ ...; ChangeFunc(...); }}</c></item>
/// <item>SliderTagHelper.cs — <c>change: function(value){{ ...; ChangeFunc(...) }}</c></item>
/// <item>DateTimeTagHelper.cs — single-field Ready/Change/Done AND the
/// two-hidden-input IsRange path's Ready/Change/Done (6 sites total)</item>
/// </list>
///
/// A statement beginning with an anonymous <c>function(...){{...}}</c> literal
/// is a hard JS SyntaxError (function DECLARATIONS require a name; an unnamed
/// one at statement-start cannot be parsed as an expression), and that error
/// kills parsing of the WHOLE enclosing &lt;script&gt; block — every other
/// handler defined in the same block goes dead too, not just the one with the
/// literal callback. The fix wraps each site in parens — <c>({{X}})(...)</c> —
/// which forces the parser into expression context, exactly like #965's fix
/// and verified compat-neutral there for bare-identifier / dotted / call-
/// expression callback shapes.
///
/// NOT in scope (tracked separately on #999 part (B)): 12 <c>FormatFuncName</c>
/// sites (<c>BaseElementTag.cs:225-242</c>) truncate the developer's callback
/// at its first <c>(</c> and append <c>(data)</c> BEFORE it ever reaches a
/// syntactic position — <c>function(v){{...}}</c> becomes the string
/// <c>function(data)</c>, and paren-wrapping that
/// (<c>(function(data));</c>) is itself a SyntaxError. Those sites need a
/// different fix and are pending cross-vendor design review.
///
/// Each test below renders the REAL TagHelper's output (not a string
/// comparison — see #965's own framing for why a golden-string test cannot
/// catch this class of bug), extracts the actual emitted &lt;script&gt;
/// block, and feeds it to Acornima — a pure-.NET, Test262-complete
/// ECMAScript parser (no interpreter/execution, no external process, no
/// `node` on PATH required — see Directory.Packages.props's Issue #965
/// comment) — asserting it parses without a syntax error.
///
/// Two additional tests verify the CLAIMED-safe exclusions actually parse
/// today, before any fix: SliderTagHelper's <c>OnTipsFunc</c> (reached only
/// via <c>return {{OnTipsFunc}}(...)</c> — expression position, not statement
/// position) and DataTableTagHelper's <c>CheckedFunc</c> (passed as a
/// call-expression ARGUMENT to <c>table.on(...)</c> — also expression
/// position). Both must already parse with a raw function-literal value with
/// NO wrapping — proving they were never broken and must not be wrapped
/// (wrapping an already-legal call-argument position, e.g.
/// <c>table.on('x',(function(){{}}))</c>, is harmless syntactically but is
/// deliberately NOT done here since it is not the bug being fixed and would
/// blur which sites #999 part (A) actually touched).
/// </summary>
[TestClass]
public class RawFuncInterpolationParens999Tests
{
    private sealed class DummyModel
    {
        public string? StringField { get; set; }
        public int IntField { get; set; }
        public string? DateField { get; set; }
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

    private static ModelExpression MakeField(string propertyName)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(DummyModel).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(DummyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, null);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext(string tagName)
        => new(tagName, new TagHelperAttributeList(), new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput(string wrapperTag)
        => new(wrapperTag, new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    /// <summary>
    /// Extracts the FIRST bare <c>&lt;script&gt;...&lt;/script&gt;</c> block —
    /// i.e. actual executable JavaScript, as opposed to
    /// <c>&lt;script type="application/json" ...&gt;</c> (eval-free islands)
    /// or <c>&lt;script type="text/html" ...&gt;</c> (LayUI template markup).
    /// Mirrors DataTableTagHelperUnwrappedIife965Tests's
    /// ExtractFirstBareScriptBlock (#965 / PR #998).
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

    private static void AssertParsesAsValidJavaScript(string? script, string siteDescription)
    {
        Assert.IsFalse(string.IsNullOrEmpty(script),
            $"Expected a bare <script> block for {siteDescription} — none was found, so this test would otherwise pass vacuously.");

        var parser = new Parser();
        try
        {
            parser.ParseScript(script!);
        }
        catch (ParseErrorException ex)
        {
            Assert.Fail(
                $"{siteDescription} emitted a <script> block that is not valid JavaScript " +
                $"(Issue #999 part (A) — likely an unwrapped IIFE from a raw function-literal *Func interpolation). " +
                $"Parser error: {ex.Message}\n--- emitted script ---\n{script}");
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ── Site 1/9: DataTableTagHelper.cs — done: function(res,curr,count){ DoneFunc(...) } ──

    public class DoneFuncListVM999 : BasePagedListVM<Student, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
        {
            return new List<GridColumn<Student>>
            {
                this.MakeGridHeader(x => x.LoginName),
            };
        }
    }

    private static WTMContext NewWtm()
        => MockWtmContext.CreateWtmContext(new DataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory));

    [TestMethod]
    public void DataTable_DoneFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        var listVm = new DoneFuncListVM999 { Wtm = NewWtm(), ViewDivId = "FixedViewDiv999" };
        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(listVm.GetType());
        var modelExplorer = new ModelExplorer(provider, metadata, listVm);
        var helper = new DataTableTagHelper
        {
            Vm = new ModelExpression(string.Empty, modelExplorer),
            Id = "wtTable_999done",
            SearchPanelId = "wtForm_999done",
            DoneFunc = "function(res,curr,count){ff.OpenDialog('/_Foo999/Index',null,'DoneCallback999',600,null,undefined,false);}",
        };

        var output = MakeOutput("table");
        helper.Process(MakeContext("wt:grid"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "OpenDialog", "Extracted block must actually contain the DoneFunc body, or a pass here proves nothing.");
        StringAssert.Contains(script, "DoneCallback999");
        AssertParsesAsValidJavaScript(script, "DataTableTagHelper's done: callback (DoneFunc)");
    }

    // ── Site 2/9: TransferTagHelper.cs — onchange: function(data,index){ ...; ChangeFunc(...); } ──

    [TestMethod]
    public void Transfer_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions()); // default OFF -> always the legacy inline <script> path
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField"),
            Id = "transfer_999change",
            ChangeFunc = "function(data,index,transferIns){ff.OpenDialog('/_Foo999/Index',null,'ChangeCallback999',600);}",
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:transfer"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "OpenDialog");
        StringAssert.Contains(script, "ChangeCallback999");
        AssertParsesAsValidJavaScript(script, "TransferTagHelper's onchange: callback (ChangeFunc)");
    }

    // ── Site 3/9: SliderTagHelper.cs — change: function(value){ ...; ChangeFunc(...) } ──

    [TestMethod]
    public void Slider_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions()); // default OFF -> always the legacy inline <script> path
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_999change",
            ChangeFunc = "function(value,sliderIns){ff.OpenDialog('/_Foo999/Index',null,'SliderChange999',600);}",
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:slider"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "OpenDialog");
        StringAssert.Contains(script, "SliderChange999");
        AssertParsesAsValidJavaScript(script, "SliderTagHelper's change: callback (ChangeFunc)");
    }

    // ── Sites 4-6/9: DateTimeTagHelper.cs single-field path (Ready/Change/Done) ──

    private static DateTimeTagHelper CreateDateTimeHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new DateTimeTagHelper(monitor.Object) { Field = MakeField("DateField") };
    }

    [TestMethod]
    public void DateTime_SingleField_ReadyFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateDateTimeHelper();
        helper.Id = "date_999ready";
        helper.IsRange = false;
        helper.ReadyFunc = "function(value,dateIns){ff.OpenDialog('/_Foo999/Index',null,'DateReady999',600);}";
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:datetime"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "OpenDialog");
        StringAssert.Contains(script, "DateReady999");
        AssertParsesAsValidJavaScript(script, "DateTimeTagHelper single-field ready: callback (ReadyFunc)");
    }

    [TestMethod]
    public void DateTime_SingleField_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateDateTimeHelper();
        helper.Id = "date_999change";
        helper.IsRange = false;
        helper.ChangeFunc = "function(value,date,endDate,dateIns){ff.OpenDialog('/_Foo999/Index',null,'DateChange999',600);}";
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:datetime"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "OpenDialog");
        StringAssert.Contains(script, "DateChange999");
        AssertParsesAsValidJavaScript(script, "DateTimeTagHelper single-field change: callback (ChangeFunc)");
    }

    [TestMethod]
    public void DateTime_SingleField_DoneFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateDateTimeHelper();
        helper.Id = "date_999done";
        helper.IsRange = false;
        helper.DoneFunc = "function(value,date,endDate,dateIns){ff.OpenDialog('/_Foo999/Index',null,'DateDone999',600);}";
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:datetime"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "OpenDialog");
        StringAssert.Contains(script, "DateDone999");
        AssertParsesAsValidJavaScript(script, "DateTimeTagHelper single-field done: callback (DoneFunc)");
    }

    // ── Sites 7-9/9: DateTimeTagHelper.cs two-hidden-input IsRange path (Ready/Change/Done) ──

    [TestMethod]
    public void DateTime_Range_ReadyFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateDateTimeHelper();
        helper.Id = "date_999rangeready";
        helper.IsRange = true;
        helper.RangeStartName = "RangeStart999a";
        helper.RangeEndName = "RangeEnd999a";
        helper.ReadyFunc = "function(value,dateIns){ff.OpenDialog('/_Foo999/Index',null,'DateRangeReady999',600);}";
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:datetime"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "OpenDialog");
        StringAssert.Contains(script, "DateRangeReady999");
        StringAssert.Contains(script, "range: true", "Must actually be exercising the IsRange path, or a pass here proves nothing.");
        AssertParsesAsValidJavaScript(script, "DateTimeTagHelper IsRange ready: callback (ReadyFunc)");
    }

    [TestMethod]
    public void DateTime_Range_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateDateTimeHelper();
        helper.Id = "date_999rangechange";
        helper.IsRange = true;
        helper.RangeStartName = "RangeStart999b";
        helper.RangeEndName = "RangeEnd999b";
        helper.ChangeFunc = "function(value,date,endDate,dateIns){ff.OpenDialog('/_Foo999/Index',null,'DateRangeChange999',600);}";
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:datetime"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "OpenDialog");
        StringAssert.Contains(script, "DateRangeChange999");
        StringAssert.Contains(script, "range: true");
        AssertParsesAsValidJavaScript(script, "DateTimeTagHelper IsRange change: callback (ChangeFunc)");
    }

    [TestMethod]
    public void DateTime_Range_DoneFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateDateTimeHelper();
        helper.Id = "date_999rangedone";
        helper.IsRange = true;
        helper.RangeStartName = "RangeStart999c";
        helper.RangeEndName = "RangeEnd999c";
        helper.DoneFunc = "function(value,date,endDate,dateIns){ff.OpenDialog('/_Foo999/Index',null,'DateRangeDone999',600);}";
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:datetime"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "OpenDialog");
        StringAssert.Contains(script, "DateRangeDone999");
        StringAssert.Contains(script, "range: true");
        // The DoneFunc call must appear AFTER the built-in split statements
        // (document.getElementById(...).value = ...) — it shares that
        // done: function(...){ ... } body, unlike Ready/Change which each
        // get their own dedicated function(...) callback.
        StringAssert.Contains(script, "getElementById('RangeStart999c')");
        AssertParsesAsValidJavaScript(script, "DateTimeTagHelper IsRange done: callback (DoneFunc)");
    }

    // ── Exclusion verification 1/2: SliderTagHelper's OnTipsFunc — expression
    //    position (`return {OnTipsFunc}(...)`), must already parse WITHOUT
    //    wrapping, proving it was never broken. ──

    [TestMethod]
    public void Slider_OnTipsFunc_FunctionLiteral_AlreadyParsesAsValidJavaScript_NoWrappingNeeded()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_999tips",
            OnTipsFunc = "function(value,sliderIns){return 'Tip999: '+value;}",
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:slider"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "setTips");
        StringAssert.Contains(script, "Tip999");
        AssertParsesAsValidJavaScript(script,
            "SliderTagHelper's setTips: function(value){return OnTipsFunc(...);} (expression position — deliberately excluded from #999 part (A))");
    }

    // ── Exclusion verification 2/2: DataTableTagHelper's CheckedFunc — passed
    //    as a call-expression ARGUMENT to table.on(...), must already parse
    //    WITHOUT wrapping. ──

    public class CheckedFuncListVM999 : BasePagedListVM<Student, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
        {
            return new List<GridColumn<Student>>
            {
                this.MakeGridHeader(x => x.LoginName),
            };
        }
    }

    [TestMethod]
    public void DataTable_CheckedFunc_FunctionLiteral_AlreadyParsesAsValidJavaScript_NoWrappingNeeded()
    {
        SetupLocalizer();
        var listVm = new CheckedFuncListVM999 { Wtm = NewWtm(), ViewDivId = "FixedViewDiv999b" };
        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(listVm.GetType());
        var modelExplorer = new ModelExplorer(provider, metadata, listVm);
        var helper = new DataTableTagHelper
        {
            Vm = new ModelExpression(string.Empty, modelExplorer),
            Id = "wtTable_999checked",
            SearchPanelId = "wtForm_999checked",
            CheckedFunc = "function(obj){console.log('Checked999',obj);}",
        };

        var output = MakeOutput("table");
        helper.Process(MakeContext("wt:grid"), output);
        var post = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(post);
        StringAssert.Contains(script, "table.on('checkbox(");
        StringAssert.Contains(script, "Checked999");
        AssertParsesAsValidJavaScript(script,
            "DataTableTagHelper's table.on('checkbox(...)', CheckedFunc) (call-argument expression position — deliberately excluded from #999 part (A))");
    }
}
