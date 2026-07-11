#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Test.VM;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

// Issue #652 (SECURITY, stored XSS — dialog trust boundary follow-up to #651):
// <wt:selector> composes its search-panel child content (`content`, the
// nested field TagHelpers a developer places inside <wt:selector>...
// </wt:selector>, e.g. <wt:radio>/<wt:checkbox>/<wt:tree>/<wt:taginput>
// searchers) into a `<script type="text/template" id="Temp{Id}">` template.
// Every field TagHelper HtmlEncodes its MODEL-DERIVED plaintext (an option
// label, a stored value, a tree node title, a tag) before writing it into
// that content — WebUtility.HtmlEncode escapes '<', '>', '&', '"', ''' but
// NOT '$'. So a stored value containing the literal text
// "$$script$$window.__pwned=1$$#script$$" survives HtmlEncode into `content`
// untouched, is NOT itself a real &lt;script&gt; element (so the pre-existing
// tokenize replaces below never "consume" it), and would previously reach
// framework_layui.js's ff.OpenDialog2, whose $$script$$/$$#script$$
// un-tokenize replace is GLOBAL over the whole template — turning the
// plaintext into a live &lt;script&gt; the instant the selector dialog opens.
//
// The fix (SelectorTagHelper.cs, immediately before the two existing
// tokenize replaces): escape every literal '$' in `content` to the private-
// use placeholder U+E000 (DollarEscapePlaceholder). After that escape,
// `content` contains ZERO literal '$' characters, so the ONLY '$' sequences
// the emitted template can ever carry are the $$dialoginit$$/$$script$$
// tokens SelectorTagHelper itself places immediately afterward — a forged
// "$$script$$" arriving via model data can no longer exist. Real developer
// <script> tags (and any '$' inside them, e.g. jQuery's `$(...)`) still get
// correctly tokenized (the placeholder escape runs BEFORE the tokenize
// replaces, so a genuine &lt;script&gt; literal is unaffected — it lives in
// the Razor-authored markup, not model data) and framework_layui.js's
// ff.OpenDialog2 restores the placeholder back to '$' AFTER un-tokenizing,
// so real scripts execute with their original '$' intact.
//
// These tests assert the SHAPE of that escape on the C# side, following the
// exact harness established by SelectorTagHelperDialogInitIsland635Tests.cs
// (MakeOutputWithChildContent hand-feeds raw child HTML standing in for
// whatever a nested field TagHelper would have rendered — the smallest
// faithful level this harness supports, matching that file's own convention
// rather than inventing a new one). The JS-side restore
// (template.split(placeholder).join('$')) is covered by
// framework_layui_652_selector_dollar_escape.test.js.
//
// Scope note: the PER-SELECTED-ITEM hidden input SelectorTagHelper emits at
// its "Issue #108" site (`<input type='hidden' name='{Field.Name}'
// value='{WebUtility.HtmlEncode(item)}' />`) is a SEPARATE, non-templated
// channel — it is written directly into output.PostElement, never into
// `content`/searchPanelTemplate, so it never reaches ff.OpenDialog2's
// tokenize/un-tokenize pipeline and is out of scope for this file (it has no
// sentinel-collision exposure to escape).
[TestClass]
public class SelectorSentinelDollarEscape652Tests
{
    // The placeholder character SelectorTagHelper.cs escapes '$' to
    // (DollarEscapePlaceholder). Kept identical here (not referenced via the
    // private const) so the test independently pins the exact wire shape
    // ff.OpenDialog2's JS-side restore must match byte-for-byte.
    private const string Placeholder = "";

    private sealed class DummyModel
    {
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

    private static ModelExpression MakeVm(object model)
    {
        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(model.GetType());
        var modelExplorer = new ModelExplorer(provider, metadata, model);
        return new ModelExpression(string.Empty, modelExplorer);
    }

    private static ModelExpression MakeStudentField(string propertyName)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(Student).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(Student));
        var modelExplorer = new ModelExplorer(provider, metadata, null);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext()
        => new("wt:selector", new TagHelperAttributeList(), new Dictionary<object, object>(), "test-id");

    // Same as SelectorTagHelperDialogInitIsland635Tests.MakeOutputWithChildContent:
    // hand-feeds raw child HTML standing in for whatever a nested field
    // TagHelper rendered inside <wt:selector>...</wt:selector> (already
    // HtmlEncoded by that TagHelper, exactly as production code would leave
    // it — HtmlEncode never touches '$').
    private static TagHelperOutput MakeOutputWithChildContent(string rawChildHtml)
        => new("input", new TagHelperAttributeList(),
               (_, __) =>
               {
                   var content = new DefaultTagHelperContent();
                   content.SetHtmlContent(rawChildHtml);
                   return Task.FromResult<TagHelperContent>(content);
               });

