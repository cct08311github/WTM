#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for <see cref="WtmIpAllowListAttribute"/>: CIDR parsing,
    /// IPv4 / IPv6 subnet containment, comma-list / "addr:port" tolerance,
    /// constructor argument validation, and MVC integration via
    /// <c>X-Forwarded-For</c>.
    /// </summary>
    [TestClass]
    public class WtmIpAllowListAttributeTests
    {
        // ── Pure IsAllowed() ────────────────────────────────────────────

        [TestMethod]
        public void IsAllowed_matches_ipv4_inside_subnet()
        {
            var a = new WtmIpAllowListAttribute("10.0.0.0/8");
            Assert.IsTrue(a.IsAllowed("10.0.0.1"));
            Assert.IsTrue(a.IsAllowed("10.255.255.254"));
        }

        [TestMethod]
        public void IsAllowed_rejects_ipv4_outside_subnet()
        {
            var a = new WtmIpAllowListAttribute("10.0.0.0/8");
            Assert.IsFalse(a.IsAllowed("11.0.0.1"));
            Assert.IsFalse(a.IsAllowed("192.168.1.1"));
        }

        [TestMethod]
        public void IsAllowed_matches_single_host_with_slash_32()
        {
            var a = new WtmIpAllowListAttribute("203.0.113.77/32");
            Assert.IsTrue(a.IsAllowed("203.0.113.77"));
            Assert.IsFalse(a.IsAllowed("203.0.113.78"));
        }

        [TestMethod]
        public void IsAllowed_multiple_cidrs_are_OR_ed()
        {
            var a = new WtmIpAllowListAttribute("10.0.0.0/8", "192.168.0.0/16");
            Assert.IsTrue(a.IsAllowed("10.5.5.5"));
            Assert.IsTrue(a.IsAllowed("192.168.1.1"));
            Assert.IsFalse(a.IsAllowed("172.16.0.1"));
        }

        [TestMethod]
        public void IsAllowed_matches_ipv6_subnet()
        {
            var a = new WtmIpAllowListAttribute("2001:db8::/32");
            Assert.IsTrue(a.IsAllowed("2001:db8:0:1::1"));
            Assert.IsFalse(a.IsAllowed("2001:db9::1"));
        }

        [TestMethod]
        public void IsAllowed_matches_single_ipv6_host_with_slash_128()
        {
            var a = new WtmIpAllowListAttribute("2001:db8::1/128");
            Assert.IsTrue(a.IsAllowed("2001:db8::1"));
            Assert.IsFalse(a.IsAllowed("2001:db8::2"));
        }

        [TestMethod]
        public void IsAllowed_rejects_unknown_empty_and_unparsable()
        {
            var a = new WtmIpAllowListAttribute("10.0.0.0/8");
            Assert.IsFalse(a.IsAllowed(null));
            Assert.IsFalse(a.IsAllowed(""));
            Assert.IsFalse(a.IsAllowed("unknown"));
            Assert.IsFalse(a.IsAllowed("not-an-ip"));
        }

        [TestMethod]
        public void IsAllowed_tolerates_comma_list_by_using_first_entry()
        {
            // X-Forwarded-For commonly looks like "client, proxy1, proxy2".
            // GetRemoteIpAddress hands us the first entry as-is — here we
            // confirm the attribute parses the first IP cleanly when a
            // caller (or misconfigured proxy) emits a comma-list.
            var a = new WtmIpAllowListAttribute("10.0.0.0/8");
            Assert.IsTrue(a.IsAllowed("10.0.0.5, 1.2.3.4"));
            Assert.IsFalse(a.IsAllowed("1.2.3.4, 10.0.0.5"),
                "First IP in the comma-list must be authoritative.");
        }

        // ── Constructor validation ──────────────────────────────────────

        [TestMethod]
        public void Ctor_null_or_empty_cidr_array_throws()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                new WtmIpAllowListAttribute(Array.Empty<string>()));
            Assert.ThrowsException<ArgumentException>(() =>
                new WtmIpAllowListAttribute((string[])null!));
        }

        [TestMethod]
        public void Ctor_null_element_throws_with_index()
        {
            var ex = Assert.ThrowsException<ArgumentException>(() =>
                new WtmIpAllowListAttribute("10.0.0.0/8", null!));
            StringAssert.Contains(ex.Message, "index 1");
        }

        [TestMethod]
        public void Ctor_invalid_cidr_throws_with_index()
        {
            var ex = Assert.ThrowsException<ArgumentException>(() =>
                new WtmIpAllowListAttribute("10.0.0.0/8", "nope", "192.168.0.0/16"));
            StringAssert.Contains(ex.Message, "index 1");
            StringAssert.Contains(ex.Message, "nope");
        }

        // ── Diagnostic surface ──────────────────────────────────────────

        [TestMethod]
        public void Cidrs_exposes_source_values_in_order()
        {
            var a = new WtmIpAllowListAttribute("10.0.0.0/8", "192.168.0.0/16");
            Assert.AreEqual(2, a.Cidrs.Count);
            Assert.AreEqual("10.0.0.0/8", a.Cidrs[0]);
            Assert.AreEqual("192.168.0.0/16", a.Cidrs[1]);
        }

        // ── MVC integration ─────────────────────────────────────────────

        /// <summary>
        /// Issue #114 (back-compat): with <c>TrustForwardedForHeader=true</c>,
        /// a matching <c>X-Forwarded-For</c> header still allows access to a
        /// guarded action — this is the legacy behaviour preserved behind the opt-in flag.
        /// </summary>
        [TestMethod]
        public async Task Allowed_ip_in_xforwarded_reaches_action_when_trust_flag_set()
        {
            using var host = await BuildHostAsync(trustXff: true);
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/ipguard/restricted");
            req.Headers.Add("X-Forwarded-For", "10.5.5.5");
            var response = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("inside", await response.Content.ReadAsStringAsync());
        }

        /// <summary>
        /// Issue #114 (secure default): with the default
        /// <c>TrustForwardedForHeader=false</c>, a spoofed
        /// <c>X-Forwarded-For</c> header does NOT bypass the IP allow-list.
        /// The real connection IP (127.0.0.1 from TestServer) is used instead,
        /// which is not in the allow-list.
        /// </summary>
        [TestMethod]
        public async Task Spoofed_xff_does_not_bypass_ip_allowlist_by_default()
        {
            using var host = await BuildHostAsync(trustXff: false);
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/ipguard/restricted");
            req.Headers.Add("X-Forwarded-For", "10.5.5.5"); // spoofed — in the CIDR list
            var response = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                "Spoofed XFF must not bypass WtmIpAllowListAttribute under the secure default.");
        }

        [TestMethod]
        public async Task Rejected_ip_returns_403()
        {
            using var host = await BuildHostAsync(trustXff: true);
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/ipguard/restricted");
            req.Headers.Add("X-Forwarded-For", "198.51.100.5");
            var response = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [TestMethod]
        public async Task Custom_fallback_status_code_is_honoured()
        {
            using var host = await BuildHostAsync(trustXff: true);
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/ipguard/teapot");
            req.Headers.Add("X-Forwarded-For", "198.51.100.5");
            var response = await client.SendAsync(req);

            Assert.AreEqual((HttpStatusCode)418, response.StatusCode);
        }

        [TestMethod]
        public async Task Undecorated_action_ignores_ip_filter()
        {
            using var host = await BuildHostAsync(trustXff: false);
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/ipguard/open");
            req.Headers.Add("X-Forwarded-For", "198.51.100.5");
            var response = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // ── Scaffolding ─────────────────────────────────────────────────

        private static async Task<IHost> BuildHostAsync(bool trustXff = false)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.ConfigureServices(services =>
                    {
                        services.AddLogging();
                        services.AddControllers()
                                .AddApplicationPart(typeof(WtmIpAllowListAttributeTests).Assembly);
                        services.Configure<WalkingTec.Mvvm.Core.Configs>(c =>
                            c.TrustForwardedForHeader = trustXff);
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

    [ApiController]
    [Route("ipguard")]
    public class WtmIpAllowListFixtureController : ControllerBase
    {
        [HttpGet("restricted")]
        [WtmIpAllowList("10.0.0.0/8", "192.168.0.0/16")]
        public IActionResult Restricted() => Ok("inside");

        [HttpGet("teapot")]
        [WtmIpAllowList("10.0.0.0/8", FallbackStatusCode = 418)]
        public IActionResult Teapot() => Ok("inside");

        [HttpGet("open")]
        public IActionResult Open() => Ok("open");
    }
}
