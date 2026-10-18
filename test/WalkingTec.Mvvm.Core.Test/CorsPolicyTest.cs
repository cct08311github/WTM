using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;
using AspNetCorsPolicy = Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicy;
using Cors = WalkingTec.Mvvm.Core.Cors;
using ConfiguredCorsPolicy = WalkingTec.Mvvm.Core.CorsPolicy;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class CorsPolicyTest
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

        private static AspNetCorsPolicy BuildDefault(Cors options)
        {
            var b = new CorsPolicyBuilder();
            WtmCorsHelper.ConfigureDefaultPolicy(b, options);
            return b.Build();
        }

        private static AspNetCorsPolicy BuildConfigured(string domain)
        {
            var b = new CorsPolicyBuilder();
            WtmCorsHelper.ConfigureConfiguredPolicy(b, domain);
            return b.Build();
        }

        [TestMethod]
        public void Option_DefaultsToTrue()
        {
            Assert.IsTrue(new Cors().AllowCredentialsWithAnyOrigin);
            Assert.IsTrue(new Configs().CorsOptions.AllowCredentialsWithAnyOrigin);
        }

        [TestMethod]
        public void DefaultPolicy_AllowsAnyOriginWithCredentials_AsBefore()
        {
            var policy = BuildDefault(new Cors());
            Assert.IsTrue(policy.SupportsCredentials);
            Assert.IsFalse(policy.AllowAnyOrigin);
            Assert.IsTrue(policy.IsOriginAllowed("https://evil.example"));
            Assert.IsTrue(policy.IsOriginAllowed("http://localhost:3000"));
            Assert.IsTrue(policy.AllowAnyHeader);
            Assert.IsTrue(policy.AllowAnyMethod);
            CollectionAssert.AreEqual(new[] { "Content-Disposition" }, policy.ExposedHeaders.ToArray());
        }

        [TestMethod]
        public void DefaultPolicy_WithoutCredentials_WhenOptionFalse()
        {
            var policy = BuildDefault(new Cors { AllowCredentialsWithAnyOrigin = false });
            Assert.IsFalse(policy.SupportsCredentials);
            Assert.IsTrue(policy.AllowAnyOrigin); // "Access-Control-Allow-Origin: *"
            Assert.IsTrue(policy.AllowAnyHeader);
            Assert.IsTrue(policy.AllowAnyMethod);
            CollectionAssert.AreEqual(new[] { "Content-Disposition" }, policy.ExposedHeaders.ToArray());
        }

        [TestMethod]
        public void ConfiguredPolicy_UnchangedForPlainDomainList()
        {
            var policy = BuildConfigured("http://a.example,https://b.example:8080");
            CollectionAssert.AreEqual(new[] { "http://a.example", "https://b.example:8080" }, policy.Origins.ToArray());
            Assert.IsTrue(policy.SupportsCredentials);
            Assert.IsTrue(policy.IsOriginAllowed("http://a.example"));
            Assert.IsFalse(policy.IsOriginAllowed("http://c.example"));
        }

        [TestMethod]
        public void ConfiguredPolicy_TrimsEntriesAndIgnoresEmpty()
        {
            var policy = BuildConfigured(" http://a.example , https://b.example ,, ,");
            CollectionAssert.AreEqual(new[] { "http://a.example", "https://b.example" }, policy.Origins.ToArray());
            Assert.IsTrue(policy.IsOriginAllowed("https://b.example"));
        }

        [TestMethod]
        public void ConfiguredPolicy_NullOrEmptyDomain_DoesNotThrowAndAllowsNothing()
        {
            foreach (var domain in new string[] { null, "", "   ", " , ," })
            {
                var policy = BuildConfigured(domain);
                Assert.AreEqual(0, policy.Origins.Count);
                Assert.IsFalse(policy.AllowAnyOrigin);
                Assert.IsFalse(policy.IsOriginAllowed("https://evil.example"));
            }
        }

        [TestMethod]
        public async Task AddWtmCrossDomain_RegistersExpectedPolicies()
        {
            // no Policy configured: default policy
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmCrossDomain(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build());
            var provider = services.BuildServiceProvider().GetRequiredService<ICorsPolicyProvider>();
            var def = await provider.GetPolicyAsync(new DefaultHttpContext(), WtmCorsHelper.DefaultPolicyName);
            Assert.IsTrue(def.SupportsCredentials);
            Assert.IsTrue(def.IsOriginAllowed("https://evil.example"));

            // option false from configuration
            services = new ServiceCollection();
            services.AddWtmCrossDomain(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["CorsOptions:AllowCredentialsWithAnyOrigin"] = "false"
            }).Build());
            provider = services.BuildServiceProvider().GetRequiredService<ICorsPolicyProvider>();
            def = await provider.GetPolicyAsync(new DefaultHttpContext(), WtmCorsHelper.DefaultPolicyName);
            Assert.IsFalse(def.SupportsCredentials);
            Assert.IsTrue(def.AllowAnyOrigin);

            // explicit policies, including a null Domain (must not throw)
            services = new ServiceCollection();
            services.AddWtmCrossDomain(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["CorsOptions:Policy:0:Name"] = "p1",
                ["CorsOptions:Policy:0:Domain"] = "http://a.example, http://b.example",
                ["CorsOptions:Policy:1:Name"] = "p2"
            }).Build());
            provider = services.BuildServiceProvider().GetRequiredService<ICorsPolicyProvider>();
            var p1 = await provider.GetPolicyAsync(new DefaultHttpContext(), "p1");
            CollectionAssert.AreEqual(new[] { "http://a.example", "http://b.example" }, p1.Origins.ToArray());
            var p2 = await provider.GetPolicyAsync(new DefaultHttpContext(), "p2");
            Assert.AreEqual(0, p2.Origins.Count);
        }

        [TestMethod]
        public void Diagnostics_EnableAllWithoutPolicy_WarnsInDevelopmentCriticalOtherwise()
        {
            var options = new Cors { EnableAll = true };

            var dev = new CapturingLogger();
            WtmCorsHelper.LogStartupDiagnostics(options, true, dev);
            Assert.AreEqual(1, dev.Entries.Count);
            Assert.AreEqual(LogLevel.Warning, dev.Entries[0].Level);
            StringAssert.Contains(dev.Entries[0].Message, "Policy");

            var prod = new CapturingLogger();
            WtmCorsHelper.LogStartupDiagnostics(options, false, prod);
            Assert.AreEqual(1, prod.Entries.Count);
            Assert.AreEqual(LogLevel.Critical, prod.Entries[0].Level);
        }

        [TestMethod]
        public void Diagnostics_NoWarningWhenPolicyConfiguredOrNotEnabled()
        {
            var withPolicy = new CapturingLogger();
            WtmCorsHelper.LogStartupDiagnostics(new Cors
            {
                EnableAll = true,
                Policy = new List<ConfiguredCorsPolicy> { new ConfiguredCorsPolicy { Name = "p", Domain = "http://a.example" } }
            }, false, withPolicy);
            Assert.AreEqual(0, withPolicy.Entries.Count);

            var disabled = new CapturingLogger();
            WtmCorsHelper.LogStartupDiagnostics(new Cors { EnableAll = false }, false, disabled);
            Assert.AreEqual(0, disabled.Entries.Count);
        }

        [TestMethod]
        public void Diagnostics_CredentialsDisabled_IsOnlyInformational()
        {
            var logger = new CapturingLogger();
            WtmCorsHelper.LogStartupDiagnostics(new Cors { EnableAll = true, AllowCredentialsWithAnyOrigin = false }, false, logger);
            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(LogLevel.Information, logger.Entries[0].Level);
        }

        [TestMethod]
        public void Diagnostics_EmptyDomainPolicy_WarnsAndNeverThrows()
        {
            var logger = new CapturingLogger();
            WtmCorsHelper.LogStartupDiagnostics(new Cors
            {
                EnableAll = true,
                Policy = new List<ConfiguredCorsPolicy> { new ConfiguredCorsPolicy { Name = "p", Domain = null }, null }
            }, false, logger);
            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(LogLevel.Warning, logger.Entries[0].Level);

            // null arguments never throw
            WtmCorsHelper.LogStartupDiagnostics(null, false, logger);
            WtmCorsHelper.LogStartupDiagnostics(new Cors { EnableAll = true }, false, null);
            WtmCorsHelper.LogStartupDiagnostics(new Cors { EnableAll = true, Policy = null }, false, new CapturingLogger());
        }
    }
}
