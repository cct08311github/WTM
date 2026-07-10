#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

// Issue #638: RadioTagHelper's per-widget
//   {Id}defaultvalues = [...];
// inline <script> used to be re-emitted ONCE PER RENDERED <input> — inside
// the same for-loop that built the radio <input> elements — instead of once,
// unconditionally, like CheckBoxTagHelper. That meant:
//   - N rendered items -> N redundant (byte-identical) writes, and
//   - 0 rendered items (e.g. the item-url branch, which never populates
//     listItems) -> the global was NEVER published at all, so a ChainChange
//     TARGET radio silently had no defaults to read (the framework_layui.js
//     half of #638, fixed independently, guards the CONSEQUENCE of that gap;
//     this file locks in the EMITTER-side fix: the script moves to
//     PostElement, unconditional, matching CheckBoxTagHelper).
//
// Issue #632 (redesigned — data as markup, not script) then replaced that
// inline script with a data-wtm-defaults attribute (authoritative for
// ff.ChainChange) plus a back-compat-only fieldDefaults wtm-dialog-init
// island.
//
// Issue #646 (Codex adversarial review, pre-10.14.4): the island-only
// back-compat #632 left in place was INCOMPLETE — on a full page it only
// dispatches at DOMContentLoaded, so app-authored JS reading
// window[id+'defaultvalues'] from an inline <script> immediately after this
// widget's markup saw `undefined`, a real regression vs. the pre-#632
// synchronous, parse-time publication. The fix restored the legacy inline
// <script>{Id}defaultvalues=[...];</script> write, unconditionally, in
// PostElement, ALONGSIDE the attribute AND the island — keeping #638's
// single-unconditional-emit-outside-the-loop placement (NOT the old
// per-item loop bug).
//
// Issue #649 (second pre-release Codex adversarial review): keeping the
// island alongside the restored inline write was itself a bug — on a
// default full-page load the inline write ran first (parse time, server
// values), an app could legitimately mutate the resulting global
// afterward, and then, at DOMContentLoaded, the island unconditionally
// overwrote it back to the original server values, silently clobbering the
// app's change. The fix removes the island emission entirely (see
// RadioTagHelper.cs's Process() comment for the full rationale, and the
// retained-as-no-op 'fieldDefaults' DispatchAction case in
// framework_layui.js). These tests now assert the attribute AND the inline
// write are present — never the island — keeping #638's
// single-unconditional-emit-outside-the-loop placement locked for the
// restored script (see framework_layui_649_clobber_regression.test.js for
// the clobber-regression proof).
[TestClass]
public class RadioTagHelperTests
{
    private enum Role
    {
        Admin,
        User,
        Guest
    }

    private sealed class DummyModel
    {
        public Role RoleField { get; set; }

