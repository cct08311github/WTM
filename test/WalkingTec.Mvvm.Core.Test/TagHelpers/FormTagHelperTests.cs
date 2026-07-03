using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #561 (#470-B slice 2) — FormTagHelper unit tests.
///
/// Verifies:
///  1. FormTagHelper emits a single wtm-dialog-init JSON island carrying an
///     initForm action (replacing ff.RenderForm(Id)) and, for standard
///     AJAX-submit forms, a bindSubmit action (replacing the inline
///     layui.form.on('submit(...)') + BeforeSubmit-gate script).
///  2. action.beforeSubmit is ALWAYS exactly the developer's BeforeSubmit
///     Razor attribute value (raw, unmutated) — null when the attribute is
///     absent — and NEVER a field/model/request-derived value (#558 trust
///     boundary).
///  3. The migrated inline script (ff.RenderForm / submit(Id+"filter")
///     binding) is gone; the residual auto-validate script
///     (submit(Id+"filterAuto")) and hidden submit button are unchanged.
///  4. OldPost forms only get the initForm action — no bindSubmit (native
///     form submission, no AJAX intercept to bind).
///  5. The island JSON is </script>-safe (System.Text.Json's default
///     encoder), matching DialogInitTagHelper's established pattern.
/// </summary>
[TestClass]
public class FormTagHelperTests
{
    private sealed class TestFormVM : BaseVM
    {
    }

    private sealed class TestSearcher : BaseSearcher
    {
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ModelExpression MakeVm(object model)
    {
        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(model.GetType());
        var modelExplorer = new ModelExplorer(provider, metadata, model);
        return new ModelExpression(string.Empty, modelExplorer);
    }

