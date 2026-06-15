#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
public class TransferTagHelperTests
{
    private sealed class DummyModel
    {
        public string? StringField { get; set; }
    }

    /// <summary>
    /// MUST be called before <c>new TransferTagHelper()</c>: its property initializers
    /// call <c>THProgram._localizer["Sys.NoData"]</c> and <c>["Sys.NoMatchingData"]</c>.
    /// </summary>
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
        => new("wt:transfer", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    // ── Structural property-existence tests ──────────────────────────────────

    [TestMethod]
    public void TransferTagHelper_HasLeftTitleProperty()
    {
        var prop = typeof(TransferTagHelper).GetProperty("LeftTitle", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public LeftTitle property");
        Assert.AreEqual(typeof(string), prop.PropertyType);
    }

    [TestMethod]
    public void TransferTagHelper_HasRightTitleProperty()
    {
        var prop = typeof(TransferTagHelper).GetProperty("RightTitle", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public RightTitle property");
        Assert.AreEqual(typeof(string), prop.PropertyType);
    }

    [TestMethod]
    public void TransferTagHelper_HasEnableSearchProperty()
    {
        var prop = typeof(TransferTagHelper).GetProperty("EnableSearch", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public EnableSearch property");
        Assert.AreEqual(typeof(bool), prop.PropertyType);
    }

    [TestMethod]
    public void TransferTagHelper_HasChangeFuncProperty()
    {
        var prop = typeof(TransferTagHelper).GetProperty("ChangeFunc", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public ChangeFunc property");
        Assert.AreEqual(typeof(string), prop.PropertyType);
    }

    // ── Pure logic tests ──────────────────────────────────────────────────────

    [TestMethod]
    public void PureLogic_CommaSplitDefaultValue_ProducesJsonArray()
    {
        var defaultValue = "a,b,c";
        var json = JsonSerializer.Serialize(defaultValue.Split(",").Select(x => x.Trim()).ToArray());
        Assert.IsTrue(json.StartsWith("["), "JsonSerializer output must be a JSON array");
        StringAssert.Contains(json, "\"a\"");
        StringAssert.Contains(json, "\"b\"");
        StringAssert.Contains(json, "\"c\"");
    }

    [TestMethod]
    public void PureLogic_SelectValWithDoubleQuote_JsonSerializeIsEscaped()
    {
        var values = new List<string> { "value with \"quote\"", "normal" };
        var json = JsonSerializer.Serialize(values);
        StringAssert.Contains(json, "value with", "Value content must be present in JSON");
        // Raw unescaped double-quote pair around inner value must not appear
        Assert.IsTrue(json.Contains("\\\"") || json.Contains("\\u0022"),
            "Double quotes within values must be escaped in JSON output");
    }

    // ── Process() integration tests ──────────────────────────────────────────

    [TestMethod]
    public void Process_PostElementContainsTransferUse()
    {
        SetupLocalizer();
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField"),
            Id = "test_transfer1"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("layui.use(['transfer']"),
            "PostElement must contain layui.use(['transfer']");
    }

    [TestMethod]
    public void Process_OutputHasWtmCtypeTransfer()
    {
        SetupLocalizer();
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField"),
            Id = "test_transfer2"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        Assert.IsTrue(output.Attributes.ContainsName("wtm-ctype"), "wtm-ctype attribute must be present");
        Assert.AreEqual("transfer", output.Attributes["wtm-ctype"].Value?.ToString());
    }

    [TestMethod]
    public void Process_PostElementContainsDoNotUseHiddenInput()
    {
        SetupLocalizer();
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField"),
            Id = "test_transfer3"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("_DONOTUSE_StringField"),
            "PostElement must contain _DONOTUSE_{FieldName} hidden input");
    }
}
