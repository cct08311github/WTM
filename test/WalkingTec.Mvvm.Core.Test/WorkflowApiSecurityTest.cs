using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

// dummy controller types: only the type name matters for the convention
namespace Elsa.Server.Api.Endpoints.Dummy
{
    public class DummyElsaController
    {
    }
}

namespace WalkingTec.Mvvm.Core.Test
{
    public class DummyOtherController
    {
    }

    [TestClass]
    public class WorkflowApiSecurityTest
    {
        private class CapturingLogger : ILogger
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new List<(LogLevel, string)>();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private static ControllerModel Model(Type t) => new ControllerModel(t.GetTypeInfo(), new List<object>());

        [TestMethod]
        public void Config_DefaultsToFalse()
        {
            var o = new Configs().WorkflowOptions;
            Assert.IsNotNull(o);
            Assert.IsFalse(o.RequireAuthentication);
            Assert.IsFalse(o.RequireAdminRole);
            Assert.IsFalse(new Configs().EnableWorkflow);
        }

        [TestMethod]
        public void Convention_AddsFilter_ToElsaApiController()
        {
            var m = Model(typeof(Elsa.Server.Api.Endpoints.Dummy.DummyElsaController));
            new WorkflowApiAuthorizationConvention(false).Apply(m);
            var f = m.Filters.OfType<AuthorizeFilter>().SingleOrDefault();
            Assert.IsNotNull(f);
            Assert.IsTrue(f.Policy.AuthenticationSchemes.Contains(CookieAuthenticationDefaults.AuthenticationScheme));
            Assert.IsTrue(f.Policy.AuthenticationSchemes.Contains(JwtBearerDefaults.AuthenticationScheme));
            Assert.IsTrue(f.Policy.Requirements.Any(), "authenticated-user requirement expected");
            Assert.IsFalse(f.Policy.Requirements.Any(x => x is WorkflowAdminRequirement));
        }

        [TestMethod]
        public void Convention_AdminRole_AddsAdminRequirement()
        {
            var m = Model(typeof(Elsa.Server.Api.Endpoints.Dummy.DummyElsaController));
            new WorkflowApiAuthorizationConvention(true).Apply(m);
            var f = m.Filters.OfType<AuthorizeFilter>().Single();
            Assert.IsTrue(f.Policy.Requirements.Any(x => x is WorkflowAdminRequirement));
        }

        [TestMethod]
        public void Convention_DoesNotTouch_OtherControllers()
        {
            var m = Model(typeof(DummyOtherController));
            new WorkflowApiAuthorizationConvention(true).Apply(m);
            Assert.AreEqual(0, m.Filters.Count);
        }

        [TestMethod]
        public void IsWorkflowAdmin_MatchesCodeOrName()
        {
            Assert.IsFalse(WorkflowApiSecurity.IsWorkflowAdmin(null));
            Assert.IsFalse(WorkflowApiSecurity.IsWorkflowAdmin(new List<SimpleRole>()));
            Assert.IsFalse(WorkflowApiSecurity.IsWorkflowAdmin(new List<SimpleRole> { new SimpleRole { RoleCode = "002", RoleName = "x" } }));
            Assert.IsTrue(WorkflowApiSecurity.IsWorkflowAdmin(new List<SimpleRole> { new SimpleRole { RoleCode = "001" } }));
            Assert.IsTrue(WorkflowApiSecurity.IsWorkflowAdmin(new List<SimpleRole> { new SimpleRole { RoleCode = "x", RoleName = "流程管理员" } }));
        }

        [TestMethod]
        public void Warning_OnlyWhenEnabledAndUnprotected()
        {
            Assert.IsFalse(WorkflowApiSecurity.ShouldWarnUnprotected(null));
            Assert.IsFalse(WorkflowApiSecurity.ShouldWarnUnprotected(new Configs()));
            Assert.IsTrue(WorkflowApiSecurity.ShouldWarnUnprotected(new Configs { EnableWorkflow = true }));
            var protectedCfg = new Configs { EnableWorkflow = true };
            protectedCfg.WorkflowOptions.RequireAuthentication = true;
            Assert.IsFalse(WorkflowApiSecurity.ShouldWarnUnprotected(protectedCfg));
        }

