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
    /// Tests for issues #844 (FrameAncestors directive) and #846
    /// (WtmCspMode three-state enum). Covers BuildHeaderValue purity,
    /// Mode-based header-name resolution, end-to-end emission via the
    /// MVC pipeline, and back-compat with the legacy ReportOnly bool.
    /// </summary>
    [TestClass]
    public class WtmCspMiddlewareModeAndFrameAncestorsTests
    {
        // ── #844: FrameAncestors directive ──────────────────────────────

        [TestMethod]
        public void BuildHeaderValue_default_options_emits_frame_ancestors_none()
        {
            var v = WtmCspMiddleware.BuildHeaderValue(new WtmCspOptions());
            StringAssert.Contains(v, "frame-ancestors 'none'");
        }

        [TestMethod]
        public void BuildHeaderValue_null_frame_ancestors_omits_directive()
        {
            var v = WtmCspMiddleware.BuildHeaderValue(new WtmCspOptions { FrameAncestors = null });
            Assert.IsFalse(v.Contains("frame-ancestors"),
                "Setting FrameAncestors to null must drop the directive entirely.");
        }

        [TestMethod]
        public void BuildHeaderValue_custom_frame_ancestors_emitted_verbatim()
        {
            var v = WtmCspMiddleware.BuildHeaderValue(new WtmCspOptions
            {
                FrameAncestors = "'self' https://admin.example.com",
            });
            StringAssert.Contains(v, "frame-ancestors 'self' https://admin.example.com");
        }

        [TestMethod]
        public void BuildHeaderValue_frame_src_and_frame_ancestors_are_independent()
        {
            // frame-src is outward (what THIS page may embed); frame-ancestors
            // is inward (who may embed THIS page). They must coexist.
            var v = WtmCspMiddleware.BuildHeaderValue(new WtmCspOptions
            {
                FrameSrc = "'self'",
                FrameAncestors = "'none'",
            });
            StringAssert.Contains(v, "frame-src 'self'");
            StringAssert.Contains(v, "frame-ancestors 'none'");
        }

        // ── #846: WtmCspMode three-state ────────────────────────────────

        [TestMethod]
        public void ResolveHeaderName_default_mode_emits_enforce_header()
        {
            var name = WtmCspMiddleware.ResolveHeaderName(new WtmCspOptions());
            Assert.AreEqual("Content-Security-Policy", name);
        }

        [TestMethod]
        public void ResolveHeaderName_report_only_mode_emits_report_only_header()
        {
            var name = WtmCspMiddleware.ResolveHeaderName(new WtmCspOptions
            {
                Mode = WtmCspMode.ReportOnly,
            });
            Assert.AreEqual("Content-Security-Policy-Report-Only", name);
        }

        [TestMethod]
        public void ResolveHeaderName_disabled_returns_null()
        {
            var name = WtmCspMiddleware.ResolveHeaderName(new WtmCspOptions
            {
                Mode = WtmCspMode.Disabled,
            });
            Assert.IsNull(name, "Disabled mode must signal 'emit nothing' via null.");
        }

        [TestMethod]
#pragma warning disable CS0618 // testing the legacy bool path on purpose
        public void ResolveHeaderName_legacy_ReportOnly_bool_still_works_when_Mode_default()
        {
            var name = WtmCspMiddleware.ResolveHeaderName(new WtmCspOptions
            {
                ReportOnly = true,
                // Mode left at default (Enforce).
            });
            Assert.AreEqual("Content-Security-Policy-Report-Only", name,
                "Pre-#846 callers using ReportOnly=true must keep working without code changes.");
        }
#pragma warning restore CS0618

        [TestMethod]
        public void ResolveHeaderName_null_options_throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmCspMiddleware.ResolveHeaderName(null!));
        }

        // ── End-to-end via TestServer ───────────────────────────────────

        [TestMethod]
        public async Task Disabled_mode_emits_no_csp_header_at_all()
        {
            using var host = await BuildHostAsync(o => o.Mode = WtmCspMode.Disabled);
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsFalse(response.Headers.Contains("Content-Security-Policy"));
            Assert.IsFalse(response.Headers.Contains("Content-Security-Policy-Report-Only"),
                "Disabled mode must NOT emit either CSP header — that is the whole point of #846.");
        }

        [TestMethod]
        public async Task ReportOnly_mode_emits_report_only_header_with_frame_ancestors()
        {
            using var host = await BuildHostAsync(o => o.Mode = WtmCspMode.ReportOnly);
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");
            var v = response.Headers.GetValues("Content-Security-Policy-Report-Only").First();
            StringAssert.Contains(v, "frame-ancestors 'none'");
        }

        [TestMethod]
        public async Task Enforce_mode_emits_blocking_header_with_frame_ancestors()
        {
            using var host = await BuildHostAsync(); // default = Enforce
            var client = host.GetTestClient();

            var response = await client.GetAsync("/");
            var v = response.Headers.GetValues("Content-Security-Policy").First();
            StringAssert.Contains(v, "frame-ancestors 'none'");
            Assert.IsFalse(response.Headers.Contains("Content-Security-Policy-Report-Only"));
        }

        // ── Scaffolding ─────────────────────────────────────────────────

        private static async Task<IHost> BuildHostAsync(Action<WtmCspOptions>? configure = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.Configure(app =>
                    {
                        if (configure != null)
                        {
                            app.UseWtmContentSecurityPolicy(configure);
                        }
                        else
                        {
                            app.UseWtmContentSecurityPolicy();
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
