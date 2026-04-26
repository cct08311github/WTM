#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.FeatureFlags;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.FeatureFlags
{
    /// <summary>
    /// Tests for <see cref="WtmFeatureFlags"/> resolution order
    /// (resolver → configuration → defaults → false), registration
    /// idempotency, snapshot semantics, fault-tolerant resolver, and
    /// the <see cref="WtmFeatureGateAttribute"/> MVC gate including
    /// fail-closed behavior when the service isn't registered.
    /// </summary>
    [TestClass]
    public class WtmFeatureFlagsTests
    {
        // ── Resolution order ─────────────────────────────────────────────

        [TestMethod]
        public void IsEnabled_returns_false_for_null_or_whitespace_name()
        {
            var flags = BuildFlags();
            Assert.IsFalse(flags.IsEnabled(null));
            Assert.IsFalse(flags.IsEnabled(""));
            Assert.IsFalse(flags.IsEnabled("   "));
        }

        [TestMethod]
        public void IsEnabled_unknown_flag_returns_false()
        {
            var flags = BuildFlags();
            Assert.IsFalse(flags.IsEnabled("unknown-flag"),
                "Fail-closed: flags never declared must resolve to false.");
        }

        [TestMethod]
        public void IsEnabled_default_true_resolves_to_true_when_config_absent()
        {
            var flags = BuildFlags(
                defaults: new Dictionary<string, bool> { ["canary"] = true });
            Assert.IsTrue(flags.IsEnabled("canary"));
        }

        [TestMethod]
        public void IsEnabled_configuration_overrides_defaults()
        {
            var flags = BuildFlags(
                configuration: new Dictionary<string, string?> { ["FeatureFlags:canary"] = "false" },
                defaults: new Dictionary<string, bool> { ["canary"] = true });
            Assert.IsFalse(flags.IsEnabled("canary"),
                "Configuration value must win over the static default.");
        }

        [TestMethod]
        public void IsEnabled_configuration_parses_case_insensitively()
        {
            foreach (var raw in new[] { "true", "True", "TRUE" })
            {
                var flags = BuildFlags(configuration: new Dictionary<string, string?>
                {
                    ["FeatureFlags:x"] = raw,
                });
                Assert.IsTrue(flags.IsEnabled("x"), $"value={raw} must parse as true");
            }
        }

        [TestMethod]
        public void IsEnabled_unparsable_configuration_value_falls_through_to_defaults()
        {
            var flags = BuildFlags(
                configuration: new Dictionary<string, string?> { ["FeatureFlags:x"] = "maybe" },
                defaults: new Dictionary<string, bool> { ["x"] = true });
            Assert.IsTrue(flags.IsEnabled("x"),
                "Non-bool config value must not lock the flag off; fall through to default.");
        }

        [TestMethod]
        public void IsEnabled_resolver_short_circuits_configuration_and_defaults()
        {
            var flags = BuildFlags(
                configuration: new Dictionary<string, string?> { ["FeatureFlags:x"] = "false" },
                defaults: new Dictionary<string, bool> { ["x"] = false },
                resolver: (_, name) => name == "x" ? true : (bool?)null);
            Assert.IsTrue(flags.IsEnabled("x"),
                "Resolver returning true must override config/defaults.");
        }

        [TestMethod]
        public void IsEnabled_resolver_returning_null_falls_through_to_configuration()
        {
            var flags = BuildFlags(
                configuration: new Dictionary<string, string?> { ["FeatureFlags:x"] = "true" },
                resolver: (_, _) => null);
            Assert.IsTrue(flags.IsEnabled("x"),
                "Resolver returning null is 'no opinion' — falls through to config.");
        }

        [TestMethod]
        public void IsEnabled_throwing_resolver_does_not_crash_and_falls_through()
        {
            var flags = BuildFlags(
                configuration: new Dictionary<string, string?> { ["FeatureFlags:x"] = "true" },
                resolver: (_, _) => throw new InvalidOperationException("flag service down"));
            Assert.IsTrue(flags.IsEnabled("x"),
                "Resolver exceptions must be caught so a flag-service outage can't take down requests.");
        }

        [TestMethod]
        public void IsEnabled_flag_lookup_is_case_insensitive_for_defaults()
        {
            var flags = BuildFlags(
                defaults: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MixedCase"] = true,
                });
            Assert.IsTrue(flags.IsEnabled("mixedcase"));
            Assert.IsTrue(flags.IsEnabled("MIXEDCASE"));
        }

        // ── ConfigurationSection override ────────────────────────────────

        [TestMethod]
        public void IsEnabled_honours_custom_configuration_section()
        {
            var flags = BuildFlags(
                configuration: new Dictionary<string, string?>
                {
                    ["FeatureFlags:x"] = "false",   // default section — should be ignored
                    ["Features:x"] = "true",        // custom section
                },
                configureOptions: o => o.ConfigurationSection = "Features");
            Assert.IsTrue(flags.IsEnabled("x"));
        }

        // ── Snapshot ─────────────────────────────────────────────────────

        [TestMethod]
        public void Snapshot_includes_defaults_and_configuration_flags()
        {
            var flags = BuildFlags(
                configuration: new Dictionary<string, string?>
                {
                    ["FeatureFlags:from-config"] = "true",
                },
                defaults: new Dictionary<string, bool>
                {
                    ["from-default"] = false,
                });

            var snap = flags.Snapshot();

            Assert.IsTrue(snap.ContainsKey("from-config"));
            Assert.IsTrue(snap["from-config"]);
            Assert.IsTrue(snap.ContainsKey("from-default"));
            Assert.IsFalse(snap["from-default"]);
        }

        // ── Registration ─────────────────────────────────────────────────

        [TestMethod]
        public void AddWtmFeatureFlags_registers_singleton_service()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().Build());
            services.AddLogging();
            services.AddWtmFeatureFlags();

            var sp = services.BuildServiceProvider();
            var first = sp.GetRequiredService<IWtmFeatureFlags>();
            var second = sp.GetRequiredService<IWtmFeatureFlags>();
            Assert.AreSame(first, second, "Service must be registered as a singleton.");
        }

        [TestMethod]
        public void AddWtmFeatureFlags_is_idempotent_on_repeated_calls()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddLogging();
            services.AddWtmFeatureFlags();
            services.AddWtmFeatureFlags(opt => opt.Defaults["k"] = true);

            var sp = services.BuildServiceProvider();
            var flags = sp.GetRequiredService<IWtmFeatureFlags>();
            Assert.IsTrue(flags.IsEnabled("k"),
                "Second registration's configure callback must still apply.");
        }

        [TestMethod]
        public void AddWtmFeatureFlags_rejects_null_configure()
        {
            var services = new ServiceCollection();
            Assert.ThrowsException<ArgumentNullException>(() =>
                services.AddWtmFeatureFlags(null!));
        }

        // ── [WtmFeatureGate] integration ─────────────────────────────────

        [TestMethod]
        public void WtmFeatureGate_ctor_rejects_null_or_empty_flag_name()
        {
            Assert.ThrowsException<ArgumentException>(() => new WtmFeatureGateAttribute(null!));
            Assert.ThrowsException<ArgumentException>(() => new WtmFeatureGateAttribute(""));
            Assert.ThrowsException<ArgumentException>(() => new WtmFeatureGateAttribute("   "));
        }

        [TestMethod]
        public async Task WtmFeatureGate_returns_404_when_flag_disabled()
        {
            using var host = await BuildMvcHostAsync(
                defaults: new Dictionary<string, bool> { ["new-feature"] = false });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/feature/gated");
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }

        [TestMethod]
        public async Task WtmFeatureGate_allows_action_when_flag_enabled()
        {
            using var host = await BuildMvcHostAsync(
                defaults: new Dictionary<string, bool> { ["new-feature"] = true });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/feature/gated");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual("gated-ok", body);
        }

        [TestMethod]
        public async Task WtmFeatureGate_ungated_action_always_runs()
        {
            using var host = await BuildMvcHostAsync(
                defaults: new Dictionary<string, bool> { ["new-feature"] = false });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/feature/open");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async Task WtmFeatureGate_fails_closed_when_service_not_registered()
        {
            using var host = await BuildMvcHostAsync(registerFlags: false);
            var client = host.GetTestClient();

            var response = await client.GetAsync("/feature/gated");
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode,
                "Service missing → treat as disabled (fail-closed); never accidentally expose gated endpoint.");
        }

        [TestMethod]
        public async Task WtmFeatureGate_custom_fallback_status_code_is_honoured()
        {
            using var host = await BuildMvcHostAsync(
                defaults: new Dictionary<string, bool> { ["new-feature"] = false });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/feature/custom-status");
            Assert.AreEqual((HttpStatusCode)418, response.StatusCode);
        }

        // ── Scaffolding ──────────────────────────────────────────────────

        private static IWtmFeatureFlags BuildFlags(
            IDictionary<string, string?>? configuration = null,
            IDictionary<string, bool>? defaults = null,
            Func<HttpContext?, string, bool?>? resolver = null,
            Action<WtmFeatureFlagsOptions>? configureOptions = null)
        {
            var services = new ServiceCollection();

            var cfgBuilder = new ConfigurationBuilder();
            if (configuration != null)
            {
                cfgBuilder.AddInMemoryCollection(configuration);
            }
            services.AddSingleton<IConfiguration>(cfgBuilder.Build());
            services.AddLogging();

            services.AddWtmFeatureFlags(opt =>
            {
                if (defaults != null)
                {
                    foreach (var kv in defaults) { opt.Defaults[kv.Key] = kv.Value; }
                }
                if (resolver != null) { opt.Resolver = resolver; }
                configureOptions?.Invoke(opt);
            });

            return services.BuildServiceProvider().GetRequiredService<IWtmFeatureFlags>();
        }

        private static async Task<IHost> BuildMvcHostAsync(
            IDictionary<string, bool>? defaults = null,
            bool registerFlags = true)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.ConfigureServices(services =>
                    {
                        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                        services.AddLogging();
                        if (registerFlags)
                        {
                            services.AddWtmFeatureFlags(opt =>
                            {
                                if (defaults != null)
                                {
                                    foreach (var kv in defaults) { opt.Defaults[kv.Key] = kv.Value; }
                                }
                            });
                        }
                        services.AddControllers()
                                .AddApplicationPart(typeof(WtmFeatureFlagsTests).Assembly);
                    });
                    w.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e => e.MapControllers());
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }
    }

    // ── Fixture controller for MVC integration ──────────────────────────

    [ApiController]
    [Route("feature")]
    public class WtmFeatureGateFixtureController : ControllerBase
    {
        [HttpGet("gated")]
        [WtmFeatureGate("new-feature")]
        public IActionResult Gated() => Ok("gated-ok");

        [HttpGet("open")]
        public IActionResult Open() => Ok("open-ok");

        [HttpGet("custom-status")]
        [WtmFeatureGate("new-feature", FallbackStatusCode = 418)]
        public IActionResult CustomStatus() => Ok("custom-ok");
    }
}
