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

// Issue #635 (#470 prerequisite): a field TagHelper nested inside a
// <wt:searchpanel> (e.g. a callback-free <wt:datetime>) can emit a
// <script type="application/json" class="wtm-dialog-init"> JSON island into
// SelectorTagHelper's search-panel child content. BEFORE this change, only
// bare <script>/</script> was escaped to $$script$$/$$#script$$ — an
// island's OPENING tag (which carries attributes) never matched that
// literal, but its CLOSING "</script>" DID (the escape's blanket replace is
// not scoped to bare-script pairs). That left the island's close tag alone
// tokenized while its open tag stayed literal: a mismatched, unpaired
// token that framework_layui.js's OpenDialog2 could neither treat as a
// matched legacy-script pair (kill-switch strip) nor restore to a valid tag
// (legacy convert-back only flips whole token pairs) — corrupting the
// composed dialog HTML with an unclosed <script>.
//
// The fix tokenizes a wtm-dialog-init island's own open+close tag as ONE
// matched pair, with dedicated $$dialoginit$$/$$#dialoginit$$ sentinel
// tokens, BEFORE the pre-existing bare-<script> escape runs. These tests
// assert the SHAPE of that tokenization (C# side only) — the JS-side
// rehydration/dispatch is covered by
// test/WalkingTec.Mvvm.Js.Tests/__tests__/framework_layui_635_opendialog2_island_dispatch.test.js,
// which hand-crafts already-tokenized fixtures (matching the existing #627
// suite's convention) rather than re-running this TagHelper.
[TestClass]
public class SelectorTagHelperDialogInitIsland635Tests
{
    private const string IslandOpenTag = "<script type=\"application/json\" class=\"wtm-dialog-init\">";
    private const string ScriptCloseTag = "</script>";

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

    // Unlike SelectorTagHelperTests.MakeOutput (which always yields EMPTY
    // child content — irrelevant to this file's concern), this builds an
    // output whose GetChildContentAsync() returns caller-supplied raw HTML,
    // simulating what a nested Form/ field TagHelper (or bare inline
    // <script>) would have rendered inside <wt:selector>...</wt:selector>.
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
    public async Task ProcessAsync_ChildIsland_TokenizedAsMatchedPair_NoLiteralOpenTagSurvives()
    {
        SetupLocalizer();
        var helper = CreateHelper("sel635_1", System.Guid.NewGuid().ToString());
        var island = "{\"actions\":[{\"type\":\"laydate\",\"opts\":{\"elem\":\"#d1\"}}]}";
        var output = MakeOutputWithChildContent("<div>field</div>" + IslandOpenTag + island + ScriptCloseTag);

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        // The dedicated sentinel pair must wrap the JSON body unchanged.
        StringAssert.Contains(postHtml, "$$dialoginit$$" + island + "$$#dialoginit$$",
            "The island's own open+close tag must be tokenized as ONE matched pair around the unmodified JSON body");

        // The literal, attribute-bearing open tag must never survive — it must
        // be fully consumed by the dialoginit tokenization, not left behind
        // for the (untargeted) bare-<script> escape to accidentally half-touch.
        Assert.IsFalse(postHtml.Contains(IslandOpenTag),
            "The literal wtm-dialog-init open tag must not appear anywhere in the emitted template");
    }

