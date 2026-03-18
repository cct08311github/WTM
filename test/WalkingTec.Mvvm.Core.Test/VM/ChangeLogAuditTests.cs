#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Text.Json;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// 驗證 [AuditChanges] + BaseCRUDVM ChangeLog 審計功能（Issue #569）。
    /// </summary>
    [TestClass]
    public class ChangeLogAuditTests
    {
        private string _seed = Guid.NewGuid().ToString();

        private BaseCRUDVM<AuditedProduct> MakeVm() =>
            new BaseCRUDVM<AuditedProduct>
            {
                Wtm = MockWtmContext.CreateWtmContext(
                    new DataContext(_seed, DBTypeEnum.Memory), "testuser")
            };

        // ──────────────────────────────────────────────────────────────────
        // DoAdd
        // ──────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("DoAdd: [AuditChanges] 模型 → 寫入 ChangeLog（Action=Add, NewValues 含欄位, OldValues=null）")]
        public void DoAdd_AuditedModel_WritesChangeLog()
        {
            var vm = MakeVm();
            vm.Entity = new AuditedProduct { Name = "Widget", Price = 9.99m };
            vm.DoAdd();

            using var ctx = new DataContext(_seed, DBTypeEnum.Memory);
            var logs = ctx.Set<ChangeLog>().ToList();
            Assert.AreEqual(1, logs.Count, "應寫入一筆 ChangeLog");

            var log = logs[0];
            Assert.AreEqual("Add", log.Action);
            Assert.AreEqual("testuser", log.ChangedBy);
            Assert.IsNull(log.OldValues, "DoAdd 的 OldValues 應為 null");
            Assert.IsNotNull(log.NewValues, "DoAdd 的 NewValues 不應為 null");

            var dict = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, JsonElement>>(log.NewValues!);
            Assert.IsNotNull(dict);
            Assert.IsTrue(dict!.ContainsKey("Name"), "NewValues 應包含 Name");
            Assert.AreEqual("Widget", dict["Name"].GetString());
        }

        [TestMethod]
        [Description("DoAdd: 無 [AuditChanges] 的模型 → 不寫入 ChangeLog")]
        public void DoAdd_NonAuditedModel_NoChangeLog()
        {
            var vm = new BaseCRUDVM<School>
            {
                Wtm = MockWtmContext.CreateWtmContext(
                    new DataContext(_seed, DBTypeEnum.Memory), "testuser")
            };
            vm.Entity = new School
            {
                SchoolCode = "001",
                SchoolName = "TestSchool",
                SchoolType = SchoolTypeEnum.PUB,
                Remark = "r"
            };
            vm.DoAdd();

            using var ctx = new DataContext(_seed, DBTypeEnum.Memory);
            Assert.AreEqual(0, ctx.Set<ChangeLog>().Count(), "非審計模型不應寫入 ChangeLog");
        }

        // ──────────────────────────────────────────────────────────────────
        // DoEdit
        // ──────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("DoEdit: [AuditChanges] 模型 → 寫入 ChangeLog（Action=Edit, OldValues 含舊值, NewValues 含新值）")]
        public void DoEdit_AuditedModel_WritesChangeLog()
        {
            // seed the entity
            Guid id;
            using (var ctx = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var p = new AuditedProduct { Name = "OldName", Price = 1.00m };
                ctx.Set<AuditedProduct>().Add(p);
                ctx.SaveChanges();
                id = p.ID;
            }

            var vm = MakeVm();
            vm.Entity = new AuditedProduct { ID = id, Name = "NewName", Price = 2.00m };
            vm.DoEdit(updateAllFields: true);

            using var ctx2 = new DataContext(_seed, DBTypeEnum.Memory);
            var logs = ctx2.Set<ChangeLog>().ToList();
            Assert.AreEqual(1, logs.Count, "應寫入一筆 ChangeLog");

            var log = logs[0];
            Assert.AreEqual("Edit", log.Action);
            Assert.IsNotNull(log.OldValues, "DoEdit 的 OldValues 不應為 null");
            Assert.IsNotNull(log.NewValues, "DoEdit 的 NewValues 不應為 null");

            var old = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, JsonElement>>(log.OldValues!);
            var newv = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, JsonElement>>(log.NewValues!);
            Assert.AreEqual("OldName", old!["Name"].GetString(), "OldValues.Name 應為舊值");
            Assert.AreEqual("NewName", newv!["Name"].GetString(), "NewValues.Name 應為新值");
        }

        // ──────────────────────────────────────────────────────────────────
        // DoRealDelete
        // ──────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("DoRealDelete: [AuditChanges] 模型 → 寫入 ChangeLog（Action=Delete, OldValues 含舊值, NewValues=null）")]
        public void DoRealDelete_AuditedModel_WritesChangeLog()
        {
            Guid id;
            using (var ctx = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var p = new AuditedProduct { Name = "ToDelete", Price = 5.00m };
                ctx.Set<AuditedProduct>().Add(p);
                ctx.SaveChanges();
                id = p.ID;
            }

            var vm = MakeVm();
            vm.Entity = new AuditedProduct { ID = id, Name = "ToDelete", Price = 5.00m };
            vm.DoRealDelete();

            using var ctx2 = new DataContext(_seed, DBTypeEnum.Memory);
            // entity removed
            Assert.AreEqual(0, ctx2.Set<AuditedProduct>().Count(), "實體應已被刪除");

            var logs = ctx2.Set<ChangeLog>().ToList();
            Assert.AreEqual(1, logs.Count, "應寫入一筆 ChangeLog");

            var log = logs[0];
            Assert.AreEqual("Delete", log.Action);
            Assert.IsNotNull(log.OldValues, "DoRealDelete 的 OldValues 不應為 null");
            Assert.IsNull(log.NewValues, "DoRealDelete 的 NewValues 應為 null");
        }

        // ──────────────────────────────────────────────────────────────────
        // EntityType / EntityId / ChangedAt
        // ──────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("ChangeLog 應正確記錄 EntityType、EntityId 和 ChangedAt")]
        public void DoAdd_SetsEntityTypeEntityIdAndChangedAt()
        {
            var vm = MakeVm();
            var before = DateTime.UtcNow.AddSeconds(-1);
            vm.Entity = new AuditedProduct { Name = "Meta", Price = 0m };
            vm.DoAdd();
            var after = DateTime.UtcNow.AddSeconds(1);

            using var ctx = new DataContext(_seed, DBTypeEnum.Memory);
            var log = ctx.Set<ChangeLog>().Single();

            StringAssert.Contains(log.EntityType, "AuditedProduct",
                "EntityType 應包含模型類別名稱");
            Assert.IsNotNull(log.EntityId, "EntityId 不應為 null");
            Assert.IsTrue(log.ChangedAt >= before && log.ChangedAt <= after,
                "ChangedAt 應在操作前後時間範圍內");
        }
    }
}
