using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// UI state cookies (pagemode, tabmode, culture ...) get the Secure attribute only when the request
    /// itself is HTTPS. Plain HTTP requests keep receiving exactly the same cookies as before, and the
    /// cookies stay readable by script (no HttpOnly), which the LayUI front end relies on.
    /// </summary>
    [TestClass]
    public class UiCookieSecureTest
    {
        private class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
        {
            public bool IsReadOnly => false;
            public long? MaxRequestBodySize { get; set; }
        }

        private static DefaultHttpContext Create(string scheme, string path)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Scheme = scheme;
            ctx.Request.Path = path;
            ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(new FakeMaxRequestBodySizeFeature());
            return ctx;
        }

        private static string[] SetCookies(HttpContext ctx)
        {
            return ctx.Response.Headers["Set-Cookie"].ToArray();
        }

        private static async Task<DefaultHttpContext> RunMiddleware(string scheme, string path)
        {
            var ctx = Create(scheme, path);
            var middleware = new WtmMiddleware(_ => Task.CompletedTask);
            await middleware.InvokeAsync(ctx, new WTMContext(null));
            return ctx;
        }

        [TestMethod]
        public async Task Middleware_Https_PageAndTabModeCookiesAreSecure()
        {
            var ctx = await RunMiddleware("https", "/");

            var cookies = SetCookies(ctx);
            Assert.AreEqual(2, cookies.Length);
            Assert.IsTrue(cookies.Any(c => c.StartsWith("pagemode=")));
            Assert.IsTrue(cookies.Any(c => c.StartsWith("tabmode=")));
            foreach (var c in cookies)
            {
                StringAssert.Contains(c.ToLowerInvariant(), "; secure");
                StringAssert.Contains(c.ToLowerInvariant(), "path=/");
                Assert.IsFalse(c.ToLowerInvariant().Contains("httponly"), "LayUI reads these cookies from script");
            }
        }

        [TestMethod]
        public async Task Middleware_Http_CookiesHaveNoSecureAttribute()
        {
            var ctx = await RunMiddleware("http", "/");

            var cookies = SetCookies(ctx);
            Assert.AreEqual(2, cookies.Length);
            foreach (var c in cookies)
            {
                Assert.IsFalse(c.ToLowerInvariant().Contains("secure"));
                Assert.IsFalse(c.ToLowerInvariant().Contains("httponly"));
                StringAssert.Contains(c.ToLowerInvariant(), "path=/");
            }
        }

        [TestMethod]
        public async Task Middleware_OtherPath_SetsNoCookie()
        {
            var ctx = await RunMiddleware("https", "/Home/Index");

            Assert.AreEqual(0, SetCookies(ctx).Length);
        }

        private static _FrameworkController CreateFrameworkController(string scheme)
        {
            var controller = new _FrameworkController(null);
            controller.ControllerContext = new ControllerContext { HttpContext = Create(scheme, "/_Framework/SetLanguageForBlazor") };
            return controller;
        }

        [TestMethod]
        public void SetLanguageForBlazor_Https_CultureCookieIsSecureAndKeepsExpires()
        {
            var controller = CreateFrameworkController("https");

            controller.SetLanguageForBlazor("zh-TW", "/");

            var cookie = SetCookies(controller.HttpContext).Single().ToLowerInvariant();
            StringAssert.StartsWith(cookie, ".aspnetcore.culture=");
            StringAssert.Contains(cookie, "; secure");
            StringAssert.Contains(cookie, "expires=");
            Assert.IsFalse(cookie.Contains("httponly"));
        }

        [TestMethod]
        public void SetLanguageForBlazor_Http_CultureCookieHasNoSecureAttribute()
        {
            var controller = CreateFrameworkController("http");

            controller.SetLanguageForBlazor("zh-TW", "/");

            var cookie = SetCookies(controller.HttpContext).Single().ToLowerInvariant();
            StringAssert.StartsWith(cookie, ".aspnetcore.culture=");
            Assert.IsFalse(cookie.Contains("secure"));
            StringAssert.Contains(cookie, "expires=");
        }
    }
}
