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
    private static DateTimeTagHelper CreateHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new DateTimeTagHelper(monitor.Object);
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
}
