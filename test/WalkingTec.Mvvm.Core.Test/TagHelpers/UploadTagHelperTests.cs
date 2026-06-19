#nullable enable
using System;
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
public class UploadTagHelperTests
{
    private sealed class DummyModel
    {
        public string? FileField { get; set; }
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

    /// <summary>
    /// UploadTagHelper directly accesses context.Items["model"] (no ContainsKey guard),
    /// so the context must always include the "model" key (value may be null).
    /// </summary>
    private static TagHelperContext MakeContext()
        => new("wt:upload", new TagHelperAttributeList(),
               new Dictionary<object, object> { { "model", null! } }, "test-id");

    private static TagHelperOutput MakeOutput()
        => new("button", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    // ── Enum existence ────────────────────────────────────────────────────────

    [TestMethod]
    public void UploadTypeEnum_HasAllFilesValue()
    {
        Assert.IsTrue(Enum.IsDefined(typeof(UploadTypeEnum), "AllFiles"),
            "UploadTypeEnum must have AllFiles value");
    }

    [TestMethod]
    public void UploadTypeEnum_HasImageFileValue()
    {
        Assert.IsTrue(Enum.IsDefined(typeof(UploadTypeEnum), "ImageFile"),
            "UploadTypeEnum must have ImageFile value");
    }

    // ── Process() integration tests ──────────────────────────────────────────

    [TestMethod]
    public void Process_PostElementContainsUploadUse()
    {
        SetupLocalizer();
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField"),
            Id = "upload_field_1",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("layui.use(['upload']"),
            "PostElement must contain layui.use(['upload']");
    }

    [TestMethod]
    public void Process_PostElementContainsUploadRender()
    {
        SetupLocalizer();
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField"),
            Id = "upload_field_2",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("layui.upload.render("),
            "PostElement must contain layui.upload.render(");
    }

    [TestMethod]
    public void Process_ImageFileType_PostElementUrlContainsUploadImage()
    {
        SetupLocalizer();
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField"),
            Id = "upload_field_3",
            UploadType = UploadTypeEnum.ImageFile
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("UploadImage"),
            "ImageFile upload URL must contain UploadImage");
    }

    [TestMethod]
    public void Process_AllFilesType_PostElementUrlContainsFrameworkUpload()
    {
        SetupLocalizer();
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField"),
            Id = "upload_field_4",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("/_Framework/Upload"),
            "AllFiles upload URL must contain /_Framework/Upload");
    }

    [TestMethod]
    public void Process_PostElementContainsHiddenInputWithFieldName()
    {
        SetupLocalizer();
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField"),
            Id = "upload_field_5",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();
        Assert.IsTrue(postHtml.Contains("FileField"),
            "PostElement hidden input must reference the field name");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
