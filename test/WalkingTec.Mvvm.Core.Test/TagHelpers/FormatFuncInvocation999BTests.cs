#nullable enable
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
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #999 part (B) — the 10 sites where a developer-supplied <c>*Func</c>
/// value is passed through <c>BaseElementTag.FormatFuncName</c> before
/// reaching an emitted &lt;script&gt; block. FormatFuncName truncates its
/// input at the FIRST "(" and appends "(data)" — a syntactic accident, not a
/// grammar decision — so <c>function(v){{...}}</c> becomes the string
/// <c>function(data)</c> BEFORE it ever reaches a syntactic position, and
/// <c>(function(data));</c> is itself a SyntaxError (confirmed with
/// Acornima). Part (A) (#1003) already fixed the 9 sites where the raw value
/// reaches the emitted JS verbatim; these 10 are different because
/// FormatFuncName's truncation corrupts the value before any paren-wrapping
/// could help.
///
/// The fix is <c>BaseElementTag.FormatFuncInvocation</c> — a NEW static
/// helper that does no grammar classification at all: it keeps the caller's
/// expression completely unmodified and wraps it as <c>(expr)(args)</c>.
/// FormatFuncName itself is untouched; its 11 other (decision-input /
/// HTML-attribute) call sites keep using it exactly as before.
///
/// Each test below renders the REAL TagHelper's output, extracts the actual
/// emitted &lt;script&gt; block, and feeds it to Acornima (test-only, see
/// RawFuncInterpolationParens999Tests.cs's file-header comment for why not
/// `node`), asserting it parses without a syntax error. Function-literal
/// cases are the RED-before-fix proof; arrow-function, plain-identifier, and
/// dotted-member (`this`-binding-preserving) cases are covered on a
/// representative subset of sites (one statement-position exemplar —
/// CheckBoxTagHelper — one expression-position exemplar — TreeTagHelper's
/// `if (X != false)` link-chain branch — plus ColorPicker for a second
/// statement-position dotted-member check).
/// </summary>
[TestClass]
public class FormatFuncInvocation999BTests
{
    private sealed class DummyModel
    {
        public string? StringField { get; set; }
        public List<string>? Roles { get; set; }
        public string? ColorField { get; set; }
        public List<TreeSelectListItem>? Items { get; set; }
        public List<string>? SelectedIds { get; set; }
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
        => MockWtmContext.CreateWtmContext(new DataContext(System.Guid.NewGuid().ToString(), DBTypeEnum.Memory));

    /// <summary>
    /// Extracts the FIRST bare <c>&lt;script&gt;...&lt;/script&gt;</c> block —
    /// mirrors RawFuncInterpolationParens999Tests's identical helper (#999 part A).
    /// </summary>
    private static string? ExtractFirstBareScriptBlock(string html)
    {
        const string openTag = "<script>";
        var start = html.IndexOf(openTag, System.StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += openTag.Length;
        var end = html.IndexOf("</script>", start, System.StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }
        return html[start..end];
    }

    /// <summary>
    /// Extracts the bare <c>&lt;script&gt;...&lt;/script&gt;</c> block whose
    /// CONTENT contains <paramref name="marker"/> — needed for TagHelpers
    /// (e.g. CheckBoxTagHelper) that emit more than one bare &lt;script&gt;
    /// block into the same PostElement buffer (a data-defaults script FIRST,
    /// then the ChangeFunc wiring script), where "the first one" would grab
    /// the wrong block.
    /// </summary>
    private static string? ExtractBareScriptBlockContaining(string html, string marker)
    {
        var searchFrom = 0;
        while (true)
        {
            var start = html.IndexOf("<script>", searchFrom, System.StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }
            start += "<script>".Length;
            var end = html.IndexOf("</script>", start, System.StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }
            var block = html[start..end];
            if (block.Contains(marker, System.StringComparison.Ordinal))
            {
                return block;
            }
            searchFrom = end + "</script>".Length;
        }
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
                $"(Issue #999 part (B) — likely FormatFuncName's truncate-then-append-\"(data)\" " +
                $"corrupting a non-identifier *Func value before it reaches a syntactic position). " +
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

    // ══════════════ Site 1/10: BaseElementTag.EmitFormChangeWiring ══════════
    // (checkbox/switch/radio ChangeFunc -> form.on(...), statement position)
    // Exercised via CheckBoxTagHelper — the statement-position exemplar, also
    // covering arrow-function / plain-identifier / dotted-member (this-binding).

    [TestMethod]
    public void CheckBox_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<string> { "Admin" }),
            Id = "cb999_lit",
            ChangeFunc = "function(data){ff.OpenDialog('/_Foo999B/Index',null,'CheckBoxChange999B',600);}",
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractBareScriptBlockContaining(postHtml, "layui.use(['form']");
        StringAssert.Contains(script, "OpenDialog");
        StringAssert.Contains(script, "CheckBoxChange999B");
        AssertParsesAsValidJavaScript(script, "CheckBoxTagHelper's form.on(...) ChangeFunc wiring (BaseElementTag.EmitFormChangeWiring)");
    }

