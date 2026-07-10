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
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #633 (#470-F, widget-islandification slice 2): ComboBoxTagHelper /
/// CheckBoxTagHelper / RadioTagHelper / TransferTagHelper's ItemUrl branch
/// migrates off the inline &lt;script&gt;ff.LoadComboItems(...)&lt;/script&gt; call
/// onto the eval-free wtm-dialog-init JSON island, riding the 'loadComboItems'
/// DispatchAction case the #551 foundation already shipped. These tests assert
/// the island is emitted (not the inline script) and that its payload fields
/// match what ff.LoadComboItems expects — mirroring the #552
/// SliderTagHelperTests/DateTimeTagHelperTests conventions.
/// </summary>
[TestClass]
public class LoadComboItemsIsland633Tests
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

    // Issue #633: same island-JSON extraction helper as
    // SliderTagHelperTests.ExtractJsonFromIsland (#552) / DateTimeTagHelperTests
    // (#556) — the loadComboItems island is a bare (non-{actions:[...]}-wrapped)
    // payload, same shape as slider/rate/colorpicker/laydate.
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

    private static ComboBoxTagHelper CreateComboBoxHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        // WTMContext is stored but never read inside ComboBoxTagHelper.Process()
        // (see the commented-out _wtm.HttpContext block) — null is safe here,
        // same as every other DI-constructor TagHelper test in this project that
        // doesn't need the field it's satisfying.
        return new ComboBoxTagHelper(monitor.Object, null!);
    }

    // ── ComboBoxTagHelper ──────────────────────────────────────────────────

    [TestMethod]
    public void ComboBox_ItemUrl_EmitsJsonIslandNotInlineScript()
    {
        SetupLocalizer();
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_633_1";
        helper.ItemUrl = "/Home/GetComboItems";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"",
            "ItemUrl mode must emit the wtm-dialog-init JSON island");
        Assert.IsFalse(postHtml.Contains("ff.LoadComboItems("),
            "Must not emit the legacy inline ff.LoadComboItems(...) call text anywhere");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("loadComboItems", root.GetProperty("type").GetString());
        Assert.AreEqual("combo", root.GetProperty("controlType").GetString());
        Assert.AreEqual("/Home/GetComboItems", root.GetProperty("url").GetString());
        Assert.AreEqual("combo_633_1", root.GetProperty("id").GetString());
        Assert.AreEqual("StringField", root.GetProperty("field").GetString());
        var selectVal = root.GetProperty("selectVal");
        Assert.AreEqual(JsonValueKind.Array, selectVal.ValueKind);
        Assert.AreEqual(1, selectVal.GetArrayLength());
        Assert.AreEqual("v1", selectVal[0].GetString());
        // No 'disabled' key at all for ComboBoxTagHelper (never sets it).
        Assert.IsFalse(root.TryGetProperty("disabled", out _),
            "ComboBox never sets Disabled on the DTO — must be omitted entirely");
    }

    [TestMethod]
    public void ComboBox_ItemUrl_StillEmitsUnconditionalXmSelectRenderScript()
    {
        // Red line: this slice must not touch the always-unconditional xmSelect
        // render script (L~345) — zero behaviour change for it.
        SetupLocalizer();
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_633_2";
        helper.ItemUrl = "/Home/GetComboItems";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "xmSelect.render(",
            "The unconditional xmSelect render script must still be emitted inline, untouched by this slice");
    }

    [TestMethod]
    public void ComboBox_NoItemUrl_DoesNotEmitLoadComboItemsIsland()
    {
        SetupLocalizer();
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_633_3";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("\"type\":\"loadComboItems\""),
            "Without ItemUrl, no loadComboItems island should be emitted");
    }

    [TestMethod]
    public void ComboBox_ItemUrlWithScriptBreakoutPayload_CannotEscapeIsland()
    {
        SetupLocalizer();
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", "v1");
        helper.Id = "combo_633_4";
        helper.ItemUrl = "/Home/Get?x=</script><script>alert(1)</script>";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("</script><script>alert(1)"),
            "Raw </script><script> must never appear — JSON escaping must neutralize it");
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        Assert.IsFalse(json!.Contains("</script>"),
            "JSON must Unicode-escape < and > to prevent script injection");
    }

    // ── CheckBoxTagHelper ──────────────────────────────────────────────────

    [TestMethod]
    public void CheckBox_ItemUrl_EmitsJsonIslandWithDisabledField()
    {
        SetupLocalizer();
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "checkbox_633_1",
            ItemUrl = "/Home/GetCheckboxItems",
            Disabled = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"");
        Assert.IsFalse(postHtml.Contains("ff.LoadComboItems("));

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("loadComboItems", root.GetProperty("type").GetString());
        Assert.AreEqual("checkbox", root.GetProperty("controlType").GetString());
        Assert.AreEqual("/Home/GetCheckboxItems", root.GetProperty("url").GetString());
        Assert.AreEqual("checkbox_633_1", root.GetProperty("id").GetString());
        Assert.AreEqual("StringField", root.GetProperty("field").GetString());
        // Issue #633 review follow-up: selectVal was previously unasserted here —
        // a mutation that dropped SelectVal from CheckBoxTagHelper's island DTO
        // assignment would have left this test green. Pin it explicitly, same as
        // ComboBoxTagHelper's test above.
        var selectVal = root.GetProperty("selectVal");
        Assert.AreEqual(JsonValueKind.Array, selectVal.ValueKind);
        Assert.AreEqual(1, selectVal.GetArrayLength());
        Assert.AreEqual("v1", selectVal[0].GetString());
        Assert.IsTrue(root.GetProperty("disabled").GetBoolean(),
            "Disabled=true must round-trip into the island — parity with the legacy explicit 7th positional arg");
    }

    [TestMethod]
    public void CheckBox_ItemUrl_DisabledFalse_StillEmitsExplicitFalse()
    {
        // The legacy inline script always passed an explicit true/false 7th arg
        // (never omitted it) — the island must reproduce that exactly, not
        // silently drop 'disabled' when false.
        SetupLocalizer();
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "checkbox_633_2",
            ItemUrl = "/Home/GetCheckboxItems",
            Disabled = false
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsTrue(doc.RootElement.TryGetProperty("disabled", out var disabledProp),
            "disabled:false must still be present (explicit), not omitted");
        Assert.IsFalse(disabledProp.GetBoolean());
    }

    // ── RadioTagHelper ─────────────────────────────────────────────────────

    [TestMethod]
    public void Radio_ItemUrl_EmitsJsonIslandWithoutDisabledField()
    {
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "radio_633_1",
            ItemUrl = "/Home/GetRadioItems"
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:radio"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"");
        Assert.IsFalse(postHtml.Contains("ff.LoadComboItems("));

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("loadComboItems", root.GetProperty("type").GetString());
        Assert.AreEqual("radio", root.GetProperty("controlType").GetString());
        Assert.AreEqual("/Home/GetRadioItems", root.GetProperty("url").GetString());
        Assert.AreEqual("radio_633_1", root.GetProperty("id").GetString());
        Assert.AreEqual("StringField", root.GetProperty("field").GetString());
        // Issue #633 review follow-up (mutation-verified, see PR/commit body):
        // selectVal was the ONE field left unasserted across all four emitters'
        // tests except ComboBox's — dropping `SelectVal = values` from
        // RadioTagHelper's LoadComboItemsIslandAction assignment left the entire
        // suite green before this assertion was added. Pin it explicitly.
        var selectVal = root.GetProperty("selectVal");
        Assert.AreEqual(JsonValueKind.Array, selectVal.ValueKind);
        Assert.AreEqual(1, selectVal.GetArrayLength());
        Assert.AreEqual("v1", selectVal[0].GetString());
        // RadioTagHelper's legacy call never passed a 7th arg — the island must
        // match: 'disabled' entirely absent (not null, not false).
        Assert.IsFalse(root.TryGetProperty("disabled", out _),
            "RadioTagHelper never sets Disabled on the DTO — must be omitted entirely");
    }

    // ── TransferTagHelper ──────────────────────────────────────────────────

    [TestMethod]
    public void Transfer_ItemUrl_EmitsJsonIslandNotInlineScript()
    {
        SetupLocalizer();
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_633_1",
            ItemUrl = "/Home/GetTransferItems"
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:transfer"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "class=\"wtm-dialog-init\"");
        Assert.IsFalse(postHtml.Contains("ff.LoadComboItems("));
        // The transfer.render() legacy script is untouched by this slice.
        StringAssert.Contains(postHtml, "layui.use(['transfer']",
            "The unconditional transfer.render() legacy script must still be emitted inline");

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("loadComboItems", root.GetProperty("type").GetString());
        Assert.AreEqual("transfer", root.GetProperty("controlType").GetString());
        Assert.AreEqual("/Home/GetTransferItems", root.GetProperty("url").GetString());
        Assert.AreEqual("transfer_633_1", root.GetProperty("id").GetString());
        Assert.AreEqual("StringField", root.GetProperty("field").GetString());
        // Issue #633 review follow-up: selectVal was previously unasserted here —
        // pin it explicitly, same as ComboBoxTagHelper's test above.
        var selectVal = root.GetProperty("selectVal");
        Assert.AreEqual(JsonValueKind.Array, selectVal.ValueKind);
        Assert.AreEqual(1, selectVal.GetArrayLength());
        Assert.AreEqual("v1", selectVal[0].GetString());
    }

    [TestMethod]
    public void Transfer_ItemUrlWithQuote_JsonEscapesUrlCorrectly()
    {
        // Issue #633 regression coverage: the legacy inline script this replaces
        // interpolated ItemUrl RAW (no JavaScriptEncoder wrap, unlike its
        // Combo/CheckBox/Radio siblings — a latent gap vs #108). The JSON island
        // must escape it correctly regardless, closing that gap for free.
        SetupLocalizer();
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_633_2",
            ItemUrl = "/Home/Get?x=\"'></script><script>alert(1)</script>"
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:transfer"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("</script><script>alert(1)"),
            "Raw </script><script> must never appear in the emitted island");
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("/Home/Get?x=\"'></script><script>alert(1)</script>",
            doc.RootElement.GetProperty("url").GetString(),
            "The decoded URL value must round-trip exactly — only the WIRE encoding changes");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
