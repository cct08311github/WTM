using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// A record that is saved may newly link a file only when the request proves that the signed-in user uploaded it (<c>UploadedFileRefs</c>, the
    /// <c>LinkRef</c> of the upload answer), or an <see cref="IFileLinkPolicy"/> allows it. A file the stored record already refers to needs nothing.
    /// </summary>
    [TestClass]
    public class FileLinkProofTest
    {
        private sealed class Policy : IFileLinkPolicy
        {
            public Func<FileLinkRequest, FileLinkDecision> Answer = r => FileLinkDecision.Default;
            public List<FileLinkRequest> Asked = new List<FileLinkRequest>();

            public FileLinkDecision Decide(FileLinkRequest request)
            {
                Asked.Add(request);
                return Answer(request);
            }
        }

        /// <summary>A view model that attaches a file by server code and says so.</summary>
        private sealed class OptOutVM : BaseCRUDVM<Album>
        {
            protected override bool RequireFileLinkProof => false;
        }

        private sealed class DeclaringVM : BaseCRUDVM<Album>
        {
            public Guid Declared;

            public override void DoEdit(bool updateAllFields = false)
            {
                Entity.ScanId = Declared;
                AllowFileLink(Declared);
                base.DoEdit(updateAllFields);
            }
        }

        private sealed class Scene
        {
            public string Seed = Guid.NewGuid().ToString();
            public Guid Cover = Guid.NewGuid();       // stored with the record
            public Guid Photo = Guid.NewGuid();       // stored with the record (sub file)
            public Guid Mine = Guid.NewGuid();        // uploaded by bob, not linked
            public Guid Foreign = Guid.NewGuid();     // uploaded by someone else, linked to another record
            public Guid AlbumId;
            public Guid OtherAlbumId;

            public DataContext Dc() => new DataContext(Seed, DBTypeEnum.Memory);

            public Album Stored(Guid id)
            {
                using var dc = Dc();
                return dc.Set<Album>().Where(x => x.ID == id).Select(x => new Album { ID = x.ID, Title = x.Title, CoverId = x.CoverId, ScanId = x.ScanId }).FirstOrDefault();
            }

            public int PhotoCount(Guid albumId)
            {
                using var dc = Dc();
                return dc.Set<AlbumPhoto>().Count(x => x.AlbumId == albumId);
            }

            public bool Exists(Guid id)
            {
                using var dc = Dc();
                return dc.Set<FileAttachment>().Any(x => x.ID == id);
            }
        }

        private static Scene Seed()
        {
            var s = new Scene();
            using var dc = s.Dc();
            foreach (var id in new[] { s.Cover, s.Photo, s.Mine, s.Foreign })
            {
                dc.Set<FileAttachment>().Add(new FileAttachment { ID = id, FileName = id + ".png", FileExt = "png", UploadTime = DateTime.Now });
            }
            var album = new Album { ID = Guid.NewGuid(), Title = "mine", CoverId = s.Cover, Photos = new List<AlbumPhoto> { new AlbumPhoto { FileId = s.Photo } } };
            var other = new Album { ID = Guid.NewGuid(), Title = "other", CoverId = s.Foreign };
            dc.Set<Album>().Add(album);
            dc.Set<Album>().Add(other);
            dc.SaveChanges();
            s.AlbumId = album.ID;
            s.OtherAlbumId = other.ID;
            return s;
        }

        private static WTMContext NewWtm(Scene s, string user = "bob", string tenant = null, Action<Configs> configure = null, Policy policy = null)
        {
            var configs = new Configs();
            configure?.Invoke(configs);
            var wtm = MockWtmContext.CreateWtmContext(s.Dc(), user, dataProtection: ImportUploadRef.Provider, configs: configs, fileLinkPolicy: policy);
            wtm.LoginUserInfo.CurrentTenant = tenant;
            wtm.LoginUserInfo.TenantCode = tenant;
            return wtm;
        }

        private static string LinkRef(Scene s, Guid file, string user = "bob", string tenant = null)
        {
            return NewWtm(s, user, tenant).CreateFileReference(file, FilePurpose.Link);
        }

        /// <summary>A form of the current user that saves the stored record again and links <paramref name="newCover"/> as its cover (the other files stay).</summary>
        private static T EditVm<T>(Scene s, WTMContext wtm, Guid? newCover, params string[] refs) where T : BaseCRUDVM<Album>, new()
        {
            var vm = new T { Wtm = wtm };
            vm.Entity = new Album { ID = s.AlbumId, Title = "edited", CoverId = newCover, Photos = new List<AlbumPhoto> { new AlbumPhoto { FileId = s.Photo } } };
            vm.UploadedFileRefs = refs.ToList();
            return vm;
        }

        private static BaseCRUDVM<Album> EditVm(Scene s, WTMContext wtm, Guid? newCover, params string[] refs) => EditVm<BaseCRUDVM<Album>>(s, wtm, newCover, refs);

        private static BaseCRUDVM<Album> AddVm(WTMContext wtm, Guid? cover, params string[] refs)
        {
            var vm = new BaseCRUDVM<Album> { Wtm = wtm };
            vm.Entity = new Album { ID = Guid.NewGuid(), Title = "new", CoverId = cover };
            vm.UploadedFileRefs = refs.ToList();
            return vm;
        }

        private static void AssertRefused(BaseCRUDVM<Album> vm, string key = "Entity.CoverId")
        {
            Assert.IsFalse(vm.MSD.IsValid, "the save is refused");
            CollectionAssert.Contains(vm.MSD.Keys.ToList(), key);
        }

        // ---- add -------------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Add_ANewFileWithAValidReference_IsLinked()
        {
            var s = Seed();
            var vm = AddVm(NewWtm(s), s.Mine, LinkRef(s, s.Mine));
            vm.DoAdd();
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(s.Mine, s.Stored(vm.Entity.ID).CoverId);
        }

        [TestMethod]
        public async Task AddAsync_ANewFileWithAValidReference_IsLinked()
        {
            var s = Seed();
            var vm = AddVm(NewWtm(s), s.Mine, LinkRef(s, s.Mine));
            await vm.DoAddAsync();
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(s.Mine, s.Stored(vm.Entity.ID).CoverId);
        }

        [TestMethod]
        public void Add_AFileWithoutAReference_IsRefused_AndNothingIsSaved()
        {
            var s = Seed();
            var vm = AddVm(NewWtm(s), s.Foreign);
            vm.DoAdd();
            AssertRefused(vm);
            Assert.IsNull(s.Stored(vm.Entity.ID), "the record is not saved");
        }

        [TestMethod]
        public async Task AddAsync_AFileWithoutAReference_IsRefused_AndNothingIsSaved()
        {
            var s = Seed();
            var vm = AddVm(NewWtm(s), s.Foreign);
            await vm.DoAddAsync();
            AssertRefused(vm);
            Assert.IsNull(s.Stored(vm.Entity.ID));
        }

        [TestMethod]
        public void Add_TheErrorIsGeneric_AndDoesNotEchoTheFileId()
        {
            var s = Seed();
            var vm = AddVm(NewWtm(s), s.Foreign);
            vm.DoAdd();
            var text = string.Join("|", vm.MSD["Entity.CoverId"].Select(x => x.ErrorMessage));
            Assert.IsFalse(text.Contains(s.Foreign.ToString(), StringComparison.OrdinalIgnoreCase), text);
            Assert.AreEqual(1, vm.MSD["Entity.CoverId"].Count);
        }

        [TestMethod]
        public void Add_ARecordWithoutFiles_NeedsNoReference()
        {
            var s = Seed();
            var vm = AddVm(NewWtm(s), null);
            vm.DoAdd();
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.IsNotNull(s.Stored(vm.Entity.ID));
        }

        // ---- references that do not count ----------------------------------------------------------------------------------------------

        private static void AssertAddRefusedWith(Scene s, string reference)
        {
            var vm = AddVm(NewWtm(s), s.Mine, reference);
            vm.DoAdd();
            AssertRefused(vm);
        }

        [TestMethod]
        public void AReferenceOfAnotherUser_DoesNotCount()
        {
            var s = Seed();
            AssertAddRefusedWith(s, LinkRef(s, s.Mine, user: "alice"));
        }

        [TestMethod]
        public void AReferenceOfAnotherTenant_DoesNotCount()
        {
            var s = Seed();
            AssertAddRefusedWith(s, LinkRef(s, s.Mine, tenant: "t1"));
            var vm = AddVm(NewWtm(s, tenant: "t2"), s.Mine, LinkRef(s, s.Mine, tenant: "t1"));
            vm.DoAdd();
            AssertRefused(vm);
            var same = AddVm(NewWtm(s, tenant: "t1"), s.Mine, LinkRef(s, s.Mine, tenant: "t1"));
            same.DoAdd();
            Assert.IsTrue(same.MSD.IsValid, "the same tenant is fine");
        }

        [TestMethod]
        public void AnExpiredReference_DoesNotCount()
        {
            var s = Seed();
            var protector = FileEndpointGuard.CreateProtector(ImportUploadRef.Provider);
            var expired = FileEndpointGuard.Issue(protector, new FileEndpointOptions(), s.Mine.ToString(), FilePurpose.Link, null, "bob", null, DateTimeOffset.UtcNow.AddDays(-2));
            Assert.IsNotNull(expired);
            AssertAddRefusedWith(s, expired);
        }

        [TestMethod]
        public void AReferenceMadeForAnotherPurpose_DoesNotCount()
        {
            var s = Seed();
            var wtm = NewWtm(s);
            foreach (var purpose in new[] { FilePurpose.Read, FilePurpose.Delete, FilePurpose.Import, FilePurpose.View | FilePurpose.Download })
            {
                AssertAddRefusedWith(s, wtm.CreateFileReference(s.Mine, purpose));
            }
        }

        [TestMethod]
        public void AReferenceForAnotherFile_DoesNotCount()
        {
            var s = Seed();
            AssertAddRefusedWith(s, LinkRef(s, s.Foreign));
        }

        [TestMethod]
        public void GarbageAndEmptyReferences_DoNotCount()
        {
            var s = Seed();
            foreach (var bad in new[] { "", "garbage", new string('x', 5000), s.Mine.ToString() })
            {
                AssertAddRefusedWith(s, bad);
            }
            var vm = AddVm(NewWtm(s), s.Mine);
            vm.UploadedFileRefs = null;
            vm.DoAdd();
            AssertRefused(vm);
        }

        [TestMethod]
        public void ALinkReference_CannotBeIssuedWithoutAUserAndAFile()
        {
            var protector = FileEndpointGuard.CreateProtector(ImportUploadRef.Provider);
            var options = new FileEndpointOptions();
            Assert.IsNull(FileEndpointGuard.Issue(protector, options, Guid.NewGuid().ToString(), FilePurpose.Link, null, null, null, DateTimeOffset.UtcNow));
            Assert.IsNull(FileEndpointGuard.Issue(protector, options, null, FilePurpose.Link, null, "bob", null, DateTimeOffset.UtcNow));
            Assert.IsNotNull(FileEndpointGuard.Issue(protector, options, Guid.NewGuid().ToString(), FilePurpose.Link, null, "bob", null, DateTimeOffset.UtcNow));
        }

        [TestMethod]
        public void ALinkReference_IsNotAReadOrDeleteReference()
        {
            var s = Seed();
            var wtm = NewWtm(s);
            var link = wtm.CreateFileReference(s.Mine, FilePurpose.Link);
            Assert.IsFalse(wtm.AuthorizeFileDeletion("DeletedFile", s.Mine.ToString(), link).Allowed, "a link reference does not delete");
            Assert.IsFalse(wtm.AuthorizeImportFile(s.Mine.ToString(), link).Allowed, "a link reference does not import");
            Assert.IsNull(wtm.ReadFileLinkReference(wtm.CreateFileReference(s.Mine, FilePurpose.Read)));
            Assert.AreEqual(s.Mine, wtm.ReadFileLinkReference(link));
        }

        // ---- edit ------------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Edit_FilesTheRecordAlreadyRefersTo_NeedNoReference()
        {
            var s = Seed();
            var vm = EditVm(s, NewWtm(s), s.Cover);
            vm.DoEdit(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual("edited", s.Stored(s.AlbumId).Title);
        }

        [TestMethod]
        public async Task EditAsync_FilesTheRecordAlreadyRefersTo_NeedNoReference()
        {
            var s = Seed();
            var vm = EditVm(s, NewWtm(s), s.Cover);
            await vm.DoEditAsync(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual("edited", s.Stored(s.AlbumId).Title);
        }

        [TestMethod]
        public void Edit_ANewCoverWithAValidReference_IsLinked()
        {
            var s = Seed();
            var vm = EditVm(s, NewWtm(s), s.Mine, LinkRef(s, s.Mine));
            vm.DoEdit(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(s.Mine, s.Stored(s.AlbumId).CoverId);
        }

        [TestMethod]
        public async Task EditAsync_ANewCoverWithAValidReference_IsLinked()
        {
            var s = Seed();
            var vm = EditVm(s, NewWtm(s), s.Mine, LinkRef(s, s.Mine));
            await vm.DoEditAsync(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(s.Mine, s.Stored(s.AlbumId).CoverId);
        }

        [TestMethod]
        public void Edit_AnotherUsersFile_IsRefused_AndTheRecordIsNotChanged()
        {
            var s = Seed();
            var vm = EditVm(s, NewWtm(s), s.Foreign);
            vm.DoEdit(true);
            AssertRefused(vm);
            Assert.AreEqual(s.Cover, s.Stored(s.AlbumId).CoverId);
            Assert.AreEqual("mine", s.Stored(s.AlbumId).Title, "nothing of the form is saved");
        }

        [TestMethod]
        public async Task EditAsync_AnotherUsersFile_IsRefused_AndTheRecordIsNotChanged()
        {
            var s = Seed();
            var vm = EditVm(s, NewWtm(s), s.Foreign);
            await vm.DoEditAsync(true);
            AssertRefused(vm);
            Assert.AreEqual(s.Cover, s.Stored(s.AlbumId).CoverId);
        }

        [TestMethod]
        public void Edit_AnotherUsersReference_ForTheFile_IsRefused()
        {
            var s = Seed();
            var vm = EditVm(s, NewWtm(s), s.Foreign, LinkRef(s, s.Foreign, user: "alice"));
            vm.DoEdit(true);
            AssertRefused(vm);
        }

        [TestMethod]
        public void Edit_ANewSubFileInTheList_NeedsAReference()
        {
            var s = Seed();
            var refused = EditVm(s, NewWtm(s), s.Cover);
            refused.Entity.Photos.Add(new AlbumPhoto { FileId = s.Foreign });
            refused.DoEdit(true);
            AssertRefused(refused, "Entity.Photos");
            Assert.AreEqual(1, s.PhotoCount(s.AlbumId));

            var accepted = EditVm(s, NewWtm(s), s.Cover, LinkRef(s, s.Mine));
            accepted.Entity.Photos.Add(new AlbumPhoto { FileId = s.Mine });
            accepted.DoEdit(true);
            Assert.IsTrue(accepted.MSD.IsValid);
            Assert.AreEqual(2, s.PhotoCount(s.AlbumId));
        }

        [TestMethod]
        public void Edit_ASubFileNamedByTheAttachmentItCarries_NeedsAReferenceToo()
        {
            var s = Seed();
            var vm = EditVm(s, NewWtm(s), s.Cover);
            vm.Entity.Photos.Add(new AlbumPhoto { File = new FileAttachment { ID = s.Foreign } });
            vm.DoEdit(true);
            AssertRefused(vm, "Entity.Photos");
        }

        [TestMethod]
        public async Task AddAsync_ASubFileInTheList_NeedsAReference()
        {
            var s = Seed();
            var refused = AddVm(NewWtm(s), null);
            refused.Entity.Photos = new List<AlbumPhoto> { new AlbumPhoto { FileId = s.Foreign } };
            await refused.DoAddAsync();
            AssertRefused(refused, "Entity.Photos");
            var accepted = AddVm(NewWtm(s), null, LinkRef(s, s.Mine));
            accepted.Entity.Photos = new List<AlbumPhoto> { new AlbumPhoto { FileId = s.Mine } };
            await accepted.DoAddAsync();
            Assert.IsTrue(accepted.MSD.IsValid);
        }

        [TestMethod]
        public void Edit_TwoFilesNewOneProven_RefusesTheOtherOne()
        {
            var s = Seed();
            var vm = EditVm(s, NewWtm(s), s.Mine, LinkRef(s, s.Mine));
            vm.Entity.ScanId = s.Foreign;
            vm.DoEdit(true);
            AssertRefused(vm, "Entity.ScanId");
            Assert.IsFalse(vm.MSD.Keys.Contains("Entity.CoverId"), "the proven file is not blamed");
        }

        [TestMethod]
        public void TwoSteps_LinkingAFileOfAnotherUser_ThenRemovingIt_IsRefusedAtTheFirstStep()
        {
            var s = Seed();
            // step 1: a record names a file that another user uploaded
            var link = EditVm(s, NewWtm(s), s.Foreign);
            link.DoEdit(true);
            AssertRefused(link);
            // step 2: with the file linked, a later save that lists it as removed would delete it; it was never linked
            var remove = EditVm(s, NewWtm(s), null);
            remove.DeletedFileIds = new List<string> { s.Foreign.ToString() };
            remove.DoEdit(true);
            Assert.IsTrue(s.Exists(s.Foreign), "the file of somebody else is still there");
        }

        // ---- policy ------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Policy_Allow_AdmitsAFileWithoutAReference()
        {
            var s = Seed();
            var policy = new Policy { Answer = r => FileLinkDecision.Allow };
            var vm = EditVm(s, NewWtm(s, policy: policy), s.Foreign);
            vm.DoEdit(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(s.Foreign, s.Stored(s.AlbumId).CoverId);
            Assert.AreEqual(1, policy.Asked.Count);
            var asked = policy.Asked[0];
            Assert.AreEqual(s.Foreign, asked.FileId);
            Assert.AreEqual("Edit", asked.Operation);
            Assert.AreEqual("CoverId", asked.Property);
            Assert.IsFalse(asked.HasValidReference);
            Assert.AreEqual("bob", asked.User);
        }

        [TestMethod]
        public void Policy_Deny_RefusesEvenWithAValidReference()
        {
            var s = Seed();
            var policy = new Policy { Answer = r => FileLinkDecision.Deny };
            var vm = AddVm(NewWtm(s, policy: policy), s.Mine, LinkRef(s, s.Mine));
            vm.DoAdd();
            AssertRefused(vm);
            Assert.IsTrue(policy.Asked.Single().HasValidReference);
            Assert.AreEqual("Add", policy.Asked.Single().Operation);
        }

        [TestMethod]
        public void Policy_AThrowingPolicy_CountsAsDeny()
        {
            var s = Seed();
            var policy = new Policy { Answer = r => throw new InvalidOperationException("boom") };
            var vm = AddVm(NewWtm(s, policy: policy), s.Mine, LinkRef(s, s.Mine));
            vm.DoAdd();
            AssertRefused(vm);
        }

        [TestMethod]
        public void Policy_Default_LeavesTheFrameworkRule()
        {
            var s = Seed();
            var policy = new Policy();
            var refused = AddVm(NewWtm(s, policy: policy), s.Foreign);
            refused.DoAdd();
            AssertRefused(refused);
            var accepted = AddVm(NewWtm(s, policy: policy), s.Mine, LinkRef(s, s.Mine));
            accepted.DoAdd();
            Assert.IsTrue(accepted.MSD.IsValid);
        }

        [TestMethod]
        public void Policy_IsAskedForNewFilesOnly()
        {
            var s = Seed();
            var policy = new Policy();
            var vm = EditVm(s, NewWtm(s, policy: policy), s.Cover);
            vm.DoEdit(true);
            Assert.AreEqual(0, policy.Asked.Count, "the files the record already refers to are not asked about");
        }

        [TestMethod]
        public void Policy_AllowForOneFile_DoesNotAdmitTheOther()
        {
            var s = Seed();
            var policy = new Policy { Answer = r => r.FileId == s.Foreign ? FileLinkDecision.Allow : FileLinkDecision.Default };
            var vm = EditVm(s, NewWtm(s, policy: policy), s.Foreign);
            vm.Entity.ScanId = s.Mine;
            vm.DoEdit(true);
            AssertRefused(vm, "Entity.ScanId");
        }

        // ---- opting out --------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void RequireFileLinkProof_False_SkipsTheCheckForThatViewModel()
        {
            var s = Seed();
            var vm = EditVm<OptOutVM>(s, NewWtm(s), s.Foreign);
            vm.DoEdit(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(s.Foreign, s.Stored(s.AlbumId).CoverId);
            // the default view model is still checked
            var other = EditVm(s, NewWtm(s), s.Mine);
            other.DoEdit(true);
            AssertRefused(other);
        }

        [TestMethod]
        public void AllowFileLink_AdmitsTheFilesServerCodeAttached_AndNoOthers()
        {
            var s = Seed();
            var declared = Guid.NewGuid();
            var vm = EditVm<DeclaringVM>(s, NewWtm(s), s.Foreign);
            vm.Declared = declared;
            vm.DoEdit(true);
            AssertRefused(vm);
            Assert.IsFalse(vm.MSD.Keys.Contains("Entity.ScanId"), "the declared file is admitted");
            Assert.AreEqual(1, vm.MSD.Keys.Count(x => x.StartsWith("Entity.")), "the file of the request is still refused");
        }

        [TestMethod]
        public void LegacyVmFileLinking_AdmitsAnyFile()
        {
            var s = Seed();
            var vm = EditVm(s, NewWtm(s, configure: c => c.FileEndpoints.LegacyVmFileLinking = true), s.Foreign);
            vm.DoEdit(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(s.Foreign, s.Stored(s.AlbumId).CoverId);
        }

        [TestMethod]
        public void LegacyVmFileLinking_StillAsksThePolicy_AndDenyWins()
        {
            var s = Seed();
            var policy = new Policy { Answer = r => FileLinkDecision.Deny };
            var vm = EditVm(s, NewWtm(s, policy: policy, configure: c => c.FileEndpoints.LegacyVmFileLinking = true), s.Foreign);
            vm.DoEdit(true);
            AssertRefused(vm);
        }

        [TestMethod]
        public void ModeLegacy_AdmitsAnyFile_ButAuditAndEnforceDoNot()
        {
            var s = Seed();
            var legacy = AddVm(NewWtm(s, configure: c => c.FileEndpoints.Mode = FileEndpointMode.Legacy), s.Foreign);
            legacy.DoAdd();
            Assert.IsTrue(legacy.MSD.IsValid);
            foreach (var mode in new[] { FileEndpointMode.Audit, FileEndpointMode.Enforce })
            {
                var vm = AddVm(NewWtm(s, configure: c => c.FileEndpoints.Mode = mode), s.Foreign);
                vm.DoAdd();
                AssertRefused(vm);
            }
        }

        [TestMethod]
        public void TheWarning_ForLegacyVmFileLinking_IsLogged()
        {
            var configs = new Configs();
            configs.FileEndpoints.LegacyVmFileLinking = true;
            var logger = new CapturingLogger();
            FileEndpointGuard.LogRelaxedWarning(configs, logger);
            Assert.IsTrue(logger.Warnings.Any(x => x.Contains("LegacyVmFileLinking")));
            var quiet = new CapturingLogger();
            FileEndpointGuard.LogRelaxedWarning(new Configs(), quiet);
            Assert.IsFalse(quiet.Warnings.Any(x => x.Contains("LegacyVmFileLinking")));
        }

        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
        {
            public List<string> Warnings = new List<string>();
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                {
                    Warnings.Add(formatter(state, exception));
                }
            }
        }

        // ---- batch edit ------------------------------------------------------------------------------------------------------------

        public class AlbumEdit : BaseVM
        {
            public Guid? CoverId { get; set; }
            public string Title { get; set; }
        }

        private static bool Batch(Scene s, WTMContext wtm, Guid cover, out BaseBatchVM<Album, AlbumEdit> vm, params string[] refs)
        {
            vm = new BaseBatchVM<Album, AlbumEdit> { Wtm = wtm };
            vm.LinkedVM = new AlbumEdit { CoverId = cover };
            vm.FC["LinkedVM.CoverId"] = "x";
            vm.Ids = new[] { s.AlbumId.ToString() };
            vm.UploadedFileRefs = refs.ToList();
            // the part of DoBatchEdit that assigns the values (the rest needs a list view model)
            var method = typeof(BaseBatchVM<Album, AlbumEdit>).GetMethod("PrepareBatchEdit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var args = new object[] { null, default(System.Threading.CancellationToken) };
            var ok = (bool)method.Invoke(vm, args);
            if (ok)
            {
                wtm.DC.SaveChanges();
            }
            return ok;
        }

        [TestMethod]
        public void BatchEdit_AFileOfAnotherUser_IsRefused_AndNothingIsChanged()
        {
            var s = Seed();
            var ok = Batch(s, NewWtm(s), s.Foreign, out var vm);
            Assert.IsFalse(ok);
            Assert.AreEqual(1, vm.ErrorMessage.Count, "reported for the record");
            Assert.IsFalse(vm.ErrorMessage.Values.Any(x => (x ?? "").Contains(s.Foreign.ToString())));
            Assert.AreEqual(s.Cover, s.Stored(s.AlbumId).CoverId);
        }

        [TestMethod]
        public void BatchEdit_AFileOfTheUser_WithAReference_IsLinked_AndPolicyAndLegacyApply()
        {
            var s = Seed();
            Assert.IsTrue(Batch(s, NewWtm(s), s.Mine, out _, LinkRef(s, s.Mine)));
            Assert.AreEqual(s.Mine, s.Stored(s.AlbumId).CoverId);
            var s2 = Seed();
            Assert.IsTrue(Batch(s2, NewWtm(s2, policy: new Policy { Answer = r => FileLinkDecision.Allow }), s2.Foreign, out _));
            var s3 = Seed();
            Assert.IsTrue(Batch(s3, NewWtm(s3, configure: c => c.FileEndpoints.LegacyVmFileLinking = true), s3.Foreign, out _));
            var s4 = Seed();
            Assert.IsFalse(Batch(s4, NewWtm(s4, policy: new Policy { Answer = r => FileLinkDecision.Deny }), s4.Mine, out _, LinkRef(s4, s4.Mine)));
        }

        // ---- the grid edit (BasePagedListVM.UpdateEntityList) -----------------------------------------------------------------------

        private static BasePagedListVM<Album, BaseSearcher> ListVm(Scene s, WTMContext wtm, Guid? cover, bool changeTitle, string[] fields, params string[] refs)
        {
            var vm = new BasePagedListVM<Album, BaseSearcher> { Wtm = wtm };
            vm.EntityList = new List<Album> { new Album { ID = s.AlbumId, Title = changeTitle ? "listed" : "mine", CoverId = cover } };
            vm.FC = fields.ToDictionary(x => "EntityList[0]." + x, x => (object)"");
            vm.UploadedFileRefs = refs.ToList();
            return vm;
        }

        [TestMethod]
        public void ListEdit_AFileWithoutAReference_IsRefused_InBothModes_AndNothingIsWritten()
        {
            foreach (var all in new[] { true, false })
            {
                var s = Seed();
                var vm = ListVm(s, NewWtm(s), s.Foreign, true, new[] { "Title", "CoverId" });
                vm.UpdateEntityList(all);
                Assert.IsFalse(vm.MSD.IsValid, "all fields: " + all);
                CollectionAssert.Contains(vm.MSD.Keys.ToList(), "EntityList[0].CoverId");
                Assert.AreEqual(s.Cover, s.Stored(s.AlbumId).CoverId);
                Assert.AreEqual("mine", s.Stored(s.AlbumId).Title, "the other fields are not written either");
            }
        }

        [TestMethod]
        public void ListEdit_AFileWithAReference_IsLinked()
        {
            foreach (var all in new[] { true, false })
            {
                var s = Seed();
                var vm = ListVm(s, NewWtm(s), s.Mine, true, new[] { "Title", "CoverId" }, LinkRef(s, s.Mine));
                vm.UpdateEntityList(all);
                Assert.IsTrue(vm.MSD.IsValid);
                Assert.AreEqual(s.Mine, s.Stored(s.AlbumId).CoverId);
                Assert.AreEqual("listed", s.Stored(s.AlbumId).Title);
            }
        }

        [TestMethod]
        public void ListEdit_TheFileTheRecordAlreadyHas_NeedsNoReference()
        {
            var s = Seed();
            var vm = ListVm(s, NewWtm(s), s.Cover, true, new[] { "Title", "CoverId" });
            vm.UpdateEntityList(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual("listed", s.Stored(s.AlbumId).Title);
            Assert.AreEqual(s.Cover, s.Stored(s.AlbumId).CoverId);
        }

        [TestMethod]
        public void ListEdit_AFileKeyThatTheRequestDoesNotWrite_IsNotChecked_AndNotWritten()
        {
            var s = Seed();
            var vm = ListVm(s, NewWtm(s), s.Foreign, true, new[] { "Title" });
            vm.UpdateEntityList(false);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual("listed", s.Stored(s.AlbumId).Title);
            Assert.AreEqual(s.Cover, s.Stored(s.AlbumId).CoverId);
        }

        [TestMethod]
        public void ListEdit_LegacyVmFileLinking_AdmitsAnyFile_AndDenyOfThePolicyWins()
        {
            var s = Seed();
            var vm = ListVm(s, NewWtm(s, configure: c => c.FileEndpoints.LegacyVmFileLinking = true), s.Foreign, true, new[] { "CoverId" });
            vm.UpdateEntityList(false);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(s.Foreign, s.Stored(s.AlbumId).CoverId);
            var s2 = Seed();
            var denied = ListVm(s2, NewWtm(s2, policy: new Policy { Answer = r => FileLinkDecision.Deny }, configure: c => c.FileEndpoints.LegacyVmFileLinking = true), s2.Foreign, true, new[] { "CoverId" });
            denied.UpdateEntityList(false);
            Assert.IsFalse(denied.MSD.IsValid);
            Assert.AreEqual(s2.Cover, s2.Stored(s2.AlbumId).CoverId);
        }

        [TestMethod]
        public void ListEdit_ARowThatIsAdded_NeedsTheReferenceToo()
        {
            var s = Seed();
            var wtm = NewWtm(s);
            var added = Guid.NewGuid();
            var vm = new BasePagedListVM<Album, BaseSearcher> { Wtm = wtm, FC = new Dictionary<string, object>() };
            vm.EntityList = new List<Album> { new Album { ID = added, Title = "new", CoverId = s.Foreign } };
            vm.UpdateEntityList(true);
            Assert.IsFalse(vm.MSD.IsValid);
            Assert.IsNull(s.Stored(added), "the row is not added");
            var s2 = Seed();
            var proven = new BasePagedListVM<Album, BaseSearcher> { Wtm = NewWtm(s2), FC = new Dictionary<string, object>(), UploadedFileRefs = new List<string> { LinkRef(s2, s2.Mine) } };
            var id2 = Guid.NewGuid();
            proven.EntityList = new List<Album> { new Album { ID = id2, Title = "new", CoverId = s2.Mine } };
            proven.UpdateEntityList(true);
            Assert.IsTrue(proven.MSD.IsValid);
            Assert.AreEqual(s2.Mine, s2.Stored(id2).CoverId);
        }

        [TestMethod]
        public void TheDefaultPolicy_HasNoOpinion()
        {
            Assert.AreEqual(FileLinkDecision.Default, new DefaultFileLinkPolicy().Decide(new FileLinkRequest()));
        }
    }
}
