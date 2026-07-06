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

// Issue #601 (#470-F): TextBoxTagHelper's ChangeFunc/DoneFunc island migration.
//
// TextBoxTagHelper used to unconditionally emit raw
// oninput="fn(this.value)" / onchange="fn(this.value)" attributes. Inline on*
// handlers are stripped by ff.SafeHtml (DOMPurify) in every dialog partial and
// PostForm/BgRequest redraw, so ChangeFunc/DoneFunc silently died in dialogs.
// Mirrors FormTagHelper's #561 BeforeSubmit 3-way decision: a plain-identifier
// func name (the shape FormatFuncName(..., appendparameter:false) always
// produces unless the developer wrote a dotted/bracketed expression) migrates
// to a 'bindInput' wtm-dialog-init JSON island; a non-identifier name keeps
// the EXACT legacy inline attribute.
[TestClass]
public class TextBoxTagHelperTests
{
    private sealed class DummyModel
    {
        public string? TextField { get; set; }
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

    private static TagHelperContext MakeContext()
        => new("wt:textbox", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    // Issue #578/#585 parity: same ambient "formid" context item FormTagHelper
    // publishes for descendant tag helpers.
    private static TagHelperContext MakeContextInsideForm(string formId)
        => new("wt:textbox", new TagHelperAttributeList(),
               new Dictionary<object, object> { ["formid"] = formId }, "test-id");

    private static TagHelperOutput MakeOutput()
        => new("input", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static string? ExtractJsonFromIsland(string html)
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

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }

    // ── No ChangeFunc/DoneFunc: no island, no inline attributes ────────────

    [TestMethod]
    public void Process_NoChangeOrDoneFunc_NoIslandNoInlineAttrs()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper { Field = MakeField("TextField"), Id = "tb_plain" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsFalse(output.Attributes.ContainsName("oninput"));
        Assert.IsFalse(output.Attributes.ContainsName("onchange"));
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"), "No island when neither ChangeFunc nor DoneFunc is set");
    }

    // ── Identifier ChangeFunc/DoneFunc: island, NO inline attribute ────────

    [TestMethod]
    public void Process_IdentifierChangeFunc_EmitsIslandNoInlineOninput()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_change1",
            ChangeFunc = "myChangeHandler"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsFalse(output.Attributes.ContainsName("oninput"),
            "A plain-identifier ChangeFunc must NOT emit the inline oninput attribute");

        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"", "Must emit the wtm-dialog-init JSON island");
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("bindInput", root.GetProperty("type").GetString());
        Assert.AreEqual("tb_change1", root.GetProperty("elemId").GetString());
        Assert.AreEqual("myChangeHandler", root.GetProperty("changeFunc").GetString());
        Assert.IsFalse(root.TryGetProperty("doneFunc", out _), "doneFunc must be omitted when DoneFunc is not set");
    }

