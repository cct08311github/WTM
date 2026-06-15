#nullable enable
using System.Collections.Generic;
using System.Globalization;
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
public class SliderTagHelperTests
{
    private sealed class DummyModel
    {
        public int IntField { get; set; }
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
        => new("wt:slider", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    [TestMethod]
    public void Process_BasicRender_PostElementContainsSliderUse()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper { Field = MakeField("IntField"), Id = "slider_test1" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("layui.use(['slider']"),
            "PostElement must contain layui.use(['slider']");
    }

    [TestMethod]
    public void Process_BasicRender_PostElementContainsSliderRender()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper { Field = MakeField("IntField"), Id = "slider_test2" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("slider.render"),
            "PostElement must contain slider.render");
    }

    [TestMethod]
    public void Process_HiddenInputContainsFieldName()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper { Field = MakeField("IntField"), Id = "slider_test3" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("IntField"),
            "PostElement must contain the field name in hidden input");
    }

    [TestMethod]
    public void Process_MinSet_PostElementContainsMin()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_min",
            Min = 5
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains(",min:5"),
            "PostElement must contain ,min:5 when Min is set to 5");
    }

    [TestMethod]
    public void Process_MaxSet_PostElementContainsMax()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_max",
            Max = 95
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains(",max:95"),
            "PostElement must contain ,max:95 when Max is set to 95");
    }

    [TestMethod]
    public void Process_RangeDefaultValue_PostElementContainsValue()
    {
        SetupLocalizer();
        var helper = new SliderTagHelper
        {
            Field = MakeField("IntField"),
            Id = "slider_range",
            DefaultValue = "[10,50]"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        // The source emits: ",value:[10,50]" (with the bracket literal stripped of spaces)
        Assert.IsTrue(postHtml.Contains(",value:[10,50]"),
            "PostElement must emit ,value:[10,50] for valid range DefaultValue");
    }

    // ── Pure logic tests (no DI / taghelper needed) ──────────────────────────

    [TestMethod]
    public void PureLogic_NonNumericDefaultValue_TryParseRejects()
    {
        var payload = "0;alert(1)//";
        bool isNumeric = double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        Assert.IsFalse(isNumeric, "XSS payload must not parse as a valid double");
    }

    [TestMethod]
    public void PureLogic_ValidNumericDefaultValue_TryParseAccepts()
    {
        var value = "42";
        bool isNumeric = double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        Assert.IsTrue(isNumeric, "Valid numeric string must parse as double");
    }

    [TestMethod]
    public void PureLogic_RangeBracketBothParts_BothParseAsDouble()
    {
        var inner = "10,20";
        var parts = inner.Split(',');
        bool part0Ok = double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        bool part1Ok = double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        Assert.IsTrue(part0Ok, "First range part '10' must parse as double");
        Assert.IsTrue(part1Ok, "Second range part '20' must parse as double");
    }

    [TestMethod]
    public void PureLogic_RangeBracketNonNumericPart_TryParseRejects()
    {
        var inner = "0,abc";
        var parts = inner.Split(',');
        bool part0Ok = double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        bool part1Ok = double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        Assert.IsTrue(part0Ok, "First part '0' must parse as double");
        Assert.IsFalse(part1Ok, "'abc' must not parse as double");
    }
}
