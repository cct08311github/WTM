#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Test.VM;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

// Issue #633 (#470-F, widget-islandification slice 2), reviewed regression
// coverage: a real ComboBoxTagHelper/CheckBoxTagHelper ItemUrl branch,
// running for real (not a hand-crafted island string, unlike
// SelectorTagHelperDialogInitIsland635Tests), emits a wtm-dialog-init island
// as SelectorTagHelper search-panel child content — the exact real-world
// shape #635 fixed SelectorTagHelper's tokenization for (a 'loadComboItems'
// island inside a <wt:searchpanel>, e.g. a callback-free combo/checkbox
// field used as a search filter). Before #635 landed in this branch's base,
// this WAS the reported HIGH regression: the island's own closing
// "</script>" alone matched the bare-<script> escape (no matching open
// token), corrupting the composed dialog HTML with an unclosed <script> —
// which silently swallowed ff.LoadComboItems ever being dispatched, and
// everything rendered after it. These tests prove — against the REAL
// ComboBoxTagHelper/CheckBoxTagHelper output, not a synthetic fixture — that
// #635's fix (SelectorTagHelper.cs's _regDialogInitIsland matched-pair
// tokenization, running before the bare-<script> escape) neutralizes it for
// THIS slice's emitter shape specifically.
[TestClass]
public class SelectorTagHelperLoadComboItemsIsland633Tests
{
    private const string IslandOpenTag = "<script type=\"application/json\" class=\"wtm-dialog-init\">";
    private const string ScriptCloseTag = "</script>";

    private sealed class SelectorDummyModel
    {
        public List<string>? SelectedIds { get; set; }
    }

    private sealed class ComboDummyModel
    {
        public string? StringField { get; set; }
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

    private static ModelExpression MakeSelectorField(string propertyName, object? modelValue = null)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(SelectorDummyModel).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(SelectorDummyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static ModelExpression MakeComboField(string propertyName, object? modelValue = null)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(ComboDummyModel).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(ComboDummyModel));
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

    private static TagHelperOutput MakeOutputWithChildContent(string rawChildHtml)
        => new("input", new TagHelperAttributeList(),
               (_, __) =>
               {
                   var content = new DefaultTagHelperContent();
                   content.SetHtmlContent(rawChildHtml);
                   return Task.FromResult<TagHelperContent>(content);
               });

    private static TagHelperContext MakeFieldContext(string tagName)
        => new(tagName, new TagHelperAttributeList(), new Dictionary<object, object>(), "field-id");

