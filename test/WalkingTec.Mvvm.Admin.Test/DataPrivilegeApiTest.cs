using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.DataPrivilegeVMs;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    [TestClass]
    public class DataPrivilegeApiTest
    {
        private DataPrivilegeController _controller;
        private string _seed;

        public DataPrivilegeApiTest()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<DataPrivilegeController>(
                new Demo.DataContext(_seed, DBTypeEnum.Memory), "user");
        }

        [TestMethod]
        public void SearchTest()
        {
            var rv = _controller.Search(new DataPrivilegeSearcher());
            Assert.IsTrue(string.IsNullOrEmpty((rv as ContentResult)?.Content) == false);
        }

        [TestMethod]
        public void GetTest()
        {
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                context.Set<DataPrivilege>().Add(new DataPrivilege
                {
                    TableName = "TestTable",
                    UserCode = "getuser",
                    RelateId = "relid1"
                });
                context.SaveChanges();
            }

            var rv = _controller.Get("TestTable", "getuser", DpTypeEnum.User);
            Assert.IsNotNull(rv);
            Assert.IsNotNull(rv.SelectedItemsID);
            Assert.IsTrue(rv.SelectedItemsID.Contains("relid1"));
        }

        [TestMethod]
        public void GetByGroupTest()
        {
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                context.Set<DataPrivilege>().Add(new DataPrivilege
                {
                    TableName = "GroupTable",
                    GroupCode = "grp01",
                    RelateId = "grel1"
                });
                context.SaveChanges();
            }

            var rv = _controller.Get("GroupTable", "grp01", DpTypeEnum.UserGroup);
            Assert.IsNotNull(rv);
            Assert.IsNotNull(rv.SelectedItemsID);
            Assert.IsTrue(rv.SelectedItemsID.Contains("grel1"));
        }

        [TestMethod]
        public void CreateTest()
        {
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                context.Set<FrameworkUser>().Add(new FrameworkUser
                {
                    ITCode = "dpuser",
                    Name = "DP Test User",
                    Password = PasswordHashHelper.HashPassword("pass")
                });
                context.SaveChanges();
            }

            DataPrivilegeVM vm = _controller.Wtm.CreateVM<DataPrivilegeVM>();
            vm.Entity = new DataPrivilege
            {
                TableName = "TestTable",
                UserCode = "dpuser"
            };
            vm.DpType = DpTypeEnum.User;
            vm.IsAll = true;

            var rv = _controller.Add(vm).Result;
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var records = context.Set<DataPrivilege>().ToList();
                Assert.AreEqual(1, records.Count);
                Assert.AreEqual("TestTable", records[0].TableName);
                Assert.AreEqual("dpuser", records[0].UserCode);
                Assert.IsNull(records[0].RelateId, "IsAll=true stores a null RelateId row");
            }
        }

        [TestMethod]
        public void CreateWithSelectedItemsTest()
        {
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                context.Set<FrameworkUser>().Add(new FrameworkUser
                {
                    ITCode = "seluser",
                    Name = "Selected Items User",
                    Password = PasswordHashHelper.HashPassword("pass")
                });
                context.SaveChanges();
            }

            DataPrivilegeVM vm = _controller.Wtm.CreateVM<DataPrivilegeVM>();
            vm.Entity = new DataPrivilege
            {
                TableName = "ItemTable",
                UserCode = "seluser"
            };
            vm.DpType = DpTypeEnum.User;
            vm.IsAll = false;
            vm.SelectedItemsID = new List<string> { "item1", "item2", "item3" };

            var rv = _controller.Add(vm).Result;
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var records = context.Set<DataPrivilege>()
                    .Where(x => x.TableName == "ItemTable")
                    .OrderBy(x => x.RelateId)
                    .ToList();
                Assert.AreEqual(3, records.Count);
                Assert.AreEqual("item1", records[0].RelateId);
                Assert.AreEqual("item2", records[1].RelateId);
                Assert.AreEqual("item3", records[2].RelateId);
            }
        }

        [TestMethod]
        public void EditTest()
        {
            // Seed user and initial privilege (IsAll)
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                context.Set<FrameworkUser>().Add(new FrameworkUser
                {
                    ITCode = "edituser",
                    Name = "Edit User",
                    Password = PasswordHashHelper.HashPassword("pass")
                });
                context.Set<DataPrivilege>().Add(new DataPrivilege
                {
                    TableName = "EditTable",
                    UserCode = "edituser",
                    RelateId = null // IsAll
                });
                context.SaveChanges();
            }

            // Edit: change from IsAll to specific items
            DataPrivilegeVM vm = _controller.Wtm.CreateVM<DataPrivilegeVM>(
                values: x => x.Entity.TableName == "EditTable"
                          && x.Entity.UserCode == "edituser"
                          && x.DpType == DpTypeEnum.User);
            vm.IsAll = false;
            vm.SelectedItemsID = new List<string> { "new1", "new2" };

            var rv = _controller.Edit(vm).Result;
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var records = context.Set<DataPrivilege>()
                    .Where(x => x.TableName == "EditTable" && x.UserCode == "edituser")
                    .OrderBy(x => x.RelateId)
                    .ToList();
                Assert.AreEqual(2, records.Count);
                Assert.AreEqual("new1", records[0].RelateId);
                Assert.AreEqual("new2", records[1].RelateId);
            }
        }

        [TestMethod]
        public void DeleteTest()
        {
            // Seed privilege records
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                context.Set<DataPrivilege>().Add(new DataPrivilege
                {
                    TableName = "DelTable",
                    UserCode = "deluser",
                    RelateId = "r1"
                });
                context.Set<DataPrivilege>().Add(new DataPrivilege
                {
                    TableName = "DelTable",
                    UserCode = "deluser",
                    RelateId = "r2"
                });
                context.SaveChanges();
            }

            var rv = _controller.Delete(new SimpleDpModel
            {
                ModelName = "DelTable",
                Id = "deluser",
                Type = DpTypeEnum.User
            }).Result;
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var count = context.Set<DataPrivilege>()
                    .Count(x => x.TableName == "DelTable" && x.UserCode == "deluser");
                Assert.AreEqual(0, count, "All records for the user+table should be deleted");
            }
        }

        [TestMethod]
        public void DeleteByGroupTest()
        {
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                context.Set<DataPrivilege>().Add(new DataPrivilege
                {
                    TableName = "GrpDelTable",
                    GroupCode = "grp01",
                    RelateId = "g1"
                });
                context.SaveChanges();
            }

            var rv = _controller.Delete(new SimpleDpModel
            {
                ModelName = "GrpDelTable",
                Id = "grp01",
                Type = DpTypeEnum.UserGroup
            }).Result;
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var count = context.Set<DataPrivilege>()
                    .Count(x => x.TableName == "GrpDelTable" && x.GroupCode == "grp01");
                Assert.AreEqual(0, count);
            }
        }
    }
}
