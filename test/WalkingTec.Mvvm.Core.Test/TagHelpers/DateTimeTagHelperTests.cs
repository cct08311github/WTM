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
    public void Process_IsRangeFalse_EmitsStandardLaydateRender()
    {
        var helper = CreateHelper();
        helper.Id = "test_date";
        helper.IsRange = false;
        var context = MakeContext();
        var output = MakeOutput();

        helper.Process(context, output);

        var content = output.PostElement.GetContent();
        Assert.IsTrue(content.Contains("laydate.render"), "Must emit laydate.render");
        Assert.IsFalse(content.Contains("range: true"), "Must not have range: true");
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
}
