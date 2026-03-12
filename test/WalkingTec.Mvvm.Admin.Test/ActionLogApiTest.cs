using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.ActionLogVMs;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    [TestClass]
    public class ActionLogApiTest
    {
        private WalkingTec.Mvvm.Admin.Api.ActionLogController _controller;
        private string _seed;

        public ActionLogApiTest()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<WalkingTec.Mvvm.Admin.Api.ActionLogController>(
                new Demo.DataContext(_seed, DBTypeEnum.Memory), "user");
        }

        [TestMethod]
        public void SearchTest()
        {
            var rv = _controller.Search(new ActionLogSearcher());
            Assert.IsTrue(string.IsNullOrEmpty((rv as ContentResult)?.Content) == false);
        }

        [TestMethod]
        public void GetTest()
        {
            ActionLog log;
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                log = new ActionLog
                {
                    ModuleName = "TestModule",
                    ActionName = "TestAction",
                    ITCode = "testuser",
                    ActionUrl = "/test/action",
                    ActionTime = DateTime.Now,
                    LogType = ActionLogTypesEnum.Normal,
                    IP = "127.0.0.1"
                };
                context.Set<ActionLog>().Add(log);
                context.SaveChanges();
            }

            var rv = _controller.Get(log.ID);
            Assert.IsNotNull(rv);
            Assert.AreEqual("TestModule", rv.Entity.ModuleName);
            Assert.AreEqual("TestAction", rv.Entity.ActionName);
            Assert.AreEqual("testuser", rv.Entity.ITCode);
        }

        [TestMethod]
        public void BatchDeleteTest()
        {
            ActionLog log1, log2;
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                log1 = new ActionLog
                {
                    ModuleName = "Mod1",
                    ActionName = "Act1",
                    ITCode = "user1",
                    ActionUrl = "/m1",
                    ActionTime = DateTime.Now,
                    LogType = ActionLogTypesEnum.Normal,
                    IP = "127.0.0.1"
                };
                log2 = new ActionLog
                {
                    ModuleName = "Mod2",
                    ActionName = "Act2",
                    ITCode = "user2",
                    ActionUrl = "/m2",
                    ActionTime = DateTime.Now,
                    LogType = ActionLogTypesEnum.Exception,
                    IP = "192.168.1.1"
                };
                context.Set<ActionLog>().Add(log1);
                context.Set<ActionLog>().Add(log2);
                context.SaveChanges();
            }

            var rv = _controller.BatchDelete(new string[] { log1.ID.ToString(), log2.ID.ToString() });
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(0, context.Set<ActionLog>().Count());
            }
        }

        [TestMethod]
        public void BatchDeleteEmptyIds_ReturnsOk()
        {
            var rv = _controller.BatchDelete(new string[] { });
            Assert.IsInstanceOfType(rv, typeof(OkResult));
        }

        [TestMethod]
        public void ExportExcelTest()
        {
            var rv = _controller.ExportExcel(new ActionLogSearcher());
            Assert.IsInstanceOfType(rv, typeof(FileResult));
        }

        [TestMethod]
        public void ExportExcelByIdsTest()
        {
            var rv = _controller.ExportExcelByIds(new string[] { });
            Assert.IsInstanceOfType(rv, typeof(FileResult));
        }
    }
}
