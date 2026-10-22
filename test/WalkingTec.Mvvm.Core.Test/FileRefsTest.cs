using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// What a list row and a detail view model tell a single page application about the files they refer to: a reference (capability) per file,
    /// next to the row (<c>Field__fref</c>) or on the view model (<c>FileRefs</c>).
    /// </summary>
    [TestClass]
    public class FileRefsTest
    {
        private static readonly Guid Photo = Guid.NewGuid();
        private static readonly Guid Other = Guid.NewGuid();

        private readonly IDataProtectionProvider _provider = new EphemeralDataProtectionProvider();

        private WTMContext Wtm(IDataContext dc = null, IDataProtectionProvider provider = null, FileEndpointMode mode = FileEndpointMode.Enforce)
        {
            var configs = new Configs();
            configs.FileEndpoints.Mode = mode;
            return MockWtmContext.CreateWtmContext(dc, "alice", dataProtection: provider ?? _provider, configs: configs);
        }

        private FileEndpointReference Open(string capability) => FileEndpointGuard.Unprotect(FileEndpointGuard.CreateProtector(_provider), capability);

        private static bool Admits(WTMContext wtm, Guid id, string capability, FilePurpose purpose)
        {
            return wtm.AuthorizeFileRequest("GetFile", id.ToString(), purpose, capability, () => new FileEndpointFileInfo { UploadTime = DateTime.Now }).Allowed;
        }

        // ---- list rows -------------------------------------------------------------------------------------------------------------------

        private class PhotoListVM : BasePagedListVM<Student, BaseSearcher>
        {
            public Func<Student, object> PhotoFormat { get; set; }

            protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
            {
                return new List<GridColumn<Student>>
                {
                    this.MakeGridHeader(x => x.LoginName),
                    this.MakeGridHeader(x => x.PhotoId).SetFormat((e, v) => PhotoFormat(e)),
                };
            }
        }

        private static object DownloadAndView(Student e) => new List<ColumnFormatInfo>
        {
            ColumnFormatInfo.MakeDownloadButton(ButtonTypesEnum.Button, e.PhotoId),
            ColumnFormatInfo.MakeViewButton(ButtonTypesEnum.Button, e.PhotoId, 640, 480),
        };

        private string Row(PhotoListVM vm, Student student, bool plain = true)
        {
            return vm.GetSingleDataJson(student, plain);
        }

        private PhotoListVM ListVm(Func<Student, object> format, WTMContext wtm = null) => new PhotoListVM { Wtm = wtm ?? Wtm(), PhotoFormat = format };

        [TestMethod]
        public void ARowWithADownloadOrViewColumn_CarriesACapabilityNextToTheCell()
        {
            var wtm = Wtm();
            var json = Row(ListVm(DownloadAndView, wtm), new Student { LoginName = "a", PhotoId = Photo });
            using var doc = JsonDocument.Parse(json);

            Assert.AreEqual(Photo.ToString(), doc.RootElement.GetProperty("PhotoId").GetString(), "the cell itself is unchanged");
            var capability = doc.RootElement.GetProperty("PhotoId__fref").GetString();
            var opened = Open(capability);
            Assert.AreEqual(string.Empty, opened.User, "not bound to a user");
            Assert.AreEqual(Photo.ToString(), opened.FileId);
            Assert.AreEqual(FilePurpose.Read, opened.Purposes);
            Assert.IsFalse(opened.Purposes.HasFlag(FilePurpose.Delete));
            foreach (var purpose in new[] { FilePurpose.View, FilePurpose.Download, FilePurpose.Name })
            {
                Assert.IsTrue(Admits(wtm, Photo, capability, purpose), purpose.ToString());
            }
            Assert.IsFalse(Admits(wtm, Other, capability, FilePurpose.View), "one file only");
        }

        [TestMethod]
        public void ADownloadColumnAlone_AndAViewColumnAlone_CarryOneToo()
        {
            foreach (Func<Student, object> format in new Func<Student, object>[]
            {
                e => ColumnFormatInfo.MakeDownloadButton(ButtonTypesEnum.Button, e.PhotoId),
                e => ColumnFormatInfo.MakeViewButton(ButtonTypesEnum.Button, e.PhotoId),
            })
            {
                var json = Row(ListVm(format), new Student { LoginName = "a", PhotoId = Photo });
                StringAssert.Contains(json, "\"PhotoId__fref\":\"");
            }
        }

        [TestMethod]
        public void NoCapability_ForOtherFormats_ANullFile_OrTheHtmlMode()
        {
            Assert.IsFalse(Row(ListVm(e => ColumnFormatInfo.MakeHtml("<b>x</b>")), new Student { PhotoId = Photo }).Contains("__fref"));
            Assert.IsFalse(Row(ListVm(e => ColumnFormatInfo.MakeDialogButton(ButtonTypesEnum.Button, "/u", "t", 100, 100)), new Student { PhotoId = Photo }).Contains("__fref"));
            Assert.IsFalse(Row(ListVm(e => "plain text"), new Student { PhotoId = Photo }).Contains("__fref"));
            Assert.IsFalse(Row(ListVm(DownloadAndView), new Student { LoginName = "a", PhotoId = null }).Contains("__fref"), "a row without a file has nothing to refer to");

            // HTML mode (the LayUI grids) renders the buttons on the server and never writes the field
            Assert.IsFalse(Row(ListVm(DownloadAndView), new Student { LoginName = "a", PhotoId = Photo }, plain: false).Contains("__fref"));
        }

        [TestMethod]
        public void WithoutDataProtection_NoCapabilityIsWritten()
        {
            var wtm = MockWtmContext.CreateWtmContext(null, "alice");
            var json = Row(ListVm(DownloadAndView, wtm), new Student { LoginName = "a", PhotoId = Photo });
            Assert.IsFalse(json.Contains("__fref"));
            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual(Photo.ToString(), doc.RootElement.GetProperty("PhotoId").GetString());
        }

        [TestMethod]
        public void TheRowStaysValidJson_AndAnExistingColumnCalledLikeTheFieldIsNotOverwritten()
        {
            var json = Row(ListVm(DownloadAndView), new Student { LoginName = "a", PhotoId = Photo });
            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual("a", doc.RootElement.GetProperty("LoginName").GetString());
            Assert.AreEqual(1, doc.RootElement.EnumerateObject().Count(p => p.Name == "PhotoId__fref"));
        }

        [TestMethod]
        public void TheDataOfAPage_GetsACapabilityForEveryRowThatHasAFile()
        {
            var seed = Guid.NewGuid().ToString();
            var dc = new DataContext(seed, DBTypeEnum.Memory);
            dc.Set<Student>().Add(new Student { LoginName = "s1", Password = "p", Name = "n", IsValid = true, PhotoId = null });
            dc.SaveChanges();
            var file = new FileAttachment { ID = Photo, FileName = "a.png", FileExt = "png", UploadTime = DateTime.Now };
            dc.Set<FileAttachment>().Add(file);
            dc.Set<Student>().Add(new Student { LoginName = "s2", Password = "p", Name = "n", IsValid = true, PhotoId = Photo });
            dc.SaveChanges();

            var vm = new PhotoListVM { Wtm = Wtm(new DataContext(seed, DBTypeEnum.Memory)), PhotoFormat = DownloadAndView };
            using var doc = JsonDocument.Parse(vm.GetJson(true));
            var rows = doc.RootElement.GetProperty("Data").EnumerateArray().ToList();
            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(1, rows.Count(r => r.TryGetProperty("PhotoId__fref", out _)));
        }

        // ---- detail view models --------------------------------------------------------------------------------------------------------

        private string SeedAlbum(string seed, Guid? cover, Guid? scan, params Guid[] photos)
        {
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            foreach (var id in new[] { cover, scan }.Concat(photos.Select(p => (Guid?)p)).Where(x => x != null).Select(x => x.Value).Distinct())
            {
                dc.Set<FileAttachment>().Add(new FileAttachment { ID = id, FileName = id + ".png", FileExt = "png", UploadTime = DateTime.Now });
            }
            var album = new Album { ID = Guid.NewGuid(), Title = "t", CoverId = cover, ScanId = scan, Photos = photos.Select((p, i) => new AlbumPhoto { FileId = p, Order = i }).ToList() };
            dc.Set<Album>().Add(album);
            dc.SaveChanges();
            return album.ID.ToString();
        }

        private BaseCRUDVM<Album> Load(string seed, string id, WTMContext wtm = null)
        {
            var vm = new BaseCRUDVM<Album>();
            vm.Wtm = wtm ?? Wtm(new DataContext(seed, DBTypeEnum.Memory));
            vm.SetInclude(x => x.Photos);
            vm.SetEntityById(id);
            return vm;
        }

        [TestMethod]
        public void TheViewModelOfARecord_ListsACapabilityForEachFileItRefersTo()
        {
            var seed = Guid.NewGuid().ToString();
            var cover = Guid.NewGuid();
            var scan = Guid.NewGuid();
            var p1 = Guid.NewGuid();
            var p2 = Guid.NewGuid();
            var id = SeedAlbum(seed, cover, scan, p1, p2);

            var vm = Load(seed, id);
            var refs = vm.FileRefs;

            Assert.IsNotNull(refs);
            CollectionAssert.AreEquivalent(new[] { cover, scan, p1, p2 }.Select(x => x.ToString()).ToList(), refs.Keys.ToList());
            foreach (var pair in refs)
            {
                var opened = Open(pair.Value);
                Assert.AreEqual(pair.Key, opened.FileId);
                Assert.AreEqual(string.Empty, opened.User);
                Assert.AreEqual(FilePurpose.Read, opened.Purposes);
            }
        }

        [TestMethod]
        public void TheCapabilities_AdmitAnAnonymousCaller_ForTheirFileOnly()
        {
            var seed = Guid.NewGuid().ToString();
            var cover = Guid.NewGuid();
            var id = SeedAlbum(seed, cover, null);
            var refs = Load(seed, id).FileRefs;

            var anonymous = Wtm();
            anonymous.LoginUserInfo = null;
            Assert.IsTrue(Admits(anonymous, cover, refs[cover.ToString()], FilePurpose.View));
            Assert.IsFalse(Admits(anonymous, Other, refs[cover.ToString()], FilePurpose.View));
        }

        [TestMethod]
        public void NullFiles_AndARecordWithoutFiles_GiveNoEntries_AndTheSameFileOnceOnly()
        {
            var seed = Guid.NewGuid().ToString();
            var none = Load(seed, SeedAlbum(seed, null, null));
            Assert.IsNull(none.FileRefs, "nothing to refer to: the property is absent from the output");

            var same = Guid.NewGuid();
            var twice = Load(seed, SeedAlbum(seed, same, same, same));
            Assert.AreEqual(1, twice.FileRefs.Count);
        }

        [TestMethod]
        public void ARecordThatDoesNotComeFromTheDatabase_HasNoCapabilities()
        {
            // the ids came from the client, not from a record the caller may read
            var vm = new BaseCRUDVM<Album>();
            vm.Wtm = Wtm(new DataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory));
            vm.Entity = new Album { CoverId = Guid.NewGuid(), Photos = new List<AlbumPhoto> { new AlbumPhoto { FileId = Guid.NewGuid() } } };
            Assert.IsNull(vm.FileRefs);
        }

        [TestMethod]
        public void WithoutDataProtectionOrContext_FileRefsIsAbsent_AndNeverThrows()
        {
            var seed = Guid.NewGuid().ToString();
            var id = SeedAlbum(seed, Guid.NewGuid(), null);
            var noProtection = Load(seed, id, MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "alice"));
            Assert.IsNull(noProtection.FileRefs);
            Assert.IsNull(new BaseCRUDVM<Album>().FileRefs);
        }

        [TestMethod]
        public void FileRefs_CannotBeBoundFromARequest()
        {
            var property = typeof(BaseCRUDVM<Album>).GetProperty(nameof(BaseCRUDVM<Album>.FileRefs));
            Assert.IsNull(property.SetMethod, "no setter");
            Assert.IsNotNull(property.GetCustomAttribute<BindNeverAttribute>(), "and not bound through a read-only collection either");

            // a body that carries the field is read, and changes nothing
            var seed = Guid.NewGuid().ToString();
            var cover = Guid.NewGuid();
            var id = SeedAlbum(seed, cover, null);
            var vm = JsonSerializer.Deserialize<BaseCRUDVM<Album>>("{\"FileRefs\":{\"" + Other + "\":\"x\"},\"Entity\":{\"Title\":\"t\"}}");
            vm.Wtm = Wtm(new DataContext(seed, DBTypeEnum.Memory));
            Assert.IsNull(vm.FileRefs);
        }

        [TestMethod]
        public void FileRefs_IsPartOfTheSerializedViewModel_OnlyWhenThereAreFiles()
        {
            var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            var seed = Guid.NewGuid().ToString();
            var cover = Guid.NewGuid();
            var with = Load(seed, SeedAlbum(seed, cover, null));
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(with, options));
            Assert.IsTrue(doc.RootElement.GetProperty("FileRefs").TryGetProperty(cover.ToString(), out _));

            var without = Load(seed, SeedAlbum(seed, null, null));
            using var doc2 = JsonDocument.Parse(JsonSerializer.Serialize(without, options));
            Assert.IsFalse(doc2.RootElement.TryGetProperty("FileRefs", out _));
        }
    }
}
