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
    /// <summary>
    /// Minimal entity for import tests — uses BasePoco (has audit fields).
    /// </summary>
    public class ImportTestItem : BasePoco
    {
        [StringLength(50)]
        public string Name { get; set; } = "";

        public int Value { get; set; }
    }

    /// <summary>
    /// DataContext that registers ImportTestItem so EF Core can query/save it.
    /// </summary>
    internal class ImportTestDataContext : DataContext
    {
        public DbSet<ImportTestItem> ImportTestItems { get; set; } = null!;

        public ImportTestDataContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    /// <summary>
    /// Minimal template VM — fields map 1:1 to ImportTestItem properties.
    /// </summary>
    public class ImportTestTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Name_Excel = ExcelPropety.CreateProperty<ImportTestItem>(x => x.Name);
        public ExcelPropety Value_Excel = ExcelPropety.CreateProperty<ImportTestItem>(x => x.Value);

        protected override void InitVM() { }
    }

    /// <summary>
    /// Test import VM that overrides SetEntityList() to bypass Excel parsing.
    /// Test code pre-populates EntityList and sets isEntityListSet = true.
    /// </summary>
    public class TestImportVM : BaseImportVM<ImportTestTemplateVM, ImportTestItem>
    {
        private readonly List<ImportTestItem> _presetEntities;

        public TestImportVM() { _presetEntities = new List<ImportTestItem>(); }

        public TestImportVM(List<ImportTestItem> entities)
        {
            _presetEntities = entities;
        }

        public override void SetEntityList()
        {
            if (!isEntityListSet)
            {
                EntityList = _presetEntities;
                isEntityListSet = true;
            }
        }
    }

    [TestClass]
    public class BaseImportVMTest
    {
        private string _seed = null!;

        [TestInitialize]
        public void Initialize()
        {
            _seed = Guid.NewGuid().ToString();
        }

        private IDataContext CreateDb() => new ImportTestDataContext(_seed, DBTypeEnum.Memory);

        [TestMethod]
        public void BatchSaveData_ValidEntities_SavesToDB()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = "Item1", Value = 10 },
                new ImportTestItem { Name = "Item2", Value = 20 }
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "importuser");

            var result = vm.BatchSaveData();

            Assert.IsTrue(result, "BatchSaveData should return true for valid entities");
            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, "No errors expected");

            using (var context = CreateDb())
            {
                var saved = ((DbContext)context).Set<ImportTestItem>().OrderBy(x => x.Name).ToList();
                Assert.AreEqual(2, saved.Count);
                Assert.AreEqual("Item1", saved[0].Name);
                Assert.AreEqual(10, saved[0].Value);
                Assert.AreEqual("Item2", saved[1].Name);
                Assert.AreEqual(20, saved[1].Value);
                Assert.AreEqual("importuser", saved[0].CreateBy);
                Assert.IsNotNull(saved[0].CreateTime);
            }
        }

        [TestMethod]
        public void BatchSaveData_EmptyList_ReturnsTrue()
        {
            var vm = new TestImportVM(new List<ImportTestItem>());
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "importuser");

            var result = vm.BatchSaveData();

            Assert.IsTrue(result, "Empty entity list should not cause failure");

            using (var context = CreateDb())
            {
                Assert.AreEqual(0, ((DbContext)context).Set<ImportTestItem>().Count());
            }
        }

        [TestMethod]
        public void BatchSaveData_SetsAuditFields()
        {
            var entities = new List<ImportTestItem>
            {
                new ImportTestItem { Name = "Audited", Value = 99 }
            };

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "audituser");

            vm.BatchSaveData();

            using (var context = CreateDb())
            {
                var item = ((DbContext)context).Set<ImportTestItem>().Single();
                Assert.AreEqual("audituser", item.CreateBy);
                Assert.IsNotNull(item.CreateTime);
                Assert.IsTrue(DateTime.Now.Subtract(item.CreateTime!.Value).TotalSeconds < 10);
            }
        }

        [TestMethod]
        public void BatchSaveData_MultipleItems_AllPersisted()
        {
            var entities = new List<ImportTestItem>();
            for (int i = 0; i < 5; i++)
            {
                entities.Add(new ImportTestItem { Name = $"Batch{i}", Value = i * 100 });
            }

            var vm = new TestImportVM(entities);
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "batchuser");

            var result = vm.BatchSaveData();

            Assert.IsTrue(result);

            using (var context = CreateDb())
            {
                Assert.AreEqual(5, ((DbContext)context).Set<ImportTestItem>().Count());
            }
        }
    }
}
