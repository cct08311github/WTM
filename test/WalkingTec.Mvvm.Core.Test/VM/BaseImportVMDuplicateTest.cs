#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ─── Entity for duplicate-detection tests ────────────────────────────────

    public class DupCheckItem : BasePoco
    {
        [StringLength(30)]
        public string Key { get; set; } = "";

        [StringLength(50)]
        public string Label { get; set; } = "";
    }

    internal class DupCheckDataContext : DataContext
    {
        public DbSet<DupCheckItem> DupItems { get; set; } = null!;
        public DupCheckDataContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    public class DupCheckTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Key_Excel = ExcelPropety.CreateProperty<DupCheckItem>(x => x.Key);
        public ExcelPropety Label_Excel = ExcelPropety.CreateProperty<DupCheckItem>(x => x.Label);
        protected override void InitVM() { }
    }

    /// <summary>
    /// Exposes protected duplicate-check methods for direct unit testing.
    /// </summary>
    public class ExposedDupImportVM : BaseImportVM<DupCheckTemplateVM, DupCheckItem>
    {
        private readonly List<DupCheckItem> _preset;
        public ExposedDupImportVM() { _preset = new List<DupCheckItem>(); }
        public ExposedDupImportVM(List<DupCheckItem> preset) { _preset = preset; }

        public override void SetEntityList()
        {
            if (!isEntityListSet)
            {
                EntityList = _preset;
                isEntityListSet = true;
            }
        }

        public override DuplicatedInfo<DupCheckItem>? SetDuplicatedCheck()
            => CreateFieldsInfo(SimpleField(x => x.Key));

        // Expose protected methods for testing
        public bool PublicIsUpdateRecordDuplicated(DuplicatedInfo<DupCheckItem> info, DupCheckItem entity)
            => IsUpdateRecordDuplicated(info, entity);

        public void PublicValidateDuplicateData(DuplicatedInfo<DupCheckItem> info, DupCheckItem entity)
            => ValidateDuplicateData(info, entity);

        public void PublicSetValidateCheck() => SetValidateCheck();

        // Expose CreateFieldsInfo for building multi-field DuplicatedInfo in tests
        public DuplicatedInfo<DupCheckItem> CreateFieldsInfo(params DuplicatedField<DupCheckItem>[] fields)
            => base.CreateFieldsInfo(fields);

        // Expose SimpleField as an instance-level helper so tests can call it on the vm
        public new DuplicatedField<DupCheckItem> SimpleField(System.Linq.Expressions.Expression<Func<DupCheckItem, object>> exp)
            => BaseImportVM<DupCheckTemplateVM, DupCheckItem>.SimpleField(exp);
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    [TestClass]
    public class BaseImportVMDuplicateTest
    {
        private string _seed = null!;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
        }

        private IDataContext CreateDb() => new DupCheckDataContext(_seed, DBTypeEnum.Memory);

        // ─── ValidateDuplicateData — no group, no error ───────────────────

        [TestMethod]
        public void ValidateDuplicateData_NullCondition_NoError()
        {
            var vm = new ExposedDupImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");

            var entity = new DupCheckItem { Key = "A", Label = "Alpha" };
            // null condition → nothing to check
            vm.PublicValidateDuplicateData(new DuplicatedInfo<DupCheckItem> { Groups = new List<DuplicatedGroup<DupCheckItem>>() }, entity);

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, "Empty groups should produce no error");
        }

        [TestMethod]
        public void ValidateDuplicateData_NoDuplicate_NoError()
        {
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "A", Label = "Alpha", ExcelIndex = 1 },
                new DupCheckItem { Key = "B", Label = "Beta",  ExcelIndex = 2 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");
            vm.SetEntityList();

            var info = vm.SetDuplicatedCheck()!;

            // Validate A — it is unique among [A, B]
            vm.PublicValidateDuplicateData(info, items[0]);
            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, "Unique entity should produce no error");
        }

        [TestMethod]
        public void ValidateDuplicateData_Duplicate_AddsError()
        {
            // Two entities share the same Key value
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "SAME", Label = "First",  ExcelIndex = 1 },
                new DupCheckItem { Key = "SAME", Label = "Second", ExcelIndex = 2 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");
            vm.SetEntityList();

            var info = vm.SetDuplicatedCheck()!;

            // Validate items[0] — it will find items[1] as duplicate (different ExcelIndex)
            vm.PublicValidateDuplicateData(info, items[0]);
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0,
                "Duplicate entity should add error to ErrorListVM");
        }

        // ─── IsUpdateRecordDuplicated — in-memory list ────────────────────

        [TestMethod]
        public void IsUpdateRecordDuplicated_NoDuplicate_ReturnsFalse()
        {
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "X", Label = "First",  ExcelIndex = 1 },
                new DupCheckItem { Key = "Y", Label = "Second", ExcelIndex = 2 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");
            vm.SetEntityList();

            var info = vm.SetDuplicatedCheck()!;

            var result = vm.PublicIsUpdateRecordDuplicated(info, items[0]);

            Assert.IsFalse(result, "No duplicate — should return false");
        }

        [TestMethod]
        public void IsUpdateRecordDuplicated_Duplicate_ReturnsTrue()
        {
            // IsUpdateRecordDuplicated checks EntityList for other records (ID != self) that share
            // the same field values. Items must have distinct non-empty IDs so the "exclude self"
            // expression (x.ID != entity.ID) actually lets item[1] appear in the results for item[0].
            var id0 = Guid.NewGuid();
            var id1 = Guid.NewGuid();
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { ID = id0, Key = "DUP", Label = "First",  ExcelIndex = 1 },
                new DupCheckItem { ID = id1, Key = "DUP", Label = "Second", ExcelIndex = 2 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");
            vm.SetEntityList();

            var info = vm.SetDuplicatedCheck()!;

            // Checking items[0]: the query will find items[1] (ID != id0, Key == "DUP")
            var result = vm.PublicIsUpdateRecordDuplicated(info, items[0]);

            Assert.IsTrue(result, "items[1] has the same Key and a different ID — should return true");
        }

        [TestMethod]
        public void IsUpdateRecordDuplicated_EmptyGroup_ReturnsFalse()
        {
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "X", Label = "First", ExcelIndex = 1 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");
            vm.SetEntityList();

            var emptyInfo = new DuplicatedInfo<DupCheckItem> { Groups = new List<DuplicatedGroup<DupCheckItem>>() };

            var result = vm.PublicIsUpdateRecordDuplicated(emptyInfo, items[0]);

            Assert.IsFalse(result, "Empty group should return false");
        }

        // ─── SetValidateCheck — exercises the assembly-scan + validation ──

        [TestMethod]
        public void SetValidateCheck_NoErrors_WhenAllValid()
        {
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "V1", Label = "Valid One",   ExcelIndex = 1 },
                new DupCheckItem { Key = "V2", Label = "Valid Two",   ExcelIndex = 2 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");
            vm.SetEntityList();

            vm.PublicSetValidateCheck();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count,
                "No errors expected for valid, non-duplicate entities");
        }

        [TestMethod]
        public void SetValidateCheck_DuplicateKeys_AddsError()
        {
            // Both rows have the same Key — SetDuplicatedCheck detects it
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "DUP", Label = "First",  ExcelIndex = 1 },
                new DupCheckItem { Key = "DUP", Label = "Second", ExcelIndex = 2 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");
            vm.SetEntityList();

            vm.PublicSetValidateCheck();

            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0,
                "Duplicate Key should be detected by SetValidateCheck");
        }

        // ─── BatchSaveData path that calls SetValidateCheck internally ────

        [TestMethod]
        public void BatchSaveData_DuplicateKey_ReturnsFalse()
        {
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "BDUP", Label = "First",  ExcelIndex = 1 },
                new DupCheckItem { Key = "BDUP", Label = "Second", ExcelIndex = 2 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsFalse(result, "Batch with duplicate keys should fail");
        }

        [TestMethod]
        public void BatchSaveData_UniqueKeys_SavesAll()
        {
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "U1", Label = "Unique One",   ExcelIndex = 1 },
                new DupCheckItem { Key = "U2", Label = "Unique Two",   ExcelIndex = 2 },
                new DupCheckItem { Key = "U3", Label = "Unique Three", ExcelIndex = 3 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsTrue(result);
            using var ctx = (DbContext)CreateDb();
            Assert.AreEqual(3, ctx.Set<DupCheckItem>().Count());
        }

        // ─── SetTemplateData — UploadFileId = null path ────────────────────

        [TestMethod]
        public void SetTemplateData_NullUploadFileId_AddsError()
        {
            // Standard TestImportVM calls SetEntityList() which bypasses SetTemplateData.
            // We need a VM that calls the base SetEntityList.
            var vm = new UploadFileRequiredImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new ImportTestDataContext(_seed, DBTypeEnum.Memory), "user");
            // UploadFileId is null by default

            vm.SetEntityList(); // Triggers SetTemplateData → UploadFileId is null → error added

            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0,
                "Null UploadFileId should produce an error from SetTemplateData");
        }

        // ─── IsOverWriteExistData false — DB duplicate detection path ─────

        [TestMethod]
        public void BatchSaveData_IsOverWriteExistData_False_NoDuplicate_Succeeds()
        {
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "NODUPE", Label = "Fresh", ExcelIndex = 1 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.IsOverWriteExistData = false;
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsTrue(result);
        }

        [TestMethod]
        public void BatchSaveData_IsOverWriteExistData_False_DbDuplicate_ReturnsFalse()
        {
            // Pre-seed a row in the DB
            using (var ctx = (DbContext)CreateDb())
            {
                ctx.Set<DupCheckItem>().Add(new DupCheckItem { Key = "DBKEY", Label = "Existing" });
                ctx.SaveChanges();
            }

            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "DBKEY", Label = "Incoming Dup", ExcelIndex = 1 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.IsOverWriteExistData = false;
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");

            var result = vm.BatchSaveData();

            Assert.IsFalse(result, "Import of existing key with overwrite=false should fail");
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0);
        }

        // ─── IsOverWriteExistData true — DB duplicate overwrite path ──────

        [TestMethod]
        public void BatchSaveData_IsOverWriteExistData_True_DbDuplicate_Succeeds()
        {
            using (var ctx = (DbContext)CreateDb())
            {
                ctx.Set<DupCheckItem>().Add(new DupCheckItem { Key = "OVWRITE", Label = "Old" });
                ctx.SaveChanges();
            }

            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "OVWRITE", Label = "New", ExcelIndex = 1 }
            };

            var vm = new ExposedDupImportVM(items);
            vm.IsOverWriteExistData = true;
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");

            var result = vm.BatchSaveData();

            // Overwrite path: should succeed with 1 row
            using (var ctx = (DbContext)CreateDb())
            {
                Assert.AreEqual(1, ctx.Set<DupCheckItem>().Count(), "Should still have exactly 1 row");
            }
        }

        // ─── CreateFieldsInfo / SimpleField / SubField ────────────────────

        [TestMethod]
        public void SimpleField_CreatesNonNullField()
        {
            var field = BaseImportVM<DupCheckTemplateVM, DupCheckItem>.SimpleField(x => x.Key);
            Assert.IsNotNull(field);
        }

        [TestMethod]
        public void CreateFieldsInfo_SingleField_ReturnsGroupWithOneField()
        {
            var vm = new ExposedDupImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");

            var info = vm.SetDuplicatedCheck()!;

            Assert.AreEqual(1, info.Groups.Count);
            Assert.IsTrue(info.Groups[0].Fields.Count > 0);
        }
    }

    /// <summary>
    /// A bare import VM that DOES call base.SetEntityList() so we can test
    /// the SetTemplateData → null-UploadFileId error path.
    /// </summary>
    public class UploadFileRequiredImportVM : BaseImportVM<ImportTestTemplateVM, ImportTestItem>
    {
        // Uses default SetEntityList() so SetTemplateData is reached
    }
}
