#nullable enable
using System;
using System.Linq;
using System.Net;
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
    /// Tests for issue #843 — WtmSecureHeadersOptions.Overwrite flag.
    /// Verifies the new defense-in-depth path overwrites upstream
    /// values, the default keeps first-writer-wins behaviour, and
    /// null/whitespace values stay no-op in either mode.
    /// </summary>
    [TestClass]
    public class WtmSecureHeadersOverwriteTests
    {
        // ── Pure SetHeader function ─────────────────────────────────────

        [TestMethod]
        public void SetHeader_overwrite_false_preserves_existing_value()
        {
            var ctx = new DefaultHttpContext();
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            WtmSecureHeadersMiddleware.SetHeader(ctx.Response.Headers, "X-Frame-Options", "SAMEORIGIN", overwrite: false);

            Assert.AreEqual("DENY", ctx.Response.Headers["X-Frame-Options"].ToString(),
                "Default first-writer-wins must keep the existing DENY.");
        }

        [TestMethod]
        public void SetHeader_overwrite_true_replaces_existing_value()
        {
            var ctx = new DefaultHttpContext();
            ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            WtmSecureHeadersMiddleware.SetHeader(ctx.Response.Headers, "X-Frame-Options", "DENY", overwrite: true);

            Assert.AreEqual("DENY", ctx.Response.Headers["X-Frame-Options"].ToString(),
                "Overwrite=true must replace the upstream value (#843).");
        }

        [TestMethod]
        public void SetHeader_null_value_is_noop_under_overwrite_true()
        {
            var ctx = new DefaultHttpContext();
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            WtmSecureHeadersMiddleware.SetHeader(ctx.Response.Headers, "X-Frame-Options", null, overwrite: true);

            Assert.AreEqual("DENY", ctx.Response.Headers["X-Frame-Options"].ToString(),
                "Setting Options.X = null must NEVER cause Overwrite to clobber the existing value with empty.");
        }

        [TestMethod]
        public void SetHeader_whitespace_value_is_noop_under_overwrite_true()
        {
            var ctx = new DefaultHttpContext();
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            WtmSecureHeadersMiddleware.SetHeader(ctx.Response.Headers, "X-Frame-Options", "   ", overwrite: true);

            Assert.AreEqual("DENY", ctx.Response.Headers["X-Frame-Options"].ToString());
        }

        // ── End-to-end via TestServer ───────────────────────────────────

        [TestMethod]
        public async Task Default_first_writer_wins_preserves_upstream_value()
        {
            using var host = await BuildHostAsync(
                preSetter: ctx => ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN");
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");
            // Middleware default = SAMEORIGIN — same as the pre-set value.
            // Confirm value present and only once.
            var values = response.Headers.GetValues("X-Frame-Options").ToList();
            Assert.AreEqual(1, values.Count);
            Assert.AreEqual("SAMEORIGIN", values[0]);
        }

        [TestMethod]
        public async Task Overwrite_true_replaces_upstream_with_application_value()
        {
            // Application wants DENY (stricter) but upstream proxy injected SAMEORIGIN.
            using var host = await BuildHostAsync(
                configureOptions: o =>
                {
                    o.Overwrite = true;
                    o.XFrameOptions = "DENY";
                },
                preSetter: ctx => ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN");
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");
            var values = response.Headers.GetValues("X-Frame-Options").ToList();
            Assert.AreEqual(1, values.Count, "Overwrite must REPLACE, not append.");
            Assert.AreEqual("DENY", values[0]);
        }

        [TestMethod]
        public async Task Overwrite_true_does_not_emit_disabled_headers()
        {
            // Setting X = null disables that specific header. Overwrite=true
            // must respect the disable, not 'overwrite with empty'.
            using var host = await BuildHostAsync(
                configureOptions: o =>
                {
                    o.Overwrite = true;
                    o.XFrameOptions = null;
                },
                preSetter: ctx => ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN");
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");
            var values = response.Headers.GetValues("X-Frame-Options").ToList();
            Assert.AreEqual(1, values.Count);
            Assert.AreEqual("SAMEORIGIN", values[0],
                "Disabled header (XFrameOptions = null) must not be touched even under Overwrite=true.");
        }

        [TestMethod]
        public async Task Overwrite_does_not_force_HSTS_on_HTTP()
        {
            // The HTTPS gate on HSTS is independent of Overwrite — sending
            // HSTS on plain HTTP is either ignored or counter-productive.
            using var host = await BuildHostAsync(configureOptions: o =>
            {
                o.Overwrite = true;
                o.HstsEnabled = true;
            });
            var client = host.GetTestClient();

            var response = await client.GetAsync("http://localhost/");
            Assert.IsFalse(response.Headers.Contains("Strict-Transport-Security"),
                "Overwrite=true must NOT bypass the HTTPS-only gate on HSTS.");
        }

        // ── Scaffolding ─────────────────────────────────────────────────

        private static async Task<IHost> BuildHostAsync(
            Action<WtmSecureHeadersOptions>? configureOptions = null,
            Action<HttpContext>? preSetter = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.Configure(app =>
                    {
                        if (preSetter != null)
                        {
                            // Write directly — this models an upstream
                            // sibling middleware (or a mocked reverse-
                            // proxy header) that has already populated
                            // the header before WtmSecureHeaders runs.
                            // OnStarting wrapping would fire AFTER the
                            // middleware's own OnStarting (LIFO),
                            // masking what we are trying to verify.
                            app.Use(async (ctx, next) =>
                            {
                                preSetter(ctx);
                                await next();
                            });
                        }

                        if (configureOptions != null)
                        {
                            app.UseWtmSecureHeaders(configureOptions);
                        }
                        else
                        {
                            app.UseWtmSecureHeaders();
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
