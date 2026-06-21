#nullable enable
using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Test.Grid;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Tests for #431 aggregate footer script and #432 typed column template rendering.
/// Targets the static helpers directly (DataTableTagHelper.GetRichTemplate,
/// DataTableTagHelper.BuildDefaultToolbar) without requiring a full HTTP context,
/// mirroring the pattern used in XssEncodingTests and DataTableTagHelperColumnFixedTests.
/// </summary>
[TestClass]
public class GridRichColumnTests
{
    // ── GetRichTemplate — Progress ────────────────────────────────────────────

    [TestMethod]
    public void GetRichTemplate_Progress_ContainsLayuiProgressClass()
    {
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Score", GridRichColumnTypeEnum.Progress,
            null, null, null, "r1");

        tmpl.Should().Contain("layui-progress");
        tmpl.Should().Contain("lay-percent");
    }

    [TestMethod]
    public void GetRichTemplate_Progress_UsesEscapeAttr_ForAttributeXss()
    {
        // lay-percent is a double-quoted HTML attribute — must use ff.EscapeAttr (not EscapeText)
        // to prevent quote-breakout XSS (#482). EscapeText alone does not encode " or '.
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Score", GridRichColumnTypeEnum.Progress,
            null, null, null, "r1");

        tmpl.Should().Contain("ff.EscapeAttr(d.Score)",
            "Progress lay-percent attribute value must use ff.EscapeAttr to prevent quote-breakout XSS (#482)");
        tmpl.Should().NotContain("ff.EscapeText(d.Score)",
            "Progress must not use EscapeText in attribute context — it does not encode double-quotes");
    }

    // ── GetRichTemplate — Tag ─────────────────────────────────────────────────

    [TestMethod]
    public void GetRichTemplate_Tag_ContainsLayuiBadgeClass()
    {
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Status", GridRichColumnTypeEnum.Tag,
            null, null, null, "r2");

        tmpl.Should().Contain("layui-badge");
    }

    [TestMethod]
    public void GetRichTemplate_Tag_WithColor_ContainsColorClass()
    {
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Status", GridRichColumnTypeEnum.Tag,
            null, "green", null, "r2");

        tmpl.Should().Contain("layui-bg-green");
    }

    [TestMethod]
    public void GetRichTemplate_Tag_UsesEscapeText_ForXss()
    {
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Status", GridRichColumnTypeEnum.Tag,
            null, null, null, "r2");

        tmpl.Should().Contain("ff.EscapeText(d.Status)",
            "Tag content must be XSS-encoded");
    }

    [TestMethod]
    public void GetRichTemplate_Tag_MaliciousColorClass_QuoteIsEncoded()
    {
        // A developer-supplied tagColor containing a double-quote must be encoded
        // so it cannot break out of the surrounding JS string literal.
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Status", GridRichColumnTypeEnum.Tag,
            null, "red\";break;\"", null, "r2");

        // The raw double-quote must not appear literally — it must be ".
        tmpl.Should().NotContain("\"red\";break",
            "Double-quote in tagColor must be JS-encoded to prevent string breakout");
    }

    // ── GetRichTemplate — Image ───────────────────────────────────────────────

    [TestMethod]
    public void GetRichTemplate_Image_ContainsImgTag()
    {
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Photo", GridRichColumnTypeEnum.Image,
            null, null, null, "r3");

        tmpl.Should().Contain("<img");
        tmpl.Should().Contain("object-fit:cover");
    }

    [TestMethod]
    public void GetRichTemplate_Image_UsesEscapeAttr_ForSrcAttributeXss()
    {
        // img src is a double-quoted HTML attribute — must use ff.EscapeAttr (not EscapeText)
        // to prevent quote-breakout XSS. Payload: x" onerror="alert(1) — without &quot; encoding
        // a double-quote in the value breaks out of the src attribute (#482).
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Photo", GridRichColumnTypeEnum.Image,
            null, null, null, "r3");

        tmpl.Should().Contain("ff.EscapeAttr(d.Photo)",
            "Image src attribute value must use ff.EscapeAttr to prevent quote-breakout XSS (#482)");
        tmpl.Should().NotContain("ff.EscapeText(d.Photo)",
            "Image src must not use EscapeText — it does not encode double-quotes");
    }

    [TestMethod]
    public void GetRichTemplate_Image_DefaultSize_Is32px()
    {
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Photo", GridRichColumnTypeEnum.Image,
            null, null, null, "r3");

        tmpl.Should().Contain("width:32px");
    }

    [TestMethod]
    public void GetRichTemplate_Image_CustomSize_IsEmbedded()
    {
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Photo", GridRichColumnTypeEnum.Image,
            null, null, 64, "r3");

        tmpl.Should().Contain("width:64px");
    }

    // ── GetRichTemplate — Currency ────────────────────────────────────────────

    [TestMethod]
    public void GetRichTemplate_Currency_ContainsToLocaleString()
    {
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Price", GridRichColumnTypeEnum.Currency,
            null, null, null, "r4");

        tmpl.Should().Contain("toLocaleString",
            "Currency template must format numbers with toLocaleString");
    }

    [TestMethod]
    public void GetRichTemplate_Currency_DoesNotContainRawFieldConcat()
    {
        // Ensure the raw field value is not simply concatenated into HTML.
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Price", GridRichColumnTypeEnum.Currency,
            null, null, null, "r4");

        // Currency uses Number(d.Price) — the value passes through the Number constructor,
        // not a raw string insert.  The template must not embed d.Price directly inside
        // HTML markup without protection.
        tmpl.Should().Contain("Number(d.Price)");
    }

    // ── GetRichTemplate — field XSS guard ────────────────────────────────────

    [TestMethod]
    public void GetRichTemplate_MaliciousFieldName_IsJsEncoded()
    {
        // Adversarial field name with JS-special characters must be encoded so it
        // cannot break out of the surrounding JS string/function.
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Field\";alert(1);//", GridRichColumnTypeEnum.Tag,
            null, null, null, "r5");

        // The literal double-quote that would break out of a JS string must be absent.
        // JavaScriptEncoder encodes " → "; the raw " must not appear verbatim.
        tmpl.Should().NotContain("\"Field\"",
            "Double-quote in field name must be JS-encoded to prevent string breakout");
        tmpl.Should().Contain("\\u0022",
            "Encoded double-quote \\u0022 must appear in the template");
    }

    // ── BuildAggregateFooterScript ────────────────────────────────────────────

    private static string InvokeBuildAggregateFooterScript(List<string> fields)
    {
        // The method is private static — invoke via reflection to keep the test
        // focused without coupling to the public API surface.
        var method = typeof(DataTableTagHelper).GetMethod(
            "BuildAggregateFooterScript",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("BuildAggregateFooterScript must exist");
        return (string)method!.Invoke(null, new object[] { fields })!;
    }

    [TestMethod]
    public void BuildAggregateFooterScript_EmptyList_ReturnsEmpty()
    {
        var result = InvokeBuildAggregateFooterScript(new List<string>());

        result.Should().BeEmpty();
    }

    [TestMethod]
    public void BuildAggregateFooterScript_NullList_ReturnsEmpty()
    {
        var result = InvokeBuildAggregateFooterScript(null!);

        result.Should().BeEmpty();
    }

    [TestMethod]
    public void BuildAggregateFooterScript_SingleField_ContainsResAggregates()
    {
        var result = InvokeBuildAggregateFooterScript(new List<string> { "Amount" });

        result.Should().Contain("res.Aggregates",
            "Script must read server aggregate values from res.Aggregates");
        result.Should().Contain("Amount",
            "Script must reference the configured field name");
    }

    [TestMethod]
    public void BuildAggregateFooterScript_MultipleFields_ContainsAll()
    {
        var result = InvokeBuildAggregateFooterScript(new List<string> { "Amount", "Quantity" });

        result.Should().Contain("Amount");
        result.Should().Contain("Quantity");
    }

    [TestMethod]
    public void BuildAggregateFooterScript_MaliciousField_IsJsEncoded()
    {
        // A field name containing JS-injection chars must be encoded.
        var result = InvokeBuildAggregateFooterScript(
            new List<string> { "Field\";alert(1);//" });

        // The literal double-quote that would break out of the CSS selector string
        // or JS object key must be absent; JavaScriptEncoder maps " → ".
        result.Should().NotContain("\"Field\"",
            "Double-quote in field name must be JS-encoded to prevent string breakout");
        result.Should().Contain("\\u0022",
            "Encoded double-quote \\u0022 must appear in the aggregate script");
    }

    [TestMethod]
    public void BuildAggregateFooterScript_ContainsLayuiTotalSelector()
    {
        // The script must target the LayUI total-row using the expected CSS class.
        var result = InvokeBuildAggregateFooterScript(new List<string> { "Amount" });

        result.Should().Contain("layui-table-total",
            "Aggregate footer script must target the layui-table-total element");
    }

    // ── Enum defaults ─────────────────────────────────────────────────────────

    [TestMethod]
    public void GridAggregateTypeEnum_Default_IsNone()
    {
        default(GridAggregateTypeEnum).Should().Be(GridAggregateTypeEnum.None);
    }

    [TestMethod]
    public void GridRichColumnTypeEnum_Default_IsDefaultVariant()
    {
        default(GridRichColumnTypeEnum).Should().Be(GridRichColumnTypeEnum.Default);
    }

    // ── GetRichTemplate — Currency, per-row CurrencyCodeField ────────────────

    [TestMethod]
    public void GetRichTemplate_Currency_WithCurrencyCodeField_ContainsIntlNumberFormat()
    {
        // Per-row currency path: template should use Intl.NumberFormat with style:'currency'
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Amount", GridRichColumnTypeEnum.Currency,
            null, null, null, "r6", currencyCodeField: "CurrencyCode");

        tmpl.Should().Contain("Intl.NumberFormat",
            "Per-row currency template must use Intl.NumberFormat");
        tmpl.Should().Contain("style:'currency'",
            "Per-row currency template must pass style:'currency' to Intl.NumberFormat");
    }

    [TestMethod]
    public void GetRichTemplate_Currency_WithCurrencyCodeField_ReferencesCodeField()
    {
        // The template must read the currency code from the sibling column field.
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Amount", GridRichColumnTypeEnum.Currency,
            null, null, null, "r6", currencyCodeField: "CurrencyCode");

        tmpl.Should().Contain("d.CurrencyCode",
            "Per-row currency template must reference the row's CurrencyCode field");
    }

    [TestMethod]
    public void GetRichTemplate_Currency_WithCurrencyCodeField_Contains3LetterIsoGuard()
    {
        // The template must validate the row currency code is exactly 3 alpha chars.
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Amount", GridRichColumnTypeEnum.Currency,
            null, null, null, "r6", currencyCodeField: "CurrencyCode");

        tmpl.Should().Contain("[A-Za-z]",
            "Per-row currency template must include a 3-letter ISO code regex guard");
    }

    [TestMethod]
    public void GetRichTemplate_Currency_WithCurrencyCodeField_ContainsTryCatch()
    {
        // Intl.NumberFormat throws RangeError on invalid currency — template must guard with try/catch.
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Amount", GridRichColumnTypeEnum.Currency,
            null, null, null, "r6", currencyCodeField: "CurrencyCode");

        tmpl.Should().Contain("try{",
            "Per-row currency template must wrap Intl.NumberFormat in try/catch");
        tmpl.Should().Contain("catch(e)",
            "Per-row currency template must have a catch block for RangeError fallback");
    }

    [TestMethod]
    public void GetRichTemplate_Currency_WithCurrencyCodeField_FallbackUsesEscapeText()
    {
        // Fallback on bad currency code must use ff.EscapeText for XSS safety.
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Amount", GridRichColumnTypeEnum.Currency,
            null, null, null, "r6", currencyCodeField: "CurrencyCode");

        tmpl.Should().Contain("ff.EscapeText(",
            "Fallback path must HTML-encode the value via ff.EscapeText");
    }

    [TestMethod]
    public void GetRichTemplate_Currency_MaliciousCurrencyCodeFieldName_IsJsEncoded()
    {
        // A developer-supplied currencyCodeField name containing JS-special chars
        // must be JS-encoded so it cannot break the template string.
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Amount", GridRichColumnTypeEnum.Currency,
            null, null, null, "r6", currencyCodeField: "Field\";alert(1);//");

        tmpl.Should().NotContain("\"Field\"",
            "Double-quote in currencyCodeField name must be JS-encoded");
        tmpl.Should().Contain("\\u0022",
            "JS-encoded double-quote \\u0022 must appear in the template");
    }

    [TestMethod]
    public void GetRichTemplate_Currency_NullCurrencyCodeField_FallbackToFixedFormat()
    {
        // When CurrencyCodeField is null, the existing CurrencyFormat behaviour must be preserved.
        var tmpl = DataTableTagHelper.GetRichTemplate(
            "Price", GridRichColumnTypeEnum.Currency,
            null, null, null, "r4", currencyCodeField: null);

        // Back-compat: no Intl.NumberFormat, uses toLocaleString instead.
        tmpl.Should().Contain("toLocaleString",
            "When CurrencyCodeField is null, fixed-format fallback must use toLocaleString");
        tmpl.Should().NotContain("Intl.NumberFormat",
            "Fixed-format fallback must not use Intl.NumberFormat");
    }

    [TestMethod]
    public void SetCurrencyColumn_SetsRichTypeAndCodeField()
    {
        var col = new GridColumn<SaleRecord>(x => x.Amount, null);
        col.SetCurrencyColumn(currencyCodeField: "CurrencyCode");

        col.RichColumnType.Should().Be(GridRichColumnTypeEnum.Currency);
        col.CurrencyCodeField.Should().Be("CurrencyCode");
    }
}
