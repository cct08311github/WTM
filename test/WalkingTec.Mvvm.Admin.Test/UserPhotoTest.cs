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

        private FrameworkUserVM Vm()
        {
            var controller = MockController.CreateApi<FrameworkUserController>(NewDc(), "admin");
            return controller.Wtm.CreateVM<FrameworkUserVM>();
        }

        private bool Add(string itcode, Guid? photo)
        {
            var vm = Vm();
            vm.Entity = new FrameworkUser { ITCode = itcode, Name = "n", Password = "p", PhotoId = photo };
            vm.DoAddAsync().GetAwaiter().GetResult();
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
            keep.DoEditAsync().GetAwaiter().GetResult();
            Assert.IsTrue(keep.MSD.IsValid);

            var change = Vm();
            change.Entity = new FrameworkUser { ID = id, ITCode = "g", Name = "n3", PhotoId = AddFile("svg") };
            change.DoEditAsync().GetAwaiter().GetResult();
            Assert.IsFalse(change.MSD.IsValid);
            Assert.AreEqual(photo, StoredPhoto("g"));
        }
    }
}