    private static TagHelperContext MakeContext()
        => new("wt:form", new TagHelperAttributeList(), new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("form", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static FormTagHelper CreateHelper(object vmModel, string url = "/Test/Save")
        => new FormTagHelper { Vm = MakeVm(vmModel), Url = url };

    private static string ExtractJsonFromIsland(string html)
    {
        const string open = "\"wtm-dialog-init\">";
        const string close = "</script>";
        var start = html.IndexOf(open);
        if (start < 0) return null;
        start += open.Length;
        var end = html.IndexOf(close, start);
        if (end < 0) return null;
        return html[start..end];
    }

    // ── TC-01: island shape — initForm + bindSubmit ────────────────────────────

    [TestMethod]
    public void Process_EmitsIsland_WithInitFormAndBindSubmitActions()
    {
        var vm = new TestFormVM();
        var helper = CreateHelper(vm);
        helper.Id = "wtForm_test1";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        StringAssert.Contains(content, "type=\"application/json\"",
            "Island script must have type=application/json");
        StringAssert.Contains(content, "class=\"wtm-dialog-init\"",
            "Island script must have class=wtm-dialog-init");

        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = JsonDocument.Parse(json);
        var actions = doc.RootElement.GetProperty("actions");
        Assert.AreEqual(2, actions.GetArrayLength(), "Must have exactly initForm + bindSubmit actions");

        var initForm = actions[0];
        Assert.AreEqual("initForm", initForm.GetProperty("type").GetString());
        Assert.AreEqual("wtForm_test1", initForm.GetProperty("filter").GetString(),
            "initForm filter must equal the form's own lay-filter (Id) — matches ff.RenderForm(Id) -> layui.form.render(null, Id)");
        Assert.IsFalse(initForm.TryGetProperty("formType", out _),
            "formType must be omitted (FormTagHelper never passes a form type to ff.RenderForm)");

        var bindSubmit = actions[1];
        Assert.AreEqual("bindSubmit", bindSubmit.GetProperty("type").GetString());
        Assert.AreEqual("wtForm_test1filter", bindSubmit.GetProperty("filter").GetString(),
            "bindSubmit filter must be Id+'filter' — the submit BUTTON's lay-filter (SubmitButtonTagHelper), a distinct identifier from the form's own lay-filter");
        Assert.AreEqual("wtForm_test1", bindSubmit.GetProperty("formId").GetString(),
            "formId must match ff.PostForm('', Id, ...)'s second argument");
        Assert.AreEqual(vm.ViewDivId, bindSubmit.GetProperty("divId").GetString(),
            "divId must match ff.PostForm(...)'s third argument (baseVM.ViewDivId)");
        Assert.IsFalse(bindSubmit.TryGetProperty("url", out _),
            "url must be omitted — ff.PostForm was always called with '' so PostForm falls back to the form's action attribute");
    }

    // ── TC-02: beforeSubmit = attribute (raw, unmutated) ───────────────────────

    [TestMethod]
    public void Process_WithBeforeSubmitAttribute_IslandCarriesRawIdentifier()
    {
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_test2";
        helper.BeforeSubmit = "myBeforeSubmitCheck";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var json = ExtractJsonFromIsland(output.PostElement.GetContent());
        using var doc = JsonDocument.Parse(json);
        var bindSubmit = doc.RootElement.GetProperty("actions")[1];
        Assert.AreEqual("myBeforeSubmitCheck", bindSubmit.GetProperty("beforeSubmit").GetString(),
            "beforeSubmit must be exactly the BeforeSubmit attribute value, with no '()' appended " +
            "(the legacy eval-style mutation is for the residual SearchPanel/OldPost inline script only; " +
            "bindSubmit resolves beforeSubmit via a plain window[name] lookup — see framework_layui.js #558)");
    }

    [TestMethod]
    public void Process_WithoutBeforeSubmitAttribute_BeforeSubmitOmitted()
    {
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_test3";
        // BeforeSubmit intentionally left unset.
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var json = ExtractJsonFromIsland(output.PostElement.GetContent());
        using var doc = JsonDocument.Parse(json);
        var bindSubmit = doc.RootElement.GetProperty("actions")[1];
        Assert.IsFalse(bindSubmit.TryGetProperty("beforeSubmit", out _),
            "beforeSubmit must be omitted (null) when BeforeSubmit is not set — the gate must never silently run");
    }

    // ── TC-03: trust boundary — beforeSubmit is NEVER a field/model value ─────

    [TestMethod]
    public void Process_BeforeSubmit_NeverLeaksVmTypeOrFieldNames()
    {
        // #558 trust-boundary regression test: FormTagHelper must only ever
        // surface the developer-authored BeforeSubmit Razor attribute as
        // action.beforeSubmit — never the VM's type name, a field name, or
        // anything else derived from Vm/model data.
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_test4";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("TestFormVM"), "The VM's type name must never leak into the island payload");

        var json = ExtractJsonFromIsland(content);
        using var doc = JsonDocument.Parse(json);
        var bindSubmit = doc.RootElement.GetProperty("actions")[1];
        Assert.IsFalse(bindSubmit.TryGetProperty("beforeSubmit", out _),
            "With no BeforeSubmit attribute set, beforeSubmit must be omitted — never backfilled from any other source");
    }

    // ── TC-04: legacy inline script removed / residual kept ────────────────────

    [TestMethod]
    public void Process_RemovesLegacyRenderFormAndSubmitBindingScript()
    {
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_test5";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("ff.RenderForm("),
            "ff.RenderForm(...) must be fully migrated to the initForm island action");
        Assert.IsFalse(content.Contains("submit(wtForm_test5filter)"),
            "The migrated submit(Id+'filter') binding must not appear as raw inline JS anymore " +
            "(only as a JSON string value inside the island)");
    }

    [TestMethod]
    public void Process_KeepsResidualAutoValidateScriptAndHiddenButton()
    {
        // The auto-validate handler (submit(Id+'filterAuto')) has no
        // corresponding DispatchAction action type — documented residual
        // inline <script>, intentionally out of scope for Issue #561.
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_test6";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        StringAssert.Contains(content, "submit(wtForm_test6filterAuto)",
            "The auto-validate handler must remain as a residual inline script");
        StringAssert.Contains(content, "<script>",
            "The residual auto-validate block must still be wrapped in its own <script> tag");

        var postContent = output.PostContent.GetContent();
        StringAssert.Contains(postContent, "wtForm_test6hidesubmit",
            "The hidden auto-submit button must remain unchanged");
    }

    // ── TC-05: OldPost forms — initForm only, no bindSubmit ────────────────────

    [TestMethod]
    public void Process_OldPost_EmitsOnlyInitFormAction_NoBindSubmit()
    {
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_test7";
        helper.OldPost = true;
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(content);
        using var doc = JsonDocument.Parse(json);
        var actions = doc.RootElement.GetProperty("actions");
        Assert.AreEqual(1, actions.GetArrayLength(),
            "OldPost forms must not get a bindSubmit action — native form submission needs no AJAX intercept");
        Assert.AreEqual("initForm", actions[0].GetProperty("type").GetString());

        Assert.IsFalse(content.Contains("filterAuto"),
            "OldPost forms must not emit the auto-validate handler either (guarded by the same condition)");
    }

    // ── TC-06: script-injection safety ──────────────────────────────────────

    [TestMethod]
    public void Json_BeforeSubmitWithCloseScript_IsUnicodeEscaped()
    {
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_test8";
        helper.BeforeSubmit = "</script><script>alert(1)</script>";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("</script><script>alert(1)"),
            "A </script> inside an attribute value must not break out of the island's <script> block");

        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json);
        var bindSubmit = doc.RootElement.GetProperty("actions")[1];
        Assert.AreEqual("</script><script>alert(1)</script>", bindSubmit.GetProperty("beforeSubmit").GetString(),
            "Decoded value must round-trip — the raw attribute is Unicode-escaped for safe embedding, not stripped or rejected");
    }

    [TestMethod]
    public void Process_IdWithAmpersand_IsUnicodeEscapedInIsland()
    {
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_a&b";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        Assert.IsFalse(json.Contains("\"wtForm_a&b\""), "Raw & must be Unicode-escaped in the JSON");

        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("wtForm_a&b", doc.RootElement.GetProperty("actions")[0].GetProperty("filter").GetString(),
            "Decoded filter must round-trip correctly");
    }

    // ── TC-07: BaseSearcher-only VM — divId gracefully omitted ─────────────────

    [TestMethod]
    public void Process_SearcherOnlyVm_BindSubmitDivIdOmitted()
    {
        // When Vm.Model is a bare BaseSearcher (not a BaseVM), baseVM stays
        // null, so bindSubmit's divId (sourced from baseVM?.ViewDivId) must be
        // omitted rather than throwing — matching the original code's
        // null-conditional '{baseVM?.ViewDivId}' interpolation (which rendered
        // an empty string in this case).
        var helper = CreateHelper(new TestSearcher());
        helper.Id = "wtForm_test9";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var json = ExtractJsonFromIsland(output.PostElement.GetContent());
        Assert.IsNotNull(json, "Island must still be emitted for a Searcher-bound form");
        using var doc = JsonDocument.Parse(json);
        var bindSubmit = doc.RootElement.GetProperty("actions")[1];
        Assert.IsFalse(bindSubmit.TryGetProperty("divId", out _),
            "divId must be omitted (null) when there is no BaseVM to source ViewDivId from");
    }
}
