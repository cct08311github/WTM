#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// Issue #948 (review finding F2): the built-in config-driven egress policy that lets
    /// a host restore a specific pre-upgrade internal REST widget destination without
    /// writing a custom IDashboardEgressPolicy implementation.
    /// </summary>
    [TestClass]
    public class ConfiguredAllowlistDashboardEgressPolicyTests
    {
        private static IOptionsMonitor<DashboardEgressAllowlistOptions> MonitorFor(DashboardEgressAllowlistOptions options)
            => new StaticOptionsMonitor(options);

        private static DashboardEgressDestination Destination(
            string requestHost, string resolvedIp, int port, bool isPrivate = true, bool isPlainHttp = false,
            string? method = null, string? tenantId = null) => new()
        {
            RequestUri = new Uri($"{(isPlainHttp ? "http" : "https")}://{requestHost}/data"),
            ResolvedAddress = IPAddress.Parse(resolvedIp),
            Port = port,
            IsPrivateNetwork = isPrivate,
            IsPlainHttp = isPlainHttp,
            Method = method,
            TenantId = tenantId,
        };

        [TestMethod]
        public async Task IsAllowedAsync_NoEntries_DeniesEverything()
        {
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(
                MonitorFor(new DashboardEgressAllowlistOptions()));

            var allowed = await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 8080));
            Assert.IsFalse(allowed, "an empty allowlist must deny, same as no policy at all");
        }

        [TestMethod]
        public async Task IsAllowedAsync_MatchingResolvedIpAndPort_Allows()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", Ports = new[] { 8080 } } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            var allowed = await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 8080));
            Assert.IsTrue(allowed);
        }

        [TestMethod]
        public async Task IsAllowedAsync_MatchingIpWrongPort_Denies()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", Ports = new[] { 8080 } } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            var allowed = await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 9090));
            Assert.IsFalse(allowed, "a port not in the entry's Ports list must be denied");
        }

        [TestMethod]
        public async Task IsAllowedAsync_NullPorts_AllowsAnyPort()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", Ports = null } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            Assert.IsTrue(await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 1)));
            Assert.IsTrue(await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 65535)));
        }

        [TestMethod]
        public async Task IsAllowedAsync_MatchingHostname_Allows()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "internal.example" } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            // Resolved IP is arbitrary/different from the entry — hostname match still applies.
            var allowed = await policy.IsAllowedAsync(Destination("internal.example", "192.168.9.9", 443));
            Assert.IsTrue(allowed);
        }

        [TestMethod]
        public async Task IsAllowedAsync_HostnameMatchIsCaseInsensitive()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "Internal.Example" } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            var allowed = await policy.IsAllowedAsync(Destination("internal.example", "10.0.0.1", 443));
            Assert.IsTrue(allowed);
        }

        [TestMethod]
        public async Task IsAllowedAsync_NonMatchingDestination_Denies()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3" } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            var allowed = await policy.IsAllowedAsync(Destination("other.example", "10.9.9.9", 443));
            Assert.IsFalse(allowed);
        }

        [TestMethod]
        public async Task IsAllowedAsync_SecondEntryMatches_Allows()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries =
                {
                    new DashboardEgressAllowlistEntry { Host = "10.1.2.3" },
                    new DashboardEgressAllowlistEntry { Host = "10.9.9.9" },
                }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            var allowed = await policy.IsAllowedAsync(Destination("other.example", "10.9.9.9", 443));
            Assert.IsTrue(allowed);
        }

        // ── #948-F8: Methods / TenantId gating ────────────────────────────────
        // A destination field with no in-repo consumer is speculative generalization — these
        // tests, plus ConfiguredAllowlistDashboardEgressPolicy.IsAllowedAsync's Methods/TenantId
        // checks, are that real consumer. Deleting either check turns the corresponding "Denies"
        // test below red without affecting the others — see
        // test/mutants/entries/948f8-configuredallowlist-method-check-neutralize.json for the
        // Methods check specifically.

        [TestMethod]
        public async Task IsAllowedAsync_MatchingMethod_Allows()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", Methods = new[] { "GET" } } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            var allowed = await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 443, method: "GET"));
            Assert.IsTrue(allowed);
        }

        [TestMethod]
        public async Task IsAllowedAsync_NonMatchingMethod_Denies()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", Methods = new[] { "GET" } } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            var allowed = await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 443, method: "POST"));
            Assert.IsFalse(allowed, "an entry restricted to GET must not approve a POST destination");
        }

        [TestMethod]
        public async Task IsAllowedAsync_MethodMatchIsCaseInsensitive()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", Methods = new[] { "get" } } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            Assert.IsTrue(await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 443, method: "GET")));
        }

        [TestMethod]
        public async Task IsAllowedAsync_NullMethods_AllowsAnyMethod()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", Methods = null } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            Assert.IsTrue(await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 443, method: "POST")));
        }

        [TestMethod]
        public async Task IsAllowedAsync_MatchingTenantId_Allows()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", TenantId = "tenant-a" } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            var allowed = await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 443, tenantId: "tenant-a"));
            Assert.IsTrue(allowed);
        }

        [TestMethod]
        public async Task IsAllowedAsync_NonMatchingTenantId_Denies()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", TenantId = "tenant-a" } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            var allowed = await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 443, tenantId: "tenant-b"));
            Assert.IsFalse(allowed, "an entry scoped to tenant-a must not approve a tenant-b destination");
        }

        [TestMethod]
        public async Task IsAllowedAsync_TenantScopedEntry_DeniesDestinationWithNoTenant()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", TenantId = "tenant-a" } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            var allowed = await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 443, tenantId: null));
            Assert.IsFalse(allowed, "a tenant-scoped entry must not approve a destination with no tenant at all");
        }

        [TestMethod]
        public async Task IsAllowedAsync_NullEntryTenantId_AllowsAnyTenant()
        {
            var options = new DashboardEgressAllowlistOptions
            {
                Entries = { new DashboardEgressAllowlistEntry { Host = "10.1.2.3", TenantId = null } }
            };
            var policy = new ConfiguredAllowlistDashboardEgressPolicy(MonitorFor(options));

            Assert.IsTrue(await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 443, tenantId: "any-tenant")));
            Assert.IsTrue(await policy.IsAllowedAsync(Destination("internal.example", "10.1.2.3", 443, tenantId: null)));
        }

        private sealed class StaticOptionsMonitor : IOptionsMonitor<DashboardEgressAllowlistOptions>
        {
            public StaticOptionsMonitor(DashboardEgressAllowlistOptions value) => CurrentValue = value;
            public DashboardEgressAllowlistOptions CurrentValue { get; }
            public DashboardEgressAllowlistOptions Get(string? name) => CurrentValue;
            public IDisposable OnChange(Action<DashboardEgressAllowlistOptions, string?> listener) => NullDisposable.Instance;

            private sealed class NullDisposable : IDisposable
            {
                public static readonly NullDisposable Instance = new();
                public void Dispose() { }
            }
        }
    }
}
