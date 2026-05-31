#nullable enable
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for <see cref="HttpContextExtention.GetRemoteIpAddress"/>:
    /// <list type="bullet">
    ///   <item>Default (secure): spoofed X-Forwarded-For is ignored.</item>
    ///   <item>Back-compat opt-in: XFF is read when
    ///     <see cref="Configs.TrustForwardedForHeader"/> is <c>true</c>.</item>
    ///   <item>Maintenance-mode IP allow-list: spoofed XFF no longer bypasses
    ///     the 503 gate under the secure default.</item>
    /// </list>
    /// Issue #114.
    /// </summary>
    [TestClass]
    public class GetRemoteIpAddressTests
    {
        // ── Pure unit: no DI ────────────────────────────────────────────────

        [TestMethod]
        public void Returns_connection_ip_when_no_di_available()
        {
            // Simulate a context with no RequestServices (e.g. unit test
            // without full pipeline). Should fall through to Connection.
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.99");
            // No IOptions<Configs> in RequestServices → TrustForwardedForHeader defaults false.
            context.Request.Headers["X-Forwarded-For"] = "10.0.0.1";

            var ip = context.GetRemoteIpAddress();

            Assert.AreEqual("203.0.113.99", ip,
                "Should return Connection.RemoteIpAddress and ignore XFF when DI is unavailable.");
        }

        [TestMethod]
        public void Returns_unknown_when_connection_ip_null_and_no_xff()
        {
            var context = new DefaultHttpContext();
            // Connection.RemoteIpAddress is null by default in DefaultHttpContext.
            var ip = context.GetRemoteIpAddress();
            Assert.AreEqual("unknown", ip);
        }

        // ── With DI, TrustForwardedForHeader = false (default, secure) ─────

        [TestMethod]
        public void Default_ignores_xff_returns_connection_ip()
        {
            var services = new ServiceCollection();
            services.Configure<Configs>(c => c.TrustForwardedForHeader = false);
            var sp = services.BuildServiceProvider();

            var context = new DefaultHttpContext { RequestServices = sp };
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.5");
            context.Request.Headers["X-Forwarded-For"] = "10.0.0.1";

            var ip = context.GetRemoteIpAddress();

            Assert.AreEqual("198.51.100.5", ip,
                "Spoofed XFF must be ignored when TrustForwardedForHeader is false.");
        }

        [TestMethod]
        public void Default_config_false_ignores_xff()
        {
            // Simulate the factory default: no explicit value set → false.
            var services = new ServiceCollection();
            services.Configure<Configs>(_ => { /* no TrustForwardedForHeader set */ });
            var sp = services.BuildServiceProvider();

            var context = new DefaultHttpContext { RequestServices = sp };
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.42");
            context.Request.Headers["X-Forwarded-For"] = "1.2.3.4";

            var ip = context.GetRemoteIpAddress();

            Assert.AreEqual("198.51.100.42", ip);
        }

        // ── With DI, TrustForwardedForHeader = true (back-compat opt-in) ───

        [TestMethod]
        public void BackCompat_returns_xff_when_trust_flag_is_true()
        {
            var services = new ServiceCollection();
            services.Configure<Configs>(c => c.TrustForwardedForHeader = true);
            var sp = services.BuildServiceProvider();

            var context = new DefaultHttpContext { RequestServices = sp };
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.5");
            context.Request.Headers["X-Forwarded-For"] = "10.0.0.1";

            var ip = context.GetRemoteIpAddress();

            Assert.AreEqual("10.0.0.1", ip,
                "Should return XFF value when TrustForwardedForHeader is true (back-compat).");
        }

        [TestMethod]
        public void BackCompat_falls_back_to_connection_ip_when_xff_absent()
        {
            var services = new ServiceCollection();
            services.Configure<Configs>(c => c.TrustForwardedForHeader = true);
            var sp = services.BuildServiceProvider();

            var context = new DefaultHttpContext { RequestServices = sp };
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.5");
            // No X-Forwarded-For header set.

            var ip = context.GetRemoteIpAddress();

            Assert.AreEqual("198.51.100.5", ip,
                "When back-compat is enabled but no XFF header present, " +
                "should fall back to Connection.RemoteIpAddress.");
        }

        // ── Full-pipeline: maintenance-mode IP allow-list ──────────────────

        /// <summary>
        /// Before Issue #114 fix: a spoofed X-Forwarded-For bypassed the
        /// maintenance-mode IP allow-list, returning 200 instead of 503.
        /// After the fix (secure default): the spoofed header is ignored and
        /// the real Connection.RemoteIpAddress is used — which is NOT in the
        /// allow-list — so the 503 gate correctly fires.
        /// </summary>
        [TestMethod]
        public async Task SpoofedXff_does_not_bypass_maintenance_mode_by_default()
        {
            // Arrange: maintenance mode on, only "10.1.2.3" allowed.
            // Client sends X-Forwarded-For: 10.1.2.3 (spoofed).
            // TestServer's Connection.RemoteIpAddress is 127.0.0.1, which is NOT allowed.
            using var host = await BuildMaintenanceHostAsync(
                maintOpts =>
                {
                    maintOpts.Enabled = true;
                    maintOpts.AllowedClientIps.Add("10.1.2.3");
                },
                trustXff: false   // default secure behaviour
            );
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/resource");
            req.Headers.Add("X-Forwarded-For", "10.1.2.3"); // spoofed

            // Act
            var response = await client.SendAsync(req);

            // Assert: spoofed XFF ignored, real peer (127.0.0.1) not in allow-list → 503
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode,
                "Spoofed X-Forwarded-For must not bypass maintenance-mode allow-list " +
                "when TrustForwardedForHeader is false (secure default).");
        }

        /// <summary>
        /// With back-compat opt-in (<c>TrustForwardedForHeader=true</c>), the
        /// old XFF-first behaviour is preserved: a matching XFF header still
        /// bypasses maintenance mode (as before the fix).
        /// </summary>
        [TestMethod]
        public async Task BackCompat_xff_can_bypass_maintenance_mode_when_trust_flag_set()
        {
            using var host = await BuildMaintenanceHostAsync(
                maintOpts =>
                {
                    maintOpts.Enabled = true;
                    maintOpts.AllowedClientIps.Add("10.1.2.3");
                },
                trustXff: true   // legacy opt-in
            );
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/resource");
            req.Headers.Add("X-Forwarded-For", "10.1.2.3");

            var response = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "With TrustForwardedForHeader=true, XFF is trusted and matching " +
                "IP should bypass maintenance mode (back-compat).");
        }

        // ── Configs property defaults ───────────────────────────────────────

        [TestMethod]
        public void Configs_TrustForwardedForHeader_defaults_to_false()
        {
            var c = new Configs();
            Assert.IsFalse(c.TrustForwardedForHeader,
                "TrustForwardedForHeader must default to false (secure).");
        }

        // ── Scaffolding ────────────────────────────────────────────────────

        private static async Task<IHost> BuildMaintenanceHostAsync(
            System.Action<WtmMaintenanceModeOptions> maintConfigure,
            bool trustXff)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.Configure<Configs>(c =>
                            c.TrustForwardedForHeader = trustXff);
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseWtmMaintenanceMode(maintConfigure);
                        app.Run(ctx => ctx.Response.WriteAsync("ok"));
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }
    }
}