    [TestMethod]
    public void CheckBox_ChangeFunc_ArrowFunction_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<string> { "Admin" }),
            Id = "cb999_arrow",
            ChangeFunc = "(data)=>{ff.OpenDialog('/_Foo999B/Index',null,'CheckBoxArrow999B',600);}",
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractBareScriptBlockContaining(postHtml, "layui.use(['form']");
        StringAssert.Contains(script, "CheckBoxArrow999B");
        AssertParsesAsValidJavaScript(script, "CheckBoxTagHelper's form.on(...) ChangeFunc wiring, arrow-function ChangeFunc");
    }

    [TestMethod]
    public void CheckBox_ChangeFunc_PlainIdentifier_StillCallsThroughUnchanged()
    {
        // Behaviour-preservation check: a bare identifier must still resolve
        // to a call on that exact identifier — (myCheckChange)(data) is
        // exactly equivalent to the pre-fix myCheckChange(data).
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<string> { "Admin" }),
            Id = "cb999_id",
            ChangeFunc = "myCheckChange999B",
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractBareScriptBlockContaining(postHtml, "layui.use(['form']");
        StringAssert.Contains(script, "(myCheckChange999B)(data);");
        AssertParsesAsValidJavaScript(script, "CheckBoxTagHelper's form.on(...) ChangeFunc wiring, plain-identifier ChangeFunc");
    }

    [TestMethod]
    public void CheckBox_ChangeFunc_DottedMember_PreservesThisBindingShape()
    {
        // (ns.obj.doThing)(data) is the textual shape that preserves `this`
        // binding to `ns.obj` at call time — the grouping operator does not
        // strip the Reference a MemberExpression produces (unlike extracting
        // to a temp var or using the comma operator), so this is exactly
        // equivalent to `ns.obj.doThing(data)`.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<string> { "Admin" }),
            Id = "cb999_dotted",
            ChangeFunc = "ns999.obj.doThing",
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractBareScriptBlockContaining(postHtml, "layui.use(['form']");
        StringAssert.Contains(script, "(ns999.obj.doThing)(data);",
            "The WHOLE dotted expression must be inside the parens, call args outside — that shape is what keeps `this` bound to ns999.obj");
        AssertParsesAsValidJavaScript(script, "CheckBoxTagHelper's form.on(...) ChangeFunc wiring, dotted-member ChangeFunc");
    }

    // ══════════════ Site 2/10: BaseElementTag.EmitAutocompleteWiring ════════
    // (TextBoxTagHelper SearchUrl+TriggerUrl -> onselect:, statement position)

    [TestMethod]
    public void TextBox_ChangeFunc_WithTriggerUrl_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("StringField", "foo"),
            Id = "tb999_trig",
            SearchUrl = "/api/search999b",
            TriggerUrl = "/api/trigger999b",
            ChangeFunc = "function(data){ff.OpenDialog('/_Foo999B/Index',null,'TextBoxTrig999B',600);}",
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:textbox"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "TextBoxTrig999B");
        StringAssert.Contains(script, "ff.ChainChange(", "The TriggerUrl branch must still wire ChainChange after the invocation");
        AssertParsesAsValidJavaScript(script, "TextBoxTagHelper's onselect: ChangeFunc wiring WITH TriggerUrl (BaseElementTag.EmitAutocompleteWiring)");
    }

    // ══════════════ Site 3/10: BaseElementTag.EmitAutocompleteWiring ════════
    // (TextBoxTagHelper SearchUrl only, no TriggerUrl -> onselect:, statement position)

    [TestMethod]
    public void TextBox_ChangeFunc_NoTriggerUrl_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("StringField", "foo"),
            Id = "tb999_notrig",
            SearchUrl = "/api/search999b",
            ChangeFunc = "function(data){ff.OpenDialog('/_Foo999B/Index',null,'TextBoxNoTrig999B',600);}",
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:textbox"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "TextBoxNoTrig999B");
        Assert.IsFalse(script!.Contains("ff.ChainChange("), "No TriggerUrl means no ChainChange call");
        AssertParsesAsValidJavaScript(script, "TextBoxTagHelper's onselect: ChangeFunc wiring WITHOUT TriggerUrl (BaseElementTag.EmitAutocompleteWiring)");
    }

    // ══════════════ Site 4/10: TreeContainerTagHelper.cs — cusmtomclick ═════
    // (ClickFunc -> click: function(data){ ...; X; }, statement position)

    [TestMethod]
    public async Task TreeContainer_ClickFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new TreeContainerTagHelper
        {
            Id = "tc999_lit",
            Items = MakeField("Items", OneItem()),
            ClickFunc = "function(data){ff.OpenDialog('/_Foo999B/Index',null,'TreeContainerClick999B',600);}",
        };
        var output = MakeOutput("div");
        await helper.ProcessAsync(MakeContext("wt:treecontainer"), output);
        var content = output.Content.GetContent();

        var script = ExtractFirstBareScriptBlock(content);
        StringAssert.Contains(script, "TreeContainerClick999B");
        AssertParsesAsValidJavaScript(script, "TreeContainerTagHelper's click: callback (ClickFunc / cusmtomclick)");
    }

    // ══════════════ Site 5/10: SelectorTagHelper.cs — BeforeOnpenDialogFunc ═
    // ("var data={};" + X + ";", statement position, synthetic `data`)

    public class BeforeOpenListVM999B : BasePagedListVM<Student, BaseSearcher>
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
    public async Task Selector_BeforeOnpenDialogFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        var listVm = new BeforeOpenListVM999B { Wtm = NewWtm() };
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
            Id = "sel999_lit",
            BeforeOnpenDialogFunc = "function(data){ff.OpenDialog('/_Foo999B/Index',null,'SelectorBeforeOpen999B',600);}",
        };
        var output = MakeOutput("input");
        await helper.ProcessAsync(MakeContext("wt:selector"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "SelectorBeforeOpen999B");
        StringAssert.Contains(script, "var data={};", "The synthetic empty-object data local must still be declared before the invocation");
        AssertParsesAsValidJavaScript(script, "SelectorTagHelper's click handler BeforeOnpenDialogFunc invocation");
    }

    // ══════════════ Site 6/10: TreeTagHelper.cs:368 — if (X != false) ═══════
    // (ChangeFunc, LinkField/LinkId SET -> expression position, if-condition)
    // The expression-position exemplar, also covering arrow-function /
    // plain-identifier / dotted-member (this-binding).

    private static TreeTagHelper CreateTreeHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new TreeTagHelper(monitor.Object);
    }

    [TestMethod]
    public void Tree_ChangeFunc_WithLink_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree999_lit";
        helper.LinkId = "someLinkTarget999b";
        helper.ChangeFunc = "function(data){ff.OpenDialog('/_Foo999B/Index',null,'TreeChangeLink999B',600);}";
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "TreeChangeLink999B");
        StringAssert.Contains(script, "!= false", "Must actually be exercising the if (X != false) link-chain branch, or a pass here proves nothing");
        AssertParsesAsValidJavaScript(script, "TreeTagHelper's if (ChangeFunc != false) link-chain gate (LinkField/LinkId set)");
    }

    [TestMethod]
    public void Tree_ChangeFunc_WithLink_ArrowFunction_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree999_arrow";
        helper.LinkId = "someLinkTarget999b";
        helper.ChangeFunc = "(data)=>{ff.OpenDialog('/_Foo999B/Index',null,'TreeArrowLink999B',600);return true;}";
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "TreeArrowLink999B");
        AssertParsesAsValidJavaScript(script, "TreeTagHelper's if (ChangeFunc != false) link-chain gate, arrow-function ChangeFunc");
    }

    [TestMethod]
    public void Tree_ChangeFunc_WithLink_PlainIdentifier_StillCallsThroughUnchanged()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree999_id";
        helper.LinkId = "someLinkTarget999b";
        helper.ChangeFunc = "myTreeChange999B";
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "if ((myTreeChange999B)(data) != false)");
        AssertParsesAsValidJavaScript(script, "TreeTagHelper's if (ChangeFunc != false) link-chain gate, plain-identifier ChangeFunc");
    }

    [TestMethod]
    public void Tree_ChangeFunc_WithLink_DottedMember_PreservesThisBindingShape()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree999_dotted";
        helper.LinkId = "someLinkTarget999b";
        helper.ChangeFunc = "ns999.tree.gate";
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "if ((ns999.tree.gate)(data) != false)",
            "The WHOLE dotted expression must be inside the parens, call args outside — that shape is what keeps `this` bound to ns999.tree");
        AssertParsesAsValidJavaScript(script, "TreeTagHelper's if (ChangeFunc != false) link-chain gate, dotted-member ChangeFunc");
    }

    // ══════════════ Site 7/10: TreeTagHelper.cs:377 — on:function(data){X} ══
    // (ChangeFunc, LinkField/LinkId NOT set -> statement position)

    [TestMethod]
    public void Tree_ChangeFunc_NoLink_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree999_nolink_lit";
        helper.ChangeFunc = "function(data){ff.OpenDialog('/_Foo999B/Index',null,'TreeChangeNoLink999B',600);}";
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "TreeChangeNoLink999B");
        Assert.IsFalse(script!.Contains("!= false"), "Must be exercising the NO-link branch (bare ChangeFunc invocation), not the if-gate branch");
        AssertParsesAsValidJavaScript(script, "TreeTagHelper's on:function(data){ChangeFunc} handler (LinkField/LinkId NOT set)");
    }

    // ══════════════ Site 8/10: ComboBoxTagHelper.cs:458 — if (X != false) ═══
    // (ChangeFunc, LinkField/LinkId SET -> expression position, if-condition)

    private static ComboBoxTagHelper CreateComboBoxHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new ComboBoxTagHelper(monitor.Object, null!);
    }

    [TestMethod]
    public void ComboBox_ChangeFunc_WithLink_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo999_lit";
        helper.LinkId = "someLinkTarget999b";
        helper.ChangeFunc = "function(data){ff.OpenDialog('/_Foo999B/Index',null,'ComboChangeLink999B',600);}";
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "ComboChangeLink999B");
        StringAssert.Contains(script, "!= false", "Must actually be exercising the if (X != false) link-chain branch, or a pass here proves nothing");
        AssertParsesAsValidJavaScript(script, "ComboBoxTagHelper's if (ChangeFunc != false) link-chain gate (LinkField/LinkId set)");
    }

    [TestMethod]
    public void ComboBox_ChangeFunc_WithLink_ArrowFunction_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo999_arrow";
        helper.LinkId = "someLinkTarget999b";
        helper.ChangeFunc = "(data)=>{ff.OpenDialog('/_Foo999B/Index',null,'ComboArrowLink999B',600);return true;}";
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "ComboArrowLink999B");
        AssertParsesAsValidJavaScript(script, "ComboBoxTagHelper's if (ChangeFunc != false) link-chain gate, arrow-function ChangeFunc");
    }

    // ══════════════ Site 9/10: ComboBoxTagHelper.cs:467 — on:function(data){X} ══
    // (ChangeFunc, LinkField/LinkId NOT set -> statement position)

    [TestMethod]
    public void ComboBox_ChangeFunc_NoLink_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo999_nolink_lit";
        helper.ChangeFunc = "function(data){ff.OpenDialog('/_Foo999B/Index',null,'ComboChangeNoLink999B',600);}";
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "ComboChangeNoLink999B");
        Assert.IsFalse(script!.Contains("!= false"), "Must be exercising the NO-link branch (bare ChangeFunc invocation), not the if-gate branch");
        AssertParsesAsValidJavaScript(script, "ComboBoxTagHelper's on:function(data){ChangeFunc} handler (LinkField/LinkId NOT set)");
    }

    // ══════════════ Site 10/10: ColorPicker.cs — done: function(data){X} ════
    // (ChangeFunc, statement position)

    [TestMethod]
    public void ColorPicker_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField"),
            Id = "cp999_lit",
            ChangeFunc = "function(data){ff.OpenDialog('/_Foo999B/Index',null,'ColorPickerChange999B',600);}",
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:colorpicker"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "ColorPickerChange999B");
        AssertParsesAsValidJavaScript(script, "ColorPickerTagHelper's done: callback (ChangeFunc)");
    }

    [TestMethod]
    public void ColorPicker_ChangeFunc_DottedMember_PreservesThisBindingShape()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new ColorPickerTagHelper
        {
            Field = MakeField("ColorField"),
            Id = "cp999_dotted",
            ChangeFunc = "ns999.picker.onChange",
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:colorpicker"), output);
        var postHtml = output.PostElement.GetContent();

        var script = ExtractFirstBareScriptBlock(postHtml);
        StringAssert.Contains(script, "(ns999.picker.onChange)(data);",
            "The WHOLE dotted expression must be inside the parens, call args outside — that shape is what keeps `this` bound to ns999.picker");
        AssertParsesAsValidJavaScript(script, "ColorPickerTagHelper's done: callback, dotted-member ChangeFunc");
    }
}