    [TestMethod]
    public void Process_IdentifierDoneFunc_EmitsIslandNoInlineOnchange()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_done1",
            DoneFunc = "myDoneHandler"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsFalse(output.Attributes.ContainsName("onchange"),
            "A plain-identifier DoneFunc must NOT emit the inline onchange attribute");

        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("bindInput", root.GetProperty("type").GetString());
        Assert.AreEqual("myDoneHandler", root.GetProperty("doneFunc").GetString());
        Assert.IsFalse(root.TryGetProperty("changeFunc", out _), "changeFunc must be omitted when ChangeFunc is not set");
    }

    [TestMethod]
    public void Process_BothIdentifierChangeAndDoneFunc_SingleIslandCarriesBoth()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_both",
            ChangeFunc = "onChangeFn",
            DoneFunc = "onDoneFn"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsFalse(output.Attributes.ContainsName("oninput"));
        Assert.IsFalse(output.Attributes.ContainsName("onchange"));

        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("onChangeFn", doc.RootElement.GetProperty("changeFunc").GetString());
        Assert.AreEqual("onDoneFn", doc.RootElement.GetProperty("doneFunc").GetString());
    }

    // ── Explicit call-argument syntax: FormatFuncName truncates to the bare
    //    identifier before the island decision, exactly as the legacy path did ──

    [TestMethod]
    public void Process_ChangeFuncWithCallSyntax_TruncatedToIdentifier_MigratesToIsland()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_callsyntax",
            ChangeFunc = "myFunc(someArg)"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsFalse(output.Attributes.ContainsName("oninput"));
        var json = ExtractJsonFromIsland(output.PostElement.GetContent());
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("myFunc", doc.RootElement.GetProperty("changeFunc").GetString(),
            "FormatFuncName(..., false) truncates explicit call-argument syntax down to the bare identifier " +
            "(pre-existing behavior, preserved unchanged) before the identifier check runs");
    }

    // ── Non-identifier ChangeFunc/DoneFunc: EXACT legacy inline attribute,
    //    no island at all ─────────────────────────────────────────────────

    [TestMethod]
    public void Process_DottedNonIdentifierChangeFunc_KeepsLegacyInlineOninput_NoIsland()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_dotted",
            ChangeFunc = "obj.check"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("oninput"));
        Assert.AreEqual("obj.check(this.value)", output.Attributes["oninput"].Value,
            "A non-identifier ChangeFunc must keep the EXACT legacy inline attribute value");

        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"),
            "A non-identifier ChangeFunc (with no other migrating callback) must not emit any island");
    }

    [TestMethod]
    public void Process_DottedNonIdentifierDoneFunc_KeepsLegacyInlineOnchange_NoIsland()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_dotted2",
            DoneFunc = "this.Validate"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("onchange"));
        Assert.AreEqual("this.Validate(this.value)", output.Attributes["onchange"].Value,
            "A non-identifier DoneFunc must keep the EXACT legacy inline attribute value");

        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"));
    }

    // ── Adversarial-review MEDIUM fix: .NET `$` vs JS `$` anchor mismatch ──
    //
    // .NET's `$` (even without RegexOptions.Multiline) also matches immediately
    // before a single trailing '\n', whereas the client-side JS resolver
    // (/^[A-Za-z_$][\w$]*$/) matches only the absolute end. If the server-side
    // regex used `$`, "myFunc\n" would be classified an identifier server-side
    // (island emitted, inline attr suppressed) while the JS gate REJECTED it —
    // silently dropping the handler end-to-end. The fix anchors on `\z`
    // (absolute end only), so a trailing-newline value is a NON-identifier
    // here → falls back to the legacy inline attribute (exactly as pre-#601) →
    // the "never silently drop" invariant holds AND both engines agree.

    [TestMethod]
    public void Process_ChangeFuncWithTrailingNewline_ClassifiedNonIdentifier_KeepsLegacyInlineOninput_NoIsland()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_nl_change",
            ChangeFunc = "myFunc\n" // identifier + trailing newline
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        // Must NOT migrate to the island (the JS gate would reject "myFunc\n").
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"),
            "A trailing-newline ChangeFunc must NOT emit a bindInput island — the JS resolver's /$/ would reject " +
            "\"myFunc\\n\", so migrating would silently drop the handler. Server-side `\\z` must classify it non-identifier.");

        // Must keep the EXACT legacy inline oninput attribute (pre-#601 behavior).
        Assert.IsTrue(output.Attributes.ContainsName("oninput"),
            "A trailing-newline ChangeFunc must fall back to the legacy inline oninput attribute");
        Assert.AreEqual("myFunc\n(this.value)", output.Attributes["oninput"].Value,
            "The legacy inline attribute value must be exactly what pre-#601 emitted (no trimming, no mutation)");
    }

    [TestMethod]
    public void Process_DoneFuncWithTrailingNewline_ClassifiedNonIdentifier_KeepsLegacyInlineOnchange_NoIsland()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_nl_done",
            DoneFunc = "myFunc\n" // identifier + trailing newline
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"),
            "A trailing-newline DoneFunc must NOT emit a bindInput island (JS gate would reject it)");
        Assert.IsTrue(output.Attributes.ContainsName("onchange"),
            "A trailing-newline DoneFunc must fall back to the legacy inline onchange attribute");
        Assert.AreEqual("myFunc\n(this.value)", output.Attributes["onchange"].Value,
            "The legacy inline attribute value must be exactly what pre-#601 emitted (no trimming, no mutation)");
    }

    // ── Mixed: one identifier (migrates), one non-identifier (stays legacy) ──

    [TestMethod]
    public void Process_MixedIdentifierChangeFunc_NonIdentifierDoneFunc_BothPathsIndependent()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_mixed",
            ChangeFunc = "goodFn",
            DoneFunc = "obj.bad"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        // ChangeFunc migrated: no inline oninput.
        Assert.IsFalse(output.Attributes.ContainsName("oninput"));
        // DoneFunc stayed legacy: exact inline onchange.
        Assert.IsTrue(output.Attributes.ContainsName("onchange"));
        Assert.AreEqual("obj.bad(this.value)", output.Attributes["onchange"].Value);

        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "The island must still be emitted because ChangeFunc migrated");
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("goodFn", doc.RootElement.GetProperty("changeFunc").GetString());
        Assert.IsFalse(doc.RootElement.TryGetProperty("doneFunc", out _),
            "doneFunc must be omitted from the island — it took the legacy inline path instead");
    }

    // ── Trust boundary: elemId is always the TagHelper's own Id ────────────

    [TestMethod]
    public void Process_IslandElemId_MatchesTagHelperId()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_customid",
            ChangeFunc = "fn1"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        var json = ExtractJsonFromIsland(output.PostElement.GetContent());
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("tb_customid", doc.RootElement.GetProperty("elemId").GetString());
    }

    // ── Issue #578/#585 parity: owning-form id plumbed into the island ─────

    [TestMethod]
    public void Process_InsideForm_JsonIslandContainsFormId()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_formid1",
            ChangeFunc = "fn2"
        };
        var output = MakeOutput();
        helper.Process(MakeContextInsideForm("wtForm_test1"), output);

        var json = ExtractJsonFromIsland(output.PostElement.GetContent());
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("wtForm_test1", doc.RootElement.GetProperty("formId").GetString());
    }

    [TestMethod]
    public void Process_NotInsideForm_JsonIslandOmitsFormId()
    {
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_noformid",
            ChangeFunc = "fn3"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        var json = ExtractJsonFromIsland(output.PostElement.GetContent());
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("formId", out _),
            "formId must be absent from the island entirely when there is no ambient owning form");
    }

    // ── Script-injection safety: the island JSON is </script>-safe ─────────

    [TestMethod]
    public void Process_ChangeFuncIdentifierIsAlwaysSafeButIslandStillScriptSafe()
    {
        // ChangeFunc/DoneFunc that migrate are, by construction, plain
        // identifiers (the island guard requires it), so there is no
        // </script>-breakout surface from THEM specifically. This test just
        // pins that the island wrapper itself remains a well-formed,
        // parseable JSON document (regression guard for the DTO wiring).
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("TextField"),
            Id = "tb_safe",
            ChangeFunc = "safeFn",
            DoneFunc = "otherSafeFn"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();
        var scriptCloseCount = 0;
        var idx = 0;
        while ((idx = postHtml.IndexOf("</script>", idx, System.StringComparison.Ordinal)) >= 0)
        {
            scriptCloseCount++;
            idx += "</script>".Length;
        }
        Assert.AreEqual(1, scriptCloseCount, "Exactly one </script> — the island's own closing tag");

        var json = ExtractJsonFromIsland(postHtml);
        using var doc = System.Text.Json.JsonDocument.Parse(json!); // must not throw
    }
}
