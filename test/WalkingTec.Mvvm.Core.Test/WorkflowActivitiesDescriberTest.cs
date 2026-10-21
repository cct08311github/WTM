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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

        private static async Task<(DefaultHttpContext Ctx, bool NextCalled, string Body)> Run(IServiceProvider sp, string path, string query, bool signedIn, string culture = "en-US")
        {
            var ctx = new DefaultHttpContext { RequestServices = sp };
            ctx.Request.Scheme = "https";
            ctx.Request.Host = new HostString("attacker.example");
            ctx.Request.Path = path;
            ctx.Request.QueryString = new QueryString(query);
            ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(new FakeMaxRequestBodySizeFeature());
            if (signedIn)
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "u") }, "cookie"));
            }
            using var body = new MemoryStream();
            ctx.Response.Body = body;
            var called = false;
            var middleware = new WtmMiddleware(_ => { called = true; return Task.CompletedTask; });
            var old = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(culture);
            try
            {
                // a bare WTMContext: CallAPI on it would throw, so a passing test proves no HTTP call is attempted
                await middleware.InvokeAsync(ctx, new WTMContext(null));
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
    }
}