    private static TagHelperOutput MakeFieldOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static SelectorTagHelper CreateSelectorHelper(string id, string seed)
    {
        var listVm = new SelectorStudentListVM
        {
            Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory))
        };
        return new SelectorTagHelper
        {
            Field = MakeSelectorField("SelectedIds"),
            ListVM = MakeVm(listVm),
            TextBind = MakeStudentField("Name"),
            Id = id
        };
    }

    // Renders a REAL ComboBoxTagHelper ItemUrl branch and returns its full
    // PostElement HTML (the wtm-dialog-init island + the unconditional
    // xmSelect render <script>, exactly what a developer's
    // <wt:combobox item-url="..."> would emit inside a <wt:searchpanel>).
    private static string RenderRealComboBoxIsland(string id, string itemUrl)
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        var helper = new ComboBoxTagHelper(monitor.Object, null!)
        {
            Field = MakeComboField("StringField", "v1"),
            Id = id,
            ItemUrl = itemUrl
        };
        var output = MakeFieldOutput();
        helper.Process(MakeFieldContext("wt:combobox"), output);
        return output.PostElement.GetContent();
    }

    // Renders a REAL CheckBoxTagHelper ItemUrl branch (the one emitter that
    // additionally sets 'disabled' on the island payload).
    private static string RenderRealCheckBoxIsland(string id, string itemUrl, bool disabled)
    {
        var helper = new CheckBoxTagHelper
        {
            Field = MakeComboField("StringField", "v1"),
            Id = id,
            ItemUrl = itemUrl,
            Disabled = disabled
        };
        var output = MakeFieldOutput();
        helper.Process(MakeFieldContext("wt:checkbox"), output);
        return output.PostElement.GetContent();
    }

    private static string? ExtractBetween(string html, string open, string close)
    {
        var start = html.IndexOf(open);
        if (start < 0) return null;
        start += open.Length;
        var end = html.IndexOf(close, start);
        if (end < 0) return null;
        return html[start..end];
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }

    [TestMethod]
    public async Task ProcessAsync_RealComboBoxItemUrlIsland_SurvivesTokenizationAsMatchedPair()
    {
        SetupLocalizer();
        // Real emitter output — not a hand-crafted island string.
        var comboHtml = RenderRealComboBoxIsland("combo_sel633_1", "/Home/GetComboItems");
        StringAssert.Contains(comboHtml, "class=\"wtm-dialog-init\"",
            "Precondition: ComboBoxTagHelper must actually emit the island for this test to be meaningful");
        var comboIslandJson = ExtractBetween(comboHtml, IslandOpenTag, ScriptCloseTag);
        Assert.IsNotNull(comboIslandJson, "Precondition: could not extract the combo island JSON body");

        var selectorHelper = CreateSelectorHelper("sel633_1", System.Guid.NewGuid().ToString());
        // Simulates <wt:selector>...<wt:combobox item-url="..."/></wt:selector>:
        // the combo's rendered island becomes part of the selector's search
        // panel child content, exactly as SelectorTagHelper.ProcessAsync
        // receives it from Razor.
        var output = MakeOutputWithChildContent("<div>filter field</div>" + comboHtml);

        await selectorHelper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        // The island's own open+close tag must be tokenized as ONE matched
        // pair around the UNMODIFIED JSON body (byte-identical to what
        // ComboBoxTagHelper actually emitted).
        StringAssert.Contains(postHtml, "$$dialoginit$$" + comboIslandJson + "$$#dialoginit$$",
            "The real ComboBoxTagHelper island must tokenize as one matched dialoginit pair, JSON body untouched");

        // The literal, attribute-bearing open tag must never survive.
        Assert.IsFalse(postHtml.Contains(IslandOpenTag),
            "The literal wtm-dialog-init open tag must not appear anywhere in the emitted search-panel template");

        // No orphan token: a bare "$$#script$$" close with no matching
        // "$$script$$" open is exactly the pre-#635 corruption signature for
        // this shape. Balanced pair counts prove no orphan survived.
        var scriptOpenCount = CountOccurrences(postHtml, "$$script$$");
        var scriptCloseCount = CountOccurrences(postHtml, "$$#script$$");
        Assert.AreEqual(scriptOpenCount, scriptCloseCount,
            "Bare-script open/close token counts must balance — an orphaned $$#script$$ from the " +
            "island's close tag alone is exactly the pre-#635 corruption this test guards against");

        // The unconditional xmSelect render <script> (bare, unrelated to the
        // island) must still tokenize independently via the ordinary
        // bare-script escape.
        StringAssert.Contains(postHtml, "$$script$$",
            "The combo's own unconditional xmSelect render script must still be present, tokenized separately");

        // The loadComboItems payload itself must be intact and parseable
        // straight out of the dialoginit-wrapped body (proves the JSON was
        // never mangled by the surrounding tokenization).
        using var doc = JsonDocument.Parse(comboIslandJson!);
        var root = doc.RootElement;
        Assert.AreEqual("loadComboItems", root.GetProperty("type").GetString());
        Assert.AreEqual("combo", root.GetProperty("controlType").GetString());
        Assert.AreEqual("/Home/GetComboItems", root.GetProperty("url").GetString());
        Assert.AreEqual("combo_sel633_1", root.GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task ProcessAsync_RealCheckBoxItemUrlIsland_WithDisabledField_SurvivesTokenizationAsMatchedPair()
    {
        // CheckBoxTagHelper is the one #633 emitter that also sets
        // 'disabled' on the payload — proves that field round-trips through
        // SelectorTagHelper's tokenization untouched too.
        SetupLocalizer();
        var checkboxHtml = RenderRealCheckBoxIsland("checkbox_sel633_1", "/Home/GetCheckboxItems", disabled: true);
        var checkboxIslandJson = ExtractBetween(checkboxHtml, IslandOpenTag, ScriptCloseTag);
        Assert.IsNotNull(checkboxIslandJson);

        var selectorHelper = CreateSelectorHelper("sel633_2", System.Guid.NewGuid().ToString());
        var output = MakeOutputWithChildContent(checkboxHtml);

        await selectorHelper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$$dialoginit$$" + checkboxIslandJson + "$$#dialoginit$$");
        Assert.IsFalse(postHtml.Contains(IslandOpenTag));

        using var doc = JsonDocument.Parse(checkboxIslandJson!);
        var root = doc.RootElement;
        Assert.AreEqual("checkbox", root.GetProperty("controlType").GetString());
        Assert.IsTrue(root.GetProperty("disabled").GetBoolean(),
            "CheckBoxTagHelper's disabled:true must survive the round trip through SelectorTagHelper unchanged");
    }

    [TestMethod]
    public async Task ProcessAsync_TwoRealLoadComboItemsIslands_BothTokenizedIndependently_NoOrphanTokens()
    {
        // Two sibling ItemUrl fields inside the same search panel (a
        // realistic multi-filter searchpanel shape) — both islands must
        // round-trip independently with no cross-contamination.
        SetupLocalizer();
        var comboHtml = RenderRealComboBoxIsland("combo_sel633_3", "/Home/GetComboItems");
        var checkboxHtml = RenderRealCheckBoxIsland("checkbox_sel633_3", "/Home/GetCheckboxItems", disabled: false);
        var comboIslandJson = ExtractBetween(comboHtml, IslandOpenTag, ScriptCloseTag);
        var checkboxIslandJson = ExtractBetween(checkboxHtml, IslandOpenTag, ScriptCloseTag);
        // Issue #632 (redesigned) originally had CheckBoxTagHelper emit a
        // SECOND wtm-dialog-init island unconditionally — the back-compat-only
        // 'fieldDefaults' island (window[id+'defaultvalues'] publisher) — in
        // addition to the ItemUrl branch's 'loadComboItems' island extracted
        // above. Issue #649 removed that second island entirely (it
        // unconditionally re-published server values at DOMContentLoaded,
        // clobbering any app mutation made to the global in the meantime — see
        // framework_layui_649_clobber_regression.test.js). CheckBoxTagHelper's
        // ItemUrl branch therefore emits exactly ONE island again, matching the
        // pre-#632 shape.
        Assert.IsFalse(
            checkboxHtml[(checkboxHtml.IndexOf(checkboxIslandJson!, System.StringComparison.Ordinal) + checkboxIslandJson!.Length)..]
                .Contains(IslandOpenTag),
            "CheckBoxTagHelper must no longer emit a second (fieldDefaults) island after its loadComboItems island (#649)");

        var selectorHelper = CreateSelectorHelper("sel633_3", System.Guid.NewGuid().ToString());
        var output = MakeOutputWithChildContent(comboHtml + "<div>separator</div>" + checkboxHtml);

        await selectorHelper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$$dialoginit$$" + comboIslandJson + "$$#dialoginit$$");
        StringAssert.Contains(postHtml, "$$dialoginit$$" + checkboxIslandJson + "$$#dialoginit$$");
        Assert.IsFalse(postHtml.Contains(IslandOpenTag));

        // 2 islands total: 1 from the combo (loadComboItems) + 1 from the
        // checkbox (loadComboItems only, since #649 removed the second
        // fieldDefaults back-compat island) — restored to the pre-#632
        // expectation of 2 (1 island per widget).
        var dialogOpenCount = CountOccurrences(postHtml, "$$dialoginit$$");
        var dialogCloseCount = CountOccurrences(postHtml, "$$#dialoginit$$");
        Assert.AreEqual(2, dialogOpenCount);
        Assert.AreEqual(2, dialogCloseCount);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