        [TestMethod]
        public void Warning_IsLoggedAsWarning_AndNeverThrows()
        {
            var logger = new CapturingLogger();
            WorkflowApiSecurity.LogUnprotectedWarning(new Configs { EnableWorkflow = true }, logger);
            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(LogLevel.Warning, logger.Entries[0].Level);
            WorkflowApiSecurity.LogUnprotectedWarning(new Configs(), logger);
            Assert.AreEqual(1, logger.Entries.Count);
            WorkflowApiSecurity.LogUnprotectedWarning(new Configs { EnableWorkflow = true }, null);
            WorkflowApiSecurity.LogUnprotectedWarning(null, null);
        }

        private static Configs Cfg(bool requireAuth, string serverAddress = null, string cookiePre = null)
        {
            var c = new Configs();
            c.WorkflowOptions.RequireAuthentication = requireAuth;
            if (cookiePre != null)
            {
                c.CookiePre = cookiePre;
            }
            if (serverAddress != null)
            {
                c.Domains = new Dictionary<string, Domain> { { "server", new Domain { Address = serverAddress } } };
            }
            return c;
        }

        private const string AuthCookie = ".AspNetCore..CookieWithJwtAuth";

        [TestMethod]
        public void SelfCall_AuthOff_UnchangedAddressAndNoHeaders()
        {
            var r = WorkflowApiSecurity.ResolveSelfCall(Cfg(false, "https://srv.example"), "app.example", "http", "app.example", "Bearer abc", AuthCookie + "=1");
            Assert.AreEqual("http://app.example", r.BaseAddress);
            Assert.IsNull(r.Headers);
            Assert.IsFalse(r.CredentialsWithheld);
        }

        private static Configs CfgInternal(bool requireAuth, string internalBase, string serverAddress = null)
        {
            var c = Cfg(requireAuth, serverAddress);
            c.WorkflowOptions.InternalCallBaseAddress = internalBase;
            return c;
        }

        [TestMethod]
        public void Config_InternalCallBaseAddress_DefaultsToNull()
        {
            Assert.IsNull(new Configs().WorkflowOptions.InternalCallBaseAddress);
        }

        [TestMethod]
        public void SelfCall_AuthOff_NoSetting_NoExplicitList_UsesRequestHost()
        {
            foreach (var allowed in new[] { null, "", "*", "*.example.com;app.example.com" })
            {
                var r = WorkflowApiSecurity.ResolveSelfCall(Cfg(false), allowed, "http", "evil.example", "Bearer abc", null);
                Assert.AreEqual("http://evil.example", r.BaseAddress, allowed);
                Assert.IsNull(r.Headers, allowed);
                Assert.IsFalse(r.Rejected, allowed);
                Assert.IsFalse(r.InvalidInternalCallBaseAddress, allowed);
            }
        }

        [TestMethod]
        public void SelfCall_AuthOff_NoSetting_ExplicitList_Match_UsesRequestHost()
        {
            var r = WorkflowApiSecurity.ResolveSelfCall(Cfg(false), "other.example; App.Example", "https", "app.example:8443", null, null);
            Assert.AreEqual("https://app.example:8443", r.BaseAddress);
            Assert.IsFalse(r.Rejected);
            var withPort = WorkflowApiSecurity.ResolveSelfCall(Cfg(false), "app.example:8443", "https", "app.example:8443", null, null);
            Assert.IsFalse(withPort.Rejected);
            Assert.AreEqual("https://app.example:8443", withPort.BaseAddress);
        }

        [TestMethod]
        public void SelfCall_AuthOff_NoSetting_ExplicitList_Mismatch_Rejected()
        {
            var r = WorkflowApiSecurity.ResolveSelfCall(Cfg(false), "app.example;other.example", "http", "attacker.example", "Bearer abc", null);
            Assert.IsTrue(r.Rejected);
            Assert.IsNull(r.BaseAddress);
            Assert.IsNull(r.Headers);
            var portMismatch = WorkflowApiSecurity.ResolveSelfCall(Cfg(false), "app.example:8443", "https", "app.example:9999", null, null);
            Assert.IsTrue(portMismatch.Rejected);
        }

