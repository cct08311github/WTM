#nullable enable
using System;
using System.Linq;
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
    /// Tests for <see cref="WtmServerTimingMiddleware"/>. Verifies the
    /// W3C Server-Timing header emission, path exclusions, minimum
    /// duration threshold, description escaping, token sanitization, and
    /// coexistence with upstream-set headers.
    /// </summary>
    [TestClass]
    public class WtmServerTimingMiddlewareTests
    {
        // ── Pure BuildHeaderValue function ───────────────────────────────

        [TestMethod]
        public void BuildHeaderValue_default_options_emits_app_dur()
        {
            var v = WtmServerTimingMiddleware.BuildHeaderValue(new WtmServerTimingOptions(), 123.456);
            Assert.AreEqual("app;dur=123.46", v);
        }

        [TestMethod]
        public void BuildHeaderValue_description_is_quoted_and_escaped()
        {
            var v = WtmServerTimingMiddleware.BuildHeaderValue(
                new WtmServerTimingOptions { Description = "db query \"orders\"" },
                10);
            Assert.AreEqual("app;dur=10;desc=\"db query \\\"orders\\\"\"", v);
        }

        [TestMethod]
        public void BuildHeaderValue_invalid_token_returns_empty()
        {
            // Token contains a forbidden separator per RFC 7230.
            var v = WtmServerTimingMiddleware.BuildHeaderValue(
                new WtmServerTimingOptions { MetricName = "bad name" }, 5);
            Assert.AreEqual(string.Empty, v,
                "Metric names containing space must yield empty header value rather than malformed output.");
        }

        [TestMethod]
        public void BuildHeaderValue_empty_metric_name_returns_empty()
        {
            var v = WtmServerTimingMiddleware.BuildHeaderValue(
                new WtmServerTimingOptions { MetricName = "" }, 5);
            Assert.AreEqual(string.Empty, v);
        }

        [TestMethod]
        public void BuildHeaderValue_null_options_throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmServerTimingMiddleware.BuildHeaderValue(null!, 5));
        }

        [TestMethod]
        public void BuildHeaderValue_culture_invariant_decimal_separator()
        {
            // Some cultures use ',' as decimal separator — verify header
            // always uses '.' regardless of the thread culture.
            var original = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var v = WtmServerTimingMiddleware.BuildHeaderValue(new WtmServerTimingOptions(), 12.34);
                StringAssert.Contains(v, "dur=12.34");
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = original;
            }
        }

        // ── Integration via TestServer ───────────────────────────────────

        [TestMethod]
        public async Task Default_options_emit_server_timing_header()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.Contains("Server-Timing"),
                "Default options must emit the Server-Timing header.");
            var value = response.Headers.GetValues("Server-Timing").First();
            StringAssert.Contains(value, "app;dur=");
        }

        [TestMethod]
        public async Task Excluded_path_emits_no_header()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/healthz");

            Assert.IsFalse(response.Headers.Contains("Server-Timing"),
                "Default /healthz exclusion must prevent header emission.");
        }

        [TestMethod]
        public async Task MinDurationMs_suppresses_header_on_fast_requests()
        {
            using var host = await BuildHostAsync(opt => opt.MinDurationMs = 999_999);
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            Assert.IsFalse(response.Headers.Contains("Server-Timing"),
                "Effectively-unreachable threshold must suppress the header.");
        }

        [TestMethod]
        public async Task Description_emitted_as_quoted_desc_parameter()
        {
            using var host = await BuildHostAsync(opt => opt.Description = "app handler");
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            var value = response.Headers.GetValues("Server-Timing").First();
            StringAssert.Contains(value, "desc=\"app handler\"");
        }

        [TestMethod]
        public async Task Upstream_server_timing_value_is_preserved_and_appended_to()
        {
            using var host = await BuildHostAsync(
                configureOptions: null,
                preSetter: ctx => ctx.Response.Headers.Append("Server-Timing", "cdn;dur=3"));
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            var values = response.Headers.GetValues("Server-Timing").ToList();
            Assert.IsTrue(values.Any(v => v.Contains("cdn;dur=3")),
                "Upstream Server-Timing value must be preserved (comma-list semantics).");
            Assert.IsTrue(values.Any(v => v.Contains("app;dur=")),
                "Middleware must append its own metric alongside upstream values.");
        }

        [TestMethod]
        public async Task Invalid_metric_name_results_in_no_header_rather_than_malformed()
        {
            using var host = await BuildHostAsync(opt => opt.MetricName = "bad name");
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            Assert.IsFalse(response.Headers.Contains("Server-Timing"),
                "Malformed token must suppress the header entirely rather than emit a broken value.");
        }

        // ── Extension argument validation ────────────────────────────────

        [TestMethod]
        public void UseWtmServerTiming_null_configure_throws()
        {
            var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmServerTimingExtension.UseWtmServerTiming(builder, null!));
        }

        // ── Scaffolding ──────────────────────────────────────────────────

        private static async Task<IHost> BuildHostAsync(
            Action<WtmServerTimingOptions>? configureOptions = null,
            Action<HttpContext>? preSetter = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.Configure(app =>
                    {
                        if (preSetter != null)
                        {
                            app.Use(async (ctx, next) =>
                            {
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
                            app.UseWtmServerTiming(configureOptions);
                        }
                        else
                        {
                            app.UseWtmServerTiming();
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
