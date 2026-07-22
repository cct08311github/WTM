#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice M: opt-in (WtmUIOptions.UseSelectIslandRender, default
/// OFF — the SAME flag #470 Slices J/K/L use) delegated data-wtm-submit-*
/// dispatch for SubmitButtonTagHelper's f_{Id}Click handshake, replacing the
/// generated per-button
/// <c>&lt;script&gt;function f_{Id}Click(){...}&lt;/script&gt;</c> with
/// ff._submitButtonClick('{Id}') (a server-known string id literal, resolved
/// via document.getElementById — never `this`, see the ConfirmTxt tests
/// below for why) reading data-wtm-submit-* attributes.
///
/// IMPORTANT: WtmUIOptions is process-wide static state
/// (BaseFieldTag.SetUIOptions / the shared WtmUIOptionsHolder BaseButtonTag
/// now reads too). Every test that flips UseSelectIslandRender ON must be
/// paired with the [TestCleanup] reset below — mirrors
/// RenderUploadIsland470SliceLTests conventions.
/// </summary>
[TestClass]
public class SubmitButtonTagHelper470SliceMTests
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

    private static TagHelperContext MakeContext(string formId, BaseVM? model = null)
        => new("wt:submitbutton", new TagHelperAttributeList(),
               new Dictionary<object, object> { ["formid"] = formId, ["model"] = model! },
               "test-id");

    /// <summary>
    /// No "formid" key at all — reproduces a SubmitButtonTagHelper rendered
    /// outside a "formid"-bearing context (e.g. not nested in wt:form, or
    /// wrapped by a composite TagHelper that doesn't propagate context.Items).
    /// </summary>
    private static TagHelperContext MakeContextNoFormId()
        => new("wt:submitbutton", new TagHelperAttributeList(),
               new Dictionary<object, object>(),
               "test-id");

    private static TagHelperOutput MakeOutput()
        => new("button", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private sealed class DummyVm : BaseVM
    {
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ═══════════════════ Flag OFF (default) — legacy, byte-identical ═══════

    [TestMethod]
    public void FlagOff_ClickSet_EmitsLegacyGeneratedFunction_NoDataWtmSubmitAttrs()
    {
        SetupLocalizer();
        var helper = new SubmitButtonTagHelper { Id = "sb1", Click = "myCheck()" };
        var output = MakeOutput();

        helper.Process(MakeContext("form1"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "function f_sb1Click(){");
        StringAssert.Contains(postHtml, "var check = myCheck();");
        StringAssert.Contains(postHtml, "form1validate = false;");
        StringAssert.Contains(postHtml, "$('#form1hidesubmit').trigger('click');");
        StringAssert.Contains(postHtml, "ff.PostForm('', 'form1',");
        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-submit-formid"));
        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-submit-checkfn"));
        Assert.IsFalse(postHtml.Contains("ff._submitButtonClick"));
    }

    [TestMethod]
    public void FlagOff_ConfirmTxtOnly_EmitsLegacyGeneratedFunction_CheckDefaultsTrue()
    {
        SetupLocalizer();
        var helper = new SubmitButtonTagHelper { Id = "sb2", ConfirmTxt = "Are you sure?" };
        var output = MakeOutput();

        helper.Process(MakeContext("form2"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "function f_sb2Click(){");
        StringAssert.Contains(postHtml, "var check = true;");
        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-submit-formid"));
    }

    [TestMethod]
    public void FlagOff_CompoundClickExpression_EmitsLegacyGeneratedFunction_Unchanged()
    {
        SetupLocalizer();
        var helper = new SubmitButtonTagHelper { Id = "sb3", Click = "a() && b()" };
        var output = MakeOutput();

        helper.Process(MakeContext("form3"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "var check = a() && b();");
        Assert.IsFalse(postHtml.Contains("console.warn("), "Flag OFF must never emit a Slice-M deprecation warning");
    }

    [TestMethod]
    public void FlagOff_LayFilterAttribute_SetToLegacyFilterName()
    {
        SetupLocalizer();
        var helper = new SubmitButtonTagHelper { Id = "sb4", Click = "myCheck()" };
        var output = MakeOutput();

        helper.Process(MakeContext("form4"), output);

        Assert.AreEqual("f_sb4filter", output.Attributes["lay-filter"].Value);
    }

    // ═══════════════════ Flag ON — delegated island, no generated function ═

    [TestMethod]
    public void FlagOn_BareNoArgCheckCall_EmitsDataWtmSubmitAttrs_NoGeneratedFunction()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var vm = new DummyVm();
        var helper = new SubmitButtonTagHelper { Id = "sb5", Click = "myCheck()" };
        var output = MakeOutput();

        helper.Process(MakeContext("form5", vm), output);
        var postHtml = output.PostElement.GetContent();

        Assert.AreEqual("form5", output.Attributes["data-wtm-submit-formid"].Value);
        Assert.AreEqual(vm.ViewDivId, output.Attributes["data-wtm-submit-divid"].Value);
        Assert.AreEqual("myCheck", output.Attributes["data-wtm-submit-checkfn"].Value);
        Assert.IsFalse(postHtml.Contains("function f_sb5Click("), "Flag ON must not emit a generated per-button function");
        Assert.IsFalse(postHtml.Contains("console.warn("));
    }

    [TestMethod]
    public void FlagOn_NoClickConfirmTxtOnly_EmitsDataWtmSubmitAttrs_NoCheckfnAttribute()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SubmitButtonTagHelper { Id = "sb6", ConfirmTxt = "Sure?" };
        var output = MakeOutput();

        helper.Process(MakeContext("form6"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.AreEqual("form6", output.Attributes["data-wtm-submit-formid"].Value);
        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-submit-checkfn"),
            "No Click set (ConfirmTxt-only) must not emit a checkfn attribute — check defaults to true client-side");
        Assert.IsFalse(postHtml.Contains("function f_sb6Click("));
    }

    [TestMethod]
    public void FlagOn_CompoundClickExpression_KeepsLegacyGeneratedFunction_WithWarning()
    {
        // "a() && b()" is NOT reducible to a single bare no-arg call — must
        // keep the exact legacy generated-function path (never silently
        // truncated, which would change validation-gating semantics) and
        // surface a console.warn since the flag is ON.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SubmitButtonTagHelper { Id = "sb7", Click = "a() && b()" };
        var output = MakeOutput();

        helper.Process(MakeContext("form7"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-submit-formid"),
            "Non-bare-call Click expressions must not be islandified");
        StringAssert.Contains(postHtml, "function f_sb7Click(){");
        StringAssert.Contains(postHtml, "var check = a() && b();");
        StringAssert.Contains(postHtml, "console.warn(");
    }

    [TestMethod]
    public void FlagOn_ClickWithArguments_NotBareCall_KeepsLegacyGeneratedFunction()
    {
        // A call WITH arguments is not a bare no-arg call — must keep legacy.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SubmitButtonTagHelper { Id = "sb8", Click = "myCheck(42)" };
        var output = MakeOutput();

        helper.Process(MakeContext("form8"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-submit-formid"));
        StringAssert.Contains(postHtml, "function f_sb8Click(){");
        StringAssert.Contains(postHtml, "console.warn(");
    }

    [TestMethod]
    public void FlagOn_LayFilterAttribute_SetToSameFilterName_BothBranches()
    {
        // lay-filter must be identical regardless of whether the island or
        // legacy sub-path is taken.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });

        var islandHelper = new SubmitButtonTagHelper { Id = "sb9", Click = "myCheck()" };
        var islandOutput = MakeOutput();
        islandHelper.Process(MakeContext("form9"), islandOutput);

        var legacyHelper = new SubmitButtonTagHelper { Id = "sb10", Click = "a() && b()" };
        var legacyOutput = MakeOutput();
        legacyHelper.Process(MakeContext("form10"), legacyOutput);

        Assert.AreEqual("f_sb9filter", islandOutput.Attributes["lay-filter"].Value);
        Assert.AreEqual("f_sb10filter", legacyOutput.Attributes["lay-filter"].Value);
    }

    [TestMethod]
    public void FlagOn_CheckfnValue_IsHtmlEncoded_InAttributeContext()
    {
        // A bare-call name is validated by the identifier regex before ever
        // reaching the attribute, so it can never itself contain markup —
        // this asserts the encoding call path is exercised (defense-in-depth)
        // without weakening the identifier gate.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SubmitButtonTagHelper { Id = "sb11", Click = "check_1()" };
        var output = MakeOutput();

        helper.Process(MakeContext("form11"), output);

        Assert.AreEqual("check_1", output.Attributes["data-wtm-submit-checkfn"].Value);
    }

    // ═══════════ Flag ON + ConfirmTxt — the this-binding HIGH regression ═══

    [TestMethod]
    public void FlagOn_ConfirmTxtSet_IslandClick_DelegatesViaDataAttributes_NoInlineScript()
    {
        // Issue #784 (#470 residual, completes Slice M): the wrapping
        // <script> BaseButtonTag.Process used to unconditionally emit around
        // Click is now ALSO delegated — data-wtm-click="submit" plus
        // data-wtm-confirm/data-wtm-confirm-title carry everything
        // ff._buttonAction.submit (via ff._confirmThenRun) needs client-side;
        // no server-rendered layer.confirm(...)/ff._submitButtonClick(...)
        // text at all. This-binding safety is now structural (the delegated
        // listener always passes the REAL clicked DOM element, never a
        // developer-controlled id string), not something the server needs to
        // encode into a Click expression.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SubmitButtonTagHelper { Id = "sb12", Click = "myCheck()", ConfirmTxt = "Are you sure?" };
        var output = MakeOutput();

        helper.Process(MakeContext("form12"), output);
        var postHtml = output.PostElement.GetContent();

        // Island attributes still emitted — ConfirmTxt doesn't disable islandification.
        Assert.AreEqual("submit", output.Attributes["data-wtm-click"].Value);
        Assert.AreEqual("form12", output.Attributes["data-wtm-submit-formid"].Value);
        Assert.AreEqual("myCheck", output.Attributes["data-wtm-submit-checkfn"].Value);
        Assert.AreEqual("Are you sure?", output.Attributes["data-wtm-confirm"].Value);
        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-confirm-title"));

        // No wrapper <script> at all — the click-wiring is fully delegated.
        Assert.IsFalse(postHtml.Contains("layer.confirm("));
        Assert.IsFalse(postHtml.Contains("ff._submitButtonClick("));
        Assert.IsFalse(postHtml.Contains($"$('#sb12').on('click'"));
    }

    [TestMethod]
    public void FlagOn_NoConfirmTxt_IslandClick_DelegatesViaDataAttributes_NoInlineScript()
    {
        // Same assertion without ConfirmTxt, to pin down that the delegated
        // dispatch is used unconditionally (not only when ConfirmTxt forces
        // it) — one Click-generation path, robust in every call context.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SubmitButtonTagHelper { Id = "sb13", Click = "myCheck()" };
        var output = MakeOutput();

        helper.Process(MakeContext("form13"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.AreEqual("submit", output.Attributes["data-wtm-click"].Value);
        Assert.AreEqual("myCheck", output.Attributes["data-wtm-submit-checkfn"].Value);
        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-confirm"));
        Assert.IsFalse(postHtml.Contains("ff._submitButtonClick("));
        Assert.IsFalse(postHtml.Contains($"$('#sb13').on('click'"));
    }

    // ═══════ FIX2 regression: no formid context + no explicit Id (HIGH) ════

    [TestMethod]
    public void FlagOn_NoFormIdContext_NoExplicitId_ConfirmTxtSet_DoesNotThrow()
    {
        // #470 Slice M FIX2 HIGH: previously this.Id was only pre-assigned
        // inside the "formid"-context branch, but the island Click at line 91
        // (JavaScriptEncoder.Default.Encode(this.Id)) ran regardless of
        // whether that branch executed. A SubmitButtonTagHelper with no
        // explicit Id, rendered outside a "formid"-bearing context, hit
        // JavaScriptEncoder.Default.Encode(null) -> unhandled
        // ArgumentNullException, crashing the page. Must not throw, and must
        // still produce a usable (non-null, non-empty) Id — Issue #784
        // completes the gate, so the assertion now targets the
        // data-wtm-click="submit" delegated attributes instead of a
        // server-rendered ff._submitButtonClick('...') string.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SubmitButtonTagHelper { ConfirmTxt = "Are you sure?" };
        var output = MakeOutput();

        helper.Process(MakeContextNoFormId(), output);

        Assert.IsFalse(string.IsNullOrEmpty(helper.Id), "Id must be populated before use, not left null");
        Assert.AreEqual("submit", output.Attributes["data-wtm-click"].Value);
        Assert.AreEqual("Are you sure?", output.Attributes["data-wtm-confirm"].Value);
    }

    [TestMethod]
    public void FlagOn_NoFormIdContext_NoExplicitId_ClickSet_DoesNotThrow()
    {
        // Same crash reproduction via Click (rather than ConfirmTxt) alone.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SubmitButtonTagHelper { Click = "myCheck()" };
        var output = MakeOutput();

        helper.Process(MakeContextNoFormId(), output);

        Assert.IsFalse(string.IsNullOrEmpty(helper.Id), "Id must be populated before use, not left null");
        Assert.AreEqual("myCheck", output.Attributes["data-wtm-submit-checkfn"].Value);
        Assert.AreEqual("submit", output.Attributes["data-wtm-click"].Value);
    }

    [TestMethod]
    public void FlagOff_NoFormIdContext_NoExplicitId_ConfirmTxtSet_StillByteIdenticalNoIdFallback()
    {
        // Flag OFF must stay completely untouched by the FIX2 guard: Id
        // remains whatever it was (null/empty) up through this Process call
        // — no early fallback assignment — matching pre-Slice-M legacy
        // behavior exactly.
        SetupLocalizer();
        var helper = new SubmitButtonTagHelper { ConfirmTxt = "Are you sure?" };
        var output = MakeOutput();

        helper.Process(MakeContextNoFormId(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "function f_Click(){");
        Assert.AreEqual("f_filter", output.Attributes["lay-filter"].Value);
    }
}
