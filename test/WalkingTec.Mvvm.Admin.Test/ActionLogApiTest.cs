using System;

using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.ActionLogVMs;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.Vue3Demo;

namespace WalkingTec.Mvvm.Admin.Test
{
    [TestClass]
    public class ActionLogApiTest
    {
        private ActionLogController _controller;
        private string _seed;

        public ActionLogApiTest()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<ActionLogController>(new DataContext(_seed, DBTypeEnum.Memory), "user");
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
            ActionLog l = new ActionLog();
            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                context.Set<ActionLog>().Add(l);
                context.SaveChanges();
            }
            var rv = _controller.Get(l.ID);
            Assert.IsNotNull(rv);
            Assert.AreEqual(l.ID, rv.Entity.ID);
        }

        [TestMethod]
        public void ExportTest()
        {
            var rv = _controller.ExportExcel(new ActionLogSearcher());
            Assert.IsInstanceOfType(rv, typeof(FileContentResult));
            Assert.IsTrue((rv as FileContentResult).FileContents.Length > 0);
        }
    }
}
