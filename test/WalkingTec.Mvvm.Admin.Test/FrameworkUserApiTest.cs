using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkUserVms;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    [TestClass]
    public class FrameworkUserApiTest
    {
        private FrameworkUserController _controller;
        private string _seed;

        public FrameworkUserApiTest()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<FrameworkUserController>(new Demo.DataContext(_seed, DBTypeEnum.Memory), "user");
        }

        [TestMethod]
        public void SearchTest()
        {
            var rv = _controller.Search(new FrameworkUserSearcher()).Result;
            Assert.IsTrue(string.IsNullOrEmpty((rv as ContentResult)?.Content)==false);
        }

        [TestMethod]
        public void CreateTest()
        {

            FrameworkUserVM vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            FrameworkUser v = new FrameworkUser();

            v.ITCode = "itcode";
            v.Name = "name";
            v.Password = "password";
            vm.Entity = v;
            var rv = _controller.Add(vm);
            Assert.IsInstanceOfType(rv.Result, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkUser>().FirstOrDefault();
                Assert.AreEqual(data.ITCode, "itcode");
                Assert.AreEqual(data.Name, "name");
                var verifyResult = PasswordHashHelper.VerifyPassword(data.Password, "password");
                Assert.AreNotEqual(PasswordVerifyResult.Failed, verifyResult);
                Assert.AreEqual(data.CreateBy, "user");
                Assert.IsTrue(DateTime.Now.Subtract(data.CreateTime.Value).TotalSeconds < 10);
            }

        }

        [TestMethod]
        public void EditTest()
        {
            FrameworkUser v = new FrameworkUser();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v.ITCode = "itcode";
                v.Name = "name";
                v.Password = "password";
                context.Set<FrameworkUser>().Add(v);
                context.SaveChanges();
            }

            FrameworkUserVM vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            var oldID = v.ID;
            v = new FrameworkUser();
            v.ID = oldID;
            v.Name = "name1";
            v.ITCode = "abc";
            vm.Entity = v;            
            vm.FC = new Dictionary<string, object>();
            vm.FC.Add("Entity.Name", "");
            var rv = _controller.Edit(vm);
            Assert.IsInstanceOfType(rv.Result, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkUser>().FirstOrDefault();
                Assert.AreEqual(data.ITCode, "itcode");
                Assert.AreEqual(data.Name, "name1");
                Assert.AreEqual(data.UpdateBy, "user");
                Assert.IsTrue(DateTime.Now.Subtract(data.UpdateTime.Value).TotalSeconds < 10);
            }

        }

        [TestMethod]
        public void GetTest()
        {
            FrameworkUser v = new FrameworkUser();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v.ITCode = "itcode";
                v.Name = "name";
                v.Password = "password";
                context.Set<FrameworkUser>().Add(v);
                context.SaveChanges();
            }
            var rv = _controller.Get(v.ID);
            Assert.IsNotNull(rv);
            Assert.AreEqual(rv.Entity.ITCode, "itcode");
        }

        [TestMethod]
        public void BatchDeleteTest()
        {
            FrameworkUser v1 = new FrameworkUser();
            FrameworkUser v2 = new FrameworkUser();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v1.ITCode = "itcode";
                v1.Name = "name";
                v1.Password = "password";
                v1.PhotoId = AddPhoto();
                v2.ITCode = "itcode2";
                v2.Name = "name2";
                v2.Password = "password2";
                v2.PhotoId = AddPhoto();
                context.Set<FrameworkUser>().Add(v1);
                context.Set<FrameworkUser>().Add(v2);
                context.SaveChanges();
            }


            var rv = _controller.BatchDelete(new string[] { v1.ID.ToString(), v2.ID.ToString() });
            Assert.IsInstanceOfType(rv.Result, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(context.Set<FrameworkUser>().Count(), 0);
            }

            rv = _controller.BatchDelete(new string[] {});
            Assert.IsInstanceOfType(rv.Result, typeof(OkResult));
        }

        [TestMethod]
        public void ExportTest()
        {
            var rv = _controller.ExportExcel(new FrameworkUserSearcher());
            Assert.IsInstanceOfType(rv, typeof(FileResult));

            rv = _controller.ExportExcelByIds(new string[] { });
            Assert.IsInstanceOfType(rv, typeof(FileResult));

            rv = _controller.GetExcelTemplate();
            Assert.IsInstanceOfType(rv, typeof(FileResult));

        }

        /// <summary>
        /// MVC-008 regression — non-admin actor must NOT be able to reset another user's password.
        /// The controller looks up ITCode from the DB by Entity.ID, so a tampered body ITCode
        /// cannot bypass the guard.
        /// </summary>
        [TestMethod]
        public void Password_NonAdmin_CannotResetAnotherUsersPassword()
        {
            // Arrange: create target user "victim" in the DB
            FrameworkUser victim = new FrameworkUser();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                victim.ITCode = "victim";
                victim.Name = "Victim User";
                victim.Password = PasswordHashHelper.HashPassword("original");
                context.Set<FrameworkUser>().Add(victim);
                context.SaveChanges();
            }

            // Actor "user" is non-admin (Roles is null — default MockWtmContext behaviour)
            FrameworkUserVM vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser
            {
                ID = victim.ID,
                ITCode = "victim",    // even if the attacker supplies the correct ITCode in the body
                Password = "hacked!"
            };

            // Act
            var rv = _controller.Password(vm);

            // Assert: must be forbidden, not Ok
            Assert.IsInstanceOfType(rv, typeof(ForbidResult),
                "Non-admin actor must not be allowed to reset another user's password.");

            // Verify the password was NOT changed in the DB
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkUser>().First(x => x.ID == victim.ID);
                var verifyHacked = PasswordHashHelper.VerifyPassword(data.Password, "hacked!");
                Assert.AreEqual(PasswordVerifyResult.Failed, verifyHacked,
                    "Password must not have been changed by the rejected request.");
            }
        }

        /// <summary>
        /// MVC-008 regression — non-admin actor CAN reset their own password (self-service path).
        /// </summary>
        [TestMethod]
        public void Password_NonAdmin_CanResetOwnPassword()
        {
            // Arrange: create the actor's own user record in the DB
            FrameworkUser self = new FrameworkUser();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                // The mock controller is created with usercode "user" (see constructor above)
                self.ITCode = "user";
                self.Name = "Self User";
                self.Password = PasswordHashHelper.HashPassword("oldpass");
                context.Set<FrameworkUser>().Add(self);
                context.SaveChanges();
            }

            FrameworkUserVM vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser
            {
                ID = self.ID,
                ITCode = "user",
                Password = "newpass"
            };

            // Act
            var rv = _controller.Password(vm);

            // Assert: self-service must succeed
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult),
                "Non-admin actor must be allowed to reset their own password.");

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkUser>().First(x => x.ID == self.ID);
                var verifyNew = PasswordHashHelper.VerifyPassword(data.Password, "newpass");
                Assert.AreNotEqual(PasswordVerifyResult.Failed, verifyNew,
                    "New password must be stored after a successful self-service reset.");
            }
        }

        /// <summary>
        /// MVC-008 regression — admin actor CAN reset any user's password.
        /// </summary>
        [TestMethod]
        public void Password_Admin_CanResetAnotherUsersPassword()
        {
            // Arrange: create target user in the DB
            FrameworkUser target = new FrameworkUser();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                target.ITCode = "victim2";
                target.Name = "Victim2 User";
                target.Password = PasswordHashHelper.HashPassword("original");
                context.Set<FrameworkUser>().Add(target);
                context.SaveChanges();
            }

            // Create a controller where the acting user is an admin
            var adminController = MockController.CreateApi<FrameworkUserController>(
                new Demo.DataContext(_seed, DBTypeEnum.Memory), "admin");
            adminController.Wtm.LoginUserInfo!.Roles = new System.Collections.Generic.List<SimpleRole>
            {
                new SimpleRole { RoleCode = "Admin", RoleName = "Administrator" }
            };

            FrameworkUserVM vm = adminController.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser
            {
                ID = target.ID,
                ITCode = "victim2",
                Password = "admin_reset"
            };

            // Act
            var rv = adminController.Password(vm);

            // Assert: admin is allowed
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult),
                "Admin actor must be allowed to reset any user's password.");

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkUser>().First(x => x.ID == target.ID);
                var verifyNew = PasswordHashHelper.VerifyPassword(data.Password, "admin_reset");
                Assert.AreNotEqual(PasswordVerifyResult.Failed, verifyNew,
                    "Password must have been updated by the admin request.");
            }
        }

        private Guid AddPhoto()
        {
            FileAttachment v = new FileAttachment();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {

                v.FileName = "PEsnw";
                v.FileExt = "celfpE";
                v.Path = "egy";
                v.Length = 61;
                v.SaveMode = "uLfM37wt";
                v.ExtraInfo = "Od3aqjgP";
                v.HandlerInfo = "tbyzFF";
                context.Set<FileAttachment>().Add(v);
                context.SaveChanges();
            }
            return v.ID;
        }

    }
}
