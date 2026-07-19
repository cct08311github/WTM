#nullable enable
using System.Collections.Generic;
using System.Text.Json;
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
/// Issue #470 Slice K: opt-in (WtmUIOptions.UseSelectIslandRender, default
/// OFF — the SAME flag #470 Slice J's 'renderSelect' island uses for
/// &lt;wt:combobox&gt;/&lt;wt:tree&gt;) eval-free 'renderTransfer' JSON island for
/// &lt;wt:transfer&gt;. Mirrors RenderSelectIsland470SliceJTests conventions
/// exactly — same helpers, same island-extraction pattern.
///
/// IMPORTANT: WtmUIOptions is process-wide static state
/// (BaseFieldTag.SetUIOptions). Every test that flips UseSelectIslandRender
/// ON must be paired with the [TestCleanup] reset below.
/// </summary>
[TestClass]
public class RenderTransferIsland470SliceKTests
{
    private sealed class DummyModel
    {
        public string? StringField { get; set; }
        public List<string>? ListField { get; set; }
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
        => new("wt:transfer", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static string? ExtractJsonFromIsland(string html, string needleType)
    {
        var marker = "\"type\":\"" + needleType + "\"";
        var idx = html.IndexOf(marker, System.StringComparison.Ordinal);
        if (idx < 0) return null;
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

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ── Flag OFF (default) — byte-for-byte legacy inline render ─────────────

    [TestMethod]
    public void Transfer_FlagOff_EmitsInlineTransferRenderScript_NoRenderTransferIsland()
    {
        SetupLocalizer();
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_1"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "layui.use(['transfer']",
            "Flag OFF (default) must keep emitting the legacy inline transfer.render");
        StringAssert.Contains(postHtml, "transfer.render(");
        Assert.IsFalse(postHtml.Contains("\"type\":\"renderTransfer\""),
            "Flag OFF must never emit a renderTransfer island");
        Assert.IsFalse(postHtml.Contains("console.warn('[WTM] TransferTagHelper"),
            "Flag OFF must never emit the deprecation warn line");
    }

    // ── Flag ON + identifier/absent ChangeFunc → island, no inline render ───

    [TestMethod]
    public void Transfer_FlagOn_NoChangeFunc_EmitsRenderTransferIsland_NoInlineRender()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_2"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"renderTransfer\"",
            "Flag ON + no ChangeFunc must emit the renderTransfer island");
        Assert.IsFalse(postHtml.Contains("layui.use(['transfer']"),
            "Flag ON + island path must NOT also emit the legacy inline transfer.render script");

        var json = ExtractJsonFromIsland(postHtml, "renderTransfer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("transfer_k_2", root.GetProperty("id").GetString());
        Assert.AreEqual("#transfer_k_2", root.GetProperty("el").GetString());
        Assert.IsFalse(root.TryGetProperty("changeFunc", out _),
            "No ChangeFunc set — changeFunc must be omitted (WhenWritingNull)");
        var defaults = root.GetProperty("defaultValue");
        Assert.AreEqual(1, defaults.GetArrayLength());
        Assert.AreEqual("v1", defaults[0].GetString());
    }

    [TestMethod]
    public void Transfer_FlagOn_IdentifierChangeFunc_EmitsRenderTransferIslandWithChangeFunc()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_3",
            ChangeFunc = "myPlainChangeFunc"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("layui.use(['transfer']"),
            "A plain-identifier ChangeFunc must still migrate to the island");
        var json = ExtractJsonFromIsland(postHtml, "renderTransfer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("myPlainChangeFunc", doc.RootElement.GetProperty("changeFunc").GetString());
    }

    // ── Flag ON + non-identifier ChangeFunc → legacy inline kept + warn ─────

