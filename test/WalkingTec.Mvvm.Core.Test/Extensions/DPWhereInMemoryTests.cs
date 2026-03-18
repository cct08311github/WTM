#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Extensions
{
    #region Test models

    public class DPTestMajor : TopBasePoco
    {
        public string? MajorName { get; set; }
    }

    public class DPTestStudent : TopBasePoco
    {
        public string? Name { get; set; }
        public Guid? MajorId { get; set; }
        public DPTestMajor? Major { get; set; }
    }

    public class DPTestEnrollment : TopBasePoco
    {
        public Guid StudentId { get; set; }
        public DPTestStudent? Student { get; set; }
        public List<DPTestTag>? Tags { get; set; }
    }

    public class DPTestTag : TopBasePoco
    {
        public string? TagName { get; set; }
        public Guid EnrollmentId { get; set; }
    }

    /// <summary>
    /// Minimal IDataPrivilege for wiring DataPrivilegeSettings without needing a DB.
    /// </summary>
    public class DPTestPrivilegeInfo : IDataPrivilege
    {
        public string ModelName { get; set; } = "";
        public string PrivillegeName { get; set; } = "";
        public Type ModelType { get; set; } = typeof(TopBasePoco);

        public List<ComboSelectListItem> GetItemList(WTMContext wtmcontext, string? filter = null, List<string>? ids = null)
            => new();

        public List<string> GetTreeParentIds(WTMContext wtmcontext, List<DataPrivilege> dps)
            => new();

        public List<string> GetTreeSubIds(WTMContext wtmcontext, List<string> pids)
            => new();
    }

    #endregion

    [TestClass]
    public class DPWhereInMemoryTests
    {
        private static WTMContext CreateWtmWithDP(
            string tableName,
            List<string?> relateIds)
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo
                {
                    ModelName = tableName,
                    PrivillegeName = tableName,
                    ModelType = typeof(TopBasePoco)
                }
            };

            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "testuser",
                DataPrivileges = relateIds.Select(id => new SimpleDataPri
                {
                    ID = Guid.NewGuid(),
                    TableName = tableName,
                    RelateId = id,
                    UserCode = "testuser"
                }).ToList()
            };

            return wtm;
        }

        #region AppendSelfDPWhere tests (via reflection since it's private)

        private static IQueryable<T> InvokeAppendSelfDPWhere<T>(
            IQueryable<T> query, WTMContext? wtm, List<SimpleDataPri>? dps) where T : TopBasePoco
        {
            var method = typeof(DCExtension)
                .GetMethod("AppendSelfDPWhere", BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(typeof(T));

            return (IQueryable<T>)method.Invoke(null, new object?[] { query, wtm, dps })!;
        }

        [TestMethod]
        public void AppendSelfDPWhere_InMemory_Filters_By_ID_List()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var id3 = Guid.NewGuid();

            var data = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" },
                new() { ID = id3, MajorName = "Physics" }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { id1.ToString(), id3.ToString() });
            var dps = wtm.LoginUserInfo!.DataPrivileges;

            var result = InvokeAppendSelfDPWhere(data, wtm, dps).ToList();

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.Any(x => x.ID == id1));
            Assert.IsTrue(result.Any(x => x.ID == id3));
        }

        [TestMethod]
        public void AppendSelfDPWhere_InMemory_NullDps_Returns_Empty()
        {
            var data = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?>());
            // Explicitly pass null dps
            var result = InvokeAppendSelfDPWhere(data, wtm, null).ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void AppendSelfDPWhere_InMemory_EmptyIds_Returns_Empty()
        {
            var data = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?>());
            // dps list exists but has no entries for this table
            var dps = new List<SimpleDataPri>();

            var result = InvokeAppendSelfDPWhere(data, wtm, dps).ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void AppendSelfDPWhere_InMemory_NullContainingIds_Returns_All()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();

            var data = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" }
            }.AsQueryable();

            // null in the ids list means "all access"
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { null });
            var dps = wtm.LoginUserInfo!.DataPrivileges;

            var result = InvokeAppendSelfDPWhere(data, wtm, dps).ToList();

            Assert.AreEqual(2, result.Count);
        }

        #endregion

        #region DPWhere tests (public extension method)

        [TestMethod]
        public void DPWhere_InMemory_Filters_By_ForeignKey()
        {
            var majorId1 = Guid.NewGuid();
            var majorId2 = Guid.NewGuid();
            var majorId3 = Guid.NewGuid();

            var data = new List<DPTestStudent>
            {
                new() { ID = Guid.NewGuid(), Name = "Alice", MajorId = majorId1 },
                new() { ID = Guid.NewGuid(), Name = "Bob", MajorId = majorId2 },
                new() { ID = Guid.NewGuid(), Name = "Carol", MajorId = majorId3 }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { majorId1.ToString(), majorId3.ToString() });

            var result = data.DPWhere(wtm, x => x.MajorId!).ToList();

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.Any(x => x.Name == "Alice"));
            Assert.IsTrue(result.Any(x => x.Name == "Carol"));
        }

        [TestMethod]
        public void DPWhere_InMemory_NullDps_Returns_Empty()
        {
            var data = new List<DPTestStudent>
            {
                new() { ID = Guid.NewGuid(), Name = "Alice", MajorId = Guid.NewGuid() }
            }.AsQueryable();

            // Create WTM with no login user (null dps)
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = nameof(DPTestMajor) }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            // LoginUserInfo is null => dps is null

            var result = data.DPWhere(wtm, x => x.MajorId!).ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void DPWhere_InMemory_EmptyIds_Returns_Empty()
        {
            var data = new List<DPTestStudent>
            {
                new() { ID = Guid.NewGuid(), Name = "Alice", MajorId = Guid.NewGuid() }
            }.AsQueryable();

            // User has data privileges configured for this table but no specific IDs
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?>());

            var result = data.DPWhere(wtm, x => x.MajorId!).ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void DPWhere_InMemory_NullContainingIds_Returns_All()
        {
            var data = new List<DPTestStudent>
            {
                new() { ID = Guid.NewGuid(), Name = "Alice", MajorId = Guid.NewGuid() },
                new() { ID = Guid.NewGuid(), Name = "Bob", MajorId = Guid.NewGuid() }
            }.AsQueryable();

            // null in ids list means "all access" — no filter applied
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { null });

            var result = data.DPWhere(wtm, x => x.MajorId!).ToList();

            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void DPWhere_InMemory_NoMatchingDPSetting_Returns_All()
        {
            var data = new List<DPTestStudent>
            {
                new() { ID = Guid.NewGuid(), Name = "Alice", MajorId = Guid.NewGuid() },
                new() { ID = Guid.NewGuid(), Name = "Bob", MajorId = Guid.NewGuid() }
            }.AsQueryable();

            // DP settings don't include DPTestMajor, so the filter should be skipped
            var wtm = CreateWtmWithDP("SomeOtherTable", new List<string?> { Guid.NewGuid().ToString() });

            var result = data.DPWhere(wtm, x => x.MajorId!).ToList();

            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void DPWhere_InMemory_SelfId_Filters_By_Own_ID()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var id3 = Guid.NewGuid();

            var data = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" },
                new() { ID = id3, MajorName = "Physics" }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { id1.ToString(), id2.ToString() });

            // IdField = x => x.ID filters on the entity's own ID
            var result = data.DPWhere(wtm, x => x.ID).ToList();

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.Any(x => x.ID == id1));
            Assert.IsTrue(result.Any(x => x.ID == id2));
        }

        #endregion
    }

    // ─── ApplyDataPrivilegeForAnalysis tests (#554) ───────────────────────────

    [TestClass]
    public class ApplyDataPrivilegeForAnalysisTests
    {
        private static WTMContext CreateWtmWithDP(string tableName, List<string?> relateIds)
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo
                {
                    ModelName = tableName,
                    PrivillegeName = tableName,
                    ModelType = typeof(TopBasePoco)
                }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "testuser",
                DataPrivileges = relateIds.Select(id => new SimpleDataPri
                {
                    ID = Guid.NewGuid(),
                    TableName = tableName,
                    RelateId = id,
                    UserCode = "testuser"
                }).ToList()
            };
            return wtm;
        }

        [TestMethod]
        public void ApplyDataPrivilege_FiltersRowsByAllowedIds()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var id3 = Guid.NewGuid();

            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" },
                new() { ID = id3, MajorName = "Physics" }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { id1.ToString(), id3.ToString() });

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.Any(x => x.ID == id1));
            Assert.IsTrue(result.Any(x => x.ID == id3));
        }

        [TestMethod]
        public void ApplyDataPrivilege_DeniesAll_WhenNoPrivilegesConfigured()
        {
            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" },
                new() { ID = Guid.NewGuid(), MajorName = "English" }
            }.AsQueryable();

            // DP setting for DPTestMajor exists, but user has no relateIds → deny all
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?>());

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void ApplyDataPrivilege_AllowsAll_WhenNullRelateIdPresent()
        {
            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" },
                new() { ID = Guid.NewGuid(), MajorName = "English" }
            }.AsQueryable();

            // null in relateIds list = unrestricted access
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { null });

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void ApplyDataPrivilege_NoOp_WhenModelNotInDPSettings()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();

            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" }
            }.AsQueryable();

            // DP settings only cover "SomeOtherTable" — DPTestMajor is not restricted
            var wtm = CreateWtmWithDP("SomeOtherTable", new List<string?> { Guid.NewGuid().ToString() });

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void ApplyDataPrivilege_NoOp_WhenElementTypeNotTopBasePoco()
        {
            // Non-TopBasePoco type should pass through unchanged
            IQueryable baseQuery = new List<string> { "a", "b", "c" }.AsQueryable();
            var wtm = CreateWtmWithDP("String", new List<string?>());

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<string>().ToList();

            Assert.AreEqual(3, result.Count);
        }

        [TestMethod]
        public void ApplyDataPrivilege_NoOp_WhenWtmIsNull()
        {
            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" }
            }.AsQueryable();

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, null)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(1, result.Count);
        }

        [TestMethod]
        public void ApplyDataPrivilege_NoOp_WhenLoginUserInfoIsNull()
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = nameof(DPTestMajor) }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            // LoginUserInfo intentionally left null

            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" }
            }.AsQueryable();

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(1, result.Count);
        }
    }
}
