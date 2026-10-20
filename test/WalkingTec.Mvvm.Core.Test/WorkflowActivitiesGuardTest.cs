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
    /// #208: /v1/activities requires a signed-in user whatever WorkflowOptions:RequireAuthentication says, and the internal
    /// ?inneruse=1 form cannot be used by an external caller to bypass that.
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

        private static DefaultHttpContext Create(string path, string query = "", FakeAuthService auth = null, bool userAuthenticated = false, string secret = null)
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
            if (secret != null)
            {
                ctx.Request.Headers[WorkflowActivitiesGuard.InternalSecretHeader] = secret;
            }
            return ctx;
        }

        private static string RealSecret()
        {
            return WorkflowActivitiesGuard.WithInternalSecret(null)[WorkflowActivitiesGuard.InternalSecretHeader];
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
        public async Task Middleware_InnerUseWithoutSecret_Returns401_AndDoesNotReachEngine()
        {
            var (ctx, next) = await Run(Create("/v1/activities", "?inneruse=1", new FakeAuthService()));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.IsFalse(next);
        }

        [TestMethod]
        public async Task Middleware_InnerUseWithWrongSecret_Returns401()
        {
            var (ctx, next) = await Run(Create("/v1/activities", "?inneruse=1", new FakeAuthService(), secret: "not-the-secret"));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.IsFalse(next);
        }

        [TestMethod]
        public async Task Middleware_InnerUseWithCorrectSecret_PassesToEngine()
        {
            var (ctx, next) = await Run(Create("/v1/activities", "?inneruse=1", new FakeAuthService(), secret: RealSecret()));
            Assert.AreNotEqual(401, ctx.Response.StatusCode);
            Assert.IsTrue(next);
        }

        [TestMethod]
        public async Task Middleware_InnerUse_SignedInWithoutSecret_PassesToEngine()
        {
            // credentials forwarded by the internal call (RequireAuthentication on) keep working across several instances
            var (ctx, next) = await Run(Create("/v1/activities", "?inneruse=1", userAuthenticated: true));
            Assert.IsTrue(next);
            Assert.AreNotEqual(401, ctx.Response.StatusCode);
        }

        [DataTestMethod]
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
            foreach (var path in new[] { "/v1/workflow-definitions", "/v1/activitiesx", "/home/v1/activities", "/" })
            {
                var (ctx, next) = await Run(Create(path, auth: new FakeAuthService()));
                Assert.AreNotEqual(401, ctx.Response.StatusCode, path);
                Assert.IsTrue(next, path);
            }
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

        [TestMethod]
        public async Task Allowed_SecretDoesNotHelpThePublicForm()
        {
            var ctx = Create("/v1/activities", auth: new FakeAuthService(), secret: RealSecret());
            Assert.IsFalse(await WorkflowActivitiesGuard.IsAllowedAsync(ctx));
        }

        // ---- secret ----

        [TestMethod]
        public void Secret_IsRandomLongAndStablePerProcess()
        {
            var a = RealSecret();
            Assert.IsTrue(a.Length >= 64);
            Assert.AreEqual(a, RealSecret());
            Assert.IsTrue(WorkflowActivitiesGuard.IsValidInternalSecret(a));
        }

        [TestMethod]
        public void Secret_RejectsMissingWrongAndTruncatedValues()
        {
            var a = RealSecret();
            Assert.IsFalse(WorkflowActivitiesGuard.IsValidInternalSecret(null));
            Assert.IsFalse(WorkflowActivitiesGuard.IsValidInternalSecret(""));
            Assert.IsFalse(WorkflowActivitiesGuard.IsValidInternalSecret("x"));
            Assert.IsFalse(WorkflowActivitiesGuard.IsValidInternalSecret(a.Substring(1)));
            Assert.IsFalse(WorkflowActivitiesGuard.IsValidInternalSecret(a + "0"));
            Assert.IsFalse(WorkflowActivitiesGuard.IsValidInternalSecret(a.ToLowerInvariant() == a ? a.ToUpperInvariant() : a.ToLowerInvariant()));
        }

        [TestMethod]
        public void Secret_ComparisonUsesFixedTimeEquals()
        {
            var src = System.IO.File.ReadAllText(FindSource("WorkflowActivitiesGuard.cs"));
            StringAssert.Contains(src, "CryptographicOperations.FixedTimeEquals");
        }

        [TestMethod]
        public void WithInternalSecret_KeepsCredentialHeaders_AndDoesNotMutateTheInput()
        {
            var input = new Dictionary<string, string> { ["Authorization"] = "Bearer abc", ["Cookie"] = "c=1" };
            var rv = WorkflowActivitiesGuard.WithInternalSecret(input);
            Assert.AreEqual("Bearer abc", rv["Authorization"]);
            Assert.AreEqual("c=1", rv["Cookie"]);
            Assert.IsTrue(rv.ContainsKey(WorkflowActivitiesGuard.InternalSecretHeader));
            Assert.AreEqual(2, input.Count);
            Assert.IsNotNull(WorkflowActivitiesGuard.WithInternalSecret(null)[WorkflowActivitiesGuard.InternalSecretHeader]);
        }

        [TestMethod]
        public void ExistingResolveSelfCall_IsUnchanged_SecretIsAddedByTheCaller()
        {
            var r = WorkflowApiSecurity.ResolveSelfCall(new Configs(), null, "http", "localhost", null, null);
            Assert.IsNull(r.Headers);
            Assert.IsFalse(r.Rejected);
        }

        private static string FindSource(string name)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var f = System.IO.Path.Combine(dir.FullName, "src", "WalkingTec.Mvvm.Mvc", "Helper", name);
                if (System.IO.File.Exists(f)) return f;
                dir = dir.Parent;
            }
            Assert.Inconclusive("source not found");
            return null;
        }
    }
}
