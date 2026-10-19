using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkUserVms;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// With the default options the demo view models store and check exactly the same MD5 values as before.
    /// </summary>
    [TestClass]
    public class PasswordVmTest
    {
        private FrameworkUserController _controller;
        private string _seed;

        public PasswordVmTest()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<FrameworkUserController>(new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory), "itcode");
        }

        private void AddUser(string password)
        {
            using (var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                context.Set<FrameworkUser>().Add(new FrameworkUser { ITCode = "itcode", Name = "name", Password = password });
                context.SaveChanges();
            }
        }

        private string StoredPassword()
        {
            using (var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                return context.Set<FrameworkUser>().Select(x => x.Password).Single();
            }
        }

        private ChangePasswordVM CreateChangeVm(string oldPassword, string newPassword)
        {
            var vm = _controller.Wtm.CreateVM<ChangePasswordVM>();
            vm.OldPassword = oldPassword;
            vm.NewPassword = newPassword;
            vm.NewPasswordComfirm = newPassword;
            return vm;
        }

        [TestMethod]
        public void ChangePasswordAcceptsCorrectOldPassword()
        {
            AddUser(Utils.GetMD5String("old"));
            var vm = CreateChangeVm("old", "new");
            vm.Validate();
            Assert.IsTrue(vm.MSD.IsValid);
            vm.DoChange();
            Assert.AreEqual(Utils.GetMD5String("new"), StoredPassword());
        }

        [TestMethod]
        public void ChangePasswordAcceptsLowerCaseStoredHash()
        {
            AddUser(Utils.GetMD5String("old").ToLowerInvariant());
            var vm = CreateChangeVm("old", "new");
            vm.Validate();
            Assert.IsTrue(vm.MSD.IsValid);
        }

        [TestMethod]
        public void ChangePasswordRejectsWrongOldPassword()
        {
            AddUser(Utils.GetMD5String("old"));
            var vm = CreateChangeVm("wrong", "new");
            vm.Validate();
            Assert.IsFalse(vm.MSD.IsValid);
            Assert.AreEqual(Utils.GetMD5String("old"), StoredPassword());
        }

        [TestMethod]
        public void ChangePasswordWithDuplicateAccountNamesChecksEveryCandidate()
        {
            // two rows with the same account name, as can happen under a case insensitive collation
            AddUser(Utils.GetMD5String("first"));
            using (var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                context.Set<FrameworkUser>().Add(new FrameworkUser { ITCode = "itcode", Name = "name2", Password = Utils.GetMD5String("second") });
                context.SaveChanges();
            }
            foreach (var oldPassword in new[] { "first", "second" })
            {
                var vm = CreateChangeVm(oldPassword, "new");
                vm.Validate();
                Assert.IsTrue(vm.MSD.IsValid, oldPassword);
            }
            var wrong = CreateChangeVm("third", "new");
            wrong.Validate();
            Assert.IsFalse(wrong.MSD.IsValid);
        }

        [TestMethod]
        public void AdminPasswordResetStoresMd5()
        {
            AddUser(Utils.GetMD5String("old"));
            FrameworkUser entity;
            using (var context = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                entity = context.Set<FrameworkUser>().AsNoTracking().Single();
            }
            var vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser { ID = entity.ID, Password = "reset" };
            vm.ChangePassword();
            Assert.AreEqual(Utils.GetMD5String("reset"), StoredPassword());
        }

        [TestMethod]
        public void SeededAdminPasswordIsTheMd5OfTheDefault()
        {
            Assert.AreEqual(Utils.GetMD5String("000000"), PasswordHasher.Default.Hash("000000"));
        }
    }
}
