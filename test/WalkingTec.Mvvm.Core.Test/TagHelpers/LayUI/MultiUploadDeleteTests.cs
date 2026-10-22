using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #282: the delete button of wt:multiupload called an endpoint no controller of the framework provides, so the file stayed
    /// in the list. Like wt:upload, the delete now only changes the page: the file leaves the list and the posted values, and the
    /// form carries DeletedFileIds (which the framework already handles when the view model is saved).
    /// </summary>
    [TestClass]
    public class MultiUploadDeleteTests : LayUITagHelperTestBase
    {
        private const string Id = "LayuiTestVM_Files";

        private static string RenderMulti(bool? preview, bool disabled, bool existing)
        {
            var vm = new LayuiTestVM { Files = existing ? new List<TestSubFile> { new TestSubFile { FileId = Guid.NewGuid() } } : new List<TestSubFile>() };
            var helper = new MultiUploadTagHelper { Field = Field(vm, x => x.Files), ShowPreview = preview, Disabled = disabled };
            return Render(helper, "wt:multiupload", FormItems(vm));
        }

        [TestMethod]
        public void DoDelete_DoesNotCallAnEndpoint()
        {
            foreach (var preview in new bool?[] { null, true })
            {
                var html = RenderMulti(preview, false, true);

                AssertNotContains(html, "/api/_file/DeletedFile", "DeletedFile/");
                AssertNotContains(html.Substring(html.IndexOf("function " + Id + "DoDelete", StringComparison.Ordinal), 600), "$.ajax");
            }
        }

        [TestMethod]
        public void DoDelete_RemembersTheFileInDeletedFileIdsLikeTheSingleUpload()
        {
            var html = RenderMulti(null, false, false);

            // the value is set through jQuery, so no markup is built from the id
            AssertContains(html,
                "function " + Id + "DoDelete(fileid){",
                "$('#" + Id + "').parents('form').append($('<input type=\"hidden\" name=\"DeletedFileIds\" />').val(fileid));");
        }

        [TestMethod]
        public void DoDelete_RemovesTheLabelAndTheSelectedValueAndRefreshesThePostedValues()
        {
            var html = RenderMulti(null, false, false);

            AssertContains(html,
                "$('#" + Id + "label label').filter(function(){ return this.id === 'label' + fileid; }).remove();",
                Id + "selected = " + Id + "selected.filter(function(item) {",
                "return item != fileid;",
                Id + "SetValues();");
            // the id is compared, not put into a selector
            AssertNotContains(html, "$('#label'+fileid)");
        }

        [TestMethod]
        public void DoDelete_IsWiredToTheDeleteControlOfExistingAndNewFiles()
        {
            foreach (var preview in new bool?[] { null, true })
            {
                var html = RenderMulti(preview, false, true);

                // a file uploaded on this page and a file the page was rendered with both call DoDelete
                AssertContains(html, Id + "DoDelete(res.Data.Id);", Id + "DoDelete('");
            }
        }

        [TestMethod]
        public void DisabledField_HasNoDeleteControlForExistingFiles()
        {
            var html = RenderMulti(null, true, true);

            AssertNotContains(html, Id + "DoDelete('");
        }
    }
}