        [TestMethod]
        public void SelfCall_AuthOff_WithSetting_AlwaysUsesSetting_NoHeaders()
        {
            foreach (var allowed in new[] { null, "*", "app.example" })
            {
                var r = WorkflowApiSecurity.ResolveSelfCall(CfgInternal(false, " https://localhost:5001/ "), allowed, "http", "anything.example", "Bearer abc", AuthCookie + "=1");
                Assert.AreEqual("https://localhost:5001", r.BaseAddress, allowed);
                Assert.IsNull(r.Headers, allowed);
                Assert.IsFalse(r.Rejected, allowed);
                Assert.IsFalse(r.CredentialsWithheld, allowed);
            }
        }

        [TestMethod]
        public void SelfCall_AuthOn_WithSetting_PreferredOverServerDomain_ForwardsCredentials()
        {
            var r = WorkflowApiSecurity.ResolveSelfCall(CfgInternal(true, "https://internal.example/", "https://srv.example"), "app.example", "https", "attacker.example", "Bearer abc", AuthCookie + "=tok; other=1");
            Assert.AreEqual("https://internal.example", r.BaseAddress);
            Assert.AreEqual("Bearer abc", r.Headers["Authorization"]);
            Assert.AreEqual(AuthCookie + "=tok", r.Headers["Cookie"]);
            Assert.IsFalse(r.CredentialsWithheld);
        }

        [TestMethod]
        public void SelfCall_AuthOn_WithSetting_PlainHttpNonLoopback_WithholdsCredentials()
        {
            var r = WorkflowApiSecurity.ResolveSelfCall(CfgInternal(true, "http://wtm-app:8080"), null, "https", "h", "Bearer abc", null);
            Assert.AreEqual("http://wtm-app:8080", r.BaseAddress);
            Assert.IsNull(r.Headers);
            Assert.IsTrue(r.CredentialsWithheld);
            var loop = WorkflowApiSecurity.ResolveSelfCall(CfgInternal(true, "http://localhost:5000"), null, "https", "h", "Bearer abc", null);
            Assert.AreEqual("Bearer abc", loop.Headers["Authorization"]);
        }

        [TestMethod]
        public void SelfCall_InvalidSetting_TreatedAsNotSet_AndFlagged()
        {
            foreach (var bad in new[] { "not-a-url", "ftp://x", "/relative" })
            {
                var off = WorkflowApiSecurity.ResolveSelfCall(CfgInternal(false, bad), null, "http", "app.example", null, null);
                Assert.AreEqual("http://app.example", off.BaseAddress, bad);
                Assert.IsTrue(off.InvalidInternalCallBaseAddress, bad);
                var rej = WorkflowApiSecurity.ResolveSelfCall(CfgInternal(false, bad), "other.example", "http", "app.example", null, null);
                Assert.IsTrue(rej.Rejected, bad);
                Assert.IsTrue(rej.InvalidInternalCallBaseAddress, bad);
                var on = WorkflowApiSecurity.ResolveSelfCall(CfgInternal(true, bad, "https://srv.example"), null, "https", "h", "Bearer abc", null);
                Assert.AreEqual("https://srv.example", on.BaseAddress, bad);
                Assert.IsTrue(on.InvalidInternalCallBaseAddress, bad);
            }
        }

        [TestMethod]
        public void IsExplicitHostList_Cases()
        {
            Assert.IsFalse(WorkflowApiSecurity.IsExplicitHostList(null));
            Assert.IsFalse(WorkflowApiSecurity.IsExplicitHostList(" "));
            Assert.IsFalse(WorkflowApiSecurity.IsExplicitHostList("*"));
            Assert.IsFalse(WorkflowApiSecurity.IsExplicitHostList("*.example.com;app.example.com"));
            Assert.IsTrue(WorkflowApiSecurity.IsExplicitHostList("app.example.com"));
            Assert.IsTrue(WorkflowApiSecurity.IsExplicitHostList("a.example;b.example;"));
        }

