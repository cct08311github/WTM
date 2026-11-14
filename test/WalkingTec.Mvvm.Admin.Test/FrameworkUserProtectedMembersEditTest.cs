using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkUserVms;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// The generic edit of a user does not write the tenant code or the create stamps, while <c>FrameworkUserBase.IsValid</c> (declared on the user
    /// itself, not an <see cref="IPersistPoco"/>) stays editable.
    /// </summary>
    [TestClass]
    public class FrameworkUserProtectedMembersEditTest
    {
        private FrameworkUserController _controller;
        private string _seed;
        private Guid _id;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<FrameworkUserController>(new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory), "itcode");
            using var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory);
            var user = new FrameworkUser { ITCode = "itcode", Name = "name", Password = Utils.GetMD5String("old"), CreateBy = "alice", CreateTime = new DateTime(2020, 1, 2) };
            context.Set<FrameworkUser>().Add(user);
            context.SaveChanges();
            _id = user.ID;
        }

        private FrameworkUser Stored()
        {
            using var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory);
            return context.Set<FrameworkUser>().AsEnumerable().Single();
        }

        private FrameworkUserVM EditVm()
        {
            var vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser { ID = _id, ITCode = "itcode", Name = "renamed", IsValid = false, TenantCode = "other", CreateBy = "mallory", CreateTime = new DateTime(1999, 1, 1) };
            vm.FC = new Dictionary<string, object>();
            foreach (var field in new[] { "Name", "IsValid", "TenantCode", "CreateBy", "CreateTime" })
            {
                vm.FC.Add("Entity." + field, "");
            }
            return vm;
        }

        private void AssertResult()
        {
            var user = Stored();
            Assert.AreEqual("renamed", user.Name);
            Assert.IsFalse(user.IsValid, "IsValid of a user is not an IPersistPoco member and stays editable");
            Assert.IsNull(user.TenantCode, "the tenant code is not written by the generic edit");
            Assert.AreEqual("alice", user.CreateBy);
            Assert.AreEqual(new DateTime(2020, 1, 2), user.CreateTime);
            Assert.AreEqual(Utils.GetMD5String("old"), user.Password);
        }

        [TestMethod]
        public void FormEdit_Sync()
        {
            EditVm().DoEdit(false);
            AssertResult();
        }

        [TestMethod]
        public async Task FormEdit_Async()
        {
            await EditVm().DoEditAsync(false);
            AssertResult();
        }
    }
}
