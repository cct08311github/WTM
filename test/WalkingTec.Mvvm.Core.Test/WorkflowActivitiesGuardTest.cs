using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// #208 / #216 / #225: /v1/activities and /v1/designer/* require a signed-in user whatever WorkflowOptions:RequireAuthentication says. There is no internal
    /// ?inneruse=1 call any more, so that form is guarded exactly like the public one.
    /// </summary>
    [TestClass]
    public class WorkflowActivitiesGuardTest
    {
        private class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
        {
            public bool IsReadOnly => false;
            public long? MaxRequestBodySize { get; set; }
        }

        // authenticates only the schemes listed in `signedInSchemes`
        private class FakeAuthService : IAuthenticationService
        {
            private readonly HashSet<string> _ok;
            public List<string> Asked { get; } = new List<string>();
            public FakeAuthService(params string[] okSchemes) { _ok = new HashSet<string>(okSchemes); }
            public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string scheme)
            {
                Asked.Add(scheme);
                if (scheme != null && _ok.Contains(scheme))
                {
                    var p = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "u") }, scheme));
                    return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(p, scheme)));
                }
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            public Task ChallengeAsync(HttpContext context, string scheme, AuthenticationProperties properties) => Task.CompletedTask;
            public Task ForbidAsync(HttpContext context, string scheme, AuthenticationProperties properties) => Task.CompletedTask;
            public Task SignInAsync(HttpContext context, string scheme, ClaimsPrincipal principal, AuthenticationProperties properties) => Task.CompletedTask;
            public Task SignOutAsync(HttpContext context, string scheme, AuthenticationProperties properties) => Task.CompletedTask;
        }

        private static DefaultHttpContext Create(string path, string query = "", FakeAuthService auth = null, bool userAuthenticated = false)
        {
            var services = new ServiceCollection();
            if (auth != null)
            {
                services.AddSingleton<IAuthenticationService>(auth);
            }
            var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("localhost");
            ctx.Request.Path = path;
            ctx.Request.QueryString = new QueryString(query);
            ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(new FakeMaxRequestBodySizeFeature());
            if (userAuthenticated)
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "u") }, "cookie"));
            }
            return ctx;
        }

        private static async Task<(DefaultHttpContext Ctx, bool NextCalled)> Run(DefaultHttpContext ctx)
        {
            var called = false;
            var middleware = new WtmMiddleware(_ => { called = true; return Task.CompletedTask; });
            await middleware.InvokeAsync(ctx, new WTMContext(null));
            return (ctx, called);
        }

        // ---- middleware: anonymous callers get 401 and never reach the internal call / the engine ----

        [TestMethod]
        public async Task Middleware_AnonymousPublicRequest_Returns401_WithoutInternalCall()
        {
            // no IAuthenticationService at all: fails closed. CallAPI would throw on this bare WTMContext, so reaching it fails the test.
            var (ctx, next) = await Run(Create("/v1/activities"));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.IsFalse(next);
        }

        [TestMethod]
        public async Task Middleware_AnonymousPublicRequest_WithAuthService_Returns401()
        {
            var auth = new FakeAuthService();
            var (ctx, next) = await Run(Create("/v1/activities", auth: auth));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.IsFalse(next);
            CollectionAssert.AreEquivalent(new[] { CookieAuthenticationDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme }, auth.Asked);
        }

        [TestMethod]
        public async Task Middleware_AnonymousInnerUse_Returns401_AndDoesNotReachEngine()
        {
            // #216: there is no internal call any more, so ?inneruse=1 gets no special treatment: anonymous callers get 401 like on the public form
            var (ctx, next) = await Run(Create("/v1/activities", "?inneruse=1", new FakeAuthService()));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.IsFalse(next);
        }

        [TestMethod]
        public async Task Middleware_InnerUseWithAnOldSecretHeader_StillReturns401()
        {
            var ctx = Create("/v1/activities", "?inneruse=1", new FakeAuthService());
            ctx.Request.Headers["X-Wtm-Internal-Call"] = "anything";
            var (res, next) = await Run(ctx);
            Assert.AreEqual(401, res.Response.StatusCode);
            Assert.IsFalse(next);
        }

        [TestMethod]
        public async Task Middleware_InnerUse_SignedIn_WithoutEngine_FallsThrough()
        {
            var (ctx, next) = await Run(Create("/v1/activities", "?inneruse=1", userAuthenticated: true));
            Assert.IsTrue(next);
            Assert.AreNotEqual(401, ctx.Response.StatusCode);
        }

        [DataTestMethod]
        [DataRow("/v1.0/activities")]
        [DataRow("/v01/activities")]
        [DataRow("/V1.0/Activities")]
        [DataRow("/v1.0/activities/")]
        [DataRow("/v2/activities")]
        [DataRow("/vX/activities/Elsa.Activities.Console.WriteLine")]
        [DataRow("/v/activities")]
        [DataRow("/v1/activities/")]
        [DataRow("/V1/Activities")]
        [DataRow("/v1/activities/Elsa.Activities.Console.WriteLine")]
        public async Task Middleware_VariantsOfThePath_AreGuardedToo(string path)
        {
            var (ctx, next) = await Run(Create(path, auth: new FakeAuthService()));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.IsFalse(next);
        }

        [DataTestMethod]
        [DataRow("/v1.0/activities", "?inneruse=1")]
        [DataRow("/v1/activities/", "?inneruse=1")]
        [DataRow("/v1/activities", "?inneruse=1&x=1")]
        [DataRow("/v1/activities", "?INNERUSE=1")]
        public async Task Middleware_InnerUseLookalikes_AreNotTreatedAsInternal(string path, string query)
        {
            var (ctx, next) = await Run(Create(path, query, new FakeAuthService()));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.IsFalse(next);
        }

        [TestMethod]
        public async Task Middleware_OtherPaths_AreNotAffected()
        {
            foreach (var path in new[] { "/v1/workflow-definitions", "/v1/activitiesx", "/home/v1/activities", "/x1/activities", "/v1/x/activities", "/" })
            {
                var (ctx, next) = await Run(Create(path, auth: new FakeAuthService()));
                Assert.AreNotEqual(401, ctx.Response.StatusCode, path);
                Assert.IsTrue(next, path);
            }
        }

        // ---- #225: /v1/designer/* requires a signed-in user too ----

        [DataTestMethod]
        [DataRow("/v1/designer/runtime-select-list")]
        [DataRow("/v1.0/designer/runtime-select-list")]
        [DataRow("/v01/designer/runtime-select-list")]
        [DataRow("/V1/Designer/Runtime-Select-List")]
        [DataRow("/v1/designer/runtime-select-list/")]
        [DataRow("/v1/designer")]
        [DataRow("/v1/designer/")]
        [DataRow("/v1/designer/x/y")]
        [DataRow("//v1/designer/runtime-select-list")]
        [DataRow("///v1.0/Designer/")]
        [DataRow("/vX/designer/runtime-select-list")]
        [DataRow("/v/designer/runtime-select-list")]
        public async Task Middleware_DesignerPaths_AnonymousGets401(string path)
        {
            foreach (var method in new[] { "POST", "GET", "PUT", "DELETE", "OPTIONS" })
            {
                var ctx = Create(path, auth: new FakeAuthService());
                ctx.Request.Method = method;
                var (res, next) = await Run(ctx);
                Assert.AreEqual(401, res.Response.StatusCode, path + " " + method);
                Assert.IsFalse(next, path + " " + method);
            }
        }

        [TestMethod]
        public async Task Middleware_DesignerPath_WithoutAuthService_FailsClosed()
        {
            var (ctx, next) = await Run(Create("/v1/designer/runtime-select-list"));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.IsFalse(next);
        }

        [DataTestMethod]
        [DataRow("/v1/designer/runtime-select-list")]
        [DataRow("/v1.0/Designer/runtime-select-list/")]
        [DataRow("//v1/designer/x")]
        public async Task Middleware_DesignerPaths_SignedIn_GoToNext(string path)
        {
            var ctx = Create(path, userAuthenticated: true);
            ctx.Request.Method = "POST";
            var (res, next) = await Run(ctx);
            Assert.IsTrue(next, path);
            Assert.AreNotEqual(401, res.Response.StatusCode, path);
        }

        [TestMethod]
        public async Task Middleware_DesignerPath_SignedInByJwt_GoesToNextAndSetsUser()
        {
            var ctx = Create("/v1/designer/runtime-select-list", auth: new FakeAuthService(JwtBearerDefaults.AuthenticationScheme));
            ctx.Request.Method = "POST";
            var (res, next) = await Run(ctx);
            Assert.IsTrue(next);
            Assert.IsTrue(res.User.Identity.IsAuthenticated);
        }

        [TestMethod]
        public async Task Middleware_NonDesignerPaths_AreNotAffectedByDesignerGuard()
        {
            foreach (var path in new[] { "/v1/designers", "/v1/designer-x", "/v1/designerx/y", "/home/v1/designer", "/x1/designer", "/v1/x/designer", "/v1/workflow-definitions", "/v1/features", "/v1/version", "/designer" })
            {
                var (ctx, next) = await Run(Create(path, auth: new FakeAuthService()));
                Assert.AreNotEqual(401, ctx.Response.StatusCode, path);
                Assert.IsTrue(next, path);
            }
        }

        [DataTestMethod]
        [DataRow("/v1/designer/runtime-select-list", true)]
        [DataRow("/V2.0/DESIGNER", true)]
        [DataRow("//v1/designer/", true)]
        [DataRow("/v1/activities", false)]
        [DataRow("/v1/designers", false)]
        [DataRow("/designer", false)]
        public void IsDesignerRequest_MatchesOnlyDesignerPaths(string path, bool expected)
        {
            Assert.AreEqual(expected, WorkflowActivitiesGuard.IsDesignerRequest(Create(path).Request));
        }

        // ---- sign-in detection ----

        [TestMethod]
        public async Task SignedIn_ByHttpContextUser()
        {
            Assert.IsTrue(await WorkflowActivitiesGuard.IsSignedInAsync(Create("/v1/activities", userAuthenticated: true)));
        }

        [TestMethod]
        public async Task SignedIn_ByCookieScheme_WhenUserNotYetPopulated()
        {
            Assert.IsTrue(await WorkflowActivitiesGuard.IsSignedInAsync(Create("/v1/activities", auth: new FakeAuthService(CookieAuthenticationDefaults.AuthenticationScheme))));
        }

        [TestMethod]
        public async Task SignedIn_ByJwtBearer_WhichIsNotTheDefaultScheme()
        {
            Assert.IsTrue(await WorkflowActivitiesGuard.IsSignedInAsync(Create("/v1/activities", auth: new FakeAuthService(JwtBearerDefaults.AuthenticationScheme))));
        }

        [TestMethod]
        public async Task NotSignedIn_WhenNothingAuthenticates_OrAuthThrows()
        {
            Assert.IsFalse(await WorkflowActivitiesGuard.IsSignedInAsync(Create("/v1/activities", auth: new FakeAuthService())));
            Assert.IsFalse(await WorkflowActivitiesGuard.IsSignedInAsync(Create("/v1/activities")));
            var anonymous = Create("/v1/activities");
            anonymous.User = new ClaimsPrincipal(new ClaimsIdentity());
            Assert.IsFalse(await WorkflowActivitiesGuard.IsSignedInAsync(anonymous));
        }

        // ---- one matcher for the guard and the internal-call branch ----

        [DataTestMethod]
        [DataRow("/v1/activities", "", true)]
        [DataRow("/v1.0/activities", "", true)]
        [DataRow("/V01/Activities/", "", true)]
        [DataRow("/v1.0/activities", "?inneruse=1", true)]
        [DataRow("/v1/activities", "?inneruse=1", true)]
        [DataRow("/v1/activities", "?inneruse=1&x=1", true)]
        [DataRow("//v1/activities", "", true)]
        [DataRow("///v1/activities/", "?inneruse=1", true)]
        [DataRow("/v1/activities/Elsa.X", "", false)]
        [DataRow("/v1/workflow-definitions", "", false)]
        public void Matchers_AgreeOnVersionCaseAndTrailingSlash(string path, string query, bool isList)
        {
            var ctx = Create(path, query);
            Assert.AreEqual(isList, WorkflowActivitiesGuard.IsActivitiesListRequest(ctx.Request));
            Assert.AreEqual(path.IndexOf("activities", StringComparison.OrdinalIgnoreCase) >= 0 && path.StartsWith("/v1/workflow") == false,
                WorkflowActivitiesGuard.IsActivitiesRequest(ctx.Request));
        }

        [TestMethod]
        public async Task Middleware_LeadingDoubleSlash_IsGuardedToo()
        {
            foreach (var path in new[] { "//v1/activities", "///v1.0/Activities/", "//v1/activities/Elsa.X" })
            {
                var (ctx, next) = await Run(Create(path, auth: new FakeAuthService()));
                Assert.AreEqual(401, ctx.Response.StatusCode, path);
                Assert.IsFalse(next, path);
            }
        }

        [TestMethod]
        public async Task Middleware_AnonymousNonGet_StillGets401()
        {
            foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH", "OPTIONS" })
            {
                var ctx = Create("/v1/activities", auth: new FakeAuthService());
                ctx.Request.Method = method;
                var (res, next) = await Run(ctx);
                Assert.AreEqual(401, res.Response.StatusCode, method);
                Assert.IsFalse(next, method);
            }
        }

        [TestMethod]
        public async Task SignedIn_ByJwtOnly_SetsHttpContextUser()
        {
            var ctx = Create("/v1/activities", auth: new FakeAuthService(JwtBearerDefaults.AuthenticationScheme));
            Assert.IsFalse(ctx.User.Identity.IsAuthenticated);
            Assert.IsTrue(await WorkflowActivitiesGuard.IsSignedInAsync(ctx));
            Assert.IsTrue(ctx.User.Identity.IsAuthenticated, "WTMContext.LoginUserInfo reads HttpContext.User");
            Assert.AreEqual(JwtBearerDefaults.AuthenticationScheme, ctx.User.Identity.AuthenticationType);
        }
    }
}
