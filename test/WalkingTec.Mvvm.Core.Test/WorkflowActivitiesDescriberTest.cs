using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Elsa;
using Elsa.Server.Api.Endpoints.Activities;
using Elsa.Server.Api.Services;
using Elsa.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json.Linq;
using WalkingTec.Mvvm.Core.WorkFlow;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// #216: WtmMiddleware answers /v1/activities in-process. The text must be the one Elsa's own endpoint
    /// (Elsa.Server.Api 2.17.0 Activities.List, executed through the real MVC Newtonsoft JSON executor) writes.
    /// </summary>
    [TestClass]
    public class WorkflowActivitiesDescriberTest
    {
        private class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
        {
            public bool IsReadOnly => false;
            public long? MaxRequestBodySize { get; set; }
        }

        // authenticates every scheme asked for; "signed in" is decided by the test through HttpContext.Items["anon"]
        private class FakeAuthService : IAuthenticationService
        {
            public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string scheme)
            {
                if (context.Items.ContainsKey("anon") || scheme == null)
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }
                var p = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "u") }, scheme));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(p, scheme)));
            }
            public Task ChallengeAsync(HttpContext context, string scheme, AuthenticationProperties properties) { context.Response.StatusCode = 401; return Task.CompletedTask; }
            public Task ForbidAsync(HttpContext context, string scheme, AuthenticationProperties properties) { context.Response.StatusCode = 403; return Task.CompletedTask; }
            public Task SignInAsync(HttpContext context, string scheme, ClaimsPrincipal principal, AuthenticationProperties properties) => Task.CompletedTask;
            public Task SignOutAsync(HttpContext context, string scheme, AuthenticationProperties properties) => Task.CompletedTask;
        }

        private static ServiceProvider BuildServices()
        {
            var seed = Guid.NewGuid().ToString();
            using (var dc = new DataContext(seed, DBTypeEnum.Memory))
            {
                dc.Set<FrameworkRole>().Add(new FrameworkRole { ID = Guid.NewGuid(), RoleCode = "r1", RoleName = "测试角色一" });
                dc.Set<FrameworkRole>().Add(new FrameworkRole { ID = Guid.NewGuid(), RoleCode = "r2", RoleName = "测试角色二" });
                dc.SaveChanges();
            }
            var wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "user");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(wtm);
            services.AddElsa(elsa => elsa
                .AddConsoleActivities()
                .AddActivity<WtmApproveActivity>());
            services.AddElsaApiEndpoints();
            services.AddMvc().AddNewtonsoftJson();
            // what AddWtmWorkflow / the demo register for the RequireAuthentication policy
            services.AddAuthorization();
            services.AddHttpContextAccessor();
            services.AddSingleton<IAuthorizationHandler, WorkflowAdminRequirementHandler>();
            services.AddSingleton<IAuthenticationService>(new FakeAuthService());
            return services.BuildServiceProvider();
        }

        // what GET /v1/activities writes: Elsa's controller, its JsonResult executed by the MVC Newtonsoft executor
        private static async Task<string> ElsaEndpointOutput(IServiceProvider sp)
        {
            var controller = new List(sp.GetRequiredService<IActivityTypeService>(), sp.GetRequiredService<IEndpointContentSerializerSettingsProvider>());
            var result = await controller.Handle(CancellationToken.None);
            var http = new DefaultHttpContext { RequestServices = sp };
            using var body = new MemoryStream();
            http.Response.Body = body;
            await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
            return Encoding.UTF8.GetString(body.ToArray());
        }

        [TestMethod]
        public async Task Describe_IsIdenticalToTheElsaEndpointOutput()
        {
            using var sp = BuildServices();
            var elsa = await ElsaEndpointOutput(sp);
            var mine = await WorkflowActivitiesDescriber.DescribeAsync(sp);

            Assert.IsTrue(elsa.Length > 1000, "the Elsa endpoint output should not be trivial");
            Assert.AreEqual(elsa, mine, "string equality, not just JSON equivalence");
        }

        [TestMethod]
        public async Task Describe_HasExpectedShape_AndApproveOptionsFromTheLocalDatabase()
        {
            using var sp = BuildServices();
            var arr = JArray.Parse(await WorkflowActivitiesDescriber.DescribeAsync(sp));

            // camelCase property names, enums as strings, nulls omitted
            var approve = arr.Single(x => (string)x["type"] == nameof(WtmApproveActivity));
            Assert.AreEqual("审批", (string)approve["displayName"]);
            var inputNames = approve["inputProperties"].Select(x => (string)x["name"]).ToList();
            CollectionAssert.Contains(inputNames, "ApproveRoles");
            // not browsable properties are removed, as the Elsa endpoint does
            CollectionAssert.DoesNotContain(inputNames, "Remark");
            CollectionAssert.DoesNotContain(inputNames, "ApprovedBy");
            CollectionAssert.DoesNotContain(inputNames, "ApproveUsersFullText");
            // GetOptions: the roles of the local database
            var roles = approve["inputProperties"].Single(x => (string)x["name"] == "ApproveRoles");
            var texts = roles["options"].Select(x => (string)x["text"]).ToList();
            CollectionAssert.AreEquivalent(new[] { "测试角色一", "测试角色二" }, texts);
            Assert.IsTrue(arr.Any(x => (string)x["type"] == "WriteLine"), "console activities are listed too");
        }

        [TestMethod]
        public async Task Describe_WithoutTheEngine_ReturnsNull()
        {
            Assert.IsNull(await WorkflowActivitiesDescriber.DescribeAsync(new ServiceCollection().BuildServiceProvider()));
            Assert.IsNull(await WorkflowActivitiesDescriber.DescribeAsync(null));
        }

        // ---- middleware ----

        private static WTMContext NewWtm(Configs configs)
        {
            var monitor = new Moq.Mock<Microsoft.Extensions.Options.IOptionsMonitor<Configs>>();
            monitor.Setup(x => x.CurrentValue).Returns(configs ?? new Configs());
            return new WTMContext(monitor.Object);
        }

        private static Configs Cfg(bool requireAuth, bool requireAdmin)
        {
            var c = new Configs();
            c.WorkflowOptions.RequireAuthentication = requireAuth;
            c.WorkflowOptions.RequireAdminRole = requireAdmin;
            return c;
        }

        // the user the admin requirement handler sees: it reads WTMContext.LoginUserInfo of the request services
        private static void SetRoles(IServiceProvider sp, params string[] roleCodes)
        {
            var wtm = sp.GetRequiredService<WTMContext>();
            wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "user",
                Roles = roleCodes.Select(x => new WalkingTec.Mvvm.Core.Support.Json.SimpleRole { ID = Guid.NewGuid(), RoleCode = x, RoleName = x }).ToList()
            };
        }

        private static async Task<(DefaultHttpContext Ctx, bool NextCalled, string Body)> Run(IServiceProvider sp, string path, string query, bool signedIn, string culture = "en-US", string method = "GET", Configs configs = null)
        {
            var ctx = new DefaultHttpContext { RequestServices = sp };
            ctx.Request.Method = method;
            if (signedIn == false)
            {
                ctx.Items["anon"] = true;
            }
            else
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "u") }, "cookie"));
            }
                        var accessor = sp.GetService<IHttpContextAccessor>();
            if (accessor != null)
            {
                accessor.HttpContext = ctx;
            }
            ctx.Request.Scheme = "https";
            ctx.Request.Host = new HostString("attacker.example");
            ctx.Request.Path = path;
            ctx.Request.QueryString = new QueryString(query);
            ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(new FakeMaxRequestBodySizeFeature());
            using var body = new MemoryStream();
            ctx.Response.Body = body;
            var called = false;
            var middleware = new WtmMiddleware(_ => { called = true; return Task.CompletedTask; });
            var old = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(culture);
            try
            {
                // a bare WTMContext: CallAPI on it would throw, so a passing test proves no HTTP call is attempted
                await middleware.InvokeAsync(ctx, NewWtm(configs));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = old;
            }
            return (ctx, called, Encoding.UTF8.GetString(body.ToArray()));
        }

        [TestMethod]
        public async Task Middleware_SignedIn_AnswersInProcess_WithTheElsaOutput_AndDoesNotCallTheEngineOrAnyHttp()
        {
            using var sp = BuildServices();
            var expected = await ElsaEndpointOutput(sp);
            // the Host header is attacker controlled; the answer must not depend on it and nothing may be sent there
            var (ctx, next, body) = await Run(sp, "/v1/activities", "", signedIn: true);
            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.IsFalse(next);
            Assert.AreEqual(expected, body);
        }

        [TestMethod]
        public async Task Middleware_SignedIn_OtherVersionSegmentAndTrailingSlash_AreAnsweredToo()
        {
            using var sp = BuildServices();
            var expected = await ElsaEndpointOutput(sp);
            foreach (var path in new[] { "/v1.0/activities", "/V1/Activities/" })
            {
                var (ctx, next, body) = await Run(sp, path, "", signedIn: true);
                Assert.IsFalse(next, path);
                Assert.AreEqual(expected, body, path);
            }
        }

        [TestMethod]
        public async Task Middleware_Anonymous_Gets401_ForAllFormsIncludingInnerUse()
        {
            using var sp = BuildServices();
            foreach (var (path, query) in new[] { ("/v1/activities", ""), ("/v1.0/activities", ""), ("/v1/activities", "?inneruse=1"), ("/v1/activities/", "?inneruse=1") })
            {
                var (ctx, next, body) = await Run(sp, path, query, signedIn: false);
                Assert.AreEqual(401, ctx.Response.StatusCode, path + query);
                Assert.IsFalse(next, path + query);
                Assert.AreEqual("", body, path + query);
            }
        }

        [TestMethod]
        public async Task Middleware_ChineseCulture_AppliesTheTranslation_SameAsBefore()
        {
            using var sp = BuildServices();
            var elsa = await ElsaEndpointOutput(sp);
            var (_, _, en) = await Run(sp, "/v1/activities", "", signedIn: true, culture: "en-US");
            var (_, _, zh) = await Run(sp, "/v1/activities", "", signedIn: true, culture: "zh-CN");
            Assert.AreEqual(elsa, en);
            // the translation table maps the listed Elsa strings; the text must still be the same JSON
            Assert.AreEqual(JToken.Parse(elsa).Count(), JToken.Parse(zh).Count());
        }

        [TestMethod]
        public async Task Middleware_SignedIn_WithoutTheEngine_FallsThroughToTheNextMiddleware()
        {
            var (ctx, next, _) = await Run(new ServiceCollection().BuildServiceProvider(), "/v1/activities", "", signedIn: true);
            Assert.IsTrue(next);
            Assert.AreNotEqual(401, ctx.Response.StatusCode);
        }

        // ---- #216 review: RequireAuthentication / RequireAdminRole still apply ----

        [TestMethod]
        public async Task Authorization_RequireAdminRole_NonAdmin_Gets403_AdminGets200()
        {
            using var sp = BuildServices();
            var expected = await ElsaEndpointOutput(sp);
            var cfg = Cfg(true, true);

            SetRoles(sp, "002");
            var (denied, nextDenied, bodyDenied) = await Run(sp, "/v1/activities", "", signedIn: true, configs: cfg);
            Assert.AreEqual(403, denied.Response.StatusCode);
            Assert.IsFalse(nextDenied);
            Assert.AreEqual("", bodyDenied);

            SetRoles(sp, WorkflowApiSecurity.AdminRoleCode);
            var (ok, nextOk, body) = await Run(sp, "/v1/activities", "", signedIn: true, configs: cfg);
            Assert.AreEqual(200, ok.Response.StatusCode);
            Assert.IsFalse(nextOk);
            Assert.AreEqual(expected, body);
        }

        [TestMethod]
        public async Task Authorization_RequireAdminRole_AlsoAppliesToInnerUseAndOtherVersionForms()
        {
            using var sp = BuildServices();
            SetRoles(sp, "002");
            foreach (var (path, query) in new[] { ("/v1/activities", "?inneruse=1"), ("/v1.0/activities", ""), ("//v1/activities/", "") })
            {
                var (ctx, next, body) = await Run(sp, path, query, signedIn: true, configs: Cfg(true, true));
                Assert.AreEqual(403, ctx.Response.StatusCode, path + query);
                Assert.IsFalse(next, path + query);
                Assert.AreEqual("", body, path + query);
            }
        }

        [TestMethod]
        public async Task Authorization_RequireAuthenticationOnly_AnySignedInUserGets200()
        {
            using var sp = BuildServices();
            var expected = await ElsaEndpointOutput(sp);
            SetRoles(sp, "002");
            var (ctx, next, body) = await Run(sp, "/v1/activities", "", signedIn: true, configs: Cfg(true, false));
            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.IsFalse(next);
            Assert.AreEqual(expected, body);
        }

        [TestMethod]
        public async Task Authorization_RequireAuthenticationOff_AdminRoleIsNotChecked()
        {
            using var sp = BuildServices();
            SetRoles(sp, "002");
            // RequireAdminRole alone has no effect without RequireAuthentication, as for the Elsa controllers
            var (ctx, _, body) = await Run(sp, "/v1/activities", "", signedIn: true, configs: Cfg(false, true));
            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.IsTrue(body.Length > 1000);
        }

        [TestMethod]
        public async Task Authorization_Anonymous_Is401_BeforeAnyPolicy()
        {
            using var sp = BuildServices();
            var (ctx, next, body) = await Run(sp, "/v1/activities", "", signedIn: false, configs: Cfg(true, true));
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.IsFalse(next);
            Assert.AreEqual("", body);
        }

        [TestMethod]
        public async Task Authorization_WithoutAuthorizationServices_FailsClosedWith500()
        {
            // RequireAuthentication is on but no IPolicyEvaluator: never answer
            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService>(new FakeAuthService());
            using var sp = services.BuildServiceProvider();
            var (ctx, next, body) = await Run(sp, "/v1/activities", "", signedIn: true, configs: Cfg(true, false));
            Assert.AreEqual(500, ctx.Response.StatusCode);
            Assert.IsFalse(next);
            Assert.AreEqual("", body);
        }

        // ---- #216 review: signed-in ?inneruse=1 takes the same path ----

        [TestMethod]
        public async Task SignedIn_InnerUse_IsAnsweredInProcess_AndTranslatedLikeThePublicForm()
        {
            using var sp = BuildServices();
            var (c1, n1, plain) = await Run(sp, "/v1/activities", "", signedIn: true, culture: "zh-CN");
            var (c2, n2, inner) = await Run(sp, "/v1/activities", "?inneruse=1", signedIn: true, culture: "zh-CN");
            Assert.IsFalse(n1);
            Assert.IsFalse(n2);
            Assert.AreEqual(plain, inner);
            Assert.AreEqual("application/json; charset=utf-8", c2.Response.ContentType);
        }

        // ---- #216 review: failure, headers, methods ----

        private class ThrowingLoggerProvider : ILoggerProvider
        {
            public System.Collections.Generic.List<(LogLevel Level, string Message)> Entries { get; } = new();
            public ILogger CreateLogger(string categoryName) => new L(this);
            public void Dispose() { }
            private class L : ILogger
            {
                private readonly ThrowingLoggerProvider _p;
                public L(ThrowingLoggerProvider p) { _p = p; }
                public IDisposable BeginScope<TState>(TState state) => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                    => _p.Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private static ServiceProvider BuildFailingServices(Exception ex, ThrowingLoggerProvider logs)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(logs));
            var svc = new Mock<IActivityTypeService>();
            svc.Setup(x => x.GetActivityTypesAsync(It.IsAny<CancellationToken>())).Throws(ex);
            services.AddSingleton(svc.Object);
            return services.BuildServiceProvider();
        }

        [TestMethod]
        public async Task Failure_Returns500WithEmptyBody_LogsTypeAndMessageOnly()
        {
            var logs = new ThrowingLoggerProvider();
            using var sp = BuildFailingServices(new InvalidOperationException("boom-detail"), logs);
            var (ctx, next, body) = await Run(sp, "/v1/activities", "?secret=do-not-log", signedIn: true);
            Assert.AreEqual(500, ctx.Response.StatusCode);
            Assert.IsFalse(next);
            Assert.AreEqual("", body);
            var entry = logs.Entries.Single(x => x.Level == LogLevel.Error);
            StringAssert.Contains(entry.Message, "System.InvalidOperationException");
            StringAssert.Contains(entry.Message, "boom-detail");
            Assert.IsFalse(entry.Message.Contains("do-not-log"));
            Assert.IsNull(ctx.Response.ContentType);
        }

        [TestMethod]
        public async Task Failure_OperationCanceled_IsRethrown()
        {
            var logs = new ThrowingLoggerProvider();
            using var sp = BuildFailingServices(new OperationCanceledException(), logs);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await Run(sp, "/v1/activities", "", signedIn: true));
            Assert.AreEqual(0, logs.Entries.Count(x => x.Level == LogLevel.Error));
        }

        [TestMethod]
        public async Task Success_SetsJsonNosniffAndNoStoreHeaders()
        {
            using var sp = BuildServices();
            var (ctx, _, body) = await Run(sp, "/v1/activities", "", signedIn: true);
            Assert.AreEqual("application/json; charset=utf-8", ctx.Response.ContentType);
            Assert.AreEqual("nosniff", ctx.Response.Headers["X-Content-Type-Options"].ToString());
            Assert.AreEqual("no-store", ctx.Response.Headers["Cache-Control"].ToString());
            Assert.IsTrue(body.Length > 1000);
        }

        [TestMethod]
        public async Task Head_IsAnsweredWithHeadersAndNoBody()
        {
            using var sp = BuildServices();
            var (ctx, next, body) = await Run(sp, "/v1/activities", "", signedIn: true, method: "HEAD");
            Assert.IsFalse(next);
            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.AreEqual("", body);
            Assert.AreEqual("no-store", ctx.Response.Headers["Cache-Control"].ToString());
        }

        [DataTestMethod]
        [DataRow("POST")]
        [DataRow("PUT")]
        [DataRow("DELETE")]
        [DataRow("PATCH")]
        [DataRow("OPTIONS")]
        public async Task SignedIn_OtherMethods_GoToTheNextMiddleware(string method)
        {
            using var sp = BuildServices();
            var (ctx, next, body) = await Run(sp, "/v1/activities", "", signedIn: true, method: method);
            Assert.IsTrue(next);
            Assert.AreEqual("", body);
            Assert.IsNull(ctx.Response.ContentType);
        }

        [DataTestMethod]
        [DataRow("POST")]
        [DataRow("DELETE")]
        public async Task Anonymous_OtherMethods_Get401NotNext(string method)
        {
            using var sp = BuildServices();
            var (ctx, next, _) = await Run(sp, "/v1/activities", "", signedIn: false, method: method);
            Assert.AreEqual(401, ctx.Response.StatusCode);
            Assert.IsFalse(next);
        }

        [TestMethod]
        public async Task LeadingDoubleSlash_SignedIn_IsAnsweredToo()
        {
            using var sp = BuildServices();
            var (ctx, next, body) = await Run(sp, "//v1/activities", "", signedIn: true);
            Assert.IsFalse(next);
            Assert.IsTrue(body.Length > 1000);
        }
    }
}
