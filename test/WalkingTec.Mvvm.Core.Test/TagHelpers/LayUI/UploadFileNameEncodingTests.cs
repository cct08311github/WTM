using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #121: wt:upload and wt:multiupload generate client script that inserts file names (from the
    /// browser's File object or from server responses) into HTML strings. Those runtime values must be
    /// HTML-encoded by a helper defined in the generated script before they are concatenated.
    /// </summary>
    [TestClass]
    public class UploadFileNameEncodingTests : LayUITagHelperTestBase
    {
        private const string UploadEnc = "LayuiTestVM_PhotoIdHtmlEnc";
        private const string MultiEnc = "LayuiTestVM_FilesHtmlEnc";

        private static string RenderUpload(bool? preview, bool disabled, bool existing)
        {
            var vm = new LayuiTestVM { PhotoId = existing ? Guid.NewGuid() : (Guid?)null };
            var helper = new UploadTagHelper { Field = Field(vm, x => x.PhotoId), ShowPreview = preview, Disabled = disabled };
            return Render(helper, "wt:upload", FormItems(vm));
        }

        private static string RenderMulti(bool? preview, bool disabled, bool existing)
        {
            var vm = new LayuiTestVM { Files = existing ? new List<TestSubFile> { new TestSubFile { FileId = Guid.NewGuid() } } : new List<TestSubFile>() };
            var helper = new MultiUploadTagHelper { Field = Field(vm, x => x.Files), ShowPreview = preview, Disabled = disabled };
            return Render(helper, "wt:multiupload", FormItems(vm));
        }

        [TestMethod]
        public void Upload_DefinesHtmlEncodeHelper()
        {
            var html = RenderUpload(true, false, false);

            AssertContains(html,
                "function " + UploadEnc + "(s){",
                "replace(/[&<>\"']/g, function(c){ return '&#' + c.charCodeAt(0) + ';'; })");
        }

        [TestMethod]
        public void Upload_PreviewAfterUpload_EncodesNameAndResult()
        {
            var html = RenderUpload(true, false, false);

            AssertContains(html,
                "src=\"'+ " + UploadEnc + "(result) +'\" alt=\"'+ " + UploadEnc + "(file.name) +'\"");
            AssertNotContains(html, "'+ result +'", "'+ file.name +'");
        }

        [TestMethod]
        public void Upload_NoPreviewAfterUpload_EncodesNameFromResponse()
        {
            var html = RenderUpload(null, false, false);

            AssertContains(html, "\"+" + UploadEnc + "(res.Data.Name) +\"");
            AssertNotContains(html, "\"+res.Data.Name");
        }

        [TestMethod]
        public void Upload_DoDelete_EncodesFileIdInInsertedInput()
        {
            var html = RenderUpload(null, false, false);

            AssertContains(html, "value='\"+" + UploadEnc + "(fileid)+\"'");
            AssertNotContains(html, "value='\"+fileid+\"'");
        }

        [TestMethod]
        public void Upload_ExistingFileWithPreview_EncodesNameFromGetFileName()
        {
            foreach (var disabled in new[] { false, true })
            {
                var html = RenderUpload(true, disabled, true);

                AssertContains(html, "alt=\"'+ " + UploadEnc + "(data) +'\"");
                AssertNotContains(html, "alt=\"'+ data +'\"");
            }
        }

        [TestMethod]
        public void Upload_ExistingFileWithoutPreview_EncodesNameFromGetFileName()
        {
            var editable = RenderUpload(null, false, true);
            var readOnly = RenderUpload(null, true, true);

            AssertContains(editable, "\"+" + UploadEnc + "(data)+\"  ");
            AssertContains(readOnly, "\"+" + UploadEnc + "(data)+\"</a>");
            AssertNotContains(editable, "\"+data+\"");
            AssertNotContains(readOnly, "\"+data+\"");
        }

        [TestMethod]
        public void MultiUpload_DefinesHtmlEncodeHelper()
        {
            var html = RenderMulti(true, false, false);

            AssertContains(html,
                "function " + MultiEnc + "(s){",
                "replace(/[&<>\"']/g, function(c){ return '&#' + c.charCodeAt(0) + ';'; })");
        }

        [TestMethod]
        public void MultiUpload_SetValues_EncodesSelectedIdInInsertedInput()
        {
            var html = RenderMulti(null, false, false);

            AssertContains(html, "value=\"' + " + MultiEnc + "(LayuiTestVM_Filesselected[count]) + '\"");
            AssertNotContains(html, "value=\"' + LayuiTestVM_Filesselected[count]");
        }

        [TestMethod]
        public void MultiUpload_PreviewAfterUpload_EncodesNameAndIdFromResponse()
        {
            var html = RenderMulti(true, false, false);

            AssertContains(html,
                "<label id=\"label'+" + MultiEnc + "(res.Data.Id)+'\"><img alt=\"'+" + MultiEnc + "(res.Data.Name)+'\"",
                "GetFile?id='+ " + MultiEnc + "(res.Data.Id)+'&_DONOT_USE_CS=",
                "id=\"preview'+" + MultiEnc + "(res.Data.Id)+'\"",
                "id=\"del'+" + MultiEnc + "(res.Data.Id)+'\"");
            AssertNotContains(html, "'+res.Data.Name+'", "id=\"preview'+res.Data.Id", "id=\"del'+res.Data.Id", "GetFile?id='+ res.Data.Id");
        }

        [TestMethod]
        public void MultiUpload_NoPreviewAfterUpload_EncodesNameAndIdFromResponse()
        {
            var html = RenderMulti(null, false, false);

            AssertContains(html,
                "<label id='label\"+" + MultiEnc + "(res.Data.Id)+\"'>",
                "id='del\"+" + MultiEnc + "(res.Data.Id)+\"'",
                "\"+" + MultiEnc + "(res.Data.Name) +\"");
            AssertNotContains(html, "\"+res.Data.Name", "id='label\"+res.Data.Id", "id='del\"+res.Data.Id");
        }

        [TestMethod]
        public void MultiUpload_ExistingFiles_EncodeNameFromGetFileName()
        {
            foreach (var preview in new bool?[] { true, null })
            {
                foreach (var disabled in new[] { false, true })
                {
                    var html = RenderMulti(preview, disabled, true);

                    AssertContains(html, MultiEnc + "(data)");
                    AssertNotContains(html, "'+data+'", "\"+data+\"");
                }
            }
        }

        [TestMethod]
        public void ExistingFileScripts_AreAppendedAfterTheHelperDefinition()
        {
            var upload = RenderUpload(null, false, true);
            var multi = RenderMulti(null, false, true);

            foreach (var (html, enc) in new[] { (upload, UploadEnc), (multi, MultiEnc) })
            {
                var helperAt = html.IndexOf("function " + enc, StringComparison.Ordinal);
                var ajaxAt = html.IndexOf("$.ajax", StringComparison.Ordinal);
                Assert.IsTrue(helperAt >= 0, "helper not defined");
                Assert.IsTrue(ajaxAt > helperAt, "existing-file script must come after the helper definition");
            }
        }
    }
}
