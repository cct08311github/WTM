#nullable enable
using System.ComponentModel.DataAnnotations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Unit tests for WF-338 opt-in LayUI UX bundle:
/// Feature A (dark-mode), B (CSV export toolbar), C (maxlength + counter), D (rate + taginput).
/// </summary>
[TestClass]
public class LayuiUxBundleTests
{
    // ── Feature A: Theme flag on WtmUIOptions ──────────────────────────────────

    [TestMethod]
    public void WtmUIOptions_DefaultThemeClass_IsEmpty()
    {
        var opts = new WtmUIOptions();
        Assert.AreEqual(string.Empty, opts.DefaultThemeClass,
            "DefaultThemeClass must default to empty (opt-in, default off).");
    }

    [TestMethod]
    public void WtmUIOptions_DefaultThemeClass_CanBeSet()
    {
        var opts = new WtmUIOptions { DefaultThemeClass = "layui-bg-black" };
        Assert.AreEqual("layui-bg-black", opts.DefaultThemeClass);
    }

    // ── Feature B: EnableClientExport on DataTableTagHelper ───────────────────

    [TestMethod]
    public void DataTableTagHelper_EnableClientExport_DefaultsFalse()
    {
        var th = new DataTableTagHelper();
        Assert.IsFalse(th.EnableClientExport,
            "EnableClientExport must default to false (opt-in).");
    }

    [TestMethod]
    public void DataTableTagHelper_ExportFileName_DefaultsNull()
    {
        var th = new DataTableTagHelper();
        Assert.IsNull(th.ExportFileName);
    }

    [TestMethod]
    public void DataTableTagHelper_BuildDefaultToolbar_WithExport_ContainsExports()
    {
        var result = DataTableTagHelper.BuildDefaultToolbar(
            needFilter: false, needPrint: false, enableClientExport: true);
        StringAssert.Contains(result, "'exports'",
            "When EnableClientExport=true the defaultToolbar must include 'exports'.");
    }

    [TestMethod]
    public void DataTableTagHelper_BuildDefaultToolbar_NoExport_DoesNotContainExports()
    {
        var result = DataTableTagHelper.BuildDefaultToolbar(
            needFilter: false, needPrint: false, enableClientExport: false);
        Assert.IsFalse(result.Contains("exports"),
            "When EnableClientExport=false the toolbar must not include 'exports'.");
    }

    [TestMethod]
    public void DataTableTagHelper_BuildDefaultToolbar_FilterAndExport_ContainsBoth()
    {
        var result = DataTableTagHelper.BuildDefaultToolbar(
            needFilter: true, needPrint: false, enableClientExport: true);
        StringAssert.Contains(result, "'filter'");
        StringAssert.Contains(result, "'exports'");
    }

    // ── Feature C: maxlength extraction ───────────────────────────────────────

    [TestMethod]
    public void GetMaxLengthFromAnnotations_StringLength_ReturnsMaximumLength()
    {
        var result = LayuiUxBundleHelpers.GetMaxLengthFromAnnotations(
            new StringLengthAttribute(100) { MinimumLength = 0 },
            null);
        Assert.AreEqual(100, result);
    }

    [TestMethod]
    public void GetMaxLengthFromAnnotations_MaxLength_ReturnsLength()
    {
        var result = LayuiUxBundleHelpers.GetMaxLengthFromAnnotations(
            null,
            new MaxLengthAttribute(50));
        Assert.AreEqual(50, result);
    }

    [TestMethod]
    public void GetMaxLengthFromAnnotations_Both_StringLengthWins()
    {
        var result = LayuiUxBundleHelpers.GetMaxLengthFromAnnotations(
            new StringLengthAttribute(200),
            new MaxLengthAttribute(50));
        Assert.AreEqual(200, result);
    }

    [TestMethod]
    public void GetMaxLengthFromAnnotations_NeitherSet_ReturnsNull()
    {
        var result = LayuiUxBundleHelpers.GetMaxLengthFromAnnotations(null, null);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void TextAreaTagHelper_ShowCounter_DefaultsFalse()
    {
        var th = new TextAreaTagHelper();
        Assert.IsFalse(th.ShowCounter,
            "ShowCounter must default to false (opt-in).");
    }

    // ── Feature D: RateTagHelper defaults ─────────────────────────────────────

    [TestMethod]
    public void RateTagHelper_Length_DefaultsFive()
    {
        var th = new RateTagHelper();
        Assert.AreEqual(5, th.Length);
    }

    [TestMethod]
    public void RateTagHelper_Half_DefaultsFalse()
    {
        var th = new RateTagHelper();
        Assert.IsFalse(th.Half);
    }

    [TestMethod]
    public void RateTagHelper_ReadOnly_DefaultsFalse()
    {
        var th = new RateTagHelper();
        Assert.IsFalse(th.ReadOnly);
    }

    [TestMethod]
    public void RateTagHelper_Text_DefaultsNull()
    {
        var th = new RateTagHelper();
        Assert.IsNull(th.Text);
    }

    // ── Feature D: TagInputTagHelper defaults ─────────────────────────────────

    [TestMethod]
    public void TagInputTagHelper_Delimiter_DefaultsComma()
    {
        var th = new TagInputTagHelper();
        Assert.AreEqual(",", th.Delimiter);
    }

    [TestMethod]
    public void TagInputTagHelper_EmptyText_DefaultsNull()
    {
        var th = new TagInputTagHelper();
        Assert.IsNull(th.EmptyText);
    }

    // ── Feature D: TagInput JSON serialization helper ─────────────────────────

    [TestMethod]
    public void BuildTagsJson_EmptyString_ReturnsEmptyArray()
    {
        var result = LayuiUxBundleHelpers.BuildTagsJson("", ",");
        Assert.AreEqual("[]", result);
    }

    [TestMethod]
    public void BuildTagsJson_SingleTag_ReturnsSingleElement()
    {
        var result = LayuiUxBundleHelpers.BuildTagsJson("hello", ",");
        Assert.AreEqual("[\"hello\"]", result);
    }

    [TestMethod]
    public void BuildTagsJson_MultipleTags_ReturnsAllElements()
    {
        var result = LayuiUxBundleHelpers.BuildTagsJson("foo,bar,baz", ",");
        Assert.AreEqual("[\"foo\",\"bar\",\"baz\"]", result);
    }

    [TestMethod]
    public void BuildTagsJson_XssValue_IsEncoded()
    {
        var result = LayuiUxBundleHelpers.BuildTagsJson("<script>", ",");
        Assert.IsFalse(result.Contains("<script>"),
            "Tag values must be JS-encoded to prevent XSS.");
    }
}

/// <summary>
/// Static helpers extracted from TagHelpers for testability.
/// </summary>
public static class LayuiUxBundleHelpers
{
    public static int? GetMaxLengthFromAnnotations(
        StringLengthAttribute? strLen,
        MaxLengthAttribute? maxLen)
    {
        if (strLen != null && strLen.MaximumLength > 0)
            return strLen.MaximumLength;
        if (maxLen?.Length > 0)
            return maxLen.Length;
        return null;
    }

    public static string BuildTagsJson(string value, string delimiter)
    {
        if (string.IsNullOrEmpty(value)) return "[]";
        var parts = value.Split(new[] { delimiter }, System.StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder("[");
        foreach (var part in parts)
        {
            if (sb.Length > 1) sb.Append(',');
            sb.Append('"');
            sb.Append(System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(part.Trim()));
            sb.Append('"');
        }
        sb.Append(']');
        return sb.ToString();
    }
}