        [TestMethod]
        public void SelfCall_ServerDomain_UsedAndCredentialsForwarded_AuthorizationAndAuthCookieOnly()
        {
            var cookie = "other=1; " + AuthCookie + "=tok; " + AuthCookie + "C1=part; theme=dark";
            var r = WorkflowApiSecurity.ResolveSelfCall(Cfg(true, "https://srv.example/"), null, "https", "attacker.example", "Bearer abc", cookie);
            Assert.AreEqual("https://srv.example", r.BaseAddress);
            Assert.AreEqual("Bearer abc", r.Headers["Authorization"]);
            Assert.AreEqual(AuthCookie + "=tok; " + AuthCookie + "C1=part", r.Headers["Cookie"]);
            Assert.AreEqual(2, r.Headers.Count);
            Assert.IsFalse(r.CredentialsWithheld);
        }

        [TestMethod]
        public void SelfCall_CookiePrefixIsPartOfCookieName()
        {
            var name = WorkflowApiSecurity.GetAuthCookieName(Cfg(true, cookiePre: "x"));
            Assert.AreEqual(".AspNetCore.x.CookieWithJwtAuth", name);
            Assert.AreEqual(AuthCookie, WorkflowApiSecurity.GetAuthCookieName(new Configs()));
            var r = WorkflowApiSecurity.ResolveSelfCall(Cfg(true, "https://srv.example", "x"), null, "https", "h", null, AuthCookie + "=1; " + name + "=2");
            Assert.AreEqual(name + "=2", r.Headers["Cookie"]);
            Assert.IsFalse(r.Headers.ContainsKey("Authorization"));
        }

        [TestMethod]
        public void SelfCall_NoServerDomain_AllowedHostMatch_ForwardsToRequestHost()
        {
            var r = WorkflowApiSecurity.ResolveSelfCall(Cfg(true), "other.example;App.Example", "https", "app.example:8443", "Bearer abc", null);
            Assert.AreEqual("https://app.example:8443", r.BaseAddress);
            Assert.AreEqual("Bearer abc", r.Headers["Authorization"]);
        }

        [TestMethod]
        public void SelfCall_NoServerDomain_WildcardOrMissingAllowedHosts_WithholdsCredentials()
        {
            foreach (var allowed in new[] { null, "", "*", "*.example", "other.example" })
            {
                var r = WorkflowApiSecurity.ResolveSelfCall(Cfg(true), allowed, "https", "app.example", "Bearer abc", AuthCookie + "=1");
                Assert.IsNull(r.Headers, allowed);
                Assert.IsTrue(r.CredentialsWithheld, allowed);
                Assert.AreEqual("https://app.example", r.BaseAddress);
            }
        }

        [TestMethod]
        public void SelfCall_PlainHttpToNonLoopback_WithholdsCredentials_LoopbackAllowed()
        {
            var bad = WorkflowApiSecurity.ResolveSelfCall(Cfg(true, "http://srv.example"), null, "https", "h", "Bearer abc", null);
            Assert.IsNull(bad.Headers);
            Assert.IsTrue(bad.CredentialsWithheld);
            var badHost = WorkflowApiSecurity.ResolveSelfCall(Cfg(true), "app.example", "http", "app.example", "Bearer abc", null);
            Assert.IsNull(badHost.Headers);
            Assert.IsTrue(badHost.CredentialsWithheld);
            foreach (var h in new[] { "localhost:5000", "127.0.0.1:5000", "[::1]:5000" })
            {
                var ok = WorkflowApiSecurity.ResolveSelfCall(Cfg(true), h, "http", h, "Bearer abc", null);
                Assert.AreEqual("Bearer abc", ok.Headers?["Authorization"], h);
            }
        }

        [TestMethod]
        public void SelfCall_NothingToForward_NoHeadersButNotWithheld()
        {
            var r = WorkflowApiSecurity.ResolveSelfCall(Cfg(true, "https://srv.example"), null, "https", "h", "", "other=1");
            Assert.IsNull(r.Headers);
            Assert.IsFalse(r.CredentialsWithheld);
        }

