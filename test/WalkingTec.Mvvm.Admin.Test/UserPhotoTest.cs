using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkUserVms;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// The photo of a user is shown to everybody, so a user record only takes a file as its photo when the file exists, is a picture
    /// and is not an attachment of another record.
    /// </summary>
    [TestClass]
    public class UserPhotoTest
    {
        private string _seed;

        private WalkingTec.Mvvm.Vue3Demo.DataContext NewDc() => new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory);

        [TestInitialize]
        public void Init() => _seed = Guid.NewGuid().ToString();

        private Guid AddFile(string ext)
        {
            using var dc = NewDc();
            var id = Guid.NewGuid();
            dc.Set<FileAttachment>().Add(new FileAttachment { ID = id, FileName = "f." + ext, FileExt = ext, UploadTime = DateTime.Now, FileData = new byte[] { 1 } });
            dc.SaveChanges();
            return id;
        }

        private static readonly Microsoft.AspNetCore.DataProtection.IDataProtectionProvider Provider = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();

        private FrameworkUserVM Vm()
        {
            var controller = MockController.CreateApi<FrameworkUserController>(NewDc(), "admin", dataProtection: Provider);
            return controller.Wtm.CreateVM<FrameworkUserVM>();
        }

        // the form posts the proof that the user uploaded the photo (the LinkRef of the upload answer), as the clients of the framework do
        private static void Prove(FrameworkUserVM vm, Guid? photo)
        {
            if (photo != null)
            {
                vm.UploadedFileRefs = new System.Collections.Generic.List<string> { vm.Wtm.CreateFileReference(photo.Value, FilePurpose.Link) };
            }
        }

        private bool Add(string itcode, Guid? photo, bool proof = true)
        {
            var vm = Vm();
            vm.Entity = new FrameworkUser { ITCode = itcode, Name = "n", Password = "p", PhotoId = photo };
            if (proof)
            {
                Prove(vm, photo);
            }
            try
            {
                vm.DoAddAsync().GetAwaiter().GetResult();
            }
            catch (System.Transactions.TransactionInDoubtException)
            {
                // the in-memory data context has no transaction: its stand-in throws when the view model rolls back a refused save
            }
            return vm.MSD.IsValid;
        }

        private Guid? StoredPhoto(string itcode)
        {
            using var dc = NewDc();
            return dc.Set<FrameworkUser>().Where(x => x.ITCode == itcode).Select(x => x.PhotoId).FirstOrDefault();
        }

        [TestMethod]
        public void APictureThatIsNotAnAttachmentOfAnotherRecord_IsAccepted()
        {
            var photo = AddFile("png");
            Assert.IsTrue(Add("a", photo));
            Assert.AreEqual(photo, StoredPhoto("a"));
            Assert.IsTrue(Add("none", null));
        }

        [TestMethod]
        public void AFileThatDoesNotExist_OrIsNotAPicture_IsRefused_AndNothingIsStored()
        {
            Assert.IsFalse(Add("b", Guid.NewGuid()));
            Assert.IsFalse(Add("c", AddFile("svg")));
            Assert.IsFalse(Add("d", AddFile("html")));
            Assert.IsFalse(Add("e", AddFile("txt")));
            using var dc = NewDc();
            Assert.AreEqual(0, dc.Set<FrameworkUser>().Count(x => new[] { "b", "c", "d", "e" }.Contains(x.ITCode)));
        }

        [TestMethod]
        public void APictureThatTheUserDidNotUpload_IsRefused_AsThePhotoOfANewUser()
        {
            var photo = AddFile("png");
            Assert.IsFalse(Add("noproof", photo, proof: false), "no LinkRef in the post");
            Assert.AreEqual(0, NewDc().Set<FrameworkUser>().Count(x => x.ITCode == "noproof"));
            Assert.IsTrue(Add("withproof", photo));
        }

        [TestMethod]
        public void AFileThatAnotherRecordUses_IsRefused()
        {
            var photo = AddFile("png");
            using (var dc = NewDc())
            {
                dc.Set<WalkingTec.Mvvm.ReactDemo.Models.SchoolVue3>().Add(new WalkingTec.Mvvm.ReactDemo.Models.SchoolVue3 { SchoolCode = "1", SchoolName = "s", PhotoId = photo });
                dc.SaveChanges();
            }
            Assert.IsFalse(Add("f", photo));
        }

        [TestMethod]
        public void OnEdit_OnlyAChangedPhotoIsChecked()
        {
            var photo = AddFile("png");
            Assert.IsTrue(Add("g", photo));
            // the photo is used elsewhere later; the user keeps it
            using (var dc = NewDc())
            {
                dc.Set<WalkingTec.Mvvm.ReactDemo.Models.SchoolVue3>().Add(new WalkingTec.Mvvm.ReactDemo.Models.SchoolVue3 { SchoolCode = "2", SchoolName = "s2", PhotoId = photo });
                dc.SaveChanges();
            }
            Guid id;
            using (var dc = NewDc()) { id = dc.Set<FrameworkUser>().Single(x => x.ITCode == "g").ID; }

            var keep = Vm();
            keep.Entity = new FrameworkUser { ID = id, ITCode = "g", Name = "n2", PhotoId = photo };
            // the photo is the one the record already has: no proof is needed
            keep.DoEditAsync().GetAwaiter().GetResult();
            Assert.IsTrue(keep.MSD.IsValid);

            var change = Vm();
            var svg = AddFile("svg");
            change.Entity = new FrameworkUser { ID = id, ITCode = "g", Name = "n3", PhotoId = svg };
            Prove(change, svg);
            change.DoEditAsync().GetAwaiter().GetResult();
            Assert.IsFalse(change.MSD.IsValid);
            Assert.AreEqual(photo, StoredPhoto("g"));
        }
    }
}
