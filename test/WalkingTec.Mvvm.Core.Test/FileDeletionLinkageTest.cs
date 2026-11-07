using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// The files a form asks to remove (<c>DeletedFileIds</c>) when a record is saved: only a file that the stored record refers to is deleted,
    /// an application policy can allow or deny more, and <c>FileEndpoints:LegacyVmFileDeletion</c> restores the earlier behavior.
    /// </summary>
    [TestClass]
    public class FileDeletionLinkageTest
    {
        private sealed class Policy : IFileDeletionPolicy
        {
            public Func<FileDeletionRequest, FileDeletionDecision> Answer = r => FileDeletionDecision.Default;
            public List<FileDeletionRequest> Asked = new List<FileDeletionRequest>();

            public FileDeletionDecision Decide(FileDeletionRequest request)
            {
                Asked.Add(request);
                return Answer(request);
            }
        }

        /// <summary>A context whose own save can be made to fail; the copies made by ReCreate (used for the file deletion) save normally.</summary>
        private sealed class FailingSaveContext : DataContext
        {
            public bool FailSave;

            public FailingSaveContext(string cs, DBTypeEnum dbtype) : base(cs, dbtype)
            {
            }

            public override int SaveChanges()
            {
                if (FailSave)
                {
                    throw new InvalidOperationException("the save failed");
                }
                return base.SaveChanges();
            }

            public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            {
                if (FailSave)
                {
                    throw new InvalidOperationException("the save failed");
                }
                return base.SaveChangesAsync(cancellationToken);
            }
        }

        private sealed class Scene
        {
            public string Seed = Guid.NewGuid().ToString();
            public Guid Cover = Guid.NewGuid();
            public Guid Photo = Guid.NewGuid();
            public Guid Foreign = Guid.NewGuid();     // linked to another record
            public Guid Pending = Guid.NewGuid();     // uploaded, linked to nothing
            public Guid AlbumId;
            public Guid OtherAlbumId;

            public DataContext Dc() => new DataContext(Seed, DBTypeEnum.Memory);

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
            foreach (var id in new[] { s.Cover, s.Photo, s.Foreign, s.Pending })
            {
                dc.Set<FileAttachment>().Add(new FileAttachment { ID = id, FileName = id + ".png", FileExt = "png", UploadTime = DateTime.Now, ExtraInfo = "uploaded by someone else" });
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

        private static BaseCRUDVM<Album> EditVm(Scene s, Policy policy = null, bool legacy = false, string user = "bob", Guid[] deleted = null, IDataContext dc = null)
        {
            deleted ??= new Guid[0];
            var configs = new Configs();
            configs.FileEndpoints.LegacyVmFileDeletion = legacy;
            configs.FileEndpoints.LegacyVmFileLinking = true; // these tests are about the deletion; linking is covered by FileLinkProofTest
            var wtm = MockWtmContext.CreateWtmContext(dc ?? s.Dc(), user, configs: configs, fileDeletionPolicy: policy);
            var vm = new BaseCRUDVM<Album> { Wtm = wtm };
            // what a form that removed the cover and the photo posts back
            vm.Entity = new Album { ID = s.AlbumId, Title = "mine, edited", CoverId = null, Photos = new List<AlbumPhoto>() };
            vm.DeletedFileIds = deleted.Select(x => x.ToString()).ToList();
            return vm;
        }

        private static BaseCRUDVM<Album> AddVm(Scene s, Policy policy = null, bool legacy = false, Guid[] deleted = null, IDataContext dc = null)
        {
            deleted ??= new Guid[0];
            var configs = new Configs();
            configs.FileEndpoints.LegacyVmFileDeletion = legacy;
            configs.FileEndpoints.LegacyVmFileLinking = true; // these tests are about the deletion; linking is covered by FileLinkProofTest
            var wtm = MockWtmContext.CreateWtmContext(dc ?? s.Dc(), "bob", configs: configs, fileDeletionPolicy: policy);
            var vm = new BaseCRUDVM<Album> { Wtm = wtm };
            vm.Entity = new Album { ID = Guid.NewGuid(), Title = "new" };
            vm.DeletedFileIds = deleted.Select(x => x.ToString()).ToList();
            return vm;
        }

        // ---- edit ------------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Edit_DeletesTheFilesTheRecordReferredTo_WhoeverUploadedThem()
        {
            var s = Seed();
            var vm = EditVm(s, deleted: new[] { s.Cover, s.Photo });
            vm.DoEdit(true);
            Assert.IsFalse(s.Exists(s.Cover), "a single attachment of the record");
            Assert.IsFalse(s.Exists(s.Photo), "a sub file of the record");
            Assert.IsTrue(s.Exists(s.Foreign));
        }

        [TestMethod]
        public void Edit_KeepsAFileThatBelongsToAnotherRecord()
        {
            var s = Seed();
            var vm = EditVm(s, deleted: new[] { s.Foreign });
            vm.DoEdit(true);
            Assert.IsTrue(s.Exists(s.Foreign));
        }

        [TestMethod]
        public void Edit_KeepsAFileThatNoRecordReferredTo_AndUnknownIds()
        {
            var s = Seed();
            var vm = EditVm(s, deleted: new[] { s.Pending, Guid.NewGuid() });
            vm.DeletedFileIds.Add("not an id");
            vm.DoEdit(true);
            Assert.IsTrue(s.Exists(s.Pending));
        }

        [TestMethod]
        public void Edit_AMixedRequest_DeletesOnlyTheLinkedOnes()
        {
            var s = Seed();
            var vm = EditVm(s, deleted: new[] { s.Foreign, s.Cover, s.Pending });
            vm.DoEdit(true);
            Assert.IsFalse(s.Exists(s.Cover));
            Assert.IsTrue(s.Exists(s.Foreign));
            Assert.IsTrue(s.Exists(s.Pending));
            Assert.IsTrue(s.Exists(s.Photo), "not asked for, not deleted");
        }

        [TestMethod]
        public async Task EditAsync_FollowsTheSameRule()
        {
            var s = Seed();
            var vm = EditVm(s, deleted: new[] { s.Foreign, s.Cover, s.Pending });
            await vm.DoEditAsync(true);
            Assert.IsFalse(s.Exists(s.Cover));
            Assert.IsTrue(s.Exists(s.Foreign));
            Assert.IsTrue(s.Exists(s.Pending));
        }

        [TestMethod]
        public void Edit_TheLinkIsJudgedByTheStoredRecord_NotByWhatTheRequestPosted()
        {
            var s = Seed();
            var vm = EditVm(s, deleted: new[] { s.Foreign });
            // the request claims that its own record refers to the foreign file
            vm.Entity.CoverId = s.Foreign;
            vm.DoEdit(true);
            Assert.IsTrue(s.Exists(s.Foreign));
        }

        // ---- add -------------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Add_DeletesNothingByDefault()
        {
            var s = Seed();
            var vm = AddVm(s, deleted: new[] { s.Foreign, s.Cover, s.Pending });
            vm.DoAdd();
            Assert.IsTrue(s.Exists(s.Foreign));
            Assert.IsTrue(s.Exists(s.Cover));
            Assert.IsTrue(s.Exists(s.Pending));
        }

        [TestMethod]
        public async Task AddAsync_DeletesNothingByDefault()
        {
            var s = Seed();
            var vm = AddVm(s, deleted: new[] { s.Foreign, s.Pending });
            await vm.DoAddAsync();
            Assert.IsTrue(s.Exists(s.Foreign));
            Assert.IsTrue(s.Exists(s.Pending));
        }

        // ---- policy ----------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Policy_CanAllowAFileTheRuleSkips_OnEditAndAdd()
        {
            var s = Seed();
            var allow = new Policy { Answer = r => r.FileId == s.Pending ? FileDeletionDecision.Allow : FileDeletionDecision.Default };
            EditVm(s, allow, deleted: new[] { s.Pending, s.Foreign }).DoEdit(true);
            Assert.IsFalse(s.Exists(s.Pending));
            Assert.IsTrue(s.Exists(s.Foreign), "the policy had no opinion on this one");

            var s2 = Seed();
            var allowAll = new Policy { Answer = r => FileDeletionDecision.Allow };
            AddVm(s2, allowAll, deleted: new[] { s2.Foreign }).DoAdd();
            Assert.IsFalse(s2.Exists(s2.Foreign));
        }

        [TestMethod]
        public void Policy_CanDenyALinkedFile()
        {
            var s = Seed();
            var deny = new Policy { Answer = r => FileDeletionDecision.Deny };
            EditVm(s, deny, deleted: new[] { s.Cover }).DoEdit(true);
            Assert.IsTrue(s.Exists(s.Cover));
        }

        [TestMethod]
        public void Policy_IsToldWhetherTheFileIsLinked_AndWhoAsks()
        {
            var s = Seed();
            var policy = new Policy();
            EditVm(s, policy, deleted: new[] { s.Cover, s.Foreign }).DoEdit(true);
            var cover = policy.Asked.Single(x => x.FileId == s.Cover);
            var foreign = policy.Asked.Single(x => x.FileId == s.Foreign);
            Assert.IsTrue(cover.IsLinked);
            Assert.IsFalse(foreign.IsLinked);
            Assert.AreEqual("bob", cover.User);
            Assert.AreEqual("Edit", cover.Operation);
        }

        [TestMethod]
        public void Policy_ThatThrows_KeepsTheFile()
        {
            var s = Seed();
            var broken = new Policy { Answer = r => throw new InvalidOperationException("boom") };
            EditVm(s, broken, deleted: new[] { s.Cover }).DoEdit(true);
            Assert.IsTrue(s.Exists(s.Cover));
        }

        // ---- compatibility setting ---------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void LegacySetting_RestoresTheEarlierBehavior()
        {
            var s = Seed();
            EditVm(s, legacy: true, deleted: new[] { s.Foreign, s.Pending, s.Cover }).DoEdit(true);
            Assert.IsFalse(s.Exists(s.Foreign));
            Assert.IsFalse(s.Exists(s.Pending));
            Assert.IsFalse(s.Exists(s.Cover));

            var s2 = Seed();
            AddVm(s2, legacy: true, deleted: new[] { s2.Foreign }).DoAdd();
            Assert.IsFalse(s2.Exists(s2.Foreign));
        }

        [TestMethod]
        public void LegacySetting_AllowsADenyingPolicyToStillKeepAFile()
        {
            var s = Seed();
            var deny = new Policy { Answer = r => FileDeletionDecision.Deny };
            EditVm(s, deny, legacy: true, deleted: new[] { s.Foreign }).DoEdit(true);
            Assert.IsTrue(s.Exists(s.Foreign));
        }

        [TestMethod]
        public void TheSettingIsOffByDefault()
        {
            Assert.IsFalse(new Configs().FileEndpoints.LegacyVmFileDeletion);
            Assert.IsInstanceOfType(new DefaultFileDeletionPolicy(), typeof(IFileDeletionPolicy));
            Assert.AreEqual(FileDeletionDecision.Default, new DefaultFileDeletionPolicy().Decide(new FileDeletionRequest()));
        }

        // ---- a save that did not happen deletes nothing ---------------------------------------------------------------------------------------

        private static FailingSaveContext Failing(Scene s) => new FailingSaveContext(s.Seed, DBTypeEnum.Memory) { FailSave = true };

        [TestMethod]
        public void Edit_WhenTheSaveFails_NoFileIsDeleted()
        {
            var s = Seed();
            var allowAll = new Policy { Answer = r => FileDeletionDecision.Allow };
            var vm = EditVm(s, allowAll, deleted: new[] { s.Cover, s.Photo, s.Pending }, dc: Failing(s));
            vm.DoEdit(true);
            Assert.IsFalse(vm.MSD.IsValid, "the failure is reported on the model state");
            Assert.IsTrue(s.Exists(s.Cover));
            Assert.IsTrue(s.Exists(s.Photo));
            Assert.IsTrue(s.Exists(s.Pending));
            Assert.AreEqual(0, allowAll.Asked.Count, "the policy is not even asked");
        }

        [TestMethod]
        public void Edit_WhenTheSaveFails_NoFileIsDeleted_EvenWithTheCompatibilitySetting()
        {
            var s = Seed();
            var vm = EditVm(s, legacy: true, deleted: new[] { s.Foreign, s.Cover }, dc: Failing(s));
            vm.DoEdit(true);
            Assert.IsTrue(s.Exists(s.Foreign));
            Assert.IsTrue(s.Exists(s.Cover));
        }

        [TestMethod]
        public async Task EditAsync_WhenTheSaveFails_NoFileIsDeleted()
        {
            var s = Seed();
            var vm = EditVm(s, legacy: true, deleted: new[] { s.Cover, s.Photo }, dc: Failing(s));
            try
            {
                await vm.DoEditAsync(true);
            }
            catch (InvalidOperationException)
            {
                // the failure of the save is allowed to surface; what counts is that nothing was deleted
            }
            Assert.IsTrue(s.Exists(s.Cover));
            Assert.IsTrue(s.Exists(s.Photo));
        }

        [TestMethod]
        public void Edit_WithAnErrorInTheModelState_NoFileIsDeleted()
        {
            var s = Seed();
            var vm = EditVm(s, deleted: new[] { s.Cover });
            vm.MSD.AddModelError("Entity.Title", "wrong");
            vm.DoEdit(true);
            Assert.IsTrue(s.Exists(s.Cover));
        }

        [TestMethod]
        public void Add_WhenTheSaveFails_NoFileIsDeleted()
        {
            var s = Seed();
            var allowAll = new Policy { Answer = r => FileDeletionDecision.Allow };
            var vm = AddVm(s, allowAll, deleted: new[] { s.Foreign, s.Pending }, dc: Failing(s));
            Assert.ThrowsExactly<InvalidOperationException>(() => vm.DoAdd());
            Assert.IsTrue(s.Exists(s.Foreign));
            Assert.IsTrue(s.Exists(s.Pending));
            Assert.AreEqual(0, allowAll.Asked.Count);
        }

        [TestMethod]
        public async Task AddAsync_WhenTheSaveFails_NoFileIsDeleted()
        {
            var s = Seed();
            var vm = AddVm(s, legacy: true, deleted: new[] { s.Foreign, s.Pending }, dc: Failing(s));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => vm.DoAddAsync());
            Assert.IsTrue(s.Exists(s.Foreign));
            Assert.IsTrue(s.Exists(s.Pending));
        }

        [TestMethod]
        public void Add_WhenTheSaveWorks_ThePolicyStillDecides_AfterTheSave()
        {
            var s = Seed();
            var allowAll = new Policy { Answer = r => FileDeletionDecision.Allow };
            var vm = AddVm(s, allowAll, deleted: new[] { s.Pending });
            vm.DoAdd();
            Assert.IsFalse(s.Exists(s.Pending));
            using var dc = s.Dc();
            Assert.IsTrue(dc.Set<Album>().Any(x => x.ID == vm.Entity.ID), "the record was saved");
        }

        // ---- the non-id entries of the request go through the policy too -----------------------------------------------------------------------

        [TestMethod]
        public void NotAnId_IsOfferedToThePolicy_OnlyWithTheCompatibilitySetting()
        {
            var s = Seed();
            var asked = new Policy();
            var withSetting = EditVm(s, asked, legacy: true);
            withSetting.DeletedFileIds.Add("not an id");
            withSetting.DoEdit(true);
            var raw = asked.Asked.Single();
            Assert.AreEqual("not an id", raw.RawFileId);
            Assert.AreEqual(Guid.Empty, raw.FileId);
            Assert.AreEqual("Edit", raw.Operation);

            var strict = new Policy();
            var withoutSetting = EditVm(s, strict, legacy: false);
            withoutSetting.DeletedFileIds.Add("not an id");
            withoutSetting.DoEdit(true);
            Assert.AreEqual(0, strict.Asked.Count, "without the setting such an entry is skipped as before");
        }

        [TestMethod]
        public void NotAnId_WithTheCompatibilitySetting_IsNotHandedToTheStoreWhenThePolicyDenies()
        {
            var s = Seed();
            // an id in a form the store resolves although Guid.TryParse refuses it is not constructible here; the contract is checked through the provider
            var deny = new Policy { Answer = r => FileDeletionDecision.Deny };
            var wtm = MockWtmContext.CreateWtmContext(s.Dc(), "bob", configs: new Configs { FileEndpoints = { LegacyVmFileDeletion = true } }, fileDeletionPolicy: deny);
            var fp = new WalkingTec.Mvvm.Core.Support.FileHandlers.WtmFileProvider(wtm);
            Assert.AreEqual(0, fp.DeleteRequestedFiles(new[] { "not an id" }, null, s.Dc(), "Edit"));
            Assert.AreEqual(1, deny.Asked.Count);
        }

        // ---- the delete action takes the files from the stored record ------------------------------------------------------------------------------

        private static BaseCRUDVM<Album> DeleteVm(Scene s, bool loadedFromStore)
        {
            var wtm = MockWtmContext.CreateWtmContext(s.Dc(), "bob", configs: new Configs());
            var vm = new BaseCRUDVM<Album> { Wtm = wtm };
            if (loadedFromStore)
            {
                vm.SetEntityById(s.AlbumId);
            }
            else
            {
                // what a form that is bound to Entity can post: the id of a record and file ids of its own choosing
                vm.Entity = new Album
                {
                    ID = s.AlbumId,
                    CoverId = s.Foreign,
                    Cover = new FileAttachment { ID = s.Foreign },
                    ScanId = s.Pending,
                    Scan = new FileAttachment { ID = s.Pending },
                    Photos = new List<AlbumPhoto> { new AlbumPhoto { FileId = s.Pending } }
                };
            }
            return vm;
        }

        [TestMethod]
        public void Delete_OfARecordReadFromTheStore_DeletesItsFiles()
        {
            var s = Seed();
            var vm = DeleteVm(s, loadedFromStore: true);
            vm.DoDelete();
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.IsFalse(s.Exists(s.Cover));
            Assert.IsFalse(s.Exists(s.Photo));
            Assert.IsTrue(s.Exists(s.Foreign));
            Assert.IsTrue(s.Exists(s.Pending));
        }

        [TestMethod]
        public void Delete_WithAnEntityThatWasNotReadFromTheStore_DeletesTheFilesOfTheStoredRecordOnly()
        {
            var s = Seed();
            var vm = DeleteVm(s, loadedFromStore: false);
            vm.DoDelete();
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.IsTrue(s.Exists(s.Foreign), "named by the request, linked to another record");
            Assert.IsTrue(s.Exists(s.Pending), "named by the request, linked to nothing");
            Assert.IsFalse(s.Exists(s.Cover), "the stored record's own files go with it");
            Assert.IsFalse(s.Exists(s.Photo));
        }

        [TestMethod]
        public async Task DeleteAsync_WithAnEntityThatWasNotReadFromTheStore_DeletesTheFilesOfTheStoredRecordOnly()
        {
            var s = Seed();
            var vm = DeleteVm(s, loadedFromStore: false);
            await vm.DoRealDeleteAsync();
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.IsTrue(s.Exists(s.Foreign));
            Assert.IsTrue(s.Exists(s.Pending));
            Assert.IsFalse(s.Exists(s.Cover));
            Assert.IsFalse(s.Exists(s.Photo));
        }
    }
}
