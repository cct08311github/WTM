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
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

[TestClass]
public class RichTextBoxTagHelperTests
{
    private sealed class DummyModel
    {
        public string? RichField { get; set; }
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

    // RichTextBoxTagHelper uses context.Items.ContainsKey("model") — safe with empty dict.
    private static TagHelperContext MakeContext()
        => new("wt:richtextbox", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("textarea", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    [TestMethod]
    public void Process_PostElementContainsLayeditBuild()
    {
        SetupLocalizer();
        var helper = new RichTextBoxTagHelper { Field = MakeField("RichField"), Id = "rich_field_1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("layedit.build("),
            "PostElement must contain layedit.build(");
    }

    [TestMethod]
    public void Process_PostElementContainsLayeditUse()
    {
        SetupLocalizer();
        var helper = new RichTextBoxTagHelper { Field = MakeField("RichField"), Id = "rich_field_2" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("layui.use('layedit'"),
            "PostElement must contain layui.use('layedit'");
    }

    [TestMethod]
    public void Process_HeightSet_PostElementContainsHeight()
    {
        SetupLocalizer();
        var helper = new RichTextBoxTagHelper
        {
            Field = MakeField("RichField"),
            Id = "rich_field_3",
            Height = 300
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("height:300"),
            "PostElement must contain height:300 when Height is set to 300");
    }

    [TestMethod]
    public void Process_IsrichAttributeIsSet()
    {
        SetupLocalizer();
        var helper = new RichTextBoxTagHelper { Field = MakeField("RichField"), Id = "rich_field_4" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        Assert.IsTrue(output.Attributes.ContainsName("isrich"),
            "isrich attribute must be set on output element");
        Assert.AreEqual("1", output.Attributes["isrich"].Value?.ToString());
    }

    [TestMethod]
    public void Process_ModelWinsOverDefaultValue()
    {
        SetupLocalizer();
        var helper = new RichTextBoxTagHelper
        {
            Field = MakeField("RichField", "saved content"),
            Id = "rich_field_5",
            DefaultValue = "default text"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var contentHtml = output.Content.GetContent();
        Assert.IsTrue(contentHtml.Contains("saved content"),
            "Non-empty model must win over DefaultValue");
        Assert.IsFalse(contentHtml.Contains("default text"),
            "DefaultValue must not appear when model is non-empty");
    }

    [TestMethod]
    public void Process_NullModel_FallsBackToDefaultValue()
    {
        SetupLocalizer();
        var helper = new RichTextBoxTagHelper
        {
            Field = MakeField("RichField", null),
            Id = "rich_field_6",
            DefaultValue = "placeholder"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var contentHtml = output.Content.GetContent();
        Assert.IsTrue(contentHtml.Contains("placeholder"),
            "Null model must fall back to DefaultValue");
    }
}
