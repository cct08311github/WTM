using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkRoleVMs;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    [TestClass]
    public class FrameworkRoleApiTest
    {
        private FrameworkRoleController _controller;
        private string _seed;

        public FrameworkRoleApiTest()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<FrameworkRoleController>(
                new Demo.DataContext(_seed, DBTypeEnum.Memory), "user");
        }

        [TestMethod]
        public void SearchTest()
        {
            var rv = _controller.Search(new FrameworkRoleSearcher()).Result;
            Assert.IsTrue(string.IsNullOrEmpty((rv as ContentResult)?.Content) == false);
        }

        [TestMethod]
        public void CreateTest()
        {
            FrameworkRoleVM vm = _controller.Wtm.CreateVM<FrameworkRoleVM>();
            FrameworkRole v = new FrameworkRole();
            v.RoleCode = "101";
            v.RoleName = "TestRole";
            vm.Entity = v;
            var rv = _controller.Add(vm);
            Assert.IsInstanceOfType(rv.Result, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkRole>().FirstOrDefault();
                Assert.AreEqual(data.RoleCode, "101");
                Assert.AreEqual(data.RoleName, "TestRole");
                Assert.AreEqual(data.CreateBy, "user");
                Assert.IsTrue(DateTime.Now.Subtract(data.CreateTime.Value).Seconds < 10);
            }
        }

        [TestMethod]
        public void EditTest()
        {
            FrameworkRole v = new FrameworkRole();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v.RoleCode = "102";
                v.RoleName = "OriginalRoleName";
                context.Set<FrameworkRole>().Add(v);
                context.SaveChanges();
            }

            FrameworkRoleVM vm = _controller.Wtm.CreateVM<FrameworkRoleVM>();
            var oldID = v.ID;
            v = new FrameworkRole();
            v.ID = oldID;
            v.RoleCode = "102";
            v.RoleName = "UpdatedRoleName";
            vm.Entity = v;
            vm.FC = new Dictionary<string, object>();
            vm.FC.Add("Entity.RoleName", "");
            var rv = _controller.Edit(vm);
            Assert.IsInstanceOfType(rv.Result, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkRole>().FirstOrDefault();
                Assert.AreEqual(data.RoleName, "UpdatedRoleName");
                Assert.AreEqual(data.UpdateBy, "user");
                Assert.IsTrue(DateTime.Now.Subtract(data.UpdateTime.Value).Seconds < 10);
            }
        }

        [TestMethod]
        public void GetTest()
        {
            FrameworkRole v = new FrameworkRole();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v.RoleCode = "103";
                v.RoleName = "GetTestRole";
                context.Set<FrameworkRole>().Add(v);
                context.SaveChanges();
            }

            var rv = _controller.Get(v.ID);
            Assert.IsNotNull(rv);
            Assert.AreEqual(rv.Entity.RoleCode, "103");
            Assert.AreEqual(rv.Entity.RoleName, "GetTestRole");
        }

        [TestMethod]
        public void BatchDeleteTest()
        {
            FrameworkRole v1 = new FrameworkRole();
            FrameworkRole v2 = new FrameworkRole();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v1.RoleCode = "104";
                v1.RoleName = "Role1";
                v2.RoleCode = "105";
                v2.RoleName = "Role2";
                context.Set<FrameworkRole>().Add(v1);
                context.Set<FrameworkRole>().Add(v2);
                context.SaveChanges();
            }

            var rv = _controller.BatchDelete(new string[] { v1.ID.ToString(), v2.ID.ToString() });
            Assert.IsInstanceOfType(rv.Result, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(context.Set<FrameworkRole>().Count(), 0);
            }

            rv = _controller.BatchDelete(new string[] { });
            Assert.IsInstanceOfType(rv.Result, typeof(OkResult));
        }
    }
}
