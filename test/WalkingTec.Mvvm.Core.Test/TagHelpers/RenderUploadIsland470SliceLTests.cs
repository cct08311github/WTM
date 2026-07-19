#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
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
/// Issue #470 Slice L: opt-in (WtmUIOptions.UseSelectIslandRender, default
/// OFF — the SAME flag #470 Slices J/K use) eval-free islandification of
/// &lt;wt:upload&gt;/&lt;wt:multiupload&gt;'s render script AND "existing file"
/// init script. Mirrors RenderTransferIsland470SliceKTests conventions
/// exactly — same helpers, same island-extraction pattern.
///
/// IMPORTANT: WtmUIOptions is process-wide static state
/// (BaseFieldTag.SetUIOptions). Every test that flips UseSelectIslandRender
/// ON must be paired with the [TestCleanup] reset below.
/// </summary>
[TestClass]
public class RenderUploadIsland470SliceLTests
{
    private sealed class DummySubFile : ISubFile
    {
        public Guid FileId { get; set; }
        public FileAttachment? File { get; set; }
        public int Order { get; set; }
    }

    private sealed class DummyModel
    {
        public Guid FileField { get; set; }
        public List<ISubFile>? FilesField { get; set; }
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
    /// Upload/MultiUpload directly access context.Items["model"] (no
    /// ContainsKey guard), so the context must always include the "model"
    /// key (value may be null).
    /// </summary>
    private static TagHelperContext MakeContext(string elementName = "wt:upload")
        => new(elementName, new TagHelperAttributeList(),
               new Dictionary<object, object> { { "model", null! } }, "test-id");

    private static TagHelperOutput MakeOutput(string tagName = "button")
        => new(tagName, new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static string? ExtractJsonFromIsland(string html, string needleType)
    {
        var marker = "\"type\":\"" + needleType + "\"";
        var idx = html.IndexOf(marker, System.StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = html.LastIndexOf('{', idx);
        if (start < 0) return null;
        var depth = 0;
        var end = -1;
        for (var i = start; i < html.Length; i++)
        {
            if (html[i] == '{') depth++;
            else if (html[i] == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }
        if (end < 0) return null;
        return html[start..(end + 1)];
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ═══════════════════════════ UploadTagHelper ═══════════════════════════

    // ── Flag OFF (default) — byte-for-byte legacy inline render ─────────────

    [TestMethod]
    public void Upload_FlagOff_EmitsInlineUploadRenderScript_NoUploadIsland()
    {
        SetupLocalizer();
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField"),
            Id = "upload_l_1",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "layui.use(['upload']",
            "Flag OFF (default) must keep emitting the legacy inline upload.render");
        StringAssert.Contains(postHtml, "layui.upload.render(");
        Assert.IsFalse(postHtml.Contains("\"type\":\"upload\""),
            "Flag OFF must never emit an 'upload' island");
        Assert.IsFalse(postHtml.Contains("data-wtm-upload-mode"),
            "Flag OFF must never emit the data-wtm-upload-* attributes");
    }

    [TestMethod]
    public void Upload_FlagOff_ExistingFile_EmitsInlineAjaxScript_NoUploadExistingIsland()
    {
        SetupLocalizer();
        var fileId = Guid.NewGuid();
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField", fileId),
            Id = "upload_l_2",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "/_Framework/GetFileName/" + fileId);
        Assert.IsFalse(postHtml.Contains("\"type\":\"uploadExisting\""),
            "Flag OFF must never emit an 'uploadExisting' island");
    }

    // ── Flag ON — island render, no inline script ───────────────────────────

    [TestMethod]
    public void Upload_FlagOn_EmitsUploadIsland_NoInlineRender()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField"),
            Id = "upload_l_3",
            UploadType = UploadTypeEnum.AllFiles,
            FileSize = 500
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"upload\"",
            "Flag ON must emit the 'upload' island");
        Assert.IsFalse(postHtml.Contains("layui.use(['upload']"),
            "Flag ON must NOT also emit the legacy inline upload.render script");

        var json = ExtractJsonFromIsland(postHtml, "upload");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("upload_l_3", root.GetProperty("id").GetString());
        Assert.AreEqual("#upload_l_3button", root.GetProperty("el").GetString());
        Assert.AreEqual(500, root.GetProperty("size").GetInt32());
        Assert.IsFalse(root.TryGetProperty("exts", out _),
            "AllFiles has no ext filter — exts omitted (WhenWritingNull)");
        Assert.IsFalse(root.TryGetProperty("number", out _),
            "UploadTagHelper never sets number — omitted (WhenWritingNull)");
    }

