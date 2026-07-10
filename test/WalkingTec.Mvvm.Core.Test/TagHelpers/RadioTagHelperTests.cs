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
// synchronous, parse-time publication. The fix restores the legacy inline
// <script>{Id}defaultvalues=[...];</script> write, unconditionally, in
// PostElement, alongside the attribute AND the island — keeping #638's
// single-unconditional-emit-outside-the-loop placement (NOT the old
// per-item loop bug). These tests now assert all THREE are present, and
// that the restored script is emitted exactly once for both 0 and N
// rendered items (the #638 regression this file already locked, now
// re-verified for the restored transport).
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
        var postContent = output.PostContent.GetContent();
        var postElement = output.PostElement.GetContent();

        Assert.AreEqual(1, CountOccurrences(postElement, "\"type\":\"fieldDefaults\""),
            "fieldDefaults island must be emitted exactly once, in PostElement");
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
        Assert.AreEqual(1, CountOccurrences(postElement, "\"type\":\"fieldDefaults\""),
            "fieldDefaults island must still be emitted exactly once even with 0 rendered items");
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
        StringAssert.Matches(output.PostElement.GetContent(), new System.Text.RegularExpressions.Regex("\"id\":\"radio_location\""));
    }

    // ── Issue #632 (redesigned — data as markup, not script) ────────────────
    // The inline <script>{Id}defaultvalues=...} the tests above lock into
    // PostElement (single, unconditional emission) gained TWO more publishers
    // alongside it:
    //   1. A data-wtm-defaults="[...]" attribute on the rendered div — the
    //      AUTHORITATIVE source ff.ChainChange now reads.
    //   2. A back-compat-only wtm-dialog-init JSON island publishing
    //      window[id+'defaultvalues'] for app-authored JS.
    // #632 originally REMOVED the inline script entirely, leaving only the
    // island as the app-facing publisher. Issue #646 (Codex adversarial
    // review, pre-10.14.4) found that incomplete: on a full page the island
    // only dispatches at DOMContentLoaded, so app code reading the global
    // from an inline <script> immediately after this widget's markup saw
    // `undefined` — a real regression vs. the pre-#632 synchronous,
    // parse-time publication. The inline script is therefore back, restored
    // unconditionally alongside the attribute and the island.

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
    public void Process_StillEmitsFieldDefaultsIsland_ForBackCompat()
    {
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.Admin),
            Id = "radio_island"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postElement = output.PostElement.GetContent();

        StringAssert.Contains(postElement, "class=\"wtm-dialog-init\"",
            "Must still emit the wtm-dialog-init back-compat island");
        StringAssert.Contains(postElement, "\"type\":\"fieldDefaults\"",
            "Island action type must be 'fieldDefaults'");
        StringAssert.Contains(postElement, "\"id\":\"radio_island\"");
        StringAssert.Contains(postElement, "\"values\":[\"Admin\"]");
    }

    [TestMethod]
    public void Process_EmitsLegacyInlineDefaultsScript_ForSynchronousBackCompat()
    {
        // Issue #646: the inline <script>{Id}defaultvalues=[...];</script>
        // write MUST be present, unconditionally, alongside the
        // data-wtm-defaults attribute and the fieldDefaults island — this is
        // what makes window[id+'defaultvalues'] readable SYNCHRONOUSLY, at
        // HTML-parse time, for app-authored JS that runs immediately after
        // this widget's markup. The island alone only publishes at
        // DOMContentLoaded, which was the #646 regression.
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
        StringAssert.Contains(postElement, "class=\"wtm-dialog-init\"",
            "fieldDefaults island must still be emitted alongside the restored inline script");

        var marker = helper.Id + "defaultvalues = ";
        var start = postElement.IndexOf(marker, System.StringComparison.Ordinal) + marker.Length;
        var end = postElement.IndexOf(';', start);
        var json = postElement[start..end];
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual(1, doc.RootElement.GetArrayLength());
        Assert.AreEqual("Guest", doc.RootElement[0].GetString());
    }

    [TestMethod]
    public void Process_WithItemUrl_ZeroStaticItems_StillEmitsAttributeAndIsland()
    {
        // The item-url branch never populates listItems (0 rendered <input>s),
        // but the attribute/island are emitted unconditionally regardless —
        // same unconditional placement #638 established, now carried by the
        // markup mechanism instead of the inline script.
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
        StringAssert.Contains(postElement, "\"type\":\"fieldDefaults\"");
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
