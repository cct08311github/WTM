using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkUserVms;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// The generic edit (BaseCRUDVM.DoEdit/DoEditAsync) never writes the stored password, whatever the request carries;
    /// the flows that are meant to set it (add, password change, reset) still do.
    /// </summary>
    [TestClass]
    public class PasswordGenericEditTest
    {
        private const string Posted = "posted-value-not-a-hash";
        private FrameworkUserController _controller;
        private string _seed;
        private Guid _id;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<FrameworkUserController>(new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory), "itcode");
            using (var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var user = new FrameworkUser { ITCode = "itcode", Name = "name", Password = Utils.GetMD5String("old") };
                context.Set<FrameworkUser>().Add(user);
                context.SaveChanges();
                _id = user.ID;
            }
        }

        private string StoredPassword()
        {
            using (var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                return context.Set<FrameworkUser>().Select(x => x.Password).Single();
            }
        }

        private string StoredName()
        {
            using (var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                return context.Set<FrameworkUser>().Select(x => x.Name).Single();
            }
        }

        // the entity and the field list as the framework builds them for a form post (FC keys come from the form)
        private FrameworkUserVM EditVm(params string[] fields)
        {
            var vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser { ID = _id, ITCode = "itcode", Name = "renamed", Password = Posted };
            vm.FC = new Dictionary<string, object>();
            foreach (var field in fields)
            {
                vm.FC.Add(field, "");
            }
            return vm;
        }

        [TestMethod]
        public void AttributeIsOnThePasswordProperty()
        {
            var property = typeof(FrameworkUserBase).GetProperty(nameof(FrameworkUserBase.Password));
            Assert.IsNotNull(property.GetCustomAttribute<CanNotEditAttribute>());
        }

        [TestMethod]
        public void FormEditKeepsStoredPassword_Sync()
        {
            var vm = EditVm("Entity.Name", "Entity.Password");
            vm.DoEdit(false);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual("renamed", StoredName());
            Assert.AreEqual(Utils.GetMD5String("old"), StoredPassword());
        }

        [TestMethod]
        public async Task FormEditKeepsStoredPassword_Async()
        {
            var vm = EditVm("Entity.Name", "Entity.Password");
            await vm.DoEditAsync(false);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual("renamed", StoredName());
            Assert.AreEqual(Utils.GetMD5String("old"), StoredPassword());
        }

        [TestMethod]
        public void FieldNameCaseDoesNotMatter()
        {
            var vm = EditVm("entity.name", "ENTITY.PASSWORD");
            vm.DoEdit(false);
            Assert.AreEqual("renamed", StoredName());
            Assert.AreEqual(Utils.GetMD5String("old"), StoredPassword());
        }

        [TestMethod]
        public async Task ApiEditKeepsStoredPassword()
        {
            // for a JSON body the filter puts every property name of the body into FC; the controller action is the one the API serves
            var vm = EditVm("Entity.Name", "Entity.Password");
            var rv = await _controller.Edit(vm);
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));
            Assert.AreEqual("renamed", StoredName());
            Assert.AreEqual(Utils.GetMD5String("old"), StoredPassword());
        }

        [TestMethod]
        public async Task ApiEditWithOnlyPasswordKeepsStoredPassword()
        {
            var vm = EditVm("Entity.Password");
            var rv = await _controller.Edit(vm);
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));
            Assert.AreEqual("name", StoredName());
            Assert.AreEqual(Utils.GetMD5String("old"), StoredPassword());
        }

        private string StoredItCode()
        {
            using (var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                return context.Set<FrameworkUser>().Select(x => x.ITCode).Single();
            }
        }

        // updateAllFields: true writes the whole record, except the members marked CanNotEdit
        [TestMethod]
        public void UpdateAllFields_KeepsCanNotEditMembers_AndUpdatesTheOthers_Sync()
        {
            var vm = EditVm("Entity.Name");
            vm.Entity.ITCode = "changed-itcode";
            vm.DoEdit(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual("renamed", StoredName());
            Assert.AreEqual(Utils.GetMD5String("old"), StoredPassword());
            Assert.AreEqual("itcode", StoredItCode());
        }

        [TestMethod]
        public async Task UpdateAllFields_KeepsCanNotEditMembers_AndUpdatesTheOthers_Async()
        {
            var vm = EditVm("Entity.Name");
            vm.Entity.ITCode = "changed-itcode";
            await vm.DoEditAsync(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual("renamed", StoredName());
            Assert.AreEqual(Utils.GetMD5String("old"), StoredPassword());
            Assert.AreEqual("itcode", StoredItCode());
        }

        [TestMethod]
        public async Task AddStillHashesAndStoresPassword()
        {
            var vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser { ITCode = "another", Name = "another", Password = "secret" };
            await vm.DoAddAsync();
            Assert.IsTrue(vm.MSD.IsValid);
            using (var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(Utils.GetMD5String("secret"), context.Set<FrameworkUser>().Where(x => x.ITCode == "another").Select(x => x.Password).Single());
            }
        }

        [TestMethod]
        public void AdministratorResetStillChangesPassword()
        {
            var vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser { ID = _id, ITCode = "itcode", Name = "name", Password = "reset-value" };
            var rv = _controller.Password(vm);
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));
            Assert.AreEqual(Utils.GetMD5String("reset-value"), StoredPassword());
        }

        [TestMethod]
        public void ChangePasswordFlowStillChangesPassword()
        {
            var vm = _controller.Wtm.CreateVM<ChangePasswordVM>();
            vm.OldPassword = "old";
            vm.NewPassword = "new";
            vm.NewPasswordComfirm = "new";
            vm.Validate();
            Assert.IsTrue(vm.MSD.IsValid);
            vm.DoChange();
            Assert.AreEqual(Utils.GetMD5String("new"), StoredPassword());
        }

        [TestMethod]
        public async Task LoginCheckStillAcceptsTheOldPasswordAfterAnEdit()
        {
            var vm = EditVm("Entity.Name", "Entity.Password");
            await vm.DoEditAsync(false);
            // the check DoLogin makes against the stored value
            Assert.AreNotEqual(PasswordVerifyResult.Failed, _controller.Wtm.VerifyPassword(StoredPassword(), "old"));
            Assert.AreEqual(PasswordVerifyResult.Failed, _controller.Wtm.VerifyPassword(StoredPassword(), Posted));
        }
    }
}
