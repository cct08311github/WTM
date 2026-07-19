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
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice J: opt-in (WtmUIOptions.UseSelectIslandRender, default
/// OFF) eval-free 'renderSelect' JSON island for &lt;wt:combobox&gt;/&lt;wt:tree&gt;,
/// plus the always-on Part-1 safe hardening (data-wtm-defaults on both
/// wrappers, tree ItemUrl/TriggerUrl encoding). Mirrors
/// LoadComboItemsIsland633Tests / TreeTagHelperLoadComboItemsIsland470Tests
/// conventions exactly — same helpers, same island-extraction pattern.
///
/// IMPORTANT: WtmUIOptions is process-wide static state
/// (BaseFieldTag.SetUIOptions). Every test that flips UseSelectIslandRender
/// ON must be paired with the [TestCleanup] reset below — mirrors
/// BaseFieldTagVerifyAriaTests' EnableAutoVerify/EnableAria convention — or a
/// later test class in the same run would silently inherit the flag.
/// </summary>
[TestClass]
public class RenderSelectIsland470SliceJTests
{
    private sealed class DummyModel
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

    private static ModelExpression MakeField(string propertyName, object? modelValue = null)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(DummyModel).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(DummyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext(string tagName)
        => new(tagName, new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static string? ExtractJsonFromIsland(string html, string needleType)
    {
        var marker = "\"type\":\"" + needleType + "\"";
        var idx = html.IndexOf(marker, System.StringComparison.Ordinal);
        if (idx < 0) return null;
        // Walk backwards to the enclosing '{' and forwards to the matching '}'.
        var start = html.LastIndexOf('{', idx);
        if (start < 0) return null;
        var depth = 0;
        var end = -1;
        for (var i = start; i < html.Length; i++)
        {
            if (html[i] == '{') depth++;
            else if (html[i] == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }
        if (end < 0) return null;
        return html[start..(end + 1)];
    }

    private static ComboBoxTagHelper CreateComboBoxHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new ComboBoxTagHelper(monitor.Object, null!);
    }

    private static TreeTagHelper CreateTreeHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new TreeTagHelper(monitor.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ── Flag OFF (default) — byte-for-byte legacy inline render ─────────────

    [TestMethod]
    public void ComboBox_FlagOff_EmitsInlineXmSelectScript_NoRenderSelectIsland()
    {
        SetupLocalizer();
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_j_1";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "xmSelect.render(",
            "Flag OFF (default) must keep emitting the legacy inline xmSelect render");
        Assert.IsFalse(postHtml.Contains("\"type\":\"renderSelect\""),
            "Flag OFF must never emit a renderSelect island");
        Assert.IsFalse(postHtml.Contains("console.warn('[WTM] ComboBoxTagHelper"),
            "Flag OFF must never emit the deprecation warn line");
    }

    [TestMethod]
    public void Tree_FlagOff_EmitsInlineXmSelectScript_NoRenderSelectIsland()
    {
        SetupLocalizer();
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree_j_1";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "xmSelect.render(",
            "Flag OFF (default) must keep emitting the legacy inline xmSelect render");
        Assert.IsFalse(postHtml.Contains("\"type\":\"renderSelect\""),
            "Flag OFF must never emit a renderSelect island");
        Assert.IsFalse(postHtml.Contains("console.warn('[WTM] TreeTagHelper"),
            "Flag OFF must never emit the deprecation warn line");
    }

    // ── Flag ON + identifier/absent ChangeFunc → island, no inline render ───

    [TestMethod]
    public void ComboBox_FlagOn_NoChangeFunc_EmitsRenderSelectIsland_NoInlineRender()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_j_2";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"renderSelect\"",
            "Flag ON + no ChangeFunc must emit the renderSelect island");
        Assert.IsFalse(postHtml.Contains("xmSelect.render("),
            "Flag ON + island path must NOT also emit the legacy inline xmSelect render");

        var json = ExtractJsonFromIsland(postHtml, "renderSelect");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("combo", root.GetProperty("widget").GetString());
        Assert.AreEqual("combo_j_2", root.GetProperty("id").GetString());
        Assert.AreEqual("#combo_j_2", root.GetProperty("el").GetString());
        Assert.IsFalse(root.TryGetProperty("changeFunc", out _),
            "No ChangeFunc set — changeFunc must be omitted (WhenWritingNull)");
        var defaults = root.GetProperty("defaultValues");
        Assert.AreEqual(1, defaults.GetArrayLength());
        Assert.AreEqual("v1", defaults[0].GetString());
    }

    [TestMethod]
    public void ComboBox_FlagOn_IdentifierChangeFunc_EmitsRenderSelectIslandWithChangeFunc()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_j_3";
        helper.ChangeFunc = "myPlainChangeFunc";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("xmSelect.render("),
            "A plain-identifier ChangeFunc must still migrate to the island");
        var json = ExtractJsonFromIsland(postHtml, "renderSelect");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("myPlainChangeFunc", doc.RootElement.GetProperty("changeFunc").GetString());
    }

    [TestMethod]
    public void Tree_FlagOn_NoChangeFunc_EmitsRenderSelectIsland_NoInlineRender()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree_j_2";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"renderSelect\"");
        Assert.IsFalse(postHtml.Contains("xmSelect.render("));

        var json = ExtractJsonFromIsland(postHtml, "renderSelect");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("tree", root.GetProperty("widget").GetString());
        Assert.AreEqual("tree_j_2", root.GetProperty("id").GetString());
        // Tree's ShowToolbar defaults to true — must round-trip.
        Assert.IsTrue(root.GetProperty("showToolbar").GetBoolean());
    }

    // ── Flag ON + non-identifier ChangeFunc → legacy inline kept + warn ─────

    [TestMethod]
    public void ComboBox_FlagOn_NonIdentifierChangeFunc_KeepsInlineRender_EmitsWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_j_4";
        helper.ChangeFunc = "some.dotted.expr";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "xmSelect.render(",
            "Non-identifier ChangeFunc must keep the legacy inline render — never silently dropped");
        Assert.IsFalse(postHtml.Contains("\"type\":\"renderSelect\""),
            "Non-identifier ChangeFunc must NOT emit a renderSelect island for this field");
        StringAssert.Contains(postHtml, "console.warn('[WTM] ComboBoxTagHelper",
            "A deprecation warn must fire naming the field/ChangeFunc when the flag is ON but the island was skipped");
        StringAssert.Contains(postHtml, "some.dotted.expr");
    }

    [TestMethod]
    public void Tree_FlagOn_NonIdentifierChangeFunc_KeepsInlineRender_EmitsWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree_j_3";
        helper.ChangeFunc = "some.dotted.expr";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "xmSelect.render(");
        Assert.IsFalse(postHtml.Contains("\"type\":\"renderSelect\""));
        StringAssert.Contains(postHtml, "console.warn('[WTM] TreeTagHelper");
    }

    // ── data-wtm-defaults attribute — present on both wrappers, ALL cases ───

    [TestMethod]
    public void ComboBox_DataWtmDefaultsAttribute_PresentRegardlessOfFlag()
    {
        SetupLocalizer();
        foreach (var flag in new[] { false, true })
        {
            BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = flag });
            var helper = CreateComboBoxHelper();
            helper.Field = MakeField("StringField", "v1");
            helper.Id = "combo_j_defaults_" + flag;
            var output = MakeOutput();
            helper.Process(MakeContext("wt:combobox"), output);

            Assert.IsTrue(output.Attributes.TryGetAttribute("data-wtm-defaults", out var attr),
                $"data-wtm-defaults must be present when flag={flag}");
            StringAssert.Contains(attr!.Value!.ToString(), "v1");
        }
    }

    [TestMethod]
    public void Tree_DataWtmDefaultsAttribute_PresentRegardlessOfFlag()
    {
        SetupLocalizer();
        foreach (var flag in new[] { false, true })
        {
            BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = flag });
            var helper = CreateTreeHelper();
            helper.Field = MakeField("StringField", "v1");
            helper.Id = "tree_j_defaults_" + flag;
            var output = MakeOutput();
            helper.Process(MakeContext("wt:tree"), output);

            Assert.IsTrue(output.Attributes.TryGetAttribute("data-wtm-defaults", out var attr),
                $"data-wtm-defaults must be present when flag={flag}");
            StringAssert.Contains(attr!.Value!.ToString(), "v1");
        }
    }

    // ── Tree ItemUrl islands only when the flag is ON (Issue #753) ──────────
    // Correction (#753): this migration was originally believed flag-
    // independent ("Slice G, unaffected") — that was itself the #753 HIGH
    // defect. Base 947ecbc9 emitted a legacy inline
    // <script>ff.LoadComboItems('tree',...)</script> here, so the island must
    // be gated on UseSelectIslandRender like every other Slice G/H/I emitter.

    [TestMethod]
    public void Tree_ItemUrl_FlagOn_EmitsLoadComboItemsIsland_NoBareInlineLoadComboItemsScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "1");
        helper.Id = "tree_j_itemurl";
        helper.ItemUrl = "/Home/GetTreeItems";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"loadComboItems\"");
        Assert.IsFalse(postHtml.Contains("ff.LoadComboItems("),
            "Tree ItemUrl flag ON must never emit a bare inline ff.LoadComboItems(...) call — islandified by Slice G");
    }

    [TestMethod]
    public void Tree_ItemUrl_FlagOff_EmitsLegacyInlineLoadComboItemsScript_NoIsland()
    {
        // Issue #753: flag OFF (default) must stay byte-identical to base
        // 947ecbc9 — the legacy inline ff.LoadComboItems('tree', ...) call,
        // not the loadComboItems JSON island.
        SetupLocalizer();
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "1");
        helper.Id = "tree_j_itemurl_flagoff";
        helper.ItemUrl = "/Home/GetTreeItems";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("\"type\":\"loadComboItems\""),
            "Tree ItemUrl flag OFF must never emit the loadComboItems island");
        StringAssert.Contains(postHtml, "ff.LoadComboItems('tree','/Home/GetTreeItems','tree_j_itemurl_flagoff','StringField',",
            "Tree ItemUrl flag OFF must emit the exact legacy inline ff.LoadComboItems(...) call");
    }

    // ── TreeTagHelper TriggerUrl encoding (Part 1d) ──────────────────────────

    [TestMethod]
    public void Tree_TriggerUrl_IsJavaScriptEncoded_InLegacyInlineScript()
    {
        SetupLocalizer();
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree_j_turl";
        helper.LinkId = "SomeLinkTarget";
        helper.TriggerUrl = "/x?a=1&b=\"</script><script>alert(1)</script>";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("</script><script>alert(1)"),
            "Raw </script><script> must never appear — TriggerUrl must be JS-encoded, mirroring ComboBoxTagHelper");
        StringAssert.Contains(postHtml, "\\u003C/script\\u003E",
            "JavaScriptEncoder must escape the TriggerUrl's '<'/'>' the same way ComboBoxTagHelper's does");
    }

    // ── ShowLine is deprecated/dead — literal showLine:true unaffected ──────

    [TestMethod]
    public void Tree_ShowLine_IsObsoleteButStillABoolProperty()
    {
        var prop = typeof(TreeTagHelper).GetProperty("ShowLine", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must still have public ShowLine property (back-compat)");
        Assert.AreEqual(typeof(bool), prop!.PropertyType);
        var obsolete = prop.GetCustomAttributes(typeof(System.ObsoleteAttribute), false);
        Assert.AreEqual(1, obsolete.Length, "ShowLine must be marked [Obsolete] — it has no effect on rendering");
    }

    [TestMethod]
    public void Tree_ShowLineFalse_RenderStillHardcodesShowLineTrue_NoSilentBehaviorChange()
    {
        SetupLocalizer();
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree_j_showline";
#pragma warning disable CS0618 // intentionally exercising the deprecated (dead) property
        helper.ShowLine = false;
#pragma warning restore CS0618
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "showLine: true",
            "ShowLine=false must have NO effect — the literal stays true, preserving today's behavior");
    }

    // ── Required-field validation (BaseFieldTag) — #470 Slice J follow-up ───
    // (2nd HIGH defect fix). The guarded-but-effectively-dead-under-the-flag
    // inline script is gone: flag OFF restores the ORIGINAL unguarded inline
    // script byte-for-byte (origin/dotnet10 parity); flag ON (island path)
    // carries required state inside the renderSelect island payload instead
    // of an inline script at all — no race, no silent drop, no double-apply.

    [TestMethod]
    public void ComboBox_Required_FlagOff_EmitsOriginalUnguardedInlineScript_ByteIdenticalToOrigin()
    {
        SetupLocalizer();
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_j_required2";
        helper.Required = true;
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        // origin/dotnet10's BaseFieldTag.cs required-validation script for
        // ComboBox/Tree, verbatim — no `if (window[...] && typeof ...)` guard.
        var expectedScript = @"
<script>
    window['combo_j_required2'].update({
    layVerify:'required',
    layReqText:'Validate.{0}required'
});
</script>
";
        StringAssert.Contains(postHtml, expectedScript,
            "Flag OFF must emit the ORIGINAL unguarded inline script, byte-for-byte identical to origin/dotnet10");
        Assert.IsFalse(postHtml.Contains("typeof window["),
            "Flag OFF must never emit the (now-removed) existence guard — window[Id] is always set synchronously on this path");
    }

    [TestMethod]
    public void Tree_Required_FlagOff_EmitsOriginalUnguardedInlineScript_ByteIdenticalToOrigin()
    {
        SetupLocalizer();
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree_j_required2";
        helper.Required = true;
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        var expectedScript = @"
<script>
    window['tree_j_required2'].update({
    layVerify:'required',
    layReqText:'Validate.{0}required'
});
</script>
";
        StringAssert.Contains(postHtml, expectedScript,
            "Flag OFF must emit the ORIGINAL unguarded inline script, byte-for-byte identical to origin/dotnet10");
        Assert.IsFalse(postHtml.Contains("typeof window["));
    }

    [TestMethod]
    public void ComboBox_Required_FlagOn_NoInlineRequiredScript_IslandCarriesLayVerify()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_j_required";
        helper.Required = true;
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains(".update({"),
            "Flag ON (island path) must NOT emit ANY inline window[Id].update(...) script — required state must be carried inside the renderSelect island instead");
        Assert.IsFalse(postHtml.Contains("layVerify:'required'"),
            "No inline layVerify literal — it must appear only inside the renderSelect island JSON");

        var json = ExtractJsonFromIsland(postHtml, "renderSelect");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("required", root.GetProperty("layVerify").GetString(),
            "The renderSelect island must carry layVerify:'required' for a Required field");
        Assert.AreEqual("Validate.{0}required", root.GetProperty("layReqText").GetString());
    }

    [TestMethod]
    public void Tree_Required_FlagOn_NoInlineRequiredScript_IslandCarriesLayVerify()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateTreeHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "tree_j_required";
        helper.Required = true;
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains(".update({"),
            "Flag ON (island path) must NOT emit ANY inline window[Id].update(...) script for Tree either");

        var json = ExtractJsonFromIsland(postHtml, "renderSelect");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("required", root.GetProperty("layVerify").GetString());
        Assert.AreEqual("Validate.{0}required", root.GetProperty("layReqText").GetString());
    }

    [TestMethod]
    public void ComboBox_NotRequired_FlagOn_IslandOmitsLayVerify()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_j_notrequired";
        helper.Required = false;
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml, "renderSelect");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("layVerify", out _),
            "A non-required field's island payload must omit layVerify entirely (WhenWritingNull)");
    }

    [TestMethod]
    public void ComboBox_Required_FlagOn_NonIdentifierChangeFunc_FallsBackToOriginalUnguardedInlineScript()
    {
        // Flag ON but ChangeFunc is non-identifier → island render is skipped
        // for this field (legacy inline render kept) — the required script
        // must ALSO fall back to the original unguarded form: window[Id] is
        // set synchronously on this path too (it's the same inline
        // xmSelect.render as flag-OFF), so no guard is needed, and none must
        // be emitted, and required-validation must not be silently dropped.
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_j_required3";
        helper.Required = true;
        helper.ChangeFunc = "some.dotted.expr";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("\"type\":\"renderSelect\""),
            "Non-identifier ChangeFunc must not emit a renderSelect island for this field");
        StringAssert.Contains(postHtml, "window['combo_j_required3'].update({",
            "The non-identifier-ChangeFunc fallback must still wire required-validation via the original unguarded inline script");
        Assert.IsFalse(postHtml.Contains("typeof window["),
            "No existence guard on this path either — window[Id] is set synchronously immediately before this script");
        StringAssert.Contains(postHtml, "layVerify:'required'");
    }
}
