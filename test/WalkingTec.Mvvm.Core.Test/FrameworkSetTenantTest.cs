using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// /_Framework/SetTenant: only a signed-in caller can switch, the target is validated by WTMContext.SetCurrentTenant and a refused
    /// target changes nothing (no new cookie, no change of the user), while a valid switch behaves as before.
    /// </summary>
    [TestClass]
    public class FrameworkSetTenantTest
    {
        private static readonly List<FrameworkTenant> Tenants = new List<FrameworkTenant>
        {
            new FrameworkTenant { TCode = "t1", TName = "t1" },
            new FrameworkTenant { TCode = "t1-sub", TName = "t1-sub", TenantCode = "t1" },
            new FrameworkTenant { TCode = "t2", TName = "t2" },
        };

        private Mock<IAuthenticationService> _auth;
        private IStringLocalizer _previousLocalizer;

        [TestInitialize]
        public void Init()
        {
            _previousLocalizer = MvcProgram._localizer;
            var localizer = new Mock<IStringLocalizer>();
            localizer.Setup(x => x["Sys.NoPrivilege"]).Returns(new LocalizedString("Sys.NoPrivilege", "No privilege"));
            localizer.Setup(x => x["Sys.NeedLogin"]).Returns(new LocalizedString("Sys.NeedLogin", "Please sign in"));
            MvcProgram._localizer = localizer.Object;
        }

        [TestCleanup]
        public void Cleanup()
        {
            MvcProgram._localizer = _previousLocalizer;
        }

        private _FrameworkController CreateController(string userTenant, bool signedIn = true)
        {
            var wtm = MockWtmContext.CreateWtmContext(null, "user");
            wtm.GlobaInfo.SetTenantGetFunc(() => Tenants);
            Mock.Get(wtm.HttpContext.Request).Setup(x => x.Query).Returns(new QueryCollection());
            if (signedIn)
            {
                wtm.LoginUserInfo = new LoginUserInfo { ITCode = "user", TenantCode = userTenant };
            }
            else
            {
                wtm.LoginUserInfo = null;
            }

            _auth = new Mock<IAuthenticationService>();
            _auth.Setup(x => x.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties>())).Returns(Task.CompletedTask);
            var services = new ServiceCollection();
            services.AddSingleton(_auth.Object);
            var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

            var controller = new _FrameworkController(null) { Wtm = wtm };
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            return controller;
        }

        private void AssertNoNewCookie() => _auth.Verify(x => x.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties>()), Times.Never);

        private static string Script(IActionResult result) => result is FResult f ? f.ContentBuilder.ToString() : ((ContentResult)result).Content;

        [TestMethod]
        public async Task SignedInUser_SwitchingToAnAllowedTenant_ReloadsThePageAndRefreshesTheCookie()
        {
            var controller = CreateController(userTenant: "t1");

            var result = await controller.SetTenant("t1-sub");

            StringAssert.Contains(Script(result), "location.reload();");
            Assert.AreEqual("t1-sub", controller.Wtm.LoginUserInfo.CurrentTenant);
            _auth.Verify(x => x.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties>()), Times.Once);
        }

        [TestMethod]
        public async Task SignedInUser_SwitchingBackToTheOwnTenant_Works()
        {
            var controller = CreateController(userTenant: "t1");
            controller.Wtm.LoginUserInfo.CurrentTenant = "t1-sub";

            var result = await controller.SetTenant("t1");

            StringAssert.Contains(Script(result), "location.reload();");
            Assert.AreEqual("t1", controller.Wtm.LoginUserInfo.CurrentTenant);
        }

        [TestMethod]
        public async Task MainTenantUser_WithAnEmptyTenant_ClearsTheCurrentTenant()
        {
            var controller = CreateController(userTenant: null);
            controller.Wtm.LoginUserInfo.CurrentTenant = "t2";

            var result = await controller.SetTenant("");

            StringAssert.Contains(Script(result), "location.reload();");
            Assert.IsNull(controller.Wtm.LoginUserInfo.CurrentTenant);
        }

        [TestMethod]
        public async Task MainTenantUser_SwitchingToAnExistingTenant_Works()
        {
            var controller = CreateController(userTenant: null);

            var result = await controller.SetTenant("t2");

            StringAssert.Contains(Script(result), "location.reload();");
            Assert.AreEqual("t2", controller.Wtm.LoginUserInfo.CurrentTenant);
        }

        [TestMethod]
        public async Task MainTenantUser_SwitchingToATenantThatDoesNotExist_ChangesNothing()
        {
            var controller = CreateController(userTenant: null);
            controller.Wtm.LoginUserInfo.CurrentTenant = "t2";

            var result = await controller.SetTenant("nowhere");

            Assert.AreEqual("t2", controller.Wtm.LoginUserInfo.CurrentTenant);
            AssertNoNewCookie();
            Assert.IsFalse(Script(result).Contains("location.reload();"));
        }

        [TestMethod]
        public async Task MainTenantUser_SwitchingToADisabledTenant_ChangesNothing()
        {
            var controller = CreateController(userTenant: null);
            controller.Wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant> { new FrameworkTenant { TCode = "off", TName = "off", Enabled = false } });

            var result = await controller.SetTenant("off");

            Assert.IsNull(controller.Wtm.LoginUserInfo.CurrentTenant);
            AssertNoNewCookie();
            Assert.IsFalse(Script(result).Contains("location.reload();"));
        }

        [TestMethod]
        public async Task MainTenantUser_WhenTenantsAreNotEnabled_OnlyTheEmptyTargetIsAccepted()
        {
            var controller = CreateController(userTenant: null);
            controller.Wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>());

            StringAssert.Contains(Script(await controller.SetTenant("")), "location.reload();");
            var refused = await controller.SetTenant("anything");

            Assert.IsNull(controller.Wtm.LoginUserInfo.CurrentTenant);
            Assert.IsFalse(Script(refused).Contains("location.reload();"));
        }

        [TestMethod]
        public async Task SignedInUser_SwitchingToATenantOfSomeoneElse_ChangesNothing()
        {
            var controller = CreateController(userTenant: "t1");
            var before = controller.Wtm.LoginUserInfo.CurrentTenant;

            var result = await controller.SetTenant("t2");

            Assert.AreEqual(before, controller.Wtm.LoginUserInfo.CurrentTenant, "The refused target must not become the current tenant.");
            AssertNoNewCookie();
            Assert.IsFalse(Script(result).Contains("location.reload();"), "A refused switch must not look like a successful one.");
            StringAssert.Contains(Script(result), "No privilege");
        }

        [TestMethod]
        public async Task TenantUser_WithAnEmptyTenant_IsRefusedAndChangesNothing()
        {
            var controller = CreateController(userTenant: "t1");
            var before = controller.Wtm.LoginUserInfo.CurrentTenant;

            var result = await controller.SetTenant("");

            Assert.AreEqual(before, controller.Wtm.LoginUserInfo.CurrentTenant);
            AssertNoNewCookie();
            Assert.IsFalse(Script(result).Contains("location.reload();"));
        }

        [TestMethod]
        public async Task AnonymousCaller_GetsARefusalInsteadOfAnException()
        {
            var controller = CreateController(userTenant: null, signedIn: false);

            var result = await controller.SetTenant("t1");

            Assert.IsInstanceOfType<ContentResult>(result);
            Assert.AreEqual(StatusCodes.Status401Unauthorized, ((ContentResult)result).StatusCode);
            AssertNoNewCookie();
        }

        [TestMethod]
        public void TheEndpoint_IsNotPublic_AndStaysAGet()
        {
            var method = typeof(_FrameworkController).GetMethod(nameof(_FrameworkController.SetTenant));
            Assert.IsNotNull(method);
            Assert.IsFalse(method.IsDefined(typeof(PublicAttribute), false), "SetTenant must go through the framework's sign-in check.");
            Assert.IsFalse(method.IsDefined(typeof(AllowAnonymousAttribute), false));
            Assert.IsTrue(typeof(_FrameworkController).IsDefined(typeof(AllRightsAttribute), false), "Signed-in users need no menu privilege for it.");
            Assert.IsFalse(method.IsDefined(typeof(HttpPostAttribute), false), "Downstream pages call it with GET.");
            Assert.IsFalse(method.IsDefined(typeof(HttpPutAttribute), false));
            Assert.IsFalse(method.IsDefined(typeof(HttpDeleteAttribute), false));
        }
    }
}