    private static SelectorTagHelper CreateHelper(string id, string seed)
    {
        var listVm = new SelectorStudentListVM
        {
            Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory))
        };
        return new SelectorTagHelper
        {
            Field = MakeField("SelectedIds"),
            ListVM = MakeVm(listVm),
            TextBind = MakeStudentField("Name"),
            Id = id
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }

    [TestMethod]
    public async Task ProcessAsync_MaliciousPlaintextSentinel_NoForgeableTokenSurvives()
    {
        // Simulates a stored option label/value that survived some nested
        // field TagHelper's WebUtility.HtmlEncode unchanged (HtmlEncode does
        // not touch '$') and landed in the search-panel content as plain
        // text — the exact #652 attack shape.
        SetupLocalizer();
        var helper = CreateHelper("sel652_1", System.Guid.NewGuid().ToString());
        const string maliciousLabel = "$$script$$window.__pwned=1$$#script$$";
        // WebUtility.HtmlEncode is a documented no-op on this string (no
        // <>&"' present) — applying it here makes explicit that this is
        // exactly what a real field TagHelper would have emitted.
        var encoded = System.Net.WebUtility.HtmlEncode(maliciousLabel);
        Assert.AreEqual(maliciousLabel, encoded, "sanity: HtmlEncode must not touch '$'");
        var output = MakeOutputWithChildContent("<div class=\"layui-form-mid\">" + encoded + "</div>");

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        // No forgeable sentinel of any kind may survive — the ONLY $$...$$
        // sequences a valid template may ever carry are the ones
        // SelectorTagHelper itself places, and none were placed here (no
        // real <script>/island in this content).
        Assert.IsFalse(postHtml.Contains("$$script$$window.__pwned=1$$#script$$"),
            "The literal malicious sentinel text must not survive verbatim");
        Assert.IsFalse(postHtml.Contains("$$script$$"),
            "No $$script$$ token may be forged from model-derived plaintext");
        Assert.IsFalse(postHtml.Contains("$$#script$$"),
            "No $$#script$$ token may be forged from model-derived plaintext");

        // The '$' characters were escaped to the placeholder, preserving the
        // text losslessly (round-trippable by the client-side restore).
        StringAssert.Contains(
            postHtml,
            Placeholder + Placeholder + "script" + Placeholder + Placeholder +
            "window.__pwned=1" + Placeholder + Placeholder + "#script" + Placeholder + Placeholder,
            "Every '$' in the malicious label must be escaped to the placeholder, 1:1");
    }

    [TestMethod]
    public async Task ProcessAsync_RealScriptTag_StillTokenizedAfterDollarEscape()
    {
        // Regression guard: a genuine Razor-authored <script> in the panel
        // content (no '$' inside it) must still be tokenized exactly as
        // before #652 — the new escape step must not interfere with real
        // sentinel placement.
        SetupLocalizer();
        var helper = CreateHelper("sel652_2", System.Guid.NewGuid().ToString());
        var output = MakeOutputWithChildContent("<script>var x=1;</script>");

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$$script$$var x=1;$$#script$$",
            "A real <script> element must still be tokenized as a matched sentinel pair");
    }

    [TestMethod]
    public async Task ProcessAsync_RealScriptWithJQueryDollar_DollarEscapedInsideTokenizedSegment()
    {
        // Proves the escape ordering: DollarEscapePlaceholder runs BEFORE the
        // bare-<script> tokenize replace, so a legitimate '$' inside a real
        // developer script (e.g. jQuery's $(...)) is escaped too — and lands
        // INSIDE the $$script$$...$$#script$$ segment, ready for
        // ff.OpenDialog2's client-side restore to reconstitute it AFTER
        // un-tokenizing.
        SetupLocalizer();
        var helper = CreateHelper("sel652_3", System.Guid.NewGuid().ToString());
        var output = MakeOutputWithChildContent("<script>$('#x').val(1);</script>");

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(
            postHtml,
            "$$script$$" + Placeholder + "('#x').val(1);$$#script$$",
            "The jQuery '$' must be escaped to the placeholder INSIDE the tokenized script segment");
        // No literal '$' survives inside the segment body itself (only the
        // sentinel tokens SelectorTagHelper placed carry '$').
        Assert.IsFalse(postHtml.Contains("$('#x')"),
            "The real jQuery '$' must not survive un-escaped");
    }

    [TestMethod]
    public async Task ProcessAsync_OrdinaryDollarInLabel_EscapedAndNonDestructive()
    {
        // Non-destructive guard: an everyday label containing a single '$'
        // (e.g. a price) must be losslessly escaped and must never itself
        // form a live sentinel — proving the fix does not require the
        // content to look attack-shaped to trigger correctly.
        // Uses <span> (not a trailing <div>...</div>) so this test is
        // unaffected by SelectorTagHelper's separate, pre-existing
        // _regColDivEnd trailing-</div> strip (the RowTagHelper
        // wrapper-removal step, unrelated to #652) — same convention as
        // SelectorTagHelperDialogInitIsland635Tests's
        // ProcessAsync_NoScriptOrIsland_TemplateUnaffected.
        SetupLocalizer();
        var helper = CreateHelper("sel652_4", System.Guid.NewGuid().ToString());
        var output = MakeOutputWithChildContent("<span>Price: $5</span>");

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "<span>Price: " + Placeholder + "5</span>",
            "The lone '$' must be escaped to the placeholder");
        Assert.IsFalse(postHtml.Contains("$$"),
            "No sentinel-shaped '$$' pair may be produced from ordinary content");
    }

    [TestMethod]
    public async Task ProcessAsync_NoDollarInContent_TemplateUnaffectedBesidesFrameworkTokens()
    {
        // Default-behaviour guard: content with no '$' at all must be
        // completely unaffected by the new escape step (zero behaviour
        // change for the common case — project red line).
        SetupLocalizer();
        var helper = CreateHelper("sel652_5", System.Guid.NewGuid().ToString());
        var output = MakeOutputWithChildContent("<span>plain field, no dollars</span>");

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "<span>plain field, no dollars</span>");
        Assert.IsFalse(postHtml.Contains(Placeholder),
            "No placeholder character may appear when the content carries no '$'");
    }
}
