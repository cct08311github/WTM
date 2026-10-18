using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #88: wt:upload, wt:multiupload and wt:ueditor read the form model from the TagHelper
    /// context. Outside wt:form the "model" item does not exist, which used to throw KeyNotFoundException.
    /// Inside a form the model is still used (its CurrentCS is appended to the upload url).
    /// </summary>
    [TestClass]
    public class UploadOutsideFormTagHelperTests : LayUITagHelperTestBase
    {
        private const string CsMarker = "_DONOT_USE_CS=layui-test-cs";

        private static LayuiTestVM NewVmWithCs()
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.CurrentCS = "layui-test-cs";
            return new LayuiTestVM { Html = "hi", Wtm = wtm };
        }

        // Items WITHOUT the "model" key, like a tag used outside wt:form.
        private static IDictionary<object, object> NoFormItems() => new Dictionary<object, object>();

        [TestMethod]
        public void Upload_OutsideForm_RendersWithoutModel()
        {
            var helper = new UploadTagHelper { Field = Field(new LayuiTestVM(), x => x.PhotoId), UploadType = UploadTypeEnum.ImageFile };

            var html = Render(helper, "wt:upload", NoFormItems());

            AssertContains(html, "<button", "id=\"LayuiTestVM_PhotoIdbutton\"", "/_Framework/UploadImage", "layui.use(['upload'");
            AssertNotContains(html, "UploadImage?1=1&_DONOT_USE_CS=", "UploadImage?1=1&amp;_DONOT_USE_CS=");
        }

        [TestMethod]
        public void Upload_OutsideForm_ExplicitConnectionStringStillApplied()
        {
            var helper = new UploadTagHelper { Field = Field(new LayuiTestVM(), x => x.PhotoId), ConnectionString = "explicit-cs" };

            AssertContains(Render(helper, "wt:upload", NoFormItems()), "_DONOT_USE_CS=explicit-cs");
        }

        [TestMethod]
        public void Upload_InsideForm_StillUsesModelConnectionString()
        {
            var vm = NewVmWithCs();
            var helper = new UploadTagHelper { Field = Field(vm, x => x.PhotoId), UploadType = UploadTypeEnum.ImageFile };

            var html = Render(helper, "wt:upload", FormItems(vm, "wtForm_x"));

            AssertContains(html, CsMarker, "/_Framework/UploadImage", "layui.use(['upload'");
        }

        [TestMethod]
        public void MultiUpload_OutsideForm_RendersWithoutModel()
        {
            var vm = new LayuiTestVM { Files = new List<TestSubFile> { new TestSubFile { FileId = Guid.NewGuid() } } };
            var helper = new MultiUploadTagHelper { Field = Field(vm, x => x.Files) };

            var html = Render(helper, "wt:multiupload", NoFormItems());

            AssertContains(html, "<button", "id=\"LayuiTestVM_Filesbutton\"", "/_Framework/Upload", "layui.use(['upload'");
            AssertNotContains(html, "Upload?1=1&_DONOT_USE_CS=", "Upload?1=1&amp;_DONOT_USE_CS=");
        }

        [TestMethod]
        public void MultiUpload_InsideForm_StillUsesModelConnectionString()
        {
            var vm = NewVmWithCs();
            vm.Files = new List<TestSubFile> { new TestSubFile { FileId = Guid.NewGuid() } };
            var helper = new MultiUploadTagHelper { Field = Field(vm, x => x.Files) };

            var html = Render(helper, "wt:multiupload", FormItems(vm, "wtForm_x"));

            AssertContains(html, CsMarker, "/_Framework/Upload", "layui.use(['upload'");
        }

        [TestMethod]
        public void UEditor_OutsideForm_RendersWithoutModel()
        {
            var helper = new UEditorTagHelper { Field = Field(new LayuiTestVM { Html = "hi" }, x => x.Html) };

            var html = Render(helper, "wt:ueditor", NoFormItems());

            AssertContains(html, "isrich=\"1\"", "layui.use(['ueditorconfig']", "ueditor.loadEditor('LayuiTestVM_Html')");
        }

        [TestMethod]
        public void UEditor_InsideForm_StillUsesModelConnectionString()
        {
            var vm = NewVmWithCs();
            var helper = new UEditorTagHelper { Field = Field(vm, x => x.Html) };

            var html = Render(helper, "wt:ueditor", FormItems(vm));

            AssertContains(html, "isrich=\"1\"", "ueditor.loadEditor('LayuiTestVM_Html')");
            // In a form the upload url (with the model's connection string) is published to the model's UEditor options.
            StringAssert.Contains(vm.ConfigInfo.UEditorOptions.FileActionName, CsMarker);
        }
    }
}
