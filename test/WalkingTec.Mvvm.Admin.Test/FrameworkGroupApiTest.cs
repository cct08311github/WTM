using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkGroupVMs;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    [TestClass]
    public class FrameworkGroupApiTest
    {
        private FrameworkGroupController _controller;
        private string _seed;

        public FrameworkGroupApiTest()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<FrameworkGroupController>(
                new Demo.DataContext(_seed, DBTypeEnum.Memory), "user");
        }

        [TestMethod]
        public void SearchTest()
        {
            var rv = _controller.Search(new FrameworkGroupSearcher()).Result;
            Assert.IsTrue(string.IsNullOrEmpty((rv as ContentResult)?.Content) == false);
        }

        [TestMethod]
        public void CreateTest()
        {
            FrameworkGroupVM vm = _controller.Wtm.CreateVM<FrameworkGroupVM>();
            FrameworkGroup v = new FrameworkGroup();
            v.GroupCode = "001";
            v.GroupName = "TestGroup";
            vm.Entity = v;
            // Add() returns IActionResult (sync), not Task<IActionResult>
            var rv = _controller.Add(vm);
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkGroup>().FirstOrDefault();
                Assert.AreEqual(data.GroupCode, "001");
                Assert.AreEqual(data.GroupName, "TestGroup");
                // Note: FrameworkGroup : TreePoco : TopBasePoco (no audit fields)
            }
        }

        [TestMethod]
        public void EditTest()
        {
            FrameworkGroup v = new FrameworkGroup();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v.GroupCode = "002";
                v.GroupName = "OriginalName";
                context.Set<FrameworkGroup>().Add(v);
                context.SaveChanges();
            }

            FrameworkGroupVM vm = _controller.Wtm.CreateVM<FrameworkGroupVM>();
            var oldID = v.ID;
            v = new FrameworkGroup();
            v.ID = oldID;
            v.GroupCode = "002";
            v.GroupName = "UpdatedName";
            vm.Entity = v;
            vm.FC = new Dictionary<string, object>();
            vm.FC.Add("Entity.GroupName", "");
            // Edit() returns IActionResult (sync), not Task<IActionResult>
            var rv = _controller.Edit(vm);
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkGroup>().FirstOrDefault();
                Assert.AreEqual(data.GroupName, "UpdatedName");
            }
        }

        [TestMethod]
        public void GetTest()
        {
            FrameworkGroup v = new FrameworkGroup();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v.GroupCode = "003";
                v.GroupName = "GetTestGroup";
                context.Set<FrameworkGroup>().Add(v);
                context.SaveChanges();
            }

            var rv = _controller.Get(v.ID);
            Assert.IsNotNull(rv);
            Assert.AreEqual(rv.Entity.GroupCode, "003");
            Assert.AreEqual(rv.Entity.GroupName, "GetTestGroup");
        }

        [TestMethod]
        public void BatchDeleteTest()
        {
            FrameworkGroup v1 = new FrameworkGroup();
            FrameworkGroup v2 = new FrameworkGroup();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v1.GroupCode = "004";
                v1.GroupName = "Group1";
                v2.GroupCode = "005";
                v2.GroupName = "Group2";
                context.Set<FrameworkGroup>().Add(v1);
                context.Set<FrameworkGroup>().Add(v2);
                context.SaveChanges();
            }

            // BatchDelete() returns Task<IActionResult> (async)
            var rv = _controller.BatchDelete(new string[] { v1.ID.ToString(), v2.ID.ToString() });
            Assert.IsInstanceOfType(rv.Result, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(context.Set<FrameworkGroup>().Count(), 0);
            }

            rv = _controller.BatchDelete(new string[] { });
            Assert.IsInstanceOfType(rv.Result, typeof(OkResult));
        }
    }
}
