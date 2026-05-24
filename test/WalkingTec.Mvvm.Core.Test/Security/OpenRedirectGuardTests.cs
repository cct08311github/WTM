using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;
using DUWENINK.Captcha;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Verifies open-redirect protection in _FrameworkController.SetLanguageForBlazor
    /// and _FrameworkController.RemoteEntry (CodeQL alerts #202 / #203).
    ///
    /// Both actions now call Url.IsLocalUrl(redirect) before Redirect(), which
    /// CodeQL recognises as a sanitizer for cs/web/unvalidated-url-redirection.
    /// </summary>
    [TestClass]
    public class OpenRedirectGuardTests
    {
        // ─── helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a _FrameworkController with a real IUrlHelper wired to a
        /// DefaultHttpContext so that Url.IsLocalUrl() produces the correct result.
        /// </summary>
        private static _FrameworkController CreateController()
        {
            var mockSecurityCode = new Mock<ISecurityCodeHelper>();
            var controller = new _FrameworkController(mockSecurityCode.Object);
            controller.Wtm = MockWtmContext.CreateWtmContext();

            // Wire an HttpContext so that Url.IsLocalUrl() has the host info it needs.
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Host = new HostString("example.com");
            httpContext.Request.Scheme = "https";

            var urlHelperFactory = new Mock<IUrlHelperFactory>();
            var serviceProvider = new Mock<System.IServiceProvider>();
            serviceProvider
                .Setup(sp => sp.GetService(typeof(IUrlHelperFactory)))
                .Returns(urlHelperFactory.Object);
            httpContext.RequestServices = serviceProvider.Object;

            // Use a real UrlHelper so that IsLocalUrl() behaves identically to production.
            var actionContext = new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
            urlHelperFactory
                .Setup(f => f.GetUrlHelper(It.IsAny<ActionContext>()))
                .Returns(new UrlHelper(actionContext));

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            controller.Url = new UrlHelper(actionContext);

            return controller;
        }

        // ─── SetLanguageForBlazor ─────────────────────────────────────────────

        [TestMethod]
        public void SetLanguageForBlazor_LocalUrl_RedirectsToUrl()
        {
            var controller = CreateController();

            var result = controller.SetLanguageForBlazor("en-US", "/admin/dashboard") as RedirectResult;

            result.Should().NotBeNull();
            result!.Url.Should().Be("/admin/dashboard");
        }

        [TestMethod]
        public void SetLanguageForBlazor_LocalUrlWithQuery_RedirectsToUrl()
        {
            var controller = CreateController();

            var result = controller.SetLanguageForBlazor("en-US", "/admin/dashboard?tab=1") as RedirectResult;

            result.Should().NotBeNull();
            result!.Url.Should().Be("/admin/dashboard?tab=1");
        }

        [TestMethod]
        public void SetLanguageForBlazor_AbsoluteExternalUrl_RedirectsToRoot()
        {
            var controller = CreateController();

            var result = controller.SetLanguageForBlazor("en-US", "https://attacker.com/evil") as RedirectResult;

            result.Should().NotBeNull();
            result!.Url.Should().Be("/", "external absolute URLs must be blocked");
        }

        [TestMethod]
        public void SetLanguageForBlazor_ProtocolRelativeAttack_RedirectsToRoot()
        {
            var controller = CreateController();

            var result = controller.SetLanguageForBlazor("en-US", "//attacker.com") as RedirectResult;

            result.Should().NotBeNull();
            result!.Url.Should().Be("/", "protocol-relative URLs must be blocked");
        }

        [TestMethod]
        public void SetLanguageForBlazor_NullRedirect_RedirectsToRoot()
        {
            var controller = CreateController();

            var result = controller.SetLanguageForBlazor("en-US", null!) as RedirectResult;

            result.Should().NotBeNull();
            result!.Url.Should().Be("/", "null redirect must fall back to root");
        }

        [TestMethod]
        public void SetLanguageForBlazor_EmptyRedirect_RedirectsToRoot()
        {
            var controller = CreateController();

            var result = controller.SetLanguageForBlazor("en-US", "") as RedirectResult;

            result.Should().NotBeNull();
            result!.Url.Should().Be("/", "empty redirect must fall back to root");
        }

        // ─── RemoteEntry ──────────────────────────────────────────────────────
        // RemoteEntry calls SignInAsync only when Wtm?.LoginUserInfo != null.
        // Setting Wtm = null skips the sign-in block (safe via null-propagation)
        // so these tests exercise only the redirect guard, not authentication.

        [TestMethod]
        public async Task RemoteEntry_LocalUrl_RedirectsToUrl()
        {
            var controller = CreateController();
            controller.Wtm = null!; // Wtm?.LoginUserInfo == null → skip SignInAsync

            var result = await controller.RemoteEntry("/dashboard") as RedirectResult;

            result.Should().NotBeNull();
            result!.Url.Should().Be("/dashboard");
        }

        [TestMethod]
        public async Task RemoteEntry_AbsoluteExternalUrl_RedirectsToRoot()
        {
            var controller = CreateController();
            controller.Wtm = null!; // Wtm?.LoginUserInfo == null → skip SignInAsync

            var result = await controller.RemoteEntry("https://attacker.com") as RedirectResult;

            result.Should().NotBeNull();
            result!.Url.Should().Be("/", "external absolute URLs must be blocked");
        }

        [TestMethod]
        public async Task RemoteEntry_ProtocolRelativeAttack_RedirectsToRoot()
        {
            var controller = CreateController();
            controller.Wtm = null!; // Wtm?.LoginUserInfo == null → skip SignInAsync

            var result = await controller.RemoteEntry("//attacker.com/steal") as RedirectResult;

            result.Should().NotBeNull();
            result!.Url.Should().Be("/", "protocol-relative URLs must be blocked");
        }

        [TestMethod]
        public async Task RemoteEntry_NullRedirect_RedirectsToRoot()
        {
            var controller = CreateController();
            controller.Wtm = null!; // Wtm?.LoginUserInfo == null → skip SignInAsync

            var result = await controller.RemoteEntry(null!) as RedirectResult;

            result.Should().NotBeNull();
            result!.Url.Should().Be("/", "null redirect must fall back to root");
        }
    }
}
