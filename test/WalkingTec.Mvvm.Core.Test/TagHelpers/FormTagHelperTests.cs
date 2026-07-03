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
/// Issue #561 (#470-B slice 2), extended by #564 (#470-D) — FormTagHelper unit tests.
///
/// Verifies:
///  1. FormTagHelper emits a single wtm-dialog-init JSON island carrying an
///     initForm action (replacing ff.RenderForm(Id)) and, for standard
///     AJAX-submit forms, a bindSubmit action (replacing the inline
///     layui.form.on('submit(...)') + BeforeSubmit-gate script) and a
///     bindValidate action (replacing the inline auto-validate script).
///  2. action.beforeSubmit is ALWAYS exactly the developer's BeforeSubmit
///     Razor attribute value (raw, unmutated) — null when the attribute is
///     absent — and NEVER a field/model/request-derived value (#558 trust
///     boundary).
///  3. The migrated inline scripts (ff.RenderForm / submit(Id+"filter")
///     binding, auto-validate submit(Id+"filterAuto") binding, ModelState
///     error highlight/focus) are gone for a fully-migrated plain dialog
///     form — the hidden submit button remains (it is a real DOM element,
///     not a script).
///  4. OldPost forms only get the initForm action — no bindSubmit/bindValidate
///     (native form submission, no AJAX intercept to bind).
///  5. The island JSON is </script>-safe (System.Text.Json's default
///     encoder), matching DialogInitTagHelper's established pattern.
///  6. ModelState errors are carried as a 'highlightErrors' island action
///     whose messages are the RAW (non-HTML-encoded) error text — the JS
///     side inserts them via textContent, so pre-encoding would double-encode.
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

    // Issue #564: attaches a WTMContext with ModelState errors to a BaseVM so
    // FormTagHelper's highlightErrors island action gets exercised. Mirrors the
    // `new WTMContext(null)` pattern used elsewhere in this test project — with
    // no HttpContext, the constructor auto-creates wtm.MSD = new BasicMSD().
    private static void AddModelError(BaseVM vm, string key, string message)
    {
        vm.Wtm ??= new WTMContext(null);
        vm.Wtm.MSD ??= new BasicMSD();
        vm.Wtm.MSD.AddModelError(key, message);
    }

    // ── TC-01: island shape — initForm + bindSubmit + bindValidate ─────────────

    [TestMethod]
    public void Process_EmitsIsland_WithInitFormBindSubmitAndBindValidateActions()
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
        Assert.AreEqual(3, actions.GetArrayLength(),
            "Must have exactly initForm + bindSubmit + bindValidate actions (no ModelState errors, so no highlightErrors)");

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

        var bindValidate = actions[2];
        Assert.AreEqual("bindValidate", bindValidate.GetProperty("type").GetString());
        Assert.AreEqual("wtForm_test1filterAuto", bindValidate.GetProperty("filter").GetString(),
            "bindValidate filter must be Id+'filterAuto' — matches the hidden #{Id}hidesubmit button's lay-filter");
        Assert.AreEqual("wtForm_test1", bindValidate.GetProperty("formId").GetString(),
            "bindValidate formId must be the form's own Id — framework_layui.js derives the window[formId+'validate'] flag name from it");

        Assert.IsFalse(content.Contains("<script>"),
            "A plain dialog form (no non-identifier BeforeSubmit, no SearchPanel/OldPost, no ModelState errors) must emit zero inline <script> tags");
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

    // Re-assert the two migrated cases (absent / bare-identifier) do NOT emit a
    // legacy inline submit binding — the whole point of #561 is that these
    // migrate onto the island. Complements the non-identifier test below, which
    // asserts the OPPOSITE for the fallback case.

    [TestMethod]
    public void Process_BareIdentifierBeforeSubmit_NoInlineSubmitBinding()
    {
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_id2";
        helper.BeforeSubmit = "myBeforeSubmitCheck"; // bare identifier
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("layui.form.on('submit(wtForm_id2filter)'"),
            "A bare-identifier BeforeSubmit must migrate to the island bindSubmit action — no legacy inline submit binding");
    }

    [TestMethod]
    public void Process_AbsentBeforeSubmit_NoInlineSubmitBinding()
    {
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_id3";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("layui.form.on('submit(wtForm_id3filter)'"),
            "An absent BeforeSubmit must migrate to the island bindSubmit action — no legacy inline submit binding");
    }

    // ── TC-02c: NON-identifier BeforeSubmit falls back to the legacy inline
    //             submit <script> (compat fix — never silently drop the gate) ────

    [TestMethod]
    public void Process_NonIdentifierBeforeSubmit_KeepsLegacyInlineSubmit_IslandHasInitFormOnly()
    {
        // Compat fix: framework_layui.js's bindSubmit window[name] lookup accepts
        // ONLY a bare identifier. A call-expression like "obj.Check()" would be
        // silently gate-skipped if emitted as an island bindSubmit action, so
        // FormTagHelper must instead keep the legacy inline
        // layui.form.on('submit(<Id>filter)') <script> (with the "()"-mutated
        // BeforeSubmit run as a live JS expression) and emit initForm ONLY in the
        // island — the developer's submit gate keeps working.
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_expr";
        helper.BeforeSubmit = "obj.Check()"; // non-identifier: dotted + call expression
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();

        // 1. The legacy inline submit binding IS present, with the gate expression.
        StringAssert.Contains(content, "layui.form.on('submit(wtForm_exprfilter)'",
            "A non-identifier BeforeSubmit must keep the legacy inline submit binding");
        StringAssert.Contains(content, "if(obj.Check() == false){return false;}",
            "The legacy inline gate must run the developer's BeforeSubmit expression as-is (already contains '()', so no mutation)");
        StringAssert.Contains(content, "ff.PostForm('', 'wtForm_expr'",
            "The legacy inline binding must post via ff.PostForm exactly as before #561");

        // 2. The island carries initForm + bindValidate — no bindSubmit action
        //    (which would otherwise silently drop the gate). bindValidate is
        //    unconditional (#564): it does not depend on BeforeSubmit at all, so
        //    it is still emitted even on this legacy-inline-submit fallback path.
        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json, "The initForm island must still be emitted for form.render");
        using var doc = JsonDocument.Parse(json);
        var actions = doc.RootElement.GetProperty("actions");
        Assert.AreEqual(2, actions.GetArrayLength(),
            "A non-identifier BeforeSubmit must emit initForm + bindValidate (no bindSubmit action)");
        Assert.AreEqual("initForm", actions[0].GetProperty("type").GetString());
        Assert.AreEqual("wtForm_expr", actions[0].GetProperty("filter").GetString());
        Assert.AreEqual("bindValidate", actions[1].GetProperty("type").GetString());
        Assert.AreEqual("wtForm_exprfilterAuto", actions[1].GetProperty("filter").GetString());

        // 3. The raw non-identifier expression must NEVER appear as a JSON
        //    beforeSubmit island value (that is the silent-drop path we are closing).
        Assert.IsFalse(json.Contains("\"bindSubmit\""),
            "No bindSubmit action may be present in the island for a non-identifier BeforeSubmit");
        Assert.IsFalse(json.Contains("beforeSubmit"),
            "The non-identifier BeforeSubmit must not be carried as an island beforeSubmit value");
    }

    [TestMethod]
    public void Process_DottedNonCallBeforeSubmit_MutatedAndKeptInline()
    {
        // A dotted-but-parenless BeforeSubmit ("this.Validate") is still a
        // non-identifier, so it takes the legacy inline path. It does NOT already
        // contain '(', so the legacy "()"-mutation applies → this.Validate().
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_dot";
        helper.BeforeSubmit = "this.Validate";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        StringAssert.Contains(content, "if(this.Validate() == false){return false;}",
            "A dotted parenless BeforeSubmit must be '()'-mutated and gated inline, exactly as the pre-#561 legacy path did");

        var json = ExtractJsonFromIsland(content);
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual(2, doc.RootElement.GetProperty("actions").GetArrayLength(),
            "A dotted non-identifier BeforeSubmit must emit initForm + bindValidate (no bindSubmit)");
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
    public void Process_MigratesAutoValidateToIslandAction_KeepsHiddenButton()
    {
        // Issue #564: the auto-validate handler (submit(Id+'filterAuto')) is now
        // a 'bindValidate' island action — no more residual inline <script> for
        // this path. The hidden #{Id}hidesubmit button is a real DOM element
        // (not a script), so it remains completely unchanged.
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_test6";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("submit(wtForm_test6filterAuto)'"),
            "The auto-validate submit(...) binding must no longer appear as raw inline JS " +
            "(only as a JSON string value inside the island)");
        Assert.IsFalse(content.Contains("<script>"),
            "This form has no non-identifier BeforeSubmit / SearchPanel / ModelState errors, " +
            "so it must emit zero inline <script> tags after the #564 migration");

        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json);
        var actions = doc.RootElement.GetProperty("actions");
        var bindValidate = actions[actions.GetArrayLength() - 1];
        Assert.AreEqual("bindValidate", bindValidate.GetProperty("type").GetString());
        Assert.AreEqual("wtForm_test6filterAuto", bindValidate.GetProperty("filter").GetString());
        Assert.AreEqual("wtForm_test6", bindValidate.GetProperty("formId").GetString());

        var postContent = output.PostContent.GetContent();
        StringAssert.Contains(postContent, "wtForm_test6hidesubmit",
            "The hidden auto-submit button must remain unchanged — it is a real DOM element, not a script");
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
    public void Json_BeforeSubmitWithCloseScript_TakesLegacyInlinePath_NeverIslandBeforeSubmit()
    {
        // A "</script>..." BeforeSubmit is a non-identifier, so (per the #561
        // compat fix) it CANNOT be an island bindSubmit value — the island's
        // window[name] guard only accepts bare identifiers. It therefore takes
        // the legacy inline submit path, exactly as pre-#561. BeforeSubmit is a
        // trusted, compile-time developer-authored Razor literal (never request /
        // user data — the #558 trust boundary), so this raw interpolation matches
        // the original framework behaviour and is not a new injection surface.
        // The key invariant this test locks: such a value is NEVER carried as a
        // JSON island `beforeSubmit` (which would be the silent-drop path) — the
        // island stays initForm-only.
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_test8";
        helper.BeforeSubmit = "</script><script>alert(1)</script>";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();

        // The island must NOT carry this value as a bindSubmit beforeSubmit.
        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        Assert.IsFalse(json.Contains("beforeSubmit"),
            "A non-identifier BeforeSubmit must never appear as an island beforeSubmit value");
        using var doc = JsonDocument.Parse(json);
        var actions = doc.RootElement.GetProperty("actions");
        Assert.AreEqual(2, actions.GetArrayLength(),
            "A non-identifier BeforeSubmit must emit initForm + bindValidate only (no island bindSubmit)");
        Assert.AreEqual("initForm", actions[0].GetProperty("type").GetString());
        Assert.AreEqual("bindValidate", actions[1].GetProperty("type").GetString());

        // It takes the legacy inline submit path instead (gate still runs).
        StringAssert.Contains(content, "layui.form.on('submit(wtForm_test8filter)'",
            "A non-identifier BeforeSubmit falls back to the legacy inline submit binding so its gate still runs");
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

    // ── TC-08: highlightErrors island action (Issue #564) ──────────────────────

    [TestMethod]
    public void Process_NoModelStateErrors_NoHighlightErrorsAction_NoInlineScript()
    {
        // Complement to TC-01: explicitly locks that a plain form with no
        // ModelState errors gets no highlightErrors action at all (not even an
        // empty one) and emits zero inline <script> tags.
        var helper = CreateHelper(new TestFormVM());
        helper.Id = "wtForm_noerr";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json);
        var actions = doc.RootElement.GetProperty("actions");
        for (var i = 0; i < actions.GetArrayLength(); i++)
        {
            Assert.AreNotEqual("highlightErrors", actions[i].GetProperty("type").GetString(),
                "No highlightErrors action may be present when there are no ModelState errors to report");
        }

        Assert.IsFalse(content.Contains("<script>"),
            "A plain dialog form with no ModelState errors must emit zero inline <script> tags");
    }

    [TestMethod]
    public void Process_WithModelStateErrors_EmitsHighlightErrorsAction_WithExpectedFieldMessagePairs()
    {
        var vm = new TestFormVM();
        AddModelError(vm, "Name", "Name is required");
        AddModelError(vm, "Email", "Email is invalid");
        var helper = CreateHelper(vm);
        helper.Id = "wtForm_err1";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json);
        var actions = doc.RootElement.GetProperty("actions");

        JsonElement? highlightErrors = null;
        for (var i = 0; i < actions.GetArrayLength(); i++)
        {
            if (actions[i].GetProperty("type").GetString() == "highlightErrors")
            {
                highlightErrors = actions[i];
                break;
            }
        }
        Assert.IsTrue(highlightErrors.HasValue, "A highlightErrors action must be present when MSD has errors");

        Assert.AreEqual("wtForm_err1", highlightErrors.Value.GetProperty("formId").GetString());
        Assert.IsTrue(highlightErrors.Value.GetProperty("focusFirst").GetBoolean(),
            "focusFirst must be true — FormTagHelper always focuses the first errored field");

        var errors = highlightErrors.Value.GetProperty("errors");
        Assert.AreEqual(2, errors.GetArrayLength(), "Must carry one entry per ModelState error message");

        // Fully qualified: this test file's namespace (WalkingTec.Mvvm.Core.Test.TagHelpers)
        // has a sibling namespace WalkingTec.Mvvm.Core.Test.Utils (the Utils/ test folder),
        // which shadows the `using WalkingTec.Mvvm.Core;` Utils class.
        var nameFieldId = global::WalkingTec.Mvvm.Core.Utils.GetIdByName(typeof(TestFormVM).Name + ".Name");
        var emailFieldId = global::WalkingTec.Mvvm.Core.Utils.GetIdByName(typeof(TestFormVM).Name + ".Email");

        Assert.AreEqual(nameFieldId, errors[0].GetProperty("field").GetString());
        Assert.AreEqual("Name is required", errors[0].GetProperty("message").GetString());
        Assert.AreEqual(emailFieldId, errors[1].GetProperty("field").GetString());
        Assert.AreEqual("Email is invalid", errors[1].GetProperty("message").GetString());

        // The legacy inline ModelState-error <script> must be fully gone.
        Assert.IsFalse(content.Contains("layui-form-danger"),
            "The legacy inline addClass('layui-form-danger') script must no longer appear as raw inline JS");
        Assert.IsFalse(content.Contains("<script>"),
            "This form has no non-identifier BeforeSubmit / SearchPanel, so even with ModelState errors present " +
            "it must emit zero inline <script> tags — highlightErrors is fully islandified");
    }

    [TestMethod]
    public void Json_ModelStateErrorMessageWithCloseScript_CannotBreakOutOfIsland()
    {
        // Adversarial: an error message containing "</script>" must not be able
        // to terminate the island's <script> block early. System.Text.Json's
        // default encoder (JavaScriptEncoder.Default) escapes '<'/'>'/'&', the
        // same mechanism already used for the rest of the island — so the raw
        // "</script>" sequence must never appear unescaped in the rendered HTML,
        // and the JSON must still parse/round-trip correctly.
        var vm = new TestFormVM();
        AddModelError(vm, "Name", "</script><script>alert(1)</script>");
        var helper = CreateHelper(vm);
        helper.Id = "wtForm_err2";
        var output = MakeOutput();

        helper.Process(MakeContext(), output);

        var content = output.PostElement.GetContent();

        // The raw, un-escaped error message must never appear as a literal
        // </script> sequence outside of the two legitimate <script> tag
        // boundaries the helper itself emits (open + close of the JSON island).
        var scriptCloseCount = 0;
        var idx = 0;
        while ((idx = content.IndexOf("</script>", idx, System.StringComparison.Ordinal)) >= 0)
        {
            scriptCloseCount++;
            idx += "</script>".Length;
        }
        Assert.AreEqual(1, scriptCloseCount,
            "Exactly one literal </script> may appear — the island's own closing tag. " +
            "An attacker-controlled ModelState message must never inject a second one.");

        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json, "Island JSON must still be extractable (the injected </script> did not terminate it early)");
        using var doc = JsonDocument.Parse(json); // must not throw — JSON round-trips correctly
        var actions = doc.RootElement.GetProperty("actions");
        JsonElement? highlightErrors = null;
        for (var i = 0; i < actions.GetArrayLength(); i++)
        {
            if (actions[i].GetProperty("type").GetString() == "highlightErrors")
            {
                highlightErrors = actions[i];
                break;
            }
        }
        Assert.IsTrue(highlightErrors.HasValue);
        var decodedMessage = highlightErrors.Value.GetProperty("errors")[0].GetProperty("message").GetString();
        Assert.AreEqual("</script><script>alert(1)</script>", decodedMessage,
            "The decoded message must round-trip back to the original raw text " +
            "(escaping is a JSON-encoding-layer concern, not a data-mutation one)");
    }
}
