#nullable enable
using System;
using System.Net;
using System.Net.Http;
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
    /// Tests for issue #838 — WtmSecureHeadersMiddleware. Verifies default
    /// headers, per-header opt-out, HSTS gating on HTTPS, and the
    /// first-writer-wins rule.
    /// </summary>
    [TestClass]
    public class WtmSecureHeadersMiddlewareTests
    {
        // ── BuildHstsValue pure function ──────────────────────────────────

        [TestMethod]
        public void BuildHstsValue_default_options_emits_max_age_and_subdomains()
        {
            var hv = WtmSecureHeadersMiddleware.BuildHstsValue(new WtmSecureHeadersOptions());
            Assert.AreEqual("max-age=31536000; includeSubDomains", hv);
        }

        [TestMethod]
        public void BuildHstsValue_preload_appended_when_opted_in()
        {
            var hv = WtmSecureHeadersMiddleware.BuildHstsValue(new WtmSecureHeadersOptions
            {
                HstsIncludePreload = true,
            });
            Assert.AreEqual("max-age=31536000; includeSubDomains; preload", hv);
        }

        [TestMethod]
        public void BuildHstsValue_subdomains_off_omits_directive()
        {
            var hv = WtmSecureHeadersMiddleware.BuildHstsValue(new WtmSecureHeadersOptions
            {
                HstsIncludeSubDomains = false,
            });
            Assert.AreEqual("max-age=31536000", hv);
        }

        [TestMethod]
        public void BuildHstsValue_negative_max_age_is_clamped_to_zero()
        {
            var hv = WtmSecureHeadersMiddleware.BuildHstsValue(new WtmSecureHeadersOptions
            {
                HstsMaxAgeSeconds = -5,
                HstsIncludeSubDomains = false,
            });
            Assert.AreEqual("max-age=0", hv);
        }

        [TestMethod]
        public void BuildHstsValue_null_options_throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmSecureHeadersMiddleware.BuildHstsValue(null!));
        }

        // ── Integration: default headers emitted ──────────────────────────

        [TestMethod]
        public async Task Default_options_emit_nosniff_frame_referrer_permissions()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").First());
            Assert.AreEqual("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").First());
            Assert.AreEqual("strict-origin-when-cross-origin",
                response.Headers.GetValues("Referrer-Policy").First());
            StringAssert.Contains(
                response.Headers.GetValues("Permissions-Policy").First(),
                "geolocation=()");
        }

        [TestMethod]
        public async Task Null_header_value_disables_that_header()
        {
            using var host = await BuildHostAsync(opt =>
            {
                opt.XFrameOptions = null;
                opt.ReferrerPolicy = null;
            });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            Assert.IsFalse(response.Headers.Contains("X-Frame-Options"));
            Assert.IsFalse(response.Headers.Contains("Referrer-Policy"));
            // Other headers still emitted.
            Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").First());
        }

        [TestMethod]
        public async Task Custom_header_value_overrides_default()
        {
            using var host = await BuildHostAsync(opt =>
            {
                opt.XFrameOptions = "DENY";
            });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            Assert.AreEqual("DENY", response.Headers.GetValues("X-Frame-Options").First());
        }

        // ── HSTS gating ───────────────────────────────────────────────────

        [TestMethod]
        public async Task HSTS_not_emitted_on_HTTP_request_even_when_enabled()
        {
            using var host = await BuildHostAsync(opt => opt.HstsEnabled = true);
            var client = host.GetTestClient();
            // TestClient default scheme is http — simulates behind-proxy
            // termination where upstream already serves HTTP to the app.

            var response = await client.GetAsync("http://localhost/");

            Assert.IsFalse(response.Headers.Contains("Strict-Transport-Security"),
                "HSTS must be gated on IsHttps; sending it on HTTP is either ignored or counter-productive");
        }

        [TestMethod]
        public async Task HSTS_emitted_on_HTTPS_request_when_enabled()
        {
            using var host = await BuildHostAsync(opt => opt.HstsEnabled = true);
            var client = host.GetTestClient();

            // TestServer exposes BaseAddress; requesting https:// causes
            // Request.Scheme = "https".
            client.BaseAddress = new Uri("https://localhost/");
            var response = await client.GetAsync("/");

            Assert.IsTrue(response.Headers.Contains("Strict-Transport-Security"));
            StringAssert.Contains(
                response.Headers.GetValues("Strict-Transport-Security").First(),
                "max-age=");
        }

        [TestMethod]
        public async Task HSTS_default_off_means_no_header_even_on_HTTPS()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();
            client.BaseAddress = new Uri("https://localhost/");

            var response = await client.GetAsync("/");

            Assert.IsFalse(response.Headers.Contains("Strict-Transport-Security"),
                "HstsEnabled default is false — explicit opt-in required");
        }

        // ── First-writer-wins ─────────────────────────────────────────────

        [TestMethod]
        public async Task First_writer_wins_upstream_header_is_preserved()
        {
            // An "upstream" middleware that pre-sets X-Frame-Options simulates
            // a reverse proxy that already hardened the response. Our
            // middleware must not overwrite.
            using var host = await BuildHostAsync(
                configureOptions: null,
                preSetter: ctx => ctx.Response.Headers["X-Frame-Options"] = "DENY");

            var client = host.GetTestClient();
            var response = await client.GetAsync("/");

            Assert.AreEqual("DENY", response.Headers.GetValues("X-Frame-Options").First(),
                "first-writer-wins: upstream header must be preserved");
        }

        // ── Extension argument validation ─────────────────────────────────

        [TestMethod]
        public void UseWtmSecureHeaders_overload_throws_on_null_configure()
        {
            var builder = new Microsoft.AspNetCore.Builder.ApplicationBuilder(
                new ServiceCollection().BuildServiceProvider());
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmSecureHeadersExtension.UseWtmSecureHeaders(builder, null!));
        }

        // ── Test scaffolding ──────────────────────────────────────────────

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
                            app.Use(async (ctx, next) =>
                            {
                                // Pre-set header before downstream middleware runs,
                                // before OnStarting hooks fire.
                                ctx.Response.OnStarting(() =>
                                {
                                    preSetter(ctx);
                                    return Task.CompletedTask;
                                });
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

    // Local using alias to keep .First() readable without importing System.Linq
    // at the top (keeps test file focused on HTTP shape).
    internal static class _HeaderLinq
    {
        public static string First(this System.Collections.Generic.IEnumerable<string> source)
        {
            foreach (var s in source) return s;
            throw new InvalidOperationException("no header values");
        }
    }
}
