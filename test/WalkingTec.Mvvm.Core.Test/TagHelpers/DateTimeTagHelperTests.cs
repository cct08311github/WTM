using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Reflection;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

[TestClass]
public class DateTimeTagHelperTests
{
    private class DummyModel { public string TestField { get; set; } }

    private static DateTimeTagHelper CreateHelper()
    {
        var localizerMock = new Mock<Microsoft.Extensions.Localization.IStringLocalizer>();
        localizerMock.Setup(x => x[It.IsAny<string>()]).Returns((string s) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        THProgram._localizer = localizerMock.Object;

        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        
        var provider = new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider();
        var propertyInfo = typeof(DummyModel).GetProperty("TestField");
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(DummyModel));
        var modelExplorer = new Microsoft.AspNetCore.Mvc.ViewFeatures.ModelExplorer(provider, metadata, null);
        var field = new Microsoft.AspNetCore.Mvc.ViewFeatures.ModelExpression("TestField", modelExplorer);
        
        return new DateTimeTagHelper(monitor.Object) { Field = field };
    }

    [TestMethod]
    public void DateTimeTagHelper_HasIsRangeProperty()
    {
        var prop = typeof(DateTimeTagHelper).GetProperty(
            "IsRange", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public IsRange property");
        Assert.AreEqual(typeof(bool), prop.PropertyType, "IsRange must be bool");
    }

    [TestMethod]
    public void DateTimeTagHelper_HasRangeStartNameProperty()
    {
        var prop = typeof(DateTimeTagHelper).GetProperty(
            "RangeStartName", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public RangeStartName property");
        Assert.AreEqual(typeof(string), prop.PropertyType, "RangeStartName must be string");
    }

    [TestMethod]
    public void DateTimeTagHelper_HasRangeEndNameProperty()
    {
        var prop = typeof(DateTimeTagHelper).GetProperty(
            "RangeEndName", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public RangeEndName property");
        Assert.AreEqual(typeof(string), prop.PropertyType, "RangeEndName must be string");
    }

    [TestMethod]
    public void DateTimeTagHelper_IsRange_DefaultsToFalse()
    {
        var helper = CreateHelper();
        Assert.IsFalse(helper.IsRange, "IsRange must default to false");
    }

    [TestMethod]
    public void DateTimeTagHelper_RangeStartName_DefaultsToNull()
    {
        var helper = CreateHelper();
        Assert.IsNull(helper.RangeStartName, "RangeStartName must default to null");
    }

    [TestMethod]
    public void DateTimeTagHelper_RangeEndName_DefaultsToNull()
    {
        var helper = CreateHelper();
        Assert.IsNull(helper.RangeEndName, "RangeEndName must default to null");
    }

    private static Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContext MakeContext()
        => new("wt:datetime", new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperAttributeList(),
               new System.Collections.Generic.Dictionary<object, object>(), "test");

    private static Microsoft.AspNetCore.Razor.TagHelpers.TagHelperOutput MakeOutput()
        => new("input", new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperAttributeList(),
               (_, __) => System.Threading.Tasks.Task.FromResult<Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContent>(new Microsoft.AspNetCore.Razor.TagHelpers.DefaultTagHelperContent()));

    [TestMethod]
    public void Process_IsRangeFalse_NoCallback_EmitsJsonIslandNotInlineScript()
    {
        // Issue #556 (#470-B slice 1): a callback-free single date field must
        // migrate off the inline <script> onto the eval-free JSON island.
        var helper = CreateHelper();
        helper.Id = "test_date";
        helper.IsRange = false;
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        StringAssert.Contains(content, "class=\"wtm-dialog-init\"",
            "Must emit the wtm-dialog-init JSON island");
        StringAssert.Contains(content, "\"type\":\"laydate\"",
            "Island action type must be 'laydate'");
        Assert.IsFalse(content.Contains("<script>\n"),
            "Must not emit the legacy plain <script> block when there is no callback");
        Assert.IsFalse(content.Contains("laydate.render("),
            "Must not emit a raw laydate.render( call — that lives in the island's JSON now");
        Assert.IsFalse(content.Contains("range: true"), "Must not have range: true");
    }

    [TestMethod]
    public void Process_IsRangeFalse_NoCallback_JsonIslandContainsExpectedOpts()
    {
        var helper = CreateHelper();
        helper.Id = "test_date_opts";
        helper.IsRange = false;
        helper.Format = "yyyy-MM-dd";
        helper.Min = "-7";
        helper.Max = "2099-12-31";
        helper.ZIndex = 12345;
        helper.ShowBottom = true;
        helper.ConfirmOnly = true;
        helper.Calendar = true;
        helper.Lang = DateTimeLangEnum.EN;
        helper.Mark = new System.Collections.Generic.Dictionary<string, string> { { "0-0-15", "mid" } };
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json, "Island must contain parseable JSON");

        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("laydate", root.GetProperty("type").GetString());

        var opts = root.GetProperty("opts");
        Assert.AreEqual("#test_date_opts", opts.GetProperty("elem").GetString());
        Assert.AreEqual("date", opts.GetProperty("type").GetString());
        Assert.AreEqual("yyyy-MM-dd", opts.GetProperty("format").GetString());
        // Day-offset Min ("-7") must be a JSON number, not a string.
        Assert.AreEqual(System.Text.Json.JsonValueKind.Number, opts.GetProperty("min").ValueKind);
        Assert.AreEqual(-7, opts.GetProperty("min").GetInt32());
        // A non-integer Max must be a JSON string (raw date text).
        Assert.AreEqual(System.Text.Json.JsonValueKind.String, opts.GetProperty("max").ValueKind);
        Assert.AreEqual("2099-12-31", opts.GetProperty("max").GetString());
        Assert.AreEqual(12345, opts.GetProperty("zIndex").GetInt32());
        Assert.IsTrue(opts.GetProperty("showBottom").GetBoolean());
        Assert.AreEqual(1, opts.GetProperty("btns").GetArrayLength());
        Assert.AreEqual("confirm", opts.GetProperty("btns")[0].GetString());
        Assert.IsTrue(opts.GetProperty("calendar").GetBoolean());
        Assert.AreEqual("en", opts.GetProperty("lang").GetString());
        Assert.AreEqual("mid", opts.GetProperty("mark").GetProperty("0-0-15").GetString());

        // No callback options ever appear in the island.
        Assert.IsFalse(opts.TryGetProperty("ready", out _));
        Assert.IsFalse(opts.TryGetProperty("change", out _));
        Assert.IsFalse(opts.TryGetProperty("done", out _));
    }

    [TestMethod]
    public void Process_IsRangeFalse_WithReadyFunc_KeepsInlineScriptFallback()
    {
        // Issue #556 (#470-B slice 1): a callback can't be JSON-expressed, so
        // the inline <script> fallback must be preserved unchanged.
        var helper = CreateHelper();
        helper.Id = "test_date_ready";
        helper.IsRange = false;
        helper.ReadyFunc = "myReadyCallback";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsTrue(content.Contains("laydate.render("), "Must still emit laydate.render(");
        StringAssert.Contains(content, "myReadyCallback", "Must reference the ready callback");
        Assert.IsFalse(content.Contains("wtm-dialog-init"),
            "Must not emit the JSON island when a callback is present");
    }

    [TestMethod]
    public void Process_IsRangeFalse_WithChangeFunc_KeepsInlineScriptFallback()
    {
        var helper = CreateHelper();
        helper.Id = "test_date_change";
        helper.IsRange = false;
        helper.ChangeFunc = "myChangeCallback";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsTrue(content.Contains("laydate.render("), "Must still emit laydate.render(");
        StringAssert.Contains(content, "myChangeCallback", "Must reference the change callback");
        Assert.IsFalse(content.Contains("wtm-dialog-init"),
            "Must not emit the JSON island when a callback is present");
    }

    [TestMethod]
    public void Process_IsRangeFalse_WithDoneFunc_KeepsInlineScriptFallback()
    {
        var helper = CreateHelper();
        helper.Id = "test_date_done";
        helper.IsRange = false;
        helper.DoneFunc = "myDoneCallback";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsTrue(content.Contains("laydate.render("), "Must still emit laydate.render(");
        StringAssert.Contains(content, "myDoneCallback", "Must reference the done callback");
        Assert.IsFalse(content.Contains("wtm-dialog-init"),
            "Must not emit the JSON island when a callback is present");
    }

    [TestMethod]
    public void Process_IsRangeFalse_NoCallback_FormatWithCloseScript_CannotBreakOut()
    {
        // System.Text.Json's default encoder Unicode-escapes '<' and '>', so a
        // Format value containing "</script>" cannot terminate the surrounding
        // <script type="application/json"> block early.
        var helper = CreateHelper();
        helper.Id = "test_date_xss";
        helper.IsRange = false;
        helper.Format = "</script><script>alert(1)</script>";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("</script><script>alert(1)"),
            "Raw </script><script> must not appear in the island output");

        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        Assert.IsFalse(json!.Contains("</script>"),
            "JSON must Unicode-escape < and > to prevent script injection");

        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.AreEqual("</script><script>alert(1)</script>", opts.GetProperty("format").GetString(),
            "Decoded format value must round-trip to the original string");
    }

    [TestMethod]
    public void Process_IsRangeTrue_EmitsRangeLaydateRenderWithoutDuplicates()
    {
        var helper = CreateHelper();
        helper.Id = "test_date_range";
        helper.IsRange = true;
        helper.RangeStartName = "StartName";
        helper.RangeEndName = "EndName";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        var renderCount = System.Text.RegularExpressions.Regex.Matches(content, "laydate\\.render").Count;

        Assert.AreEqual(1, renderCount, "Must emit exactly one laydate.render call");
        Assert.IsTrue(content.Contains("range: true"), "Must have range: true");
        Assert.IsTrue(content.Contains("StartName"), "Must contain RangeStartName");
        Assert.IsTrue(content.Contains("EndName"), "Must contain RangeEndName");
        Assert.IsTrue(content.Contains("type=\"hidden\""), "Must contain hidden inputs for range");
    }

    [TestMethod]
    public void Process_IsRangeTrue_NoUserCallback_StillEmitsInlineScriptNeverJsonIsland()
    {
        // Issue #556 (#470-B slice 1): the two-hidden-input range path always
        // needs its own built-in `done` callback (splitting the picked value
        // into RangeStartName/RangeEndName) — that can't be JSON-expressed, so
        // this path never migrates to the JSON island, even when the caller
        // supplied no ReadyFunc/ChangeFunc/DoneFunc of their own.
        var helper = CreateHelper();
        helper.Id = "test_date_range_no_callback";
        helper.IsRange = true;
        helper.RangeStartName = "Start2";
        helper.RangeEndName = "End2";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("wtm-dialog-init"),
            "Range path must never emit the JSON island");
        Assert.IsTrue(content.Contains("laydate.render("),
            "Range path must always keep the inline laydate.render( call");
    }

    [TestMethod]
    public void Process_IsRangeTrue_RangePlaceholderSet_EmitsPlaceholderAttribute()
    {
        var helper = CreateHelper();
        helper.Id = "test_date_range_placeholder";
        helper.IsRange = true;
        helper.RangeStartName = "Start";
        helper.RangeEndName = "End";
        helper.RangePlaceholder = "Custom Range Placeholder";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var preContent = output.PreElement.GetContent();
        var mainHtml = output.PreContent.GetContent() + output.Content.GetContent() + output.PostContent.GetContent() + output.PostElement.GetContent();
        
        Assert.IsTrue(output.Attributes.ContainsName("placeholder"), "Output attributes must contain placeholder");
        Assert.AreEqual("Custom Range Placeholder", output.Attributes["placeholder"].Value, "Placeholder attribute must match RangePlaceholder");
    }

    // Issue #556 (#470-B slice 1): extracts the JSON payload from a
    // <script type="application/json" class="wtm-dialog-init">...</script>
    // island, mirroring DialogInitTagHelperTests.ExtractJsonFromIsland.
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
    }
}
