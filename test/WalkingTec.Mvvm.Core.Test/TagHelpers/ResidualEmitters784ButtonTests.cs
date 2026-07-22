#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #784 (#470 residual, GROUP 2): gates
/// Abstraction/BaseButton.cs's <c>BaseButtonTag.Process</c> click-wiring
/// wrapper &lt;script&gt; behind WtmUIOptions.UseSelectIslandRender (default
/// OFF), and completes SubmitButtonTagHelper's #470 Slice M treatment — its
/// wrapper is now ALSO gated, not just its closure body.
///
/// BaseButtonTag is a base class shared by ButtonTagHelper,
/// LinkButtonTagHelper, ResetButtonTagHelper, CloseButtonTagHelper,
/// DownloadTemplateButtonTagHelper, and SubmitButtonTagHelper — this file
/// exercises the flag-OFF byte-identity guard (the #754 gate) across that
/// WHOLE family, plus the flag-ON delegated dispatch behavior.
///
/// IMPORTANT: WtmUIOptions is process-wide static state — every test that
/// flips UseSelectIslandRender ON is paired with the [TestCleanup] reset.
/// </summary>
[TestClass]
public class ResidualEmitters784ButtonTests
{
    private static void SetupLocalizer()
    {
        var localizerMock = new Mock<Microsoft.Extensions.Localization.IStringLocalizer>();
        localizerMock.Setup(x => x[It.IsAny<string>()])
            .Returns((string s) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        localizerMock.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
            .Returns((string s, object[] _) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        THProgram._localizer = localizerMock.Object;
    }

    private static TagHelperContext MakeContext()
        => new("wt:button", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput(string tagName = "button")
        => new(tagName, new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ═══════════════════════ ButtonTagHelper (generic) ════════════════════

    [TestMethod]
    public void Button_FlagOff_BareCallClick_KeepsExactLegacyWrapper_NoDataAttrs()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new ButtonTagHelper { Id = "btn1", Click = "doThing()" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$('#btn1').on('click',function(){");
        StringAssert.Contains(postHtml, "doThing();return false;");
        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-click"));
        Assert.IsFalse(postHtml.Contains("console.warn("));
    }

    [TestMethod]
    public void Button_FlagOff_CompoundClick_KeepsExactLegacyWrapper()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new ButtonTagHelper { Id = "btn2", Click = "a();b();" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$('#btn2').on('click',function(){");
        StringAssert.Contains(postHtml, "a();b();;return false;");
        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-click"));
    }

    [TestMethod]
    public void Button_FlagOn_BareCallClick_DelegatesViaDataAttrs_NoWrapperScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ButtonTagHelper { Id = "btn3", Click = "doThing()" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.AreEqual("button", output.Attributes["data-wtm-click"].Value);
        Assert.AreEqual("doThing", output.Attributes["data-wtm-clickfn"].Value);
        Assert.IsFalse(postHtml.Contains($"$('#btn3').on('click'"),
            "Flag ON with a bare-call Click must not emit the wrapper <script>");
        Assert.IsFalse(postHtml.Contains("console.warn("));
    }

    [TestMethod]
    public void Button_FlagOn_BareCallClickWithConfirmTxt_CarriesConfirmDataAttrs()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ButtonTagHelper { Id = "btn4", Click = "doThing()", ConfirmTxt = "Sure?" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.AreEqual("button", output.Attributes["data-wtm-click"].Value);
        Assert.AreEqual("doThing", output.Attributes["data-wtm-clickfn"].Value);
        Assert.AreEqual("Sure?", output.Attributes["data-wtm-confirm"].Value);
        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-confirm-title"));
        Assert.IsFalse(postHtml.Contains("layer.confirm("));
    }

    [TestMethod]
    public void Button_FlagOn_CompoundClick_KeepsLegacyWrapper_WithWarning()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ButtonTagHelper { Id = "btn5", Click = "a();b();" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-click"));
        StringAssert.Contains(postHtml, "$('#btn5').on('click',function(){");
        StringAssert.Contains(postHtml, "a();b();;return false;");
        StringAssert.Contains(postHtml, "console.warn(");
    }

    [TestMethod]
    public void Button_FlagOn_ClickWithArguments_NotBareCall_KeepsLegacyWrapper_WithWarning()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ButtonTagHelper { Id = "btn6", Click = "doThing(42)" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-click"));
        StringAssert.Contains(postHtml, "console.warn(");
    }

    [TestMethod]
    public void Button_FlagOn_NoClick_EmitsNothingAtAll()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ButtonTagHelper { Id = "btn7" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-click"));
        Assert.IsFalse(postHtml.Contains("<script>"), "No Click at all means zero click-wiring output when flag is ON");
    }

    [TestMethod]
    public void Button_FlagOn_Disabled_EmitsNothingAtAll_EvenWithClick()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ButtonTagHelper { Id = "btn8", Click = "doThing()", Disabled = true };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-click"));
        Assert.IsFalse(postHtml.Contains("<script>"));
    }

    [TestMethod]
    public void Button_FlagOff_Disabled_EmitsExactLegacyNoOpWrapper()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new ButtonTagHelper { Id = "btn9", Click = "doThing()", Disabled = true };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$('#btn9').on('click',function(){");
        Assert.IsFalse(postHtml.Contains("doThing"));
    }

    // ═══════════════════════ ResetButtonTagHelper ═════════════════════════

    [TestMethod]
    public void ResetButton_FlagOff_NoClick_ExactLegacyNoOpWrapper()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new ResetButtonTagHelper { Id = "rst1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$('#rst1').on('click',function(){");
    }

    [TestMethod]
    public void ResetButton_FlagOn_NoClick_EmitsNothingAtAll()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ResetButtonTagHelper { Id = "rst2" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-click"));
        Assert.IsFalse(postHtml.Contains("<script>"));
    }

    // ═══════════════════════ LinkButtonTagHelper ══════════════════════════
    // LinkButtonTagHelper always assigns Click to a dotted framework call
    // (ff.OpenDialog/ff.BgRequest/...) — never a bare identifier — so it is
    // NEVER island-safe via the generic path and always keeps the legacy
    // wrapper, loudly deprecated once the flag is ON.

    [TestMethod]
    public void LinkButton_FlagOff_LayerTarget_ExactLegacyWrapper()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new LinkButtonTagHelper { Id = "lnk1", Url = "/some/url" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$('#lnk1').on('click',function(){");
        StringAssert.Contains(postHtml, "ff.OpenDialog('/some/url'");
        Assert.IsFalse(postHtml.Contains("console.warn("));
    }

    [TestMethod]
    public void LinkButton_FlagOn_LayerTarget_KeepsLegacyWrapper_WithWarning()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new LinkButtonTagHelper { Id = "lnk2", Url = "/some/url" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-click"),
            "A dotted framework call (ff.OpenDialog(...)) is never generic-island-safe");
        StringAssert.Contains(postHtml, "$('#lnk2').on('click',function(){");
        StringAssert.Contains(postHtml, "ff.OpenDialog('/some/url'");
        StringAssert.Contains(postHtml, "console.warn(");
    }

    // ═══════════════════════ CloseButtonTagHelper ═════════════════════════

    [TestMethod]
    public void CloseButton_FlagOff_ExactLegacyWrapper()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new CloseButtonTagHelper { Id = "cls1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$('#cls1').on('click',function(){");
        StringAssert.Contains(postHtml, "ff.CloseDialog();return false;");
        Assert.IsFalse(postHtml.Contains("console.warn("));
    }

    [TestMethod]
    public void CloseButton_FlagOn_KeepsLegacyWrapper_WithWarning()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new CloseButtonTagHelper { Id = "cls2" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-click"));
        StringAssert.Contains(postHtml, "$('#cls2').on('click',function(){");
        StringAssert.Contains(postHtml, "ff.CloseDialog();return false;");
        StringAssert.Contains(postHtml, "console.warn(");
    }

    // ═══════════════════════ DownloadTemplateButtonTagHelper ══════════════

    private static ModelExpression MakeVmField(object model)
    {
        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(model.GetType());
        var modelExplorer = new ModelExplorer(provider, metadata, model);
        return new ModelExpression("Vm", modelExplorer);
    }

    private sealed class DummyDownloadVm
    {
    }

    [TestMethod]
    public void DownloadTemplateButton_FlagOff_NoClickEver_ExactLegacyNoOpWrapper()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new DownloadTemplateButtonTagHelper { Id = "dl1", Vm = MakeVmField(new DummyDownloadVm()) };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$('#dl1').on('click',function(){");
    }

    [TestMethod]
    public void DownloadTemplateButton_FlagOn_NoClickEver_EmitsNothingAtAll()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new DownloadTemplateButtonTagHelper { Id = "dl2", Vm = MakeVmField(new DummyDownloadVm()) };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-click"));
        Assert.IsFalse(postHtml.Contains("<script>"));
    }

    // ═══════ REVIEW FIX regression (CRITICAL, security): a literal source- ═
    // ═══════ markup `data-wtm-click` attribute must NEVER spoof subclass ═══
    // ═══════ signaling — see BaseButtonTag.SubclassDelegatedClickItemKey. ═══
    //
    // output.Attributes begins PRE-POPULATED by the Razor TagHelper runtime
    // with every literal HTML attribute present on the source tag, BEFORE
    // Process ever runs — these tests reproduce that by calling
    // output.Attributes.SetAttribute("data-wtm-click", ...) before
    // helper.Process(...), exactly mirroring what
    // `<wt:button data-wtm-click="...">` would hand the TagHelper.

    [TestMethod]
    public void Button_FlagOff_LiteralDataWtmClickMarkupAttribute_StillEmitsExactLegacyWrapper()
    {
        // HARD INVARIANT (1) regression pin: flag OFF must be byte-identical
        // regardless of what attributes the developer's markup happens to
        // carry. Before the fix, output.Attributes.ContainsName("data-wtm-click")
        // being true (from this literal attribute alone) made
        // BaseButtonTag.Process silently skip the legacy click wrapper even
        // with UseSelectIslandRender OFF.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new ButtonTagHelper { Id = "btnLit1", Click = "doThing()" };
        var output = MakeOutput();
        output.Attributes.SetAttribute("data-wtm-click", "some-unrelated-developer-value");

        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "$('#btnLit1').on('click',function(){");
        StringAssert.Contains(postHtml, "doThing();return false;");
    }

    [TestMethod]
    public void Button_FlagOn_LiteralDataWtmClickMarkupAttribute_DoesNotSkipGenericIslandLogic()
    {
        // Flag ON: a stray literal data-wtm-click attribute must not be
        // mistaken for SubmitButtonTagHelper's own subclass-to-base signal
        // (context.Items) — the generic (non-SubmitButton) island path below
        // must still run normally and correctly re-derive the delegated
        // dispatch attributes from THIS button's own Click/ConfirmTxt.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new ButtonTagHelper { Id = "btnLit2", Click = "doThing()" };
        var output = MakeOutput();
        output.Attributes.SetAttribute("data-wtm-click", "some-unrelated-developer-value");

        helper.Process(MakeContext(), output);

        Assert.AreEqual("button", output.Attributes["data-wtm-click"].Value,
            "The generic island path must overwrite the stray literal value with its own, not skip wiring entirely");
        Assert.AreEqual("doThing", output.Attributes["data-wtm-clickfn"].Value);
    }

    [TestMethod]
    public void SubmitButton_FlagOn_LegitimateSubclassSignal_StillSkipsBaseWrapper()
    {
        // Sanity check the OTHER direction: SubmitButtonTagHelper's real
        // context.Items signal must still correctly suppress
        // BaseButtonTag.Process's wrapper — the fix must not have broken the
        // legitimate collaboration it replaces.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SubmitButtonTagHelper { Id = "sblit1", Click = "myCheck()" };
        var output = MakeOutput();
        var context = new TagHelperContext("wt:submitbutton", new TagHelperAttributeList(),
            new Dictionary<object, object> { ["formid"] = "formlit1" }, "test-id");

        helper.Process(context, output);
        var postHtml = output.PostElement.GetContent();

        Assert.AreEqual("submit", output.Attributes["data-wtm-click"].Value);
        Assert.IsFalse(postHtml.Contains($"$('#sblit1').on('click'"),
            "Legitimate SubmitButtonTagHelper island wiring must still skip the base wrapper");
    }
}
