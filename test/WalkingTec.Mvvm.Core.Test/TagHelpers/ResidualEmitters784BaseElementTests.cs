#nullable enable
using System.Collections.Generic;
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
/// Issue #784 (#470 residual, GROUP 1): gates the checkbox/switch/radio
/// ChangeFunc -&gt; layui.form.on(...) wiring and TextBoxTagHelper's
/// SearchUrl/TriggerUrl -&gt; layui.autocomplete.render(...) wiring — all
/// previously unconditional inline &lt;script&gt; emitters in
/// Abstraction/BaseElementTag.cs — behind WtmUIOptions.UseSelectIslandRender
/// (default OFF). Follows the #754 byte-identity gate pattern: flag OFF
/// keeps the EXACT pre-#784 legacy inline &lt;script&gt;; flag ON with an
/// identifier ChangeFunc migrates to the eval-free 'formChange'/'autocomplete'
/// JSON island; a non-identifier ChangeFunc (flag ON) keeps the legacy inline
/// &lt;script&gt;, loudly deprecated via console.warn.
///
/// IMPORTANT: WtmUIOptions is process-wide static state
/// (BaseFieldTag.SetUIOptions / the shared WtmUIOptionsHolder) — every test
/// that flips UseSelectIslandRender ON is paired with the [TestCleanup]
/// reset below.
/// </summary>
[TestClass]
public class ResidualEmitters784BaseElementTests
{
    private sealed class DummyModel
    {
        public List<string>? Roles { get; set; }
        public string? RoleField { get; set; }
        public bool Flag { get; set; }
        public string? Name { get; set; }
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
        var metadata = provider.GetMetadataForProperty(typeof(DummyModel), propertyName);
        var modelExplorer = new ModelExplorer(provider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext(string tagName)
        => new(tagName, new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput(string tagName)
        => new(tagName, new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ═══════════════════════ CheckBoxTagHelper ═══════════════════════════

    [TestMethod]
    public void CheckBox_FlagOff_IdentifierChangeFunc_KeepsExactLegacyScript_NoWarnNoIsland()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<string> { "Admin" }),
            Id = "chk1",
            ChangeFunc = "myCheckChange"
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"), "Flag OFF must never emit the JSON island");
        Assert.IsFalse(postHtml.Contains("console.warn("), "Flag OFF must contribute ZERO warning characters");
        StringAssert.Contains(postHtml, "layui.use(['form'],function(){");
        StringAssert.Contains(postHtml, "form.on('checkbox(chk1filter)', function(data){");
        // Issue #999 part (B): ChangeFunc is now paren-wrapped — (ChangeFunc)(data)
        // — so a function-literal ChangeFunc can't produce an unwrapped
        // "function(data)" SyntaxError. A bare identifier like this fixture's
        // still calls through fine: (myCheckChange)(data) is exactly
        // equivalent to myCheckChange(data).
        StringAssert.Contains(postHtml, "(myCheckChange)(data);");
    }

    [TestMethod]
    public void CheckBox_FlagOn_IdentifierChangeFunc_EmitsFormChangeIsland_NoLegacyScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<string> { "Admin" }),
            Id = "chk2",
            ChangeFunc = "myCheckChange"
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"formChange\"");
        StringAssert.Contains(postHtml, "\"kind\":\"checkbox\"");
        StringAssert.Contains(postHtml, "\"filter\":\"chk2filter\"");
        StringAssert.Contains(postHtml, "\"changeFunc\":\"myCheckChange\"");
        Assert.IsFalse(postHtml.Contains("layui.use(['form'],function(){"),
            "Flag ON with an identifier ChangeFunc must not emit the legacy inline script");
        Assert.IsFalse(postHtml.Contains("console.warn("));
    }

    [TestMethod]
    public void CheckBox_FlagOn_NonIdentifierChangeFunc_KeepsLegacyScript_WithWarning()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("Roles", new List<string> { "Admin" }),
            Id = "chk3",
            ChangeFunc = "obj.myCheckChange"
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"));
        StringAssert.Contains(postHtml, "form.on('checkbox(chk3filter)', function(data){");
        // Issue #999 part (B): now paren-wrapped — (obj.myCheckChange)(data)
        // — exactly equivalent to obj.myCheckChange(data), and this is the
        // shape that keeps `this` bound to `obj` at call time.
        StringAssert.Contains(postHtml, "(obj.myCheckChange)(data);");
        StringAssert.Contains(postHtml, "console.warn(");
    }

    [TestMethod]
    public void CheckBox_NoChangeFunc_NoScriptAtAllEitherFlag()
    {
        SetupLocalizer();
        var helper = new CheckBoxTagHelper { Field = MakeField("Roles", new List<string>()), Id = "chk4" };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("layui.form.on") || postHtml.Contains("formChange"),
            "No ChangeFunc means no form.on wiring at all, island or legacy");
    }

    // ═══════════════════════ SwitchTagHelper ══════════════════════════════

    [TestMethod]
    public void Switch_FlagOff_IdentifierChangeFunc_KeepsExactLegacyScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new SwitchTagHelper
        {
            Field = MakeField("Flag", false),
            Id = "sw1",
            ChangeFunc = "mySwitchChange"
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:switch"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"));
        Assert.IsFalse(postHtml.Contains("console.warn("));
        StringAssert.Contains(postHtml, "form.on('switch(sw1filter)', function(data){");
        // Issue #999 part (B): now paren-wrapped — (mySwitchChange)(data) —
        // exactly equivalent to mySwitchChange(data).
        StringAssert.Contains(postHtml, "(mySwitchChange)(data);");
    }

    [TestMethod]
    public void Switch_FlagOn_IdentifierChangeFunc_EmitsFormChangeIsland()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SwitchTagHelper
        {
            Field = MakeField("Flag", false),
            Id = "sw2",
            ChangeFunc = "mySwitchChange"
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:switch"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"formChange\"");
        StringAssert.Contains(postHtml, "\"kind\":\"switch\"");
        Assert.IsFalse(postHtml.Contains("layui.use(['form'],function(){"));
    }

    [TestMethod]
    public void Switch_FlagOn_NonIdentifierChangeFunc_KeepsLegacyScript_WithWarning()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new SwitchTagHelper
        {
            Field = MakeField("Flag", false),
            Id = "sw3",
            ChangeFunc = "obj.mySwitchChange"
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:switch"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("wtm-dialog-init"));
        StringAssert.Contains(postHtml, "form.on('switch(sw3filter)', function(data){");
        StringAssert.Contains(postHtml, "console.warn(");
    }

    // ═══════════════════════ RadioTagHelper ═══════════════════════════════

    [TestMethod]
    public void Radio_FlagOff_IdentifierChangeFunc_KeepsExactLegacyScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", "Admin"),
            Id = "rad1",
            ChangeFunc = "myRadioChange"
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:radio"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("console.warn("));
        StringAssert.Contains(postHtml, "form.on('radio(rad1filter)', function(data){");
        // Issue #999 part (B): now paren-wrapped — (myRadioChange)(data) —
        // exactly equivalent to myRadioChange(data).
        StringAssert.Contains(postHtml, "(myRadioChange)(data);");
    }

    [TestMethod]
    public void Radio_FlagOn_IdentifierChangeFunc_EmitsFormChangeIsland()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", "Admin"),
            Id = "rad2",
            ChangeFunc = "myRadioChange"
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:radio"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"formChange\"");
        StringAssert.Contains(postHtml, "\"kind\":\"radio\"");
        Assert.IsFalse(postHtml.Contains("layui.use(['form'],function(){"));
    }

    [TestMethod]
    public void Radio_FlagOn_NonIdentifierChangeFunc_KeepsLegacyScript_WithWarning()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", "Admin"),
            Id = "rad3",
            ChangeFunc = "obj.myRadioChange"
        };
        var output = MakeOutput("div");
        helper.Process(MakeContext("wt:radio"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "form.on('radio(rad3filter)', function(data){");
        StringAssert.Contains(postHtml, "console.warn(");
    }

    // ═══════════════════════ TextBoxTagHelper (autocomplete) ══════════════

    [TestMethod]
    public void TextBox_FlagOff_SearchUrlNoTrigger_KeepsExactLegacyScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("Name", "foo"),
            Id = "tb1",
            SearchUrl = "/api/search"
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:textbox"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("\"type\":\"autocomplete\""));
        Assert.IsFalse(postHtml.Contains("console.warn("));
        StringAssert.Contains(postHtml, "layui.use(['autocomplete'],function(){");
        StringAssert.Contains(postHtml, "url: '/api/search',");
        Assert.IsFalse(postHtml.Contains("ff.ChainChange("), "No TriggerUrl means no ChainChange call");
    }

    [TestMethod]
    public void TextBox_FlagOff_SearchUrlWithTrigger_KeepsExactLegacyScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("Name", "foo"),
            Id = "tb2",
            SearchUrl = "/api/search",
            TriggerUrl = "/api/trigger"
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:textbox"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("\"type\":\"autocomplete\""));
        StringAssert.Contains(postHtml, "ff.ChainChange('/api/trigger/'+data.Value, data.elem);");
    }

    [TestMethod]
    public void TextBox_FlagOn_NoChangeFunc_EmitsAutocompleteIsland_NoLegacyScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("Name", "foo"),
            Id = "tb3",
            SearchUrl = "/api/search",
            TriggerUrl = "/api/trigger"
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:textbox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"autocomplete\"");
        StringAssert.Contains(postHtml, "\"id\":\"tb3\"");
        StringAssert.Contains(postHtml, "\"url\":\"/api/search\"");
        StringAssert.Contains(postHtml, "\"triggerUrl\":\"/api/trigger\"");
        Assert.IsFalse(postHtml.Contains("layui.use(['autocomplete'],function(){"),
            "Flag ON with no ChangeFunc (island-safe) must not emit the legacy inline script for the SearchUrl wiring");
    }

    [TestMethod]
    public void TextBox_FlagOn_IdentifierChangeFunc_EmitsAutocompleteIslandWithChangeFunc()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("Name", "foo"),
            Id = "tb4",
            SearchUrl = "/api/search",
            ChangeFunc = "myTextChange"
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:textbox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"autocomplete\"");
        StringAssert.Contains(postHtml, "\"changeFunc\":\"myTextChange\"");
    }

    [TestMethod]
    public void TextBox_FlagOn_NonIdentifierChangeFunc_KeepsLegacyAutocompleteScript_WithWarning()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeField("Name", "foo"),
            Id = "tb5",
            SearchUrl = "/api/search",
            ChangeFunc = "obj.myTextChange"
        };
        var output = MakeOutput("input");
        helper.Process(MakeContext("wt:textbox"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("\"type\":\"autocomplete\""));
        StringAssert.Contains(postHtml, "layui.use(['autocomplete'],function(){");
        // Issue #999 part (B): now paren-wrapped — (obj.myTextChange)(data)
        // — exactly equivalent to obj.myTextChange(data), and this is the
        // shape that keeps `this` bound to `obj` at call time.
        StringAssert.Contains(postHtml, "(obj.myTextChange)(data);");
        StringAssert.Contains(postHtml, "console.warn(");
    }
}
