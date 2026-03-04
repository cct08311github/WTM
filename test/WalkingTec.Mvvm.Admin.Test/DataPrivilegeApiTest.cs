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
            // Seed a DataPrivilege record and query it back
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
        public void CreateTest()
        {
            // DataPrivilegeVM.Validate() checks that UserCode maps to a real FrameworkUser
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
    }
}
