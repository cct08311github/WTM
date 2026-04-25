#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text;
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
    /// Tests for issue #845 — WtmCspReportMiddleware. Covers report
    /// parsing for both envelope shapes, path matching, method gate,
    /// rate limiting, body-size cap, OnReport callback dispatch, and
    /// the structured-log fallback.
    /// </summary>
    [TestClass]
    public class WtmCspReportMiddlewareTests
    {
        private const string CanonicalReportBody =
            """{"csp-report":{"document-uri":"https://app.example/page","violated-directive":"script-src","blocked-uri":"https://evil.example/x.js","line-number":42}}""";

        // ── Pure ParseReport ────────────────────────────────────────────

        [TestMethod]
        public void ParseReport_canonical_envelope_unwrapped()
        {
            var r = WtmCspReportMiddleware.ParseReport(CanonicalReportBody);
            Assert.IsNotNull(r);
            Assert.AreEqual("https://app.example/page", r!.DocumentUri);
            Assert.AreEqual("script-src", r.ViolatedDirective);
            Assert.AreEqual("https://evil.example/x.js", r.BlockedUri);
            Assert.AreEqual(42, r.LineNumber);
        }

        [TestMethod]
        public void ParseReport_flat_object_also_works_for_legacy_clients()
        {
            // Some non-Chromium browsers / proxies post the inner record
            // directly, without the {"csp-report": ...} envelope.
            var flat = """{"document-uri":"https://app.example/p","violated-directive":"img-src"}""";
            var r = WtmCspReportMiddleware.ParseReport(flat);
            Assert.IsNotNull(r);
            Assert.AreEqual("img-src", r!.ViolatedDirective);
        }

        [TestMethod]
        public void ParseReport_null_or_empty_returns_null()
        {
            Assert.IsNull(WtmCspReportMiddleware.ParseReport(""));
            Assert.IsNull(WtmCspReportMiddleware.ParseReport("   "));
        }

        // ParseReport's exception type for invalid JSON is an
        // implementation detail of System.Text.Json; the *public*
        // contract (malformed body → HTTP 400) is covered by the
        // Malformed_body_returns_400 integration test below, which
        // exercises the middleware's try/catch around ParseReport.

        // ── End-to-end via TestServer ───────────────────────────────────

        [TestMethod]
        public async Task Posting_a_report_returns_204_and_invokes_OnReport()
        {
            CspViolationReport? captured = null;
            using var host = await BuildHostAsync(opt =>
            {
                opt.OnReport = r => { captured = r; return Task.CompletedTask; };
            });
            var client = host.GetTestClient();

            var response = await PostReport(client, "/_csp/report", CanonicalReportBody);

            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            Assert.IsNotNull(captured);
            Assert.AreEqual("script-src", captured!.ViolatedDirective);
        }

        [TestMethod]
        public async Task Custom_path_is_honoured()
        {
            using var host = await BuildHostAsync(opt => opt.Path = "/custom/csp");
            var client = host.GetTestClient();

            var response = await PostReport(client, "/custom/csp", CanonicalReportBody);
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        }

        [TestMethod]
        public async Task Non_matching_path_falls_through()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/some/other/path");
            // Inner handler responds with 200 + "ok".
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task Non_post_method_falls_through_to_inner_handler()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            // Inner handler also matches /_csp/report because the
            // pipeline's app.Run runs only when nothing earlier writes.
            // GET goes through the middleware (path matches) but the
            // method gate lets it pass to the inner handler.
            var response = await client.GetAsync("/_csp/report");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task Malformed_body_returns_400()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await PostReport(client, "/_csp/report", "not-json");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [TestMethod]
        public async Task Body_exceeding_cap_returns_413()
        {
            using var host = await BuildHostAsync(opt => opt.MaxBodyBytes = 100);
            var client = host.GetTestClient();

            var oversized = new string('a', 200);
            var body = "{\"csp-report\":{\"document-uri\":\"" + oversized + "\"}}";
            var response = await PostReport(client, "/_csp/report", body);
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }

        [TestMethod]
        public async Task Rate_limit_returns_429_after_exceeding_quota()
        {
            using var host = await BuildHostAsync(opt =>
            {
                opt.RateLimitPerMinute = 2;
            });
            var client = host.GetTestClient();

            var first = await PostReport(client, "/_csp/report", CanonicalReportBody);
            var second = await PostReport(client, "/_csp/report", CanonicalReportBody);
            var third = await PostReport(client, "/_csp/report", CanonicalReportBody);

            Assert.AreEqual(HttpStatusCode.NoContent, first.StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, second.StatusCode);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, third.StatusCode,
                "Third report within the same window must be rejected with 429.");
        }

        [TestMethod]
        public async Task Rate_limit_zero_disables_throttle()
        {
            using var host = await BuildHostAsync(opt => opt.RateLimitPerMinute = 0);
            var client = host.GetTestClient();

            // 5 reports through; all must succeed.
            for (var i = 0; i < 5; i++)
            {
                var response = await PostReport(client, "/_csp/report", CanonicalReportBody);
                Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, $"call {i + 1}");
            }
        }

        [TestMethod]
        public async Task OnReport_callback_exception_does_not_propagate()
        {
            using var host = await BuildHostAsync(opt =>
            {
                opt.OnReport = _ => throw new InvalidOperationException("dispatcher down");
            });
            var client = host.GetTestClient();

            // Browser still gets 204; the failure is logged at Warning.
            var response = await PostReport(client, "/_csp/report", CanonicalReportBody);
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        }

        [TestMethod]
        public void UseWtmCspReport_null_configure_throws()
        {
            var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmCspReportExtension.UseWtmCspReport(builder, null!));
        }

        // ── Scaffolding ─────────────────────────────────────────────────

        private static Task<HttpResponseMessage> PostReport(HttpClient client, string path, string body)
        {
            var content = new StringContent(body, Encoding.UTF8, "application/csp-report");
            return client.PostAsync(path, content);
        }

        private static async Task<IHost> BuildHostAsync(Action<WtmCspReportOptions>? configure = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.ConfigureServices(s => s.AddLogging());
                    w.Configure(app =>
                    {
                        if (configure != null)
                        {
                            app.UseWtmCspReport(configure);
                        }
                        else
                        {
                            app.UseWtmCspReport();
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