        public bool Flag { get; set; }
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
        // Deliberately uses the (containerType, propertyName) overload — see
        // CheckBoxTagHelperTests for the full rationale (RadioTagHelper branches
        // heavily on Field.Metadata.ModelType, which needs the property-level
        // type, not the container-level type the PropertyInfo overload returns).
        var metadata = provider.GetMetadataForProperty(typeof(DummyModel), propertyName);
        var modelExplorer = new ModelExplorer(provider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext()
        => new("wt:radio", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    [TestMethod]
    public void Process_WithMultipleItems_EmitsDefaultValuesScriptExactlyOnce()
    {
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.User),
            Id = "radio_multi"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        // RoleField is an enum -> ToListItems() renders one <input> per enum
        // value (3: Admin/User/Guest), so the old per-item loop would have
        // emitted the script 3 times.
        //
        // NOTE (updated by #632, redesigned — data as markup, not script): the
        // "{Id}defaultvalues =" bare inline script this test originally locked
        // was replaced by the fieldDefaults wtm-dialog-init island
        // (framework_layui_632_fielddefaults_markup.test.js / #632's
        // CheckBoxTagHelperTests cover the new transport in full). The
        // "exactly once, in PostElement, never in PostContent" INVARIANT this
        // test exists to lock is unchanged and still checked below.
        //
        // NOTE (updated by #646): the bare inline "{Id}defaultvalues =" script
        // is BACK, restored alongside the island for synchronous back-compat
        // — re-asserting the same "exactly once, in PostElement, never in
        // PostContent" invariant for the restored transport too, so #638's
        // regression (N redundant per-item writes) cannot silently return.
        //
        // NOTE (updated by #649): the island is GONE — it clobbered app
        // mutations of the global at DOMContentLoaded (see
        // framework_layui_649_clobber_regression.test.js). Only the inline
        // write's "exactly once" invariant remains to check.
        var postContent = output.PostContent.GetContent();
        var postElement = output.PostElement.GetContent();

        Assert.AreEqual(0, CountOccurrences(postElement, "\"type\":\"fieldDefaults\""),
            "The fieldDefaults island must no longer be emitted anywhere (#649)");
        Assert.AreEqual(0, CountOccurrences(postContent, "fieldDefaults"),
            "PostContent (the per-item loop) must never contain the fieldDefaults island");
        Assert.AreEqual(1, CountOccurrences(postElement, helper.Id + "defaultvalues ="),
            "Restored inline defaultvalues script must be emitted exactly once, in PostElement");
        Assert.AreEqual(0, CountOccurrences(postContent, helper.Id + "defaultvalues ="),
            "PostContent (the per-item loop) must never contain the inline defaultvalues script");

        var inputCount = CountOccurrences(postContent, "<input type=\"radio\"");
        Assert.AreEqual(3, inputCount, "sanity check: 3 radio <input> elements are still rendered");
    }

    [TestMethod]
    public void Process_WithItemUrl_ZeroStaticItems_StillEmitsDefaultValuesScriptExactlyOnce()
    {
        // Issue #638: the item-url branch never populates listItems, so the OLD
        // per-item loop never ran and the global was never published at all —
        // the reachable "chained target has no defaults" configuration that made
        // ff.ChainChange's usedefaultvalue=true path crash. After the fix, the
        // unconditional PostElement emission covers this branch too (and, since
        // #632, that emission is the fieldDefaults island — see the note above).
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.Admin),
            Id = "radio_itemurl",
            ItemUrl = "/api/roles"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        var postContent = output.PostContent.GetContent();
        var postElement = output.PostElement.GetContent();

        Assert.AreEqual(0, CountOccurrences(postContent, "<input type=\"radio\""),
            "sanity check: item-url branch renders zero static <input> elements");
        // Issue #649: the fieldDefaults island is no longer emitted at all —
        // RadioTagHelper's ItemUrl branch still emits its own loadComboItems
        // island (#633), but never a second fieldDefaults one.
        Assert.AreEqual(0, CountOccurrences(postElement, "\"type\":\"fieldDefaults\""),
            "The fieldDefaults island must no longer be emitted, even with 0 rendered items");
        // Issue #646: the restored inline defaultvalues script must also be
        // emitted exactly once with 0 rendered items — the exact #638
        // "chained target has no defaults" gap this file exists to guard,
        // now re-verified for the restored transport.
        Assert.AreEqual(1, CountOccurrences(postElement, helper.Id + "defaultvalues ="),
            "Restored inline defaultvalues script must still be emitted exactly once even with 0 rendered items");
    }

    [TestMethod]
    public void Process_DefaultValuesScriptIsInPostElementNotPostContent()
    {
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.Guest),
            Id = "radio_location"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        StringAssert.DoesNotMatch(output.PostContent.GetContent(), new System.Text.RegularExpressions.Regex("fieldDefaults"));
        // Issue #649: the fieldDefaults island (whose JSON body carried
        // "id":"radio_location") is gone — assert the restored inline write
        // (the only remaining PostElement publisher of the id) lands in
        // PostElement instead, keeping the ORIGINAL "PostElement, never
        // PostContent" placement invariant this test locks.
        StringAssert.Matches(output.PostElement.GetContent(), new System.Text.RegularExpressions.Regex(
            System.Text.RegularExpressions.Regex.Escape(helper.Id + "defaultvalues =")));
    }