    [TestMethod]
    public void Transfer_FlagOn_NonIdentifierChangeFunc_KeepsInlineRender_EmitsWarn()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_4",
            ChangeFunc = "some.dotted.expr"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "layui.use(['transfer']",
            "Non-identifier ChangeFunc must keep the legacy inline render — never silently dropped");
        Assert.IsFalse(postHtml.Contains("\"type\":\"renderTransfer\""),
            "Non-identifier ChangeFunc must NOT emit a renderTransfer island for this field");
        StringAssert.Contains(postHtml, "console.warn('[WTM] TransferTagHelper",
            "A deprecation warn must fire naming the field/ChangeFunc when the flag is ON but the island was skipped");
        StringAssert.Contains(postHtml, "some.dotted.expr");
        // The legacy fallback must still splice ChangeFunc directly into the
        // onchange handler exactly as before — never silently dropped.
        StringAssert.Contains(postHtml, "some.dotted.expr(data, index,transferIns);");
    }

    // ── ItemUrl still islands via loadComboItems (#633) — unaffected ───────

    [TestMethod]
    public void Transfer_ItemUrl_StillEmitsLoadComboItemsIsland_RegardlessOfFlag()
    {
        SetupLocalizer();
        foreach (var flag in new[] { false, true })
        {
            BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = flag });
            var helper = new TransferTagHelper
            {
                Field = MakeField("StringField", "1"),
                Id = "transfer_k_itemurl_" + flag,
                ItemUrl = "/Home/GetTransferItems"
            };
            var output = MakeOutput();
            helper.Process(MakeContext(), output);
            var postHtml = output.PostElement.GetContent();

            StringAssert.Contains(postHtml, "\"type\":\"loadComboItems\"",
                $"ItemUrl must still emit the #633 loadComboItems island when flag={flag}");
        }
    }

    // ── Required-field validation — Transfer is excluded from BaseFieldTag's
    // required-validation block entirely (see BaseFieldTag.Process's
    // `!(this is ... || this is TransferTagHelper)` guard), so NEITHER the
    // legacy inline window[Id].update(...) script NOR the renderTransfer
    // island should ever carry a layVerify/layReqText — confirming #470
    // Slice K correctly found "none exists" per the task's point 3.

    [TestMethod]
    public void Transfer_Required_FlagOff_NeverEmitsWindowUpdateRequiredScript()
    {
        SetupLocalizer();
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_required_off",
            Required = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains(".update({"),
            "TransferTagHelper is excluded from BaseFieldTag's required-validation block — no window[Id].update(...) script should ever be emitted");
    }

    [TestMethod]
    public void Transfer_Required_FlagOn_IslandNeverCarriesLayVerify()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_required_on",
            Required = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains(".update({"),
            "TransferTagHelper's island path must never emit a window[Id].update(...) script either");
        var json = ExtractJsonFromIsland(postHtml, "renderTransfer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("layVerify", out _),
            "RenderTransferIslandAction has no layVerify field at all — Transfer never carries required state");
    }

    // ── Disabled / Width / Height round-trip into the island payload ───────

    [TestMethod]
    public void Transfer_FlagOn_Disabled_IslandCarriesDisabledTrue()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_disabled",
            Disabled = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml, "renderTransfer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsTrue(doc.RootElement.GetProperty("disabled").GetBoolean());
    }

    [TestMethod]
    public void Transfer_FlagOn_WidthHeight_IslandCarriesWidthHeight()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_widthheight",
            Width = 500,
            Height = 400
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml, "renderTransfer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual(500, doc.RootElement.GetProperty("width").GetInt32());
        Assert.AreEqual(400, doc.RootElement.GetProperty("height").GetInt32());
    }

    [TestMethod]
    public void Transfer_FlagOn_NoWidthHeight_IslandOmitsWidthHeight()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_nowidthheight"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml, "renderTransfer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("width", out _),
            "Width omitted (WhenWritingNull) when not set");
        Assert.IsFalse(doc.RootElement.TryGetProperty("height", out _),
            "Height omitted (WhenWritingNull) when not set");
    }

    // ── data / title round-trip ──────────────────────────────────────────────

    [TestMethod]
    public void Transfer_FlagOn_LeftRightTitle_IslandCarriesTitleArray()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_titles",
            LeftTitle = "Available",
            RightTitle = "Chosen"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml, "renderTransfer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var title = doc.RootElement.GetProperty("title");
        Assert.AreEqual(2, title.GetArrayLength());
        Assert.AreEqual("Available", title[0].GetString());
        Assert.AreEqual("Chosen", title[1].GetString());
    }

    [TestMethod]
    public void Transfer_FlagOn_EnableSearch_IslandCarriesShowSearchTrue()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", "v1"),
            Id = "transfer_k_search",
            EnableSearch = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml, "renderTransfer");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.IsTrue(doc.RootElement.GetProperty("showSearch").GetBoolean());
    }
}