        private class FakeAccessor : IHttpContextAccessor
        {
            private readonly Func<HttpContext> _get;
            public FakeAccessor(Func<HttpContext> get) { _get = get; }
            public HttpContext HttpContext { get => _get(); set { } }
        }

        private static bool RunHandler(IHttpContextAccessor accessor)
        {
            var req = new WorkflowAdminRequirement();
            var ctx = new AuthorizationHandlerContext(new[] { req }, new ClaimsPrincipal(), null);
            new WorkflowAdminRequirementHandler(accessor).HandleAsync(ctx).GetAwaiter().GetResult();
            return ctx.HasSucceeded;
        }

        private static IHttpContextAccessor AccessorWithRoles(params SimpleRole[] roles)
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.LoginUserInfo = new LoginUserInfo { ITCode = "u", Roles = roles.ToList() };
            var services = new ServiceCollection();
            services.AddSingleton(wtm);
            var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
            return new FakeAccessor(() => http);
        }

        [TestMethod]
        public void Handler_NoHttpContext_NoWtmContext_Exception_NotSucceeded()
        {
            Assert.IsFalse(RunHandler(new FakeAccessor(() => null)));
            Assert.IsFalse(RunHandler(new FakeAccessor(() => new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() })));
            Assert.IsFalse(RunHandler(new FakeAccessor(() => throw new InvalidOperationException("boom"))));
            Assert.IsFalse(RunHandler(null));
        }

        [TestMethod]
        public void Handler_NonAdmin_NotSucceeded()
        {
            Assert.IsFalse(RunHandler(AccessorWithRoles(new SimpleRole { RoleCode = "002", RoleName = "user" })));
            Assert.IsFalse(RunHandler(AccessorWithRoles()));
        }

        [TestMethod]
        public void Handler_AdminByCodeOrName_Succeeded()
        {
            Assert.IsTrue(RunHandler(AccessorWithRoles(new SimpleRole { RoleCode = "001", RoleName = "x" })));
            Assert.IsTrue(RunHandler(AccessorWithRoles(new SimpleRole { RoleCode = "002", RoleName = "流程管理员" })));
        }

        [TestMethod]
        public void Convention_CoversEveryControllerInElsaServerApiAssembly()
        {
            var asm = Assembly.Load("Elsa.Server.Api");
            var controllers = asm.GetTypes()
                .Where(t => t.IsClass && t.IsAbstract == false && typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
                .ToList();
            Assert.IsTrue(controllers.Count > 0, "no controllers found in the Elsa.Server.Api assembly");
            foreach (var t in controllers)
            {
                var m = Model(t);
                new WorkflowApiAuthorizationConvention(false).Apply(m);
                Assert.IsTrue(m.Filters.OfType<AuthorizeFilter>().Any(), t.FullName);
                Assert.IsFalse(t.GetCustomAttributes(true).OfType<IAllowAnonymous>().Any(), "anonymous controller: " + t.FullName);
                foreach (var method in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.IsFalse(method.GetCustomAttributes(true).OfType<IAllowAnonymous>().Any(), "anonymous action: " + t.FullName + "." + method.Name);
                }
            }
        }

        [TestMethod]
        public void Config_BindsWorkflowOptionsFromConfiguration()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                { "EnableWorkflow", "true" },
                { "WorkflowOptions:RequireAuthentication", "true" },
                { "WorkflowOptions:RequireAdminRole", "true" },
            }).Build();
            var c = config.Get<Configs>();
            Assert.IsTrue(c.EnableWorkflow);
            Assert.IsTrue(c.WorkflowOptions.RequireAuthentication);
            Assert.IsTrue(c.WorkflowOptions.RequireAdminRole);
            var empty = new ConfigurationBuilder().Build().Get<Configs>() ?? new Configs();
            Assert.IsFalse(empty.WorkflowOptions.RequireAuthentication);
        }
    }
}
