#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.ConfigOptions
{
    /// <summary>
    /// Tests for Issue #422 — opt-in config eager validation via
    /// <see cref="WtmConfigValidationExtension.AddWtmConfigValidation"/>.
    ///
    /// All tests use real IConfiguration binding (AddInMemoryCollection) so that
    /// the validators read from the same IOptions&lt;Configs&gt; that production code uses,
    /// not from a manually-injected instance that would mask the JwtOption binding bug.
    /// </summary>
    [TestClass]
    public class WtmConfigValidationTests
    {
        // ── Valid base config keys ───────────────────────────────────────────────────

        private static readonly Dictionary<string, string?> _validConnectionConfig = new()
        {
            ["Connections:0:Key"]     = "default",
            ["Connections:0:Value"]   = "Server=.;Database=WTM;Trusted_Connection=True;",
            ["Connections:0:Enabled"] = "true",
        };

        private static readonly Dictionary<string, string?> _validJwtConfig = new()
        {
            ["JwtOptions:SecurityKey"] = "a-very-strong-and-unique-secret-key-for-production-use-12345",
            ["JwtOptions:Issuer"]      = "https://myapp.com",
            ["JwtOptions:Audience"]    = "https://myapp.com",
        };

        // ── Helper ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build a host whose <see cref="Configs"/> is bound from an in-memory
        /// IConfiguration — exactly how AddWtmContext binds it in production.
        /// </summary>
        private static async Task<IHost> BuildAndStartHostAsync(
            Dictionary<string, string?> configValues,
            bool addValidation = true)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            var host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    // Bind Configs from IConfiguration — mirrors AddWtmContext behaviour.
                    services.Configure<Configs>(config);
                    if (addValidation)
                        services.AddWtmConfigValidation();
                })
                .Build();

            await host.StartAsync();
            return host;
        }

        // ── Tests: Connections ───────────────────────────────────────────────────────

        [TestMethod]
        public async Task AddWtmConfigValidation_valid_connection_and_no_jwt_passes_on_start()
        {
            // Valid connection, no JwtOptions keys — JWT at factory defaults → no false-positive.
            using var host = await BuildAndStartHostAsync(_validConnectionConfig);
            await host.StopAsync();
        }

        [TestMethod]
        public async Task AddWtmConfigValidation_valid_connection_and_valid_jwt_passes_on_start()
        {
            var config = new Dictionary<string, string?>(_validConnectionConfig);
            foreach (var kv in _validJwtConfig) config[kv.Key] = kv.Value;

            using var host = await BuildAndStartHostAsync(config);
            await host.StopAsync();
        }

        [TestMethod]
        public async Task AddWtmConfigValidation_no_connections_throws_on_start()
        {
            // No Connections entries → must throw.
            await Assert.ThrowsExceptionAsync<OptionsValidationException>(
                () => BuildAndStartHostAsync(new Dictionary<string, string?>()));
        }

        [TestMethod]
        public async Task AddWtmConfigValidation_empty_value_connection_throws_on_start()
        {
            var config = new Dictionary<string, string?>
            {
                ["Connections:0:Key"]     = "default",
                ["Connections:0:Value"]   = "",
                ["Connections:0:Enabled"] = "true",
            };
            await Assert.ThrowsExceptionAsync<OptionsValidationException>(
                () => BuildAndStartHostAsync(config));
        }

        [TestMethod]
        public async Task AddWtmConfigValidation_all_connections_disabled_throws_on_start()
        {
            var config = new Dictionary<string, string?>
            {
                ["Connections:0:Key"]     = "default",
                ["Connections:0:Value"]   = "Server=.;Database=WTM;Trusted_Connection=True;",
                ["Connections:0:Enabled"] = "false",
            };
            await Assert.ThrowsExceptionAsync<OptionsValidationException>(
                () => BuildAndStartHostAsync(config));
        }

        // ── Tests: JWT ───────────────────────────────────────────────────────────────

        [TestMethod]
        public async Task AddWtmConfigValidation_default_jwt_key_but_custom_issuer_throws_on_start()
        {
            // Issuer changed (non-localhost) but SecurityKey not set → default/weak key.
            // IsJwtActive = true (Issuer != localhost). SecurityKey validator → FAIL.
            var config = new Dictionary<string, string?>(_validConnectionConfig)
            {
                ["JwtOptions:Issuer"] = "https://myapp.com",
                // SecurityKey intentionally absent → CLR default (weak key)
            };
            await Assert.ThrowsExceptionAsync<OptionsValidationException>(
                () => BuildAndStartHostAsync(config));
        }

        [TestMethod]
        public async Task AddWtmConfigValidation_valid_jwt_empty_issuer_throws_on_start()
        {
            var config = new Dictionary<string, string?>(_validConnectionConfig)
            {
                ["JwtOptions:SecurityKey"] = "a-very-strong-and-unique-secret-key-for-production-use-12345",
                ["JwtOptions:Issuer"]      = "",
                ["JwtOptions:Audience"]    = "https://myapp.com",
            };
            await Assert.ThrowsExceptionAsync<OptionsValidationException>(
                () => BuildAndStartHostAsync(config));
        }

        [TestMethod]
        public async Task AddWtmConfigValidation_valid_jwt_empty_audience_throws_on_start()
        {
            var config = new Dictionary<string, string?>(_validConnectionConfig)
            {
                ["JwtOptions:SecurityKey"] = "a-very-strong-and-unique-secret-key-for-production-use-12345",
                ["JwtOptions:Issuer"]      = "https://myapp.com",
                ["JwtOptions:Audience"]    = "",
            };
            await Assert.ThrowsExceptionAsync<OptionsValidationException>(
                () => BuildAndStartHostAsync(config));
        }

        [TestMethod]
        public async Task AddWtmConfigValidation_all_jwt_at_defaults_passes_on_start()
        {
            // JwtOptions section present but all values at factory defaults.
            // IsJwtActive: !IsFactoryDefaultKey()=false, Issuer=="http://localhost"=false,
            // Audience=="http://localhost"=false → IsJwtActive=false → validators skip → PASS.
            var config = new Dictionary<string, string?>(_validConnectionConfig)
            {
                ["JwtOptions:Issuer"]   = "http://localhost",
                ["JwtOptions:Audience"] = "http://localhost",
                // SecurityKey absent → factory default (weak)
            };
            using var host = await BuildAndStartHostAsync(config);
            await host.StopAsync();
        }

        /// <summary>
        /// Issue #923 coherence regression: a demo-shipped key ("super") combined with
        /// Issuer/Audience still at their localhost defaults must still throw.
        /// <c>IsJwtActive</c> uses <see cref="JwtOption.IsFactoryDefaultKey"/> (unchanged,
        /// narrow "is it literally the CLR default" semantics) specifically so that "super"
        /// — which is NOT the factory default — still evaluates IsJwtActive=true, letting
        /// the SecurityKey validator (which uses the real
        /// <see cref="JwtOption.IsWeakSigningKey"/> invariant) run and reject it. A version
        /// of <c>IsJwtActive</c> that widened its own SecurityKey check to
        /// <c>IsWeakSigningKey</c> would make THIS scenario evaluate IsJwtActive=false
        /// (since Issuer/Audience are still localhost) and the SecurityKey validator would
        /// be skipped — this test would then pass for the wrong reason (skipped, not
        /// rejected). See WtmConfigValidationExtension.IsJwtActive's remarks.
        /// </summary>
        [TestMethod]
        public async Task AddWtmConfigValidation_demo_security_key_super_with_localhost_issuer_audience_throws_on_start()
        {
            var config = new Dictionary<string, string?>(_validConnectionConfig)
            {
                ["JwtOptions:SecurityKey"] = "super",
                ["JwtOptions:Issuer"]      = "http://localhost",
                ["JwtOptions:Audience"]    = "http://localhost",
            };
            await Assert.ThrowsExceptionAsync<OptionsValidationException>(
                () => BuildAndStartHostAsync(config));
        }

        // ── Tests: opt-out ───────────────────────────────────────────────────────────

        [TestMethod]
        public async Task Without_AddWtmConfigValidation_invalid_config_does_not_throw_on_start()
        {
            // No Connections, no validation registered → must NOT throw (opt-in only).
            using var host = await BuildAndStartHostAsync(
                new Dictionary<string, string?>(),
                addValidation: false);
            await host.StopAsync();
        }
    }
}
