using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Reflection;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
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
    public void Process_IsRangeFalse_WithIdentifierReadyFunc_MigratesToJsonIsland()
    {
        // Issue #470 Slice H: a PLAIN-IDENTIFIER callback name is the one
        // shape ff._resolveGuardedWindowFn can safely resolve by name, so it
        // now migrates to the eval-free JSON island (superseding #556's
        // blanket "any callback keeps the inline script" rule). Issue #753:
        // this migration is now gated on UseSelectIslandRender — must be ON.
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper();
        helper.Id = "test_date_ready";
        helper.IsRange = false;
        helper.ReadyFunc = "myReadyCallback";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        StringAssert.Contains(content, "class=\"wtm-dialog-init\"", "Must emit the JSON island");
        Assert.IsFalse(content.Contains("laydate.render("),
            "Must not emit the legacy inline laydate.render( call");

        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("myReadyCallback", doc.RootElement.GetProperty("readyFn").GetString());
        Assert.IsFalse(doc.RootElement.TryGetProperty("changeFn", out _));
        Assert.IsFalse(doc.RootElement.TryGetProperty("doneFn", out _));
    }

    [TestMethod]
    public void Process_IsRangeFalse_WithIdentifierChangeFunc_MigratesToJsonIsland()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper();
        helper.Id = "test_date_change";
        helper.IsRange = false;
        helper.ChangeFunc = "myChangeCallback";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        StringAssert.Contains(content, "class=\"wtm-dialog-init\"", "Must emit the JSON island");
        Assert.IsFalse(content.Contains("laydate.render("),
            "Must not emit the legacy inline laydate.render( call");

        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("myChangeCallback", doc.RootElement.GetProperty("changeFn").GetString());
    }

    [TestMethod]
    public void Process_IsRangeFalse_WithIdentifierDoneFunc_MigratesToJsonIsland()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper();
        helper.Id = "test_date_done";
        helper.IsRange = false;
        helper.DoneFunc = "myDoneCallback";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        StringAssert.Contains(content, "class=\"wtm-dialog-init\"", "Must emit the JSON island");
        Assert.IsFalse(content.Contains("laydate.render("),
            "Must not emit the legacy inline laydate.render( call");

        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("myDoneCallback", doc.RootElement.GetProperty("doneFn").GetString());
    }

    [TestMethod]
    public void Process_IsRangeFalse_WithAllThreeIdentifierCallbacks_MigratesToJsonIslandWithAllThree()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper();
        helper.Id = "test_date_all_cb";
        helper.IsRange = false;
        helper.ReadyFunc = "r1";
        helper.ChangeFunc = "c1";
        helper.DoneFunc = "d1";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("r1", doc.RootElement.GetProperty("readyFn").GetString());
        Assert.AreEqual("c1", doc.RootElement.GetProperty("changeFn").GetString());
        Assert.AreEqual("d1", doc.RootElement.GetProperty("doneFn").GetString());
    }

    [TestMethod]
    public void Process_IsRangeFalse_WithNonIdentifierReadyFunc_KeepsInlineScriptFallbackAndWarns()
    {
        // Issue #470 Slice H: a non-identifier expression (dotted/call-syntax)
        // can never be safely resolved by ff._resolveGuardedWindowFn, so it
        // keeps the exact legacy inline <script> fallback — never silently
        // dropping the developer's handler — but must surface a loud
        // deprecation console.warn (mirroring FormTagHelper's #558/#561
        // non-identifier BeforeSubmit decision). Issue #753: the warning
        // itself is gated on UseSelectIslandRender — must be ON here, or it
        // must contribute ZERO characters (see the flag-OFF tests below).
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper();
        helper.Id = "test_date_ready_dotted";
        helper.IsRange = false;
        helper.ReadyFunc = "obj.myReadyCallback";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsTrue(content.Contains("laydate.render("), "Must still emit laydate.render(");
        StringAssert.Contains(content, "obj.myReadyCallback", "Must reference the ready callback");
        Assert.IsFalse(content.Contains("wtm-dialog-init"),
            "Must not emit the JSON island for a non-identifier callback");
        StringAssert.Contains(content, "console.warn(", "Must emit a deprecation console.warn");
        StringAssert.Contains(content, "ReadyFunc", "Warning must name the offending attribute");
    }

    [TestMethod]
    public void Process_IsRangeFalse_WithNonIdentifierChangeFunc_KeepsInlineScriptFallbackAndWarns()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper();
        helper.Id = "test_date_change_call_expr";
        helper.IsRange = false;
        helper.ChangeFunc = "myChangeCallback()";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsTrue(content.Contains("laydate.render("), "Must still emit laydate.render(");
        Assert.IsFalse(content.Contains("wtm-dialog-init"),
            "Must not emit the JSON island for a non-identifier callback");
        StringAssert.Contains(content, "console.warn(", "Must emit a deprecation console.warn");
        StringAssert.Contains(content, "ChangeFunc", "Warning must name the offending attribute");
    }

    [TestMethod]
    public void Process_IsRangeFalse_MixedIdentifierAndNonIdentifierCallbacks_WholeFieldFallsBackAndWarns()
    {
        // Issue #470 Slice H: the 3-way decision is PER-FIELD, not per-callback
        // — one non-identifier callback forces the ENTIRE field back to the
        // inline <script>, even though the other callback is a valid identifier.
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper();
        helper.Id = "test_date_mixed";
        helper.IsRange = false;
        helper.ReadyFunc = "validIdentifier";
        helper.ChangeFunc = "obj.notAnIdentifier";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("wtm-dialog-init"),
            "A single non-identifier callback must force the whole field back to inline");
        StringAssert.Contains(content, "validIdentifier");
        StringAssert.Contains(content, "obj.notAnIdentifier");
        StringAssert.Contains(content, "console.warn(");
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
    public void Process_IsRangeTrue_WithNonIdentifierCallback_EmitsRangeLaydateRenderWithoutDuplicates()
    {
        // Issue #470 Slice H: a non-identifier callback forces the range path
        // back to the legacy inline <script> (exercising the pre-existing
        // "exactly one laydate.render(" invariant on that fallback path).
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper();
        helper.Id = "test_date_range";
        helper.IsRange = true;
        helper.RangeStartName = "StartName";
        helper.RangeEndName = "EndName";
        helper.DoneFunc = "obj.notAnIdentifier";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        var renderCount = System.Text.RegularExpressions.Regex.Matches(content, "laydate\\.render").Count;

        Assert.AreEqual(1, renderCount, "Must emit exactly one laydate.render call");
        Assert.IsFalse(content.Contains("wtm-dialog-init"), "Must not emit the JSON island");
        Assert.IsTrue(content.Contains("range: true"), "Must have range: true");
        Assert.IsTrue(content.Contains("StartName"), "Must contain RangeStartName");
        Assert.IsTrue(content.Contains("EndName"), "Must contain RangeEndName");
        Assert.IsTrue(content.Contains("type=\"hidden\""), "Must contain hidden inputs for range");
        StringAssert.Contains(content, "console.warn(");
    }

    [TestMethod]
    public void Process_IsRangeTrue_NoUserCallback_MigratesToJsonIslandCarryingRangeData()
    {
        // Issue #470 Slice H: the two-hidden-input range path's built-in
        // `done` split is now carried as island data (rangeStartId/
        // rangeEndId/rangeSplitStr) instead of always needing the inline
        // <script> — a callback-free range field migrates to the island,
        // exactly like the non-range #556 path. Issue #753: unlike the
        // single-field no-callback path (which predates the flag and stays
        // unconditional), the RANGE island path — including the no-callback
        // case — was introduced entirely by Slice H, so it is gated too.
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper();
        helper.Id = "test_date_range_no_callback";
        helper.IsRange = true;
        helper.RangeStartName = "Start2";
        helper.RangeEndName = "End2";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        StringAssert.Contains(content, "class=\"wtm-dialog-init\"", "Must emit the JSON island");
        Assert.IsFalse(content.Contains("laydate.render("),
            "Must not emit the legacy inline laydate.render( call");
        Assert.IsTrue(content.Contains("type=\"hidden\""), "Must still emit the two hidden inputs");
        Assert.IsTrue(content.Contains("Start2"), "Must contain RangeStartName in the hidden input markup");
        Assert.IsTrue(content.Contains("End2"), "Must contain RangeEndName in the hidden input markup");

        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("laydate", root.GetProperty("type").GetString());
        Assert.AreEqual("Start2", root.GetProperty("rangeStartId").GetString());
        Assert.AreEqual("End2", root.GetProperty("rangeEndId").GetString());
        Assert.AreEqual(" - ", root.GetProperty("rangeSplitStr").GetString());
        Assert.IsTrue(root.GetProperty("opts").GetProperty("range").GetBoolean(),
            "opts.range must be the boolean laydate range:true mode");
        Assert.IsFalse(root.TryGetProperty("readyFn", out _));
        Assert.IsFalse(root.TryGetProperty("changeFn", out _));
        Assert.IsFalse(root.TryGetProperty("doneFn", out _));
    }

    [TestMethod]
    public void Process_IsRangeTrue_WithIdentifierDoneFunc_MigratesToJsonIslandCarryingDoneFn()
    {
        // Issue #470 Slice H: a plain-identifier DoneFunc on a range field
        // also migrates — framework_layui.js chains it AFTER the built-in
        // split (see the JS-side test for the ordering proof).
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = CreateHelper();
        helper.Id = "test_date_range_done";
        helper.IsRange = true;
        helper.RangeStartName = "Start3";
        helper.RangeEndName = "End3";
        helper.DoneFunc = "myRangeDone";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        StringAssert.Contains(content, "class=\"wtm-dialog-init\"", "Must emit the JSON island");
        Assert.IsFalse(content.Contains("laydate.render("),
            "Must not emit the legacy inline laydate.render( call");

        var json = ExtractJsonFromIsland(content);
        Assert.IsNotNull(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        Assert.AreEqual("myRangeDone", doc.RootElement.GetProperty("doneFn").GetString());
        Assert.AreEqual("Start3", doc.RootElement.GetProperty("rangeStartId").GetString());
        Assert.AreEqual("End3", doc.RootElement.GetProperty("rangeEndId").GetString());
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

    // ── Issue #753: flag-OFF byte-identical-to-base regression coverage ─────
    // WtmUIOptions is process-wide static state (BaseFieldTag.SetUIOptions) —
    // every test above that flips UseSelectIslandRender ON is paired with the
    // [TestCleanup] reset below (mirrors RenderSelectIsland470SliceJTests'
    // convention), so a later test class in the same run never inherits it.

    [TestMethod]
    public void Process_IsRangeFalse_FlagOff_IdentifierReadyFunc_KeepsLegacyInlineScript_NoWarnNoIsland()
    {
        // Issue #753: Slice H shipped BEFORE UseSelectIslandRender existed and
        // migrated whenever callbacks were plain identifiers regardless of the
        // flag. With the flag OFF (default), even a fully-migratable
        // identifier callback must fall through to the exact legacy inline
        // <script> — byte-identical to base 947ecbc9 — with ZERO warning text.
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateHelper();
        helper.Id = "test_date_flagoff_ready";
        helper.IsRange = false;
        helper.ReadyFunc = "myReadyCallback";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("wtm-dialog-init"),
            "Flag OFF must never emit the JSON island, even for a migratable identifier callback");
        Assert.IsTrue(content.Contains("laydate.render("), "Flag OFF must emit the legacy laydate.render(");
        Assert.IsFalse(content.Contains("console.warn("),
            "Flag OFF must contribute ZERO warning characters — base never had this warning");
        StringAssert.Contains(content, "myReadyCallback");
    }

    [TestMethod]
    public void Process_IsRangeFalse_FlagOff_NonIdentifierReadyFunc_KeepsLegacyInlineScript_NoWarn()
    {
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateHelper();
        helper.Id = "test_date_flagoff_ready_dotted";
        helper.IsRange = false;
        helper.ReadyFunc = "obj.myReadyCallback";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("wtm-dialog-init"), "Flag OFF must never emit the JSON island");
        Assert.IsTrue(content.Contains("laydate.render("), "Flag OFF must emit the legacy laydate.render(");
        Assert.IsFalse(content.Contains("console.warn("),
            "Flag OFF must contribute ZERO warning characters — base never had this warning");
        StringAssert.Contains(content, "obj.myReadyCallback");
    }

    [TestMethod]
    public void Process_IsRangeTrue_FlagOff_NoCallback_KeepsLegacyInlineScript_NoIsland()
    {
        // Issue #753: base 947ecbc9's IsRange branch ALWAYS emitted the inline
        // <script> unconditionally, even with zero callbacks — the island for
        // the range path (including the no-callback case) is entirely a
        // Slice H addition and must be fully flag-gated.
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = CreateHelper();
        helper.Id = "test_date_flagoff_range_nocallback";
        helper.IsRange = true;
        helper.RangeStartName = "Start9";
        helper.RangeEndName = "End9";
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsFalse(content.Contains("wtm-dialog-init"), "Flag OFF must never emit the JSON island");
        Assert.IsTrue(content.Contains("laydate.render("), "Flag OFF must emit the legacy laydate.render(");
        Assert.IsTrue(content.Contains("range: true"), "Must have range: true");
        Assert.IsFalse(content.Contains("console.warn("),
            "Flag OFF must contribute ZERO warning characters — base never had this warning");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }
}
