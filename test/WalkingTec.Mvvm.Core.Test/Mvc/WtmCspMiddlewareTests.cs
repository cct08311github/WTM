#nullable enable
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for issue #789 Phase 3D: the opt-in CSP middleware that ships the
    /// eval-free security posture built up in Phases 1-3C to response headers.
    /// Core invariant: the default policy MUST NOT contain 'unsafe-eval'.
    /// </summary>
    [TestClass]
    public class WtmCspMiddlewareTests
    {
        private const string CspHeader = "Content-Security-Policy";
        private const string CspReportOnlyHeader = "Content-Security-Policy-Report-Only";

        // ── Pure-function tests on BuildHeaderValue ────────────────────────

        [TestMethod]
        public void BuildHeaderValue_default_options_emits_all_standard_directives()
        {
            var hv = WtmCspMiddleware.BuildHeaderValue(new WtmCspOptions());
            StringAssert.Contains(hv, "default-src 'self'");
            StringAssert.Contains(hv, "script-src 'self' 'unsafe-inline'");
            StringAssert.Contains(hv, "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com");
            StringAssert.Contains(hv, "img-src 'self' data: https:");
            StringAssert.Contains(hv, "font-src 'self' data: https://fonts.gstatic.com");
            StringAssert.Contains(hv, "connect-src 'self'");
            StringAssert.Contains(hv, "frame-src 'self'");
            StringAssert.Contains(hv, "object-src 'none'");
            StringAssert.Contains(hv, "base-uri 'self'");
            StringAssert.Contains(hv, "form-action 'self'");
        }

        [TestMethod]
        public void BuildHeaderValue_default_policy_does_not_contain_unsafe_eval()
        {
            var hv = WtmCspMiddleware.BuildHeaderValue(new WtmCspOptions());
            Assert.IsFalse(hv.Contains("'unsafe-eval'"),
                "Phase 3D invariant: default CSP must block 'unsafe-eval'. Got: " + hv);
        }

        [TestMethod]
        public void BuildHeaderValue_custom_ScriptSrc_overrides_default_and_can_strip_unsafe_inline()
        {
            var hv = WtmCspMiddleware.BuildHeaderValue(new WtmCspOptions
            {
                ScriptSrc = "'self'"
            });
            StringAssert.Contains(hv, "script-src 'self';");
            Assert.IsFalse(hv.Contains("script-src 'self' 'unsafe-inline'"),
                "Custom ScriptSrc should have replaced the default.");
        }

        [TestMethod]
        public void BuildHeaderValue_omits_report_uri_when_null()
        {
            var hv = WtmCspMiddleware.BuildHeaderValue(new WtmCspOptions { ReportUri = null });
            Assert.IsFalse(hv.Contains("report-uri"));
        }

        [TestMethod]
        public void BuildHeaderValue_includes_report_uri_when_set()
        {
            var hv = WtmCspMiddleware.BuildHeaderValue(new WtmCspOptions
            {
                ReportUri = "/csp-report"
            });
            StringAssert.Contains(hv, "report-uri /csp-report");
        }

        [TestMethod]
        public void BuildHeaderValue_skips_empty_directives()
        {
            var hv = WtmCspMiddleware.BuildHeaderValue(new WtmCspOptions
            {
                ConnectSrc = ""
            });
            Assert.IsFalse(hv.Contains("connect-src"));
            StringAssert.Contains(hv, "default-src 'self'");
        }

        // ── Integration tests via TestServer ───────────────────────────────

        private static async Task<IHost> BuildHostAsync(System.Action<IApplicationBuilder> configure)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.Configure(app =>
                    {
                        configure(app);
                        app.Run(async ctx => await ctx.Response.WriteAsync("OK"));
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }

        [TestMethod]
        public async Task UseWtmContentSecurityPolicy_default_emits_enforcing_header()
        {
            using var host = await BuildHostAsync(app => app.UseWtmContentSecurityPolicy());
            var client = host.GetTestClient();

            var response = await client.GetAsync("/any");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.Contains(CspHeader),
                $"Response should expose {CspHeader} header. Got: " + string.Join(",", response.Headers));
            var value = string.Join("", response.Headers.GetValues(CspHeader));
            StringAssert.Contains(value, "default-src 'self'");
            Assert.IsFalse(value.Contains("'unsafe-eval'"),
                "Default response CSP header must not contain 'unsafe-eval'. Got: " + value);
        }

        [TestMethod]
        public async Task UseWtmContentSecurityPolicy_report_only_uses_report_only_header_name()
        {
            using var host = await BuildHostAsync(app =>
                app.UseWtmContentSecurityPolicy(o => o.ReportOnly = true));
            var client = host.GetTestClient();

            var response = await client.GetAsync("/any");
            Assert.IsTrue(response.Headers.Contains(CspReportOnlyHeader));
            Assert.IsFalse(response.Headers.Contains(CspHeader),
                "ReportOnly=true should emit Content-Security-Policy-Report-Only, not Content-Security-Policy.");
        }

        [TestMethod]
        public async Task UseWtmContentSecurityPolicy_respects_existing_header_from_upstream_middleware()
        {
            var existing = "default-src 'none'";
            using var host = await BuildHostAsync(app =>
            {
                // Simulate an upstream middleware (or reverse-proxy-style
                // decoration) that synchronously sets the CSP header before
                // the WTM middleware gets a chance to register its
                // OnStarting callback. WtmCspMiddleware must observe the
                // already-set header and not overwrite it.
                app.Use(async (ctx, next) =>
                {
                    ctx.Response.Headers[CspHeader] = existing;
                    await next();
                });
                app.UseWtmContentSecurityPolicy();
            });
            var client = host.GetTestClient();

            var response = await client.GetAsync("/any");
            var value = string.Join("", response.Headers.GetValues(CspHeader));
            Assert.AreEqual(existing, value,
                "WtmCspMiddleware must not overwrite a CSP header set by upstream middleware.");
        }
    }
}
