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

[TestClass]
public class SwitchTagHelperTests
{
    private sealed class DummyModel
    {
        public bool BoolField { get; set; }
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

    private static TagHelperContext MakeContext()
        => new("wt:switch", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("input", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    [TestMethod]
    public void Process_EmitsLaySkinSwitch()
    {
        SetupLocalizer();
        var helper = new SwitchTagHelper { Field = MakeField("BoolField"), Id = "switch_test1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        Assert.IsTrue(output.Attributes.ContainsName("lay-skin"), "lay-skin attribute must be present");
        Assert.AreEqual("switch", output.Attributes["lay-skin"].Value?.ToString());
    }

    [TestMethod]
    public void Process_EmitsTypeCheckbox()
    {
        SetupLocalizer();
        var helper = new SwitchTagHelper { Field = MakeField("BoolField"), Id = "switch_test2" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        Assert.IsTrue(output.Attributes.ContainsName("type"), "type attribute must be present");
        Assert.AreEqual("checkbox", output.Attributes["type"].Value?.ToString());
    }

    [TestMethod]
    public void Process_PostElementContainsDoNotUseHiddenInput()
    {
        SetupLocalizer();
        var helper = new SwitchTagHelper { Field = MakeField("BoolField"), Id = "switch_test3" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("_DONOTUSE_BoolField"),
            "PostElement must contain _DONOTUSE_{FieldName} hidden input");
    }

    [TestMethod]
    public void Process_CheckedTrue_NullModel_EmitsCheckedAttribute()
    {
        SetupLocalizer();
        var helper = new SwitchTagHelper
        {
            Field = MakeField("BoolField", null),
            Id = "switch_checked",
            Checked = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        Assert.IsTrue(output.Attributes.ContainsName("checked"),
            "When Checked=true and Field.Model is null, checked attribute must be emitted");
    }

    [TestMethod]
    public void Process_ValueNotSet_DefaultsToTrue()
    {
        SetupLocalizer();
        var helper = new SwitchTagHelper { Field = MakeField("BoolField"), Id = "switch_defval" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        Assert.IsTrue(output.Attributes.ContainsName("value"), "value attribute must be present");
        Assert.AreEqual("true", output.Attributes["value"].Value?.ToString(),
            "value must default to 'true' when not explicitly set");
    }

    [TestMethod]
    public void Process_ValueSet_UsesExplicitValue()
    {
        SetupLocalizer();
        var helper = new SwitchTagHelper
        {
            Field = MakeField("BoolField"),
            Id = "switch_explicit",
            Value = "yes"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        Assert.AreEqual("yes", output.Attributes["value"].Value?.ToString(),
            "Explicit Value must be used when set");
    }
}
