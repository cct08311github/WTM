#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for the maintenance-mode middleware. Covers the zero-cost
    /// no-op path, static + dynamic toggles, path + IP allow-listing, and
    /// the shape of the 503 response.
    /// </summary>
    [TestClass]
    public class WtmMaintenanceModeMiddlewareTests
    {
        // ── No-op path ────────────────────────────────────────────────────

        [TestMethod]
        public async Task Disabled_middleware_is_transparent()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());
            Assert.IsFalse(response.Headers.Contains("Retry-After"));
        }

        // ── Static enable ─────────────────────────────────────────────────

        [TestMethod]
        public async Task Enabled_returns_503_problem_json_by_default()
        {
            using var host = await BuildHostAsync(o => o.Enabled = true);
            var client = host.GetTestClient();

            var response = await client.GetAsync("/some/endpoint");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual("application/problem+json",
                response.Content.Headers.ContentType?.MediaType);

            var retry = response.Headers.GetValues("Retry-After");
            Assert.AreEqual("60", retry.First());

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.AreEqual(503, doc.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual("Service Unavailable", doc.RootElement.GetProperty("title").GetString());
            StringAssert.Contains(doc.RootElement.GetProperty("detail").GetString(), "maintenance");
            Assert.AreEqual(60, doc.RootElement.GetProperty("retryAfterSeconds").GetInt32());
        }

        [TestMethod]
        public async Task RetryAfter_null_omits_header()
        {
            using var host = await BuildHostAsync(o =>
            {
                o.Enabled = true;
                o.RetryAfterSeconds = null;
            });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/foo");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.IsFalse(response.Headers.Contains("Retry-After"));
        }

        [TestMethod]
        public async Task Custom_status_code_is_honoured()
        {
            using var host = await BuildHostAsync(o =>
            {
                o.Enabled = true;
                o.StatusCode = 418; // I'm a teapot, for the lulz
            });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            Assert.AreEqual(418, (int)response.StatusCode);
        }

        // ── Allow-list: paths ─────────────────────────────────────────────

        [TestMethod]
        public async Task Allowed_path_prefix_bypasses_maintenance()
        {
            using var host = await BuildHostAsync(o => o.Enabled = true);
            var client = host.GetTestClient();

            var health = await client.GetAsync("/healthz");
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);

            var admin = await client.GetAsync("/_admin/MaintenanceMode");
            Assert.AreEqual(HttpStatusCode.OK, admin.StatusCode);

            var user = await client.GetAsync("/app/home");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, user.StatusCode);
        }

        [TestMethod]
        public async Task Custom_allowed_prefix_replaces_default_when_assigned()
        {
            using var host = await BuildHostAsync(o =>
            {
                o.Enabled = true;
                o.AllowedPathPrefixes = new System.Collections.Generic.List<string> { "/only-this" };
            });
            var client = host.GetTestClient();

            var hz = await client.GetAsync("/healthz");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, hz.StatusCode,
                "default prefixes no longer apply once the caller replaces the list");

            var ok = await client.GetAsync("/only-this/foo");
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        }

        // ── Allow-list: IP ────────────────────────────────────────────────

        /// <summary>
        /// Issue #114 (back-compat): with <c>TrustForwardedForHeader=true</c>,
        /// a matching <c>X-Forwarded-For</c> header still bypasses maintenance
        /// mode — this is the legacy behaviour preserved behind the opt-in flag.
        /// </summary>
        [TestMethod]
        public async Task Allowed_client_ip_via_xforwarded_bypasses_maintenance_when_trust_flag_set()
        {
            using var host = await BuildHostAsync(
                o =>
                {
                    o.Enabled = true;
                    o.AllowedClientIps.Add("10.1.2.3");
                },
                trustXff: true);  // opt-in to back-compat XFF-first behaviour
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/secret");
            req.Headers.Add("X-Forwarded-For", "10.1.2.3");
            var response = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        /// <summary>
        /// Issue #114 (secure default): with the default
        /// <c>TrustForwardedForHeader=false</c>, a spoofed
        /// <c>X-Forwarded-For</c> header does NOT bypass maintenance mode.
        /// The real connection IP (127.0.0.1 from TestServer) is used instead.
        /// </summary>
        [TestMethod]
        public async Task Spoofed_xff_does_not_bypass_maintenance_by_default()
        {
            using var host = await BuildHostAsync(
                o =>
                {
                    o.Enabled = true;
                    o.AllowedClientIps.Add("10.1.2.3");
                    // 127.0.0.1 (TestServer peer) is intentionally NOT in the list.
                },
                trustXff: false);  // secure default
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/secret");
            req.Headers.Add("X-Forwarded-For", "10.1.2.3"); // spoofed
            var response = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode,
                "Spoofed XFF must not bypass the maintenance-mode IP allow-list " +
                "under the secure default (TrustForwardedForHeader=false).");
        }

        [TestMethod]
        public async Task Unrelated_client_ip_still_gets_503()
        {
            using var host = await BuildHostAsync(
                o =>
                {
                    o.Enabled = true;
                    o.AllowedClientIps.Add("10.1.2.3");
                },
                trustXff: true);  // use back-compat to keep original test logic
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/secret");
            req.Headers.Add("X-Forwarded-For", "198.51.100.7");
            var response = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        // ── Dynamic toggle ────────────────────────────────────────────────

        [TestMethod]
        public async Task IsEnabled_delegate_overrides_static_flag()
        {
            var active = false;
            using var host = await BuildHostAsync(o =>
            {
                // Static says "off", delegate says "on" — delegate wins.
                o.Enabled = false;
                o.IsEnabled = _ => active;
            });
            var client = host.GetTestClient();

            var first = await client.GetAsync("/");
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);

            active = true;

            var second = await client.GetAsync("/");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, second.StatusCode);

            active = false;

            var third = await client.GetAsync("/");
            Assert.AreEqual(HttpStatusCode.OK, third.StatusCode);
        }

        [TestMethod]
        public async Task IsEnabled_delegate_throwing_falls_back_to_static_flag()
        {
            using var host = await BuildHostAsync(o =>
            {
                o.Enabled = true;
                o.IsEnabled = _ => throw new InvalidOperationException("flag service down");
            });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            // Delegate blew up; fall back to static Enabled=true → still 503.
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        // ── HTML body ─────────────────────────────────────────────────────

        [TestMethod]
        public async Task Html_content_type_emits_html_body()
        {
            using var host = await BuildHostAsync(o =>
            {
                o.Enabled = true;
                o.ContentType = "text/html; charset=utf-8";
                o.Title = "Down for Maintenance";
            });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "<html");
            StringAssert.Contains(body, "Down for Maintenance");
        }

        [TestMethod]
        public async Task Html_body_factory_overrides_default_body()
        {
            using var host = await BuildHostAsync(o =>
            {
                o.Enabled = true;
                o.ContentType = "text/html";
                o.HtmlBodyFactory = _ => "<p>Custom down page</p>";
            });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual("<p>Custom down page</p>", body);
        }

        // ── Extension argument validation ─────────────────────────────────

        [TestMethod]
        public void UseWtmMaintenanceMode_null_configure_throws()
        {
            var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmMaintenanceModeExtension.UseWtmMaintenanceMode(builder, (Action<WtmMaintenanceModeOptions>)null!));
        }

        [TestMethod]
        public void UseWtmMaintenanceMode_null_options_throws()
        {
            var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmMaintenanceModeExtension.UseWtmMaintenanceMode(builder, (WtmMaintenanceModeOptions)null!));
        }

        // ── Test scaffolding ──────────────────────────────────────────────

        private static async Task<IHost> BuildHostAsync(
            Action<WtmMaintenanceModeOptions>? configure = null,
            bool trustXff = false)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.Configure<WalkingTec.Mvvm.Core.Configs>(c =>
                            c.TrustForwardedForHeader = trustXff);
                    });
                    webBuilder.Configure(app =>
                    {
                        if (configure != null)
                        {
                            app.UseWtmMaintenanceMode(configure);
                        }
                        else
                        {
                            app.UseWtmMaintenanceMode();
                        }

                        app.Run(ctx => ctx.Response.WriteAsync("ok"));
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }
    }
}