    // ── Issue #632 (redesigned — data as markup, not script) ────────────────
    // The inline <script>{Id}defaultvalues=...} the tests above lock into
    // PostElement (single, unconditional emission) gained a companion
    // publisher: a data-wtm-defaults="[...]" attribute on the rendered div —
    // the AUTHORITATIVE source ff.ChainChange now reads.
    // #632 originally REMOVED the inline script entirely, replacing it with
    // that attribute PLUS a back-compat-only wtm-dialog-init 'fieldDefaults'
    // JSON island as the app-facing publisher. Issue #646 (Codex adversarial
    // review, pre-10.14.4) found the island-only back-compat incomplete: on a
    // full page the island only dispatches at DOMContentLoaded, so app code
    // reading the global from an inline <script> immediately after this
    // widget's markup saw `undefined` — a real regression vs. the pre-#632
    // synchronous, parse-time publication. #646 restored the inline script
    // alongside the attribute AND the island. Issue #649 (second pre-release
    // Codex adversarial review) then found that keeping BOTH publishers was
    // itself a bug — the island's DOMContentLoaded dispatch unconditionally
    // clobbered any app mutation made to the global after the inline write
    // ran — so the island is now gone entirely; only the attribute and the
    // inline script remain.

    [TestMethod]
    public void Process_EmitsDataWtmDefaultsAttribute_AsAuthoritativeSource()
    {
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.User),
            Id = "radio_attr"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-defaults"),
            "Must emit a data-wtm-defaults attribute on the rendered div");
        var attrValue = output.Attributes["data-wtm-defaults"].Value?.ToString();
        Assert.IsNotNull(attrValue);

        using var doc = System.Text.Json.JsonDocument.Parse(attrValue!);
        Assert.AreEqual(1, doc.RootElement.GetArrayLength());
        Assert.AreEqual("User", doc.RootElement[0].GetString());
    }

    [TestMethod]
    public void Process_DoesNotEmitFieldDefaultsIsland_RemovedByIssue649()
    {
        // Issue #649: the back-compat fieldDefaults wtm-dialog-init island used to
        // be emitted unconditionally alongside the inline defaultvalues script and
        // the data-wtm-defaults attribute. Keeping it caused a real clobber bug (a
        // DOMContentLoaded-deferred re-assignment could overwrite an app mutation
        // made after the inline write ran) — see
        // framework_layui_649_clobber_regression.test.js. RadioTagHelper no
        // longer emits it at all (no ItemUrl here, so there is no island of any
        // kind); only the attribute and the inline write remain.
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.Admin),
            Id = "radio_island"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postElement = output.PostElement.GetContent();

        Assert.IsFalse(postElement.Contains("class=\"wtm-dialog-init\""),
            "No wtm-dialog-init island of any kind should be present without ItemUrl set");
        StringAssert.DoesNotMatch(postElement, new System.Text.RegularExpressions.Regex("\"type\":\"fieldDefaults\""),
            "The fieldDefaults island must no longer be emitted (#649)");
    }

    [TestMethod]
    public void Process_EmitsLegacyInlineDefaultsScript_ForSynchronousBackCompat()
    {
        // Issue #646: the inline <script>{Id}defaultvalues=[...];</script>
        // write MUST be present, unconditionally, alongside the
        // data-wtm-defaults attribute — this is what makes
        // window[id+'defaultvalues'] readable SYNCHRONOUSLY, at HTML-parse
        // time, for app-authored JS that runs immediately after this widget's
        // markup.
        //
        // Issue #649: the island that #646 kept alongside this write is GONE —
        // it re-published the original server values at DOMContentLoaded,
        // clobbering any mutation app-authored JS made to the global between
        // the inline write and DOMContentLoaded. The inline write below is now
        // the sole publisher.
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.Guest),
            Id = "radio_nolegacy"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postElement = output.PostElement.GetContent();

        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-defaults"),
            "Attribute must still be present alongside the restored inline script");
        Assert.IsTrue(postElement.Contains(helper.Id + "defaultvalues ="),
            "Must emit the restored legacy raw '{Id}defaultvalues =' bare inline script");
        // CRLF-tolerant: raw string literal line endings vary per source file.
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(postElement, "<script>\r?\n"),
            "Must emit a bare (non-application/json) <script> block for the legacy global");
        Assert.IsFalse(postElement.Contains("class=\"wtm-dialog-init\""),
            "The fieldDefaults island must no longer be emitted alongside the inline script (#649)");

        var marker = helper.Id + "defaultvalues = ";
        var start = postElement.IndexOf(marker, System.StringComparison.Ordinal) + marker.Length;
        var end = postElement.IndexOf(';', start);
        var json = postElement[start..end];
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual(1, doc.RootElement.GetArrayLength());
        Assert.AreEqual("Guest", doc.RootElement[0].GetString());
    }

    [TestMethod]
    public void Process_WithItemUrl_ZeroStaticItems_StillEmitsAttributeAndInlineScript()
    {
        // The item-url branch never populates listItems (0 rendered <input>s),
        // but the attribute/inline script are emitted unconditionally
        // regardless — same unconditional placement #638 established, now
        // carried by the markup mechanism instead of the per-item inline
        // script. Issue #649: the fieldDefaults island is no longer emitted —
        // RadioTagHelper's ItemUrl branch still emits its own loadComboItems
        // island (#633), but never a second fieldDefaults one.
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.Admin),
            Id = "radio_itemurl2",
            ItemUrl = "/api/roles"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postContent = output.PostContent.GetContent();
        var postElement = output.PostElement.GetContent();

        Assert.AreEqual(0, CountOccurrences(postContent, "<input type=\"radio\""));
        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-defaults"));
        // Issue #646: the restored inline defaultvalues script must be present
        // here too — zero rendered items is exactly the #638 gap this file
        // guards.
        StringAssert.Contains(postElement, helper.Id + "defaultvalues =");
        StringAssert.DoesNotMatch(postElement, new System.Text.RegularExpressions.Regex("\"type\":\"fieldDefaults\""));
    }

    [TestMethod]
    public void Process_ValueWithHostileCharacters_CannotBreakOutOfAttributeIslandOrInlineScript()
    {
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField"),
            Id = "radio_xss",
            DefaultValue = "</script><script>alert(1)</script>"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postElement = output.PostElement.GetContent();

        Assert.IsFalse(postElement.Contains("</script><script>alert(1)"),
            "Raw </script><script> must not appear anywhere in the emitted markup");

        var attrValue = output.Attributes["data-wtm-defaults"].Value?.ToString();
        Assert.IsNotNull(attrValue);
        using var attrDoc = System.Text.Json.JsonDocument.Parse(attrValue!);
        Assert.AreEqual("</script><script>alert(1)</script>", attrDoc.RootElement[0].GetString());

        // Issue #646: the restored inline "{Id}defaultvalues = [...];" write
        // must use the same default (HTML/JS-safe) JsonSerializer encoder — a
        // hostile value cannot break out of the bare <script> block either.
        var marker = helper.Id + "defaultvalues = ";
        var start = postElement.IndexOf(marker, System.StringComparison.Ordinal) + marker.Length;
        var end = postElement.IndexOf(';', start);
        var inlineJson = postElement[start..end];
        Assert.IsFalse(inlineJson.Contains("</script>"),
            "Inline defaultvalues script JSON must Unicode-escape < and > to prevent script injection");
        using var inlineDoc = System.Text.Json.JsonDocument.Parse(inlineJson);
        Assert.AreEqual("</script><script>alert(1)</script>", inlineDoc.RootElement[0].GetString(),
            "Decoded inline-script value must round-trip to the original string");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
