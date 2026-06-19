using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc.Admin.Controllers;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkUserVms;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    [TestClass]
    public class FrameworkUserControllerTest
    {
        private FrameworkUserController _controller;
        private string _seed;

        public FrameworkUserControllerTest()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateController<FrameworkUserController>(new Demo.DataContext(_seed, DBTypeEnum.Memory), "user");
        }

        [TestMethod]
        public void SearchTest()
        {
            PartialViewResult rv = (PartialViewResult)_controller.Index();
            Assert.IsInstanceOfType(rv.Model, typeof(IBasePagedListVM<TopBasePoco, BaseSearcher>));
            string rv2 = _controller.Search((rv.Model as FrameworkUserListVM).Searcher);
            Assert.IsTrue(rv2.Contains("\"Code\":200"));
        }

        [TestMethod]
        public void ExportTest()
        {
            PartialViewResult rv = (PartialViewResult)_controller.Index();
            Assert.IsInstanceOfType(rv.Model, typeof(IBasePagedListVM<TopBasePoco, BaseSearcher>));
            IActionResult rv2 = _controller.ExportExcel(rv.Model as FrameworkUserListVM);
            Assert.IsTrue((rv2 as FileContentResult).FileContents.Length > 0);
        }


        [TestMethod]
        public void CreateTest()
        {
            PartialViewResult rv = (PartialViewResult)_controller.Create();
            Assert.IsInstanceOfType(rv.Model, typeof(FrameworkUserVM));

            FrameworkUserVM vm = rv.Model as FrameworkUserVM;
            FrameworkUser v = new FrameworkUser();

            v.ITCode = "itcode";
            v.Name = "name";
            v.Password = "password";
            vm.Entity = v;
            _controller.Create(vm).Wait();

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

            PartialViewResult rv = (PartialViewResult)_controller.Edit(v.ID.ToString());
            Assert.IsInstanceOfType(rv.Model, typeof(FrameworkUserVM));

            FrameworkUserVM vm = rv.Model as FrameworkUserVM;
            v = new FrameworkUser();
            v.ID = vm.Entity.ID;
            v.Name = "name1";
            v.ITCode = "abc";
            vm.Entity = v;
            vm.FC = new Dictionary<string, object>();
            vm.FC.Add("Entity.Name", "");
            _controller.Edit(vm).Wait();

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkUser>().FirstOrDefault();
                Assert.AreEqual(data.Name, "name1");
                Assert.AreEqual(data.UpdateBy, "user");
                Assert.IsTrue(DateTime.Now.Subtract(data.UpdateTime.Value).TotalSeconds < 10);
            }

        }


        [TestMethod]
        public void DeleteTest()
        {
            FrameworkUser v = new FrameworkUser();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v.ITCode = "itcode";
                v.Name = "name";
                v.Password = "password";
                v.PhotoId = AddPhoto();
                context.Set<FrameworkUser>().Add(v);
                context.SaveChanges();
            }

            PartialViewResult rv = (PartialViewResult)_controller.Delete(v.ID);
            Assert.IsInstanceOfType(rv.Model, typeof(FrameworkUserVM));

            FrameworkUserVM vm = rv.Model as FrameworkUserVM;
            v = new FrameworkUser();
            v.ID = vm.Entity.ID;
            vm.Entity = v;
            _controller.Delete(v.ID,null).Wait();

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(context.Set<FrameworkUser>().Count(), 0);
            }

        }


        [TestMethod]
        public void DetailsTest()
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
            PartialViewResult rv = (PartialViewResult)_controller.Details(v.ID);
            Assert.IsInstanceOfType(rv.Model, typeof(IBaseCRUDVM<TopBasePoco>));
            Assert.AreEqual(v.ID, (rv.Model as IBaseCRUDVM<TopBasePoco>).Entity.ID);
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

            PartialViewResult rv = (PartialViewResult)_controller.BatchDelete(new string[] { v1.ID.ToString(), v2.ID.ToString() });
            Assert.IsInstanceOfType(rv.Model, typeof(FrameworkUserBatchVM));
            (rv.Model as FrameworkUserBatchVM).ListVM.DoSearch();

            FrameworkUserBatchVM vm = rv.Model as FrameworkUserBatchVM;
            vm.Ids = new string[] { v1.ID.ToString(), v2.ID.ToString() };
            _controller.DoBatchDelete(vm, null).Wait();

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(context.Set<FrameworkUser>().Count(), 0);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // MVC-008 regression tests (#411) — Razor demo Password POST guard
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Non-admin attacker POSTs victim's ID but supplies their own ITCode in the body.
        /// The old guard compared vm.Entity.ITCode (body-supplied) to the login ITCode, so
        /// this bypass worked. The fixed guard looks up the canonical ITCode from the DB by
        /// Entity.ID, so the attack is rejected.
        /// </summary>
        [TestMethod]
        public void Password_NonAdmin_WithOwnITCodeInBodyButVictimID_IsRejected()
        {
            // Arrange: create a victim user whose password we want to protect
            FrameworkUser victim = new FrameworkUser();
            string originalPasswordHash;
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                victim.ITCode = "victim";
                victim.Name = "Victim User";
                originalPasswordHash = PasswordHashHelper.HashPassword("original_password");
                victim.Password = originalPasswordHash;
                context.Set<FrameworkUser>().Add(victim);
                context.SaveChanges();
            }

            // The controller is created with usercode "user" (non-admin, Roles is null)
            // Attacker submits: victim's ID + their own ITCode in the body (the exploit vector)
            FrameworkUserVM vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser
            {
                ID       = victim.ID,
                ITCode   = "user",      // attacker's own ITCode in the body — old guard passed this
                Password = "hacked!"
            };

            // Act
            ActionResult rv = (ActionResult)_controller.Password(vm);

            // Assert: must return the no-privilege content result, not a success result
            Assert.IsInstanceOfType(rv, typeof(ContentResult),
                "Non-admin attacker supplying own ITCode in body but victim ID must be rejected.");

            // Verify the victim's password was NOT changed
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var data = context.Set<FrameworkUser>().First(x => x.ID == victim.ID);
                var hackResult = PasswordHashHelper.VerifyPassword(data.Password, "hacked!");
                Assert.AreEqual(PasswordVerifyResult.Failed, hackResult,
                    "Victim's password must remain unchanged after a rejected ownership bypass attempt.");
            }
        }

        /// <summary>
        /// Non-admin actor CAN reset their own password (self-service path must still work).
        /// </summary>
        [TestMethod]
        public void Password_NonAdmin_CanResetOwnPassword()
        {
            // Arrange: the actor's own user record in the DB (ITCode matches the controller login "user")
            FrameworkUser self = new FrameworkUser();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                self.ITCode  = "user";
                self.Name    = "Self User";
                self.Password = PasswordHashHelper.HashPassword("oldpass");
                context.Set<FrameworkUser>().Add(self);
                context.SaveChanges();
            }

            FrameworkUserVM vm = _controller.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser
            {
                ID       = self.ID,
                ITCode   = "user",
                Password = "newpass"
            };

            // Act
            ActionResult rv = (ActionResult)_controller.Password(vm);

            // Assert: self-service must NOT return the no-privilege content result
            Assert.IsNotInstanceOfType(rv, typeof(ContentResult),
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
        /// Admin actor CAN reset any user's password regardless of ITCode.
        /// </summary>
        [TestMethod]
        public void Password_Admin_CanResetAnotherUsersPassword()
        {
            // Arrange: create a target user
            FrameworkUser target = new FrameworkUser();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                target.ITCode   = "victim2";
                target.Name     = "Victim2 User";
                target.Password = PasswordHashHelper.HashPassword("original");
                context.Set<FrameworkUser>().Add(target);
                context.SaveChanges();
            }

            // Create a controller where the acting user is an admin
            var adminController = MockController.CreateController<WalkingTec.Mvvm.Mvc.Admin.Controllers.FrameworkUserController>(
                new Demo.DataContext(_seed, DBTypeEnum.Memory), "admin");
            adminController.Wtm.LoginUserInfo!.Roles = new System.Collections.Generic.List<SimpleRole>
            {
                new SimpleRole { RoleCode = "Admin", RoleName = "Administrator" }
            };

            FrameworkUserVM vm = adminController.Wtm.CreateVM<FrameworkUserVM>();
            vm.Entity = new FrameworkUser
            {
                ID       = target.ID,
                ITCode   = "victim2",
                Password = "admin_reset"
            };

            // Act
            ActionResult rv = (ActionResult)adminController.Password(vm);

            // Assert: admin must not be blocked
            Assert.IsNotInstanceOfType(rv, typeof(ContentResult),
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
