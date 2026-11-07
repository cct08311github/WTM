using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// The upload control of an import form (<c>wt:upload field="UploadFileId"</c>) also carries the reference the upload endpoint gives to the
    /// user (<c>UploadFileRef</c>); other upload fields are rendered as before.
    /// </summary>
    [TestClass]
    public class UploadImportReferenceTests : LayUITagHelperTestBase
    {
        private class ImportFormVM : BaseVM
        {
            public string UploadFileId { get; set; }
            public string UploadFileRef { get; set; }
        }

        private static string RenderImport(ImportFormVM vm)
        {
            return Render(new UploadTagHelper { Field = Field(vm, x => x.UploadFileId) }, "wt:upload", FormItems(vm));
        }

        [TestMethod]
        public void TheImportUpload_CarriesAHiddenReferenceField_FilledAfterTheUpload()
        {
            var html = RenderImport(new ImportFormVM());
            AssertContains(html,
                "name='UploadFileId'",
                "name='UploadFileRef'",
                "$('#ImportFormVM_UploadFileIdImportRef').val(res.Data.ImportRef || '');");
        }

        [TestMethod]
        public void TheImportUpload_ClearsTheReferenceWhenTheFileIsRemoved()
        {
            var html = RenderImport(new ImportFormVM());
            AssertContains(html, "$('#ImportFormVM_UploadFileIdImportRef').val('');");
        }

        [TestMethod]
        public void TheImportUpload_KeepsAReferenceTheViewModelAlreadyHas()
        {
            var html = RenderImport(new ImportFormVM { UploadFileId = "abc", UploadFileRef = "r<ef'" });
            AssertContains(html, "value='r&lt;ef&#39;'");
        }

        [TestMethod]
        public void OtherUploadFields_AreRenderedWithoutAReferenceField()
        {
            var vm = new LayuiTestVM();
            var html = Render(new UploadTagHelper { Field = Field(vm, x => x.PhotoId) }, "wt:upload", FormItems(vm));
            AssertNotContains(html, "ImportRef", "UploadFileRef");
        }

        [TestMethod]
        public void TheReferenceFieldName_FollowsThePrefixOfTheUploadField()
        {
            Assert.AreEqual("UploadFileRef", UploadTagHelper.ImportReferenceFieldName("UploadFileId"));
            Assert.AreEqual("vm.UploadFileRef", UploadTagHelper.ImportReferenceFieldName("vm.UploadFileId"));
            Assert.IsNull(UploadTagHelper.ImportReferenceFieldName("MyUploadFileId"));
            Assert.IsNull(UploadTagHelper.ImportReferenceFieldName("PhotoId"));
            Assert.IsNull(UploadTagHelper.ImportReferenceFieldName(null));
        }
    }
}
