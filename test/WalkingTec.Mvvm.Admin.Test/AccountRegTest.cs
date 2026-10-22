using System;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// The anonymous demo Reg action must not reveal whether an ITCode exists (#209).
    /// </summary>
    [TestClass]
    public class AccountRegTest
    {
        private AccountController _controller;
        private string _seed;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<AccountController>(new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory), "anonymous");
        }

        private AccountController NewController()
        {
            return MockController.CreateApi<AccountController>(new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory), "anonymous");
        }

        private static SimpleReg Reg(string itcode, string name = "name", string password = "Passw0rd!")
        {
            return new SimpleReg { ITCode = itcode, Name = name, Password = password };
        }

        private static string Body(IActionResult rv, int expectedStatus)
        {
            var obj = rv as ObjectResult;
            Assert.IsNotNull(obj, "expected an ObjectResult but got " + rv?.GetType().Name);
            Assert.AreEqual(expectedStatus, obj.StatusCode);
            return JsonSerializer.Serialize(obj.Value);
        }

        [TestMethod]
        public void RegSuccessUnchanged()
        {
            var rv = _controller.Reg(Reg("newuser"));
            Assert.IsInstanceOfType(rv, typeof(OkResult));
            using (var dc = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(1, dc.Set<FrameworkUser>().Count(x => x.ITCode == "newuser"));
            }
        }

        [TestMethod]
        public void DuplicateItcodeGivesGenericError()
        {
            _controller.Reg(Reg("taken"));

            var rv = NewController().Reg(Reg("taken", "other", "Another1!"));
            var obj = rv as BadRequestObjectResult;
            Assert.IsNotNull(obj);
            var err = obj.Value as ErrorObj;
            Assert.IsNotNull(err);
            Assert.AreEqual(0, err.Form.Count, "no field specific error may be returned");
            Assert.AreEqual(1, err.Message.Count);
            Assert.IsFalse(Body(rv, 400).Contains("ITCode"));
            Assert.IsFalse(Body(rv, 400).Contains("uplicate"));
            Assert.AreEqual(1, new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory).Set<FrameworkUser>().Count(x => x.ITCode == "taken"));
        }

        [TestMethod]
        public void DuplicateItcodeIsCaseInsensitiveAndSameResponse()
        {
            _controller.Reg(Reg("taken"));
            var exact = Body(NewController().Reg(Reg("taken")), 400);
            var upper = Body(NewController().Reg(Reg("TAKEN", "x", "y")), 400);
            Assert.AreEqual(exact, upper);
        }

        [TestMethod]
        public void RegFailedResponseIsTheSameForEveryAccountRelatedFailure()
        {
            // RegFailed is the only response for account related failures; the duplicate path must produce exactly its body
            _controller.Reg(Reg("taken"));
            var dup = Body(NewController().Reg(Reg("taken")), 400);
            var reg = typeof(AccountController).GetMethod("RegFailed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(reg);
            var direct = Body((IActionResult)reg.Invoke(NewController(), null), 400);
            Assert.AreEqual(direct, dup);
        }

        [TestMethod]
        public void FormatErrorsKeepSpecificMessage()
        {
            var c = NewController();
            c.ModelState.AddModelError("Password", "Password is required");
            var rv = c.Reg(Reg("someone", password: null));
            var obj = rv as BadRequestObjectResult;
            Assert.IsNotNull(obj);
            var err = obj.Value as ErrorObj;
            Assert.AreEqual("Password is required", err.Form["Password"]);
            // nothing was created
            Assert.AreEqual(0, new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory).Set<FrameworkUser>().Count(x => x.ITCode == "someone"));
        }

        [TestMethod]
        public void FormatErrorIsReportedEvenWhenItcodeExists()
        {
            // format validation runs before the existence check, so it cannot be used to learn anything about accounts
            _controller.Reg(Reg("taken"));
            var c = NewController();
            c.ModelState.AddModelError("Password", "Password is required");
            var withExisting = Body(c.Reg(Reg("taken", password: null)), 400);
            var c2 = NewController();
            c2.ModelState.AddModelError("Password", "Password is required");
            var withNew = Body(c2.Reg(Reg("brandnew", password: null)), 400);
            Assert.AreEqual(withNew, withExisting);
        }

        [TestMethod]
        public void RegIgnoresAPhotoThatTheCallerSupplies()
        {
            // the photo of a user can be shown without a reference, so an anonymous caller must not be able to name any file as one
            var someonesFile = Guid.NewGuid();
            var rv = _controller.Reg(new SimpleReg { ITCode = "withphoto", Name = "name", Password = "Passw0rd!", PhotoId = someonesFile });
            Assert.IsInstanceOfType(rv, typeof(OkResult));
            using (var dc = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                var user = dc.Set<FrameworkUser>().Single(x => x.ITCode == "withphoto");
                Assert.IsNull(user.PhotoId);
            }
        }
    }
}
