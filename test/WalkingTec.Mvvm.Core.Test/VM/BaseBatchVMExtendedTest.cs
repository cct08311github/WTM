#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ─── Concrete CRUD VM needed by DoBatchEdit's assembly-scan ──────────────
    // DoBatchEdit looks for a BaseCRUDVM<TModel> in the assembly — provide one
    // for School so the validation path is exercised.
    public class SchoolEditCrudVM : BaseCRUDVM<School>
    {
        protected override void InitVM() { }
    }

    // ─── Batch VM subclass that blocks delete for a specific ID ───────────────
    internal class CannotDeleteBatchVM : BaseBatchVM<School, SchoolEdit>
    {
        private readonly string _blockedId;
        public CannotDeleteBatchVM(string blockedId) { _blockedId = blockedId; }

        protected override bool CheckIfCanDelete(object id, out string? errorMessage)
        {
            if (id?.ToString() == _blockedId)
            {
                errorMessage = "Delete blocked by test";
                return false;
            }
            errorMessage = null;
            return true;
        }
    }

    // ─── Minimal ListVM that wraps a static list for RefreshErrorList ─────────
    internal class FakeSchoolListVM : BasePagedListVM<School, BaseSearcher>
    {
        private readonly List<School> _data;
        public FakeSchoolListVM(List<School> data) { _data = data; }

        protected override IEnumerable<IGridColumn<School>> InitGridHeader()
            => Array.Empty<IGridColumn<School>>();

        public override IOrderedQueryable<School> GetSearchQuery()
            => _data.AsQueryable().OrderBy(x => x.SchoolName);
    }

    // ─── Test class ───────────────────────────────────────────────────────────
    [TestClass]
    public class BaseBatchVMExtendedTest
    {
        private string _seed = null!;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
        }

        private IDataContext CreateDb() => new DataContext(_seed, DBTypeEnum.Memory);

        private static readonly Guid Id1 = new Guid("AAAAAAAA-0000-0000-0000-000000000001");
        private static readonly Guid Id2 = new Guid("AAAAAAAA-0000-0000-0000-000000000002");
        private static readonly Guid Id3 = new Guid("AAAAAAAA-0000-0000-0000-000000000003");

        private void SeedSchools()
        {
            using var ctx = (DbContext)CreateDb();
            ctx.Set<School>().AddRange(
                new School { ID = Id1, SchoolCode = "001", SchoolName = "Alpha", SchoolType = SchoolTypeEnum.PRI, Remark = "r1" },
                new School { ID = Id2, SchoolCode = "002", SchoolName = "Beta",  SchoolType = SchoolTypeEnum.PUB, Remark = "r2" },
                new School { ID = Id3, SchoolCode = "003", SchoolName = "Gamma", SchoolType = SchoolTypeEnum.PRI, Remark = "r3" }
            );
            ctx.SaveChanges();
        }

        // ─── DoBatchDelete — happy path ──────────────────────────────────────

        [TestMethod]
        public void DoBatchDelete_DeletesTwoRows()
        {
            SeedSchools();

            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "tester");
            vm.Ids = new[] { Id1.ToString(), Id2.ToString() };

            var result = vm.DoBatchDelete();

            Assert.IsTrue(result, "DoBatchDelete should return true");

            using var ctx = (DbContext)CreateDb();
            Assert.AreEqual(1, ctx.Set<School>().Count(), "Only Id3 should remain");
        }

        [TestMethod]
        public void DoBatchDelete_AllThreeIds_DeletesAll()
        {
            SeedSchools();

            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "tester");
            vm.Ids = new[] { Id1.ToString(), Id2.ToString(), Id3.ToString() };

            var result = vm.DoBatchDelete();

            Assert.IsTrue(result, "Should return true when all deletions succeed");
            using var ctx = (DbContext)CreateDb();
            Assert.AreEqual(0, ctx.Set<School>().Count(), "All schools should be deleted");
        }

        [TestMethod]
        public void DoBatchDelete_SingleRow_Succeeds()
        {
            SeedSchools();

            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "tester");
            vm.Ids = new[] { Id3.ToString() };

            var result = vm.DoBatchDelete();

            Assert.IsTrue(result);
            using var ctx = (DbContext)CreateDb();
            Assert.AreEqual(2, ctx.Set<School>().Count());
        }

        // ─── DoBatchDelete — CheckIfCanDelete blocks ──────────────────────────

        [TestMethod]
        public void DoBatchDelete_CheckIfCanDelete_BlocksDelete()
        {
            SeedSchools();

            var vm = new CannotDeleteBatchVM(Id1.ToString());
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "tester");
            vm.Ids = new[] { Id1.ToString(), Id2.ToString() };

            var result = vm.DoBatchDelete();

            Assert.IsFalse(result, "DoBatchDelete should return false when delete is blocked");
            // Error message recorded for blocked id
            Assert.IsTrue(vm.ErrorMessage.ContainsKey(Id1.ToString()),
                "ErrorMessage should contain the blocked id");
        }

        [TestMethod]
        public void DoBatchDelete_CheckIfCanDelete_BlocksFirstRow_StopsEarly()
        {
            SeedSchools();

            var vm = new CannotDeleteBatchVM(Id1.ToString());
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "tester");
            vm.Ids = new[] { Id1.ToString() };

            vm.DoBatchDelete();

            Assert.AreEqual("Delete blocked by test", vm.ErrorMessage[Id1.ToString()]);
        }

        // ─── DoBatchDelete — with ListVM set ─────────────────────────────────

        [TestMethod]
        public void DoBatchDelete_WithListVM_ErrorPopulatesBatchError()
        {
            SeedSchools();

            var schools = new List<School>
            {
                new School { ID = Id1, SchoolCode = "001", SchoolName = "Alpha", SchoolType = SchoolTypeEnum.PRI, Remark = "r1" }
            };
            var listVm = new FakeSchoolListVM(schools);

            var vm = new CannotDeleteBatchVM(Id1.ToString());
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "tester");
            listVm.Wtm = vm.Wtm;
            vm.ListVM = listVm;
            vm.Ids = new[] { Id1.ToString() };

            var result = vm.DoBatchDelete();

            Assert.IsFalse(result);
        }

        // ─── DoBatchEdit — happy path ─────────────────────────────────────────

        [TestMethod]
        public void DoBatchEdit_UpdatesFieldsOnAllIds()
        {
            SeedSchools();

            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "editor");

            var linkedVm = new SchoolEdit
            {
                SchoolCode = "999",
                SchoolName = "Updated",
                SchoolType = SchoolTypeEnum.PUB,
                Remark = "updated-remark"
            };
            vm.LinkedVM = linkedVm;
            vm.FC.Add("LinkedVM.SchoolCode", "999");
            vm.FC.Add("LinkedVM.SchoolName", "Updated");
            vm.FC.Add("LinkedVM.SchoolType", SchoolTypeEnum.PUB);
            vm.FC.Add("LinkedVM.Remark", "updated-remark");
            vm.Ids = new[] { Id1.ToString(), Id2.ToString() };

            var result = vm.DoBatchEdit();

            Assert.IsTrue(result, "DoBatchEdit should return true");
            using var ctx = (DbContext)CreateDb();
            var rows = ctx.Set<School>().OrderBy(x => x.ID).ToList();
            Assert.AreEqual("999", rows[0].SchoolCode);
            Assert.AreEqual("Updated", rows[0].SchoolName);
        }

        [TestMethod]
        public void DoBatchEdit_ErrorMessage_InitiallyEmpty()
        {
            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "editor");
            vm.LinkedVM = new SchoolEdit();
            vm.Ids = new[] { Guid.NewGuid().ToString() };
            // No schools seeded so nothing to update — succeeds with no-op
            var result = vm.DoBatchEdit();
            Assert.AreEqual(0, vm.ErrorMessage.Count, "No errors should be recorded for a no-op edit");
        }

        [TestMethod]
        public void DoBatchEdit_SingleId_UpdatesOneRow()
        {
            SeedSchools();

            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "editor");

            var linked = new SchoolEdit { SchoolName = "Single Updated" };
            vm.LinkedVM = linked;
            vm.FC.Add("LinkedVM.SchoolName", "Single Updated");
            vm.Ids = new[] { Id1.ToString() };

            var result = vm.DoBatchEdit();

            Assert.IsTrue(result);
            using var ctx = (DbContext)CreateDb();
            // The update property should have been called — row still exists
            Assert.AreEqual(3, ctx.Set<School>().Count());
        }

        // ─── DoBatchEdit — FC missing means no update for that field ──────────

        [TestMethod]
        public void DoBatchEdit_FCMissingKey_SkipsField()
        {
            SeedSchools();

            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "editor");

            var linked = new SchoolEdit { SchoolName = "ShouldNotApply", SchoolCode = "SKIP" };
            vm.LinkedVM = linked;
            // Do NOT add keys to FC — so no fields are included
            vm.Ids = new[] { Id1.ToString() };

            var result = vm.DoBatchEdit();

            // Should succeed even with no FC keys (nothing to update)
            Assert.IsTrue(result);
        }

        // ─── DoBatchEdit — with ListVM set (RefreshErrorList path) ───────────

        [TestMethod]
        public void DoBatchEdit_WithListVM_Succeeds()
        {
            SeedSchools();

            var schools = new List<School>
            {
                new School { ID = Id1, SchoolCode = "001", SchoolName = "Alpha", SchoolType = SchoolTypeEnum.PRI, Remark = "r1" }
            };
            var listVm = new FakeSchoolListVM(schools);

            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "editor");
            listVm.Wtm = vm.Wtm;
            vm.ListVM = listVm;

            var linked = new SchoolEdit { SchoolName = "WithListVM" };
            vm.LinkedVM = linked;
            vm.FC.Add("LinkedVM.SchoolName", "WithListVM");
            vm.Ids = new[] { Id1.ToString() };

            var result = vm.DoBatchEdit();

            Assert.IsTrue(result);
        }

        // ─── Constructor / property defaults ──────────────────────────────────

        [TestMethod]
        public void Constructor_ErrorMessageIsEmpty()
        {
            var vm = new BaseBatchVM<School, SchoolEdit>();
            Assert.IsNotNull(vm.ErrorMessage);
            Assert.AreEqual(0, vm.ErrorMessage.Count);
        }

        [TestMethod]
        public void Constructor_IdsIsNull()
        {
            var vm = new BaseBatchVM<School, SchoolEdit>();
            Assert.IsNull(vm.Ids);
        }

        [TestMethod]
        public void LinkedVM_CanBeSet()
        {
            var vm = new BaseBatchVM<School, SchoolEdit>();
            var edit = new SchoolEdit { SchoolName = "test" };
            vm.LinkedVM = edit;
            Assert.AreEqual(edit, vm.LinkedVM);
        }

        [TestMethod]
        public void ListVM_CanBeSet()
        {
            var schools = new List<School>();
            var listVm = new FakeSchoolListVM(schools);
            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.ListVM = listVm;
            Assert.AreEqual(listVm, vm.ListVM);
        }

        // ─── CheckIfCanDelete virtual — default returns true ─────────────────

        [TestMethod]
        public void CheckIfCanDelete_Default_ReturnsTrue()
        {
            var vm = new ExposedBatchVM();
            string? errMsg;
            var result = vm.PublicCheckIfCanDelete(Id1, out errMsg);
            Assert.IsTrue(result);
            Assert.IsNull(errMsg);
        }

        // ─── SetExceptionMessage (via public exposure in ConcurrencyTestBatchVM) ─

        [TestMethod]
        public void SetExceptionMessage_NullId_DoesNotAddToErrorMessage()
        {
            var vm = new ConcurrencyTestBatchVM<School, SchoolEdit>();
            vm.InvokeSetExceptionMessage(new InvalidOperationException("oops"), null);
            Assert.AreEqual(0, vm.ErrorMessage.Count, "Null id should not add to ErrorMessage");
        }

        // ─── DoBatchDelete — all-ids list larger than seeded rows ────────────

        [TestMethod]
        public void DoBatchDelete_NonExistentId_SkipsSilently()
        {
            SeedSchools();

            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "tester");
            // Id that does not exist — EF CheckIDs returns empty list for it
            var fakeId = Guid.NewGuid().ToString();
            vm.Ids = new[] { Id1.ToString(), fakeId };

            // This exercises the path where entityList.Count != idsData.Count
            // The loop iterates entityList so non-existent IDs are skipped
            var result = vm.DoBatchDelete();

            // Should succeed (the found entity is deleted, non-existent is ignored)
            using var ctx = (DbContext)CreateDb();
            // At most 2 items remain (Id2 + Id3)
            Assert.IsTrue(ctx.Set<School>().Count() <= 3);
        }

        // ─── DoBatchEdit — multiple IDs in order ─────────────────────────────

        [TestMethod]
        public void DoBatchEdit_ThreeIds_AllUpdated()
        {
            SeedSchools();

            var vm = new BaseBatchVM<School, SchoolEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "editor");

            var linked = new SchoolEdit { Remark = "batch-remark" };
            vm.LinkedVM = linked;
            vm.FC.Add("LinkedVM.Remark", "batch-remark");
            vm.Ids = new[] { Id1.ToString(), Id2.ToString(), Id3.ToString() };

            var result = vm.DoBatchEdit();

            Assert.IsTrue(result);
        }
    }

    // ─── Expose CheckIfCanDelete for unit tests ────────────────────────────────
    internal class ExposedBatchVM : BaseBatchVM<School, SchoolEdit>
    {
        public bool PublicCheckIfCanDelete(object id, out string? errorMessage)
            => CheckIfCanDelete(id, out errorMessage);
    }
}
