using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.Test.Mock;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// A file uploaded in a form carries the proof of the upload (<c>LinkRef</c>) into the post (<c>UploadedFileRefs</c>), and a form that is shown
    /// again hands out the references that read a file only for a file the record refers to or that the request proves was uploaded by the user.
    /// </summary>
    [TestClass]
    public class UploadLinkReferenceTests : LayUITagHelperTestBase
    {
        private class AlbumVM : BaseCRUDVM<Album>
        {
        }

        private string _seed;
        private Guid _stored, _storedPhoto, _foreign, _mine, _albumId;

        [TestInitialize]
        public void Seed()
        {
            _seed = Guid.NewGuid().ToString();
            _stored = Guid.NewGuid();
            _storedPhoto = Guid.NewGuid();
            _foreign = Guid.NewGuid();
            _mine = Guid.NewGuid();
            using var dc = new DataContext(_seed, DBTypeEnum.Memory);
            foreach (var id in new[] { _stored, _storedPhoto, _foreign, _mine })
            {
                dc.Set<FileAttachment>().Add(new FileAttachment { ID = id, FileName = id + ".png", FileExt = "png", UploadTime = DateTime.Now });
            }
            var album = new Album { ID = Guid.NewGuid(), Title = "a", CoverId = _stored, Photos = new List<AlbumPhoto> { new AlbumPhoto { FileId = _storedPhoto } } };
            dc.Set<Album>().Add(album);
            dc.SaveChanges();
            _albumId = album.ID;
        }

        private WTMContext NewWtm(Action<Configs> configure = null)
        {
            var configs = new Configs();
            configure?.Invoke(configs);
            return MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "bob", dataProtection: ImportUploadRef.Provider, configs: configs);
        }

        private AlbumVM PostedVm(Guid cover, IEnumerable<Guid> photos = null, string[] refs = null, Action<Configs> configure = null)
        {
            var vm = new AlbumVM { Wtm = NewWtm(configure) };
            vm.Entity = new Album { ID = _albumId, CoverId = cover, Photos = (photos ?? new Guid[0]).Select(x => new AlbumPhoto { FileId = x }).ToList() };
            vm.UploadedFileRefs = (refs ?? new string[0]).ToList();
            return vm;
        }

        private static string RenderUpload(AlbumVM vm)
        {
            return Render(new UploadTagHelper { Field = Field(vm, x => x.Entity.CoverId) }, "wt:upload", FormItems(vm));
        }

        private static string RenderMulti(AlbumVM vm)
        {
            return Render(new MultiUploadTagHelper { Field = Field(vm, x => x.Entity.Photos) }, "wt:multiupload", FormItems(vm));
        }

        private string LinkRef(Guid id) => NewWtm().CreateFileReference(id, FilePurpose.Link);

        private const string Fref = "_DONOT_USE_FREF";

        // ---- upload ---------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Upload_PutsTheLinkRefOfTheAnswerIntoTheForm()
        {
            var html = RenderUpload(PostedVm(Guid.Empty));
            AssertContains(html,
                "if (res.Data.LinkRef)",
                "name=\"UploadedFileRefs\"",
                ".val(res.Data.LinkRef)");
            // set as a value (not written into markup), and a replaced or removed file takes its proof away
            AssertNotContains(html, "value=\"'+res.Data.LinkRef", "value='\"+res.Data.LinkRef");
            Assert.IsTrue(Regex.Matches(html, @"\$\('#[A-Za-z0-9_]*LinkRef'\)\.remove\(\);").Count >= 2, "removed on delete and on replace");
        }

        [TestMethod]
        public void Upload_OfAnImportForm_CarriesNoLinkRefScript()
        {
            var vm = new ImportLikeVM();
            var html = Render(new UploadTagHelper { Field = Field(vm, x => x.UploadFileId) }, "wt:upload", FormItems(vm));
            AssertNotContains(html, "LinkRef", "UploadedFileRefs");
        }

        private class ImportLikeVM : BaseVM
        {
            public string UploadFileId { get; set; }
            public string UploadFileRef { get; set; }
        }

        [TestMethod]
        public void Upload_ShownAgain_KeepsTheProofOfTheFileTheFormHolds()
        {
            var reference = LinkRef(_mine);
            var html = RenderUpload(PostedVm(_mine, refs: new[] { reference }));
            Assert.IsTrue(Regex.IsMatch(html, @"<input type='hidden' id='[A-Za-z0-9_]*LinkRef' name='UploadedFileRefs' value='" + Regex.Escape(reference) + "' />"), html);
            StringAssert.Contains(html, Fref, "the file is shown with its references");
        }

        [TestMethod]
        public void Upload_ShownAgain_DoesNotKeepTheProofOfAnotherFileOrUser()
        {
            var other = PostedVm(_stored, refs: new[] { LinkRef(_mine) });
            AssertNotContains(RenderUpload(other), "name='UploadedFileRefs'");
            var alice = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "alice", dataProtection: ImportUploadRef.Provider);
            var ofAnotherUser = PostedVm(_mine, refs: new[] { alice.CreateFileReference(_mine, FilePurpose.Link) });
            AssertNotContains(RenderUpload(ofAnotherUser), "name='UploadedFileRefs'", Fref);
        }

        [TestMethod]
        public void Upload_ShownAgain_ForAFileOfTheRecord_KeepsTheReferences_AndNeedsNoProof()
        {
            var html = RenderUpload(PostedVm(_stored));
            StringAssert.Contains(html, Fref);
            AssertNotContains(html, "name='UploadedFileRefs'");
        }

        [TestMethod]
        public void Upload_ShownAgain_ForAFileOfSomebodyElse_HandsOutNoReference()
        {
            var html = RenderUpload(PostedVm(_foreign));
            AssertNotContains(html, Fref);
            // the control still shows the id that was posted (the user may fix the form), only without the references
            StringAssert.Contains(html, _foreign.ToString());
        }

        [TestMethod]
        public void Upload_ShownAgain_NoReferenceWithoutProof_ButTheOldBehaviorInLegacy()
        {
            var legacy = RenderUpload(PostedVm(_foreign, configure: c => c.FileEndpoints.LegacyVmFileLinking = true));
            StringAssert.Contains(legacy, Fref);
            var modeLegacy = RenderUpload(PostedVm(_foreign, configure: c => c.FileEndpoints.Mode = FileEndpointMode.Legacy));
            StringAssert.Contains(modeLegacy, Fref);
        }

        [TestMethod]
        public void Upload_OnAViewModelThatSavesNothing_IsRenderedAsBefore()
        {
            var vm = new LayuiTestVM { PhotoId = Guid.NewGuid(), Wtm = NewWtm() };
            var html = Render(new UploadTagHelper { Field = Field(vm, x => x.PhotoId) }, "wt:upload", FormItems(vm));
            StringAssert.Contains(html, Fref);
        }

        [TestMethod]
        public void Upload_AFormLoadedFromTheStore_ShowsItsFilesWithReferences()
        {
            var vm = new AlbumVM { Wtm = NewWtm() };
            vm.SetEntityById(_albumId);
            var html = RenderUpload(vm);
            StringAssert.Contains(html, Fref);
        }

        // ---- several files --------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void MultiUpload_KeepsTheLinkRefOfEachUploadedFile_WhileItStaysInTheList()
        {
            var html = RenderMulti(PostedVm(Guid.Empty));
            AssertContains(html,
                "if (res.Data.LinkRef) {",
                "name=\"UploadedFileRefs\"",
                "delete LayuiTestVM_PhotosPlaceholder".Replace("LayuiTestVM_PhotosPlaceholder", Regex.Match(html, @"var ([A-Za-z0-9_]*)refs = ").Groups[1].Value + "refs[fileid];"));
            AssertNotContains(html, "value=\"'+res.Data.LinkRef");
        }

        [TestMethod]
        public void MultiUpload_ShownAgain_KeepsTheProofOfTheFilesItHolds()
        {
            var reference = LinkRef(_mine);
            var html = RenderMulti(PostedVm(Guid.Empty, new[] { _storedPhoto, _mine }, new[] { reference }));
            StringAssert.Contains(html, "\"" + _mine + "\":\"" + reference + "\"");
            Assert.IsFalse(html.Contains("\"" + _storedPhoto + "\":\""), "a stored file needs no proof");
        }

        [TestMethod]
        public void MultiUpload_ShownAgain_ForAFileOfSomebodyElse_HandsOutNoReference_ButStoredFilesDo()
        {
            var html = RenderMulti(PostedVm(Guid.Empty, new[] { _storedPhoto, _foreign }));
            var withRefs = Regex.Matches(html, "/_Framework/GetFileName/([0-9a-f-]{36})[^'\"]*" + Fref).Select(x => x.Groups[1].Value).ToList();
            CollectionAssert.AreEqual(new[] { _storedPhoto.ToString() }, withRefs);
            StringAssert.Contains(html, _foreign.ToString());
        }
    }
}