    [TestMethod]
    public async Task ProcessAsync_ChildIsland_NoOrphanedScriptCloseToken()
    {
        // Regression guard for the exact #635 corruption: pre-fix, the
        // island's close tag alone became "$$#script$$" (blanket-replaced by
        // the bare-script escape) while its open tag stayed literal — an
        // unpaired, orphaned token. Post-fix, "$$#script$$" (the BARE-script
        // close token) must not appear at all when the only script-like
        // element in the content is a wtm-dialog-init island.
        SetupLocalizer();
        var helper = CreateHelper("sel635_2", System.Guid.NewGuid().ToString());
        var island = "{\"type\":\"alert\",\"message\":\"hi\"}";
        var output = MakeOutputWithChildContent(IslandOpenTag + island + ScriptCloseTag);

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("$$#script$$"),
            "No bare-script close token may be produced from an island's own closing tag");
        Assert.IsFalse(postHtml.Contains("$$script$$"),
            "No bare-script open token may be produced — nothing here matched the bare <script> literal");
        StringAssert.Contains(postHtml, "$$dialoginit$$" + island + "$$#dialoginit$$");
    }

    [TestMethod]
    public async Task ProcessAsync_BareScriptAlone_StillTokenizedAsBefore()
    {
        // Regression guard: the pre-existing bare-<script> escape (unrelated
        // to islands) must be completely unaffected by this change.
        SetupLocalizer();
        var helper = CreateHelper("sel635_3", System.Guid.NewGuid().ToString());
        var output = MakeOutputWithChildContent("<script>var x=1;</script>");

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$$script$$var x=1;$$#script$$");
        Assert.IsFalse(postHtml.Contains("dialoginit"),
            "A plain bare script must never produce a dialoginit token");
    }

    [TestMethod]
    public async Task ProcessAsync_BareScriptAndIsland_BothTokenizedIndependently_NoInterleavingCorruption()
    {
        // The realistic shape: a developer inline <script> (e.g. a legacy
        // change-func wiring) followed by a callback-free field's island —
        // both must round-trip independently, regardless of adjacency.
        SetupLocalizer();
        var helper = CreateHelper("sel635_4", System.Guid.NewGuid().ToString());
        var island = "{\"actions\":[{\"type\":\"initForm\",\"filter\":\"f1\"}]}";
        var output = MakeOutputWithChildContent(
            "<script>var x=1;</script>" + IslandOpenTag + island + ScriptCloseTag);

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$$script$$var x=1;$$#script$$");
        StringAssert.Contains(postHtml, "$$dialoginit$$" + island + "$$#dialoginit$$");
        Assert.IsFalse(postHtml.Contains(IslandOpenTag));
        Assert.IsFalse(postHtml.Contains("$$#script$$$$dialoginit$$" + island + "$$script$$"),
            "Tokens must not cross-pair across the bare-script/island boundary");
    }

    [TestMethod]
    public async Task ProcessAsync_IslandThenBareScript_OrderReversed_BothTokenizedIndependently()
    {
        // Same as above with the island BEFORE the bare script — proves the
        // fix does not depend on which comes first (the pre-#635 corruption
        // manifested regardless of ordering; see the #635 PR analysis).
        SetupLocalizer();
        var helper = CreateHelper("sel635_5", System.Guid.NewGuid().ToString());
        var island = "{\"type\":\"rate\",\"opts\":{\"elem\":\"#r1\"}}";
        var output = MakeOutputWithChildContent(
            IslandOpenTag + island + ScriptCloseTag + "<script>var y=2;</script>");

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$$dialoginit$$" + island + "$$#dialoginit$$");
        StringAssert.Contains(postHtml, "$$script$$var y=2;$$#script$$");
        Assert.IsFalse(postHtml.Contains(IslandOpenTag));
    }

    [TestMethod]
    public async Task ProcessAsync_TwoIslands_BothTokenizedIndependently()
    {
        SetupLocalizer();
        var helper = CreateHelper("sel635_6", System.Guid.NewGuid().ToString());
        var island1 = "{\"type\":\"slider\",\"opts\":{\"elem\":\"#s1\"}}";
        var island2 = "{\"type\":\"colorpicker\",\"opts\":{\"elem\":\"#c1\"}}";
        var output = MakeOutputWithChildContent(
            IslandOpenTag + island1 + ScriptCloseTag + "<div></div>" + IslandOpenTag + island2 + ScriptCloseTag);

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$$dialoginit$$" + island1 + "$$#dialoginit$$");
        StringAssert.Contains(postHtml, "$$dialoginit$$" + island2 + "$$#dialoginit$$");
        Assert.IsFalse(postHtml.Contains(IslandOpenTag));
    }

    [TestMethod]
    public async Task ProcessAsync_NoScriptOrIsland_TemplateUnaffected()
    {
        // Default-behaviour guard: content with no <script> of any kind must
        // be completely untouched by both escapes (zero behaviour change —
        // project red line). Uses <span> (not a trailing <div>...</div>) so
        // this test is unaffected by SelectorTagHelper's separate, pre-
        // existing _regColDivEnd trailing-</div> strip (the RowTagHelper
        // wrapper-removal step, unrelated to #635) and stays focused purely
        // on script/island tokenization.
        SetupLocalizer();
        var helper = CreateHelper("sel635_7", System.Guid.NewGuid().ToString());
        var output = MakeOutputWithChildContent("<span>plain field, no scripts</span>");

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "<span>plain field, no scripts</span>");
        Assert.IsFalse(postHtml.Contains("dialoginit"));
        Assert.IsFalse(postHtml.Contains("$$script$$"));
    }
}
