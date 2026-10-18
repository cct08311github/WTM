using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;

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

        [TestMethod]
        public void ForwardHeaders_NullWhenNothingPresent()
        {
            Assert.IsNull(WtmMiddleware.BuildForwardHeaders(new DefaultHttpContext()));
            Assert.IsNull(WtmMiddleware.BuildForwardHeaders(null));
        }

        [TestMethod]
        public void ForwardHeaders_CopiesAuthorizationAndCookieOnly()
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["Authorization"] = "Bearer abc";
            ctx.Request.Headers["Cookie"] = "a=1; b=2";
            ctx.Request.Headers["X-Other"] = "zzz";
            var h = WtmMiddleware.BuildForwardHeaders(ctx);
            Assert.AreEqual(2, h.Count);
            Assert.AreEqual("Bearer abc", h["Authorization"]);
            Assert.AreEqual("a=1; b=2", h["Cookie"]);

            var only = new DefaultHttpContext();
            only.Request.Headers["Cookie"] = "a=1";
            var h2 = WtmMiddleware.BuildForwardHeaders(only);
            Assert.AreEqual(1, h2.Count);
            Assert.IsFalse(h2.ContainsKey("Authorization"));
        }
    }
}
