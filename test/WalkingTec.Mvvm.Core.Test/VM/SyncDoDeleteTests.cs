#nullable enable
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Regression tests for the DoDelete() sync method fixes:
    ///   1. FC["Entity.IsValid"] = 0 is idempotent (no ArgumentException on duplicate key)
    ///   2. PersistPoco soft-delete correctly sets IsValid = false
    /// </summary>
    [TestClass]
    public class SyncDoDeleteTests
    {
        private string _seed = null!;

        [TestInitialize]
        public void Initialize()
        {
            _seed = Guid.NewGuid().ToString();
        }

        /// <summary>
        /// If FC already contains "Entity.IsValid" before DoDelete() is called
        /// (e.g., appended via query string), the old FC.Add() would throw
        /// ArgumentException. The fix replaces Add with indexer assignment.
        /// </summary>
        [TestMethod]
        [Description("DoDelete must not throw ArgumentException when FC already has Entity.IsValid key")]
        public void DoDelete_FC_IsValid_DuplicateKey_DoesNotThrow()
        {
            // Arrange: create a GoodsSpecification record via DoAdd
            var addVm = new BaseCRUDVM<GoodsSpecification>
            {
                Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "testuser")
            };
            addVm.Entity = new GoodsSpecification { Name = "TestGoods", IsValid = true };
            addVm.DoAdd();

            int insertedId;
            using (var ctx = new DataContext(_seed, DBTypeEnum.Memory))
            {
                insertedId = ctx.Set<GoodsSpecification>().IgnoreQueryFilters().First().ID;
            }

            // Arrange: create a delete VM and pre-populate the FC key that would cause
            // ArgumentException with the old FC.Add() code.
            var deleteVm = new BaseCRUDVM<GoodsSpecification>
            {
                Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "testuser")
            };

            using (var ctx = new DataContext(_seed, DBTypeEnum.Memory))
            {
                deleteVm.Entity = ctx.Set<GoodsSpecification>().IgnoreQueryFilters()
                    .First(x => x.ID == insertedId);
            }

            // Pre-populate the key — this simulates "Entity.IsValid=0" coming in via query string
            deleteVm.FC["Entity.IsValid"] = 0;

            // Act + Assert: must NOT throw ArgumentException
            try
            {
                deleteVm.DoDelete();
            }
            catch (ArgumentException ex) when (ex.Message.Contains("key"))
            {
                Assert.Fail("DoDelete() threw ArgumentException for duplicate FC key 'Entity.IsValid' — " +
                            "indexer assignment fix was not applied");
            }
        }

        /// <summary>
        /// DoDelete() on a PersistPoco (GoodsSpecification) must set IsValid=false (soft-delete).
        /// </summary>
        [TestMethod]
        [Description("DoDelete on PersistPoco must soft-delete by setting IsValid=false")]
        public void DoDelete_PersistPoco_SetsIsValidFalse()
        {
            // Arrange: add a record
            var addVm = new BaseCRUDVM<GoodsSpecification>
            {
                Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "testuser")
            };
            addVm.Entity = new GoodsSpecification { Name = "SoftDeleteTest", IsValid = true };
            addVm.DoAdd();

            int insertedId;
            using (var ctx = new DataContext(_seed, DBTypeEnum.Memory))
            {
                insertedId = ctx.Set<GoodsSpecification>().IgnoreQueryFilters().First().ID;
            }

            // Arrange: set up delete VM with the entity
            var deleteVm = new BaseCRUDVM<GoodsSpecification>
            {
                Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "testuser")
            };
            using (var ctx = new DataContext(_seed, DBTypeEnum.Memory))
            {
                deleteVm.Entity = ctx.Set<GoodsSpecification>().IgnoreQueryFilters()
                    .First(x => x.ID == insertedId);
            }

            // Act
            deleteVm.DoDelete();

            // Assert: the record must be soft-deleted (IsValid = false)
            using (var ctx = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var record = ctx.Set<GoodsSpecification>().IgnoreQueryFilters()
                    .First(x => x.ID == insertedId);
                Assert.IsFalse(record.IsValid,
                    "DoDelete() on PersistPoco must set IsValid=false (soft-delete)");
            }
        }
    }
}