    [TestMethod]
    public void Upload_FlagOn_ImageFileWithCustomType_IslandCarriesExts()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField"),
            Id = "upload_l_4",
            UploadType = UploadTypeEnum.ImageFile
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml, "upload");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual("jpg|jpeg|gif|bmp|png|tif", doc.RootElement.GetProperty("exts").GetString());
    }

    [TestMethod]
    public void Upload_FlagOn_HiddenInputCarriesUploadModeSingleAndCs()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField"),
            Id = "upload_l_5",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "data-wtm-upload-mode=\"single\"");
        StringAssert.Contains(postHtml, "id='upload_l_5'");
    }

    [TestMethod]
    public void Upload_FlagOn_ExistingFile_EmitsUploadExistingIsland_NoInlineAjaxScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var fileId = Guid.NewGuid();
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField", fileId),
            Id = "upload_l_6",
            UploadType = UploadTypeEnum.AllFiles,
            ShowPreview = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"uploadExisting\"");
        Assert.IsFalse(postHtml.Contains("$.ajax({"),
            "Flag ON must not also emit the legacy existing-file $.ajax script");

        var json = ExtractJsonFromIsland(postHtml, "uploadExisting");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("single", root.GetProperty("mode").GetString());
        Assert.IsTrue(root.GetProperty("showPreview").GetBoolean());
        var files = root.GetProperty("files");
        Assert.AreEqual(1, files.GetArrayLength());
        Assert.AreEqual(fileId.ToString(), files[0].GetProperty("fileId").GetString());
        StringAssert.Contains(files[0].GetProperty("getUrl").GetString()!, "/_Framework/GetFileName/" + fileId);
    }

    [TestMethod]
    public void Upload_FlagOn_NoExistingFile_NeverEmitsUploadExistingIsland()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField", Guid.Empty),
            Id = "upload_l_7",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("\"type\":\"uploadExisting\""),
            "Guid.Empty must never trigger the existing-file init path, island or legacy");
    }

    // ═══════════════════════════ MultiUploadTagHelper ══════════════════════

    [TestMethod]
    public void MultiUpload_FlagOff_EmitsInlineRenderScript_NoIsland()
    {
        SetupLocalizer();
        var helper = new MultiUploadTagHelper
        {
            Field = MakeField("FilesField"),
            Id = "multiupload_l_1",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:multiupload"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "layui.use(['upload']");
        StringAssert.Contains(postHtml, $"{helper.Id}SetValues();");
        Assert.IsFalse(postHtml.Contains("\"type\":\"multiUpload\""),
            "Flag OFF must never emit a 'multiUpload' island");
        Assert.IsFalse(postHtml.Contains("data-wtm-upload-mode"),
            "Flag OFF must never emit the data-wtm-upload-* attributes");
    }

    [TestMethod]
    public void MultiUpload_FlagOn_EmitsMultiUploadIsland_NoInlineRender()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new MultiUploadTagHelper
        {
            Field = MakeField("FilesField"),
            Id = "multiupload_l_2",
            UploadType = UploadTypeEnum.AllFiles,
            NumFileOnce = 3
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:multiupload"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"multiUpload\"");
        Assert.IsFalse(postHtml.Contains("layui.use(['upload']"),
            "Flag ON must NOT also emit the legacy inline render script");

        var json = ExtractJsonFromIsland(postHtml, "multiUpload");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.AreEqual(3, doc.RootElement.GetProperty("number").GetInt32());
    }

    [TestMethod]
    public void MultiUpload_FlagOn_HiddenInputCarriesModeFieldNameAndSelected()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var f1 = Guid.NewGuid();
        var f2 = Guid.NewGuid();
        var helper = new MultiUploadTagHelper
        {
            Field = MakeField("FilesField", new List<ISubFile>
            {
                new DummySubFile { FileId = f1 },
                new DummySubFile { FileId = f2 }
            }),
            Id = "multiupload_l_3",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:multiupload"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "data-wtm-upload-mode=\"multi\"");
        StringAssert.Contains(postHtml, "data-wtm-field-name=\"FilesField\"");
        StringAssert.Contains(postHtml, f1.ToString());
        StringAssert.Contains(postHtml, f2.ToString());
    }

    [TestMethod]
    public void MultiUpload_FlagOn_ExistingFiles_EmitsUploadExistingIslandWithAllFiles_NoInlineLoop()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var f1 = Guid.NewGuid();
        var f2 = Guid.NewGuid();
        var helper = new MultiUploadTagHelper
        {
            Field = MakeField("FilesField", new List<ISubFile>
            {
                new DummySubFile { FileId = f1 },
                new DummySubFile { FileId = f2 }
            }),
            Id = "multiupload_l_4",
            UploadType = UploadTypeEnum.AllFiles,
            ShowPreview = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:multiupload"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "\"type\":\"uploadExisting\"");
        Assert.IsFalse(postHtml.Contains("$.ajax({"),
            "Flag ON must not also emit the legacy per-file existing-file $.ajax loop");

        var json = ExtractJsonFromIsland(postHtml, "uploadExisting");
        Assert.IsNotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.AreEqual("multi", root.GetProperty("mode").GetString());
        var files = root.GetProperty("files");
        Assert.AreEqual(2, files.GetArrayLength());
    }

    [TestMethod]
    public void MultiUpload_FlagOn_NoExistingFiles_NeverEmitsUploadExistingIsland()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new MultiUploadTagHelper
        {
            Field = MakeField("FilesField", new List<ISubFile>()),
            Id = "multiupload_l_5",
            UploadType = UploadTypeEnum.AllFiles
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:multiupload"), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("\"type\":\"uploadExisting\""),
            "No pre-existing files must never trigger the existing-file init path, island or legacy");
    }

    // ── Required-field validation — Upload/MultiUpload are excluded from
    // BaseFieldTag's required-validation block entirely (see
    // BaseFieldTag.Process's `!(this is UploadTagHelper || ... ||
    // MultiUploadTagHelper || ...)` guard), so NEITHER the legacy inline
    // window[Id].update(...) script NOR either island should ever carry a
    // layVerify/layReqText — confirming #470 Slice L correctly found "none
    // exists", same as Transfer in Slice K.

    [TestMethod]
    public void Upload_Required_FlagOn_NeverEmitsWindowUpdateRequiredScript()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { UseSelectIslandRender = true });
        var helper = new UploadTagHelper
        {
            Field = MakeField("FileField"),
            Id = "upload_l_required",
            UploadType = UploadTypeEnum.AllFiles,
            Required = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains(".update({"),
            "UploadTagHelper is excluded from BaseFieldTag's required-validation block");
    }
}
