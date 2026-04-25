#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
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
    /// Tests for <see cref="WtmRequestTimeoutsMiddleware"/>: pure
    /// timeout resolution (longest-prefix-wins), fast-handler pass-through,
    /// slow-handler 504 with problem+json, path exclusion, default-disabled
    /// when <c>DefaultTimeoutMs &lt;= 0</c>, and extension-arg validation.
    /// </summary>
    [TestClass]
    public class WtmRequestTimeoutsMiddlewareTests
    {
        // ── Pure ResolveTimeout (longest-prefix-wins) ───────────────────

        [TestMethod]
        public void ResolveTimeout_uses_default_when_no_override_matches()
        {
            var (mw, _) = BuildMiddleware(opt =>
            {
                opt.DefaultTimeoutMs = 30_000;
                opt.PathOverrides["/api/export"] = 300_000;
            });

            Assert.AreEqual(30_000, mw.ResolveTimeout(new PathString("/api/orders")));
        }

        [TestMethod]
        public void ResolveTimeout_picks_longest_matching_prefix()
        {
            var (mw, _) = BuildMiddleware(opt =>
            {
                opt.DefaultTimeoutMs = 30_000;
                opt.PathOverrides["/api"] = 60_000;
                opt.PathOverrides["/api/export"] = 300_000;
                opt.PathOverrides["/api/export/jobs/cancel"] = 5_000;
            });

            Assert.AreEqual(5_000, mw.ResolveTimeout(new PathString("/api/export/jobs/cancel")),
                "Most specific prefix wins.");
            Assert.AreEqual(300_000, mw.ResolveTimeout(new PathString("/api/export/jobs/queue")));
            Assert.AreEqual(60_000, mw.ResolveTimeout(new PathString("/api/orders")));
        }

        [TestMethod]
        public void ResolveTimeout_zero_or_negative_override_disables_for_that_prefix()
        {
            // Useful for streaming / SSE / WebSocket endpoints that
            // intentionally hold the connection open.
            var (mw, _) = BuildMiddleware(opt =>
            {
                opt.DefaultTimeoutMs = 30_000;
                opt.PathOverrides["/sse"] = 0;
            });

            Assert.AreEqual(0, mw.ResolveTimeout(new PathString("/sse/events")),
                "Zero override must indicate no timeout for the matched path.");
        }

        // ── Fast handler ────────────────────────────────────────────────

        [TestMethod]
        public async Task Fast_handler_completes_normally_without_504()
        {
            using var host = await BuildHostAsync(opt => opt.DefaultTimeoutMs = 5_000);
            var client = host.GetTestClient();

            var response = await client.GetAsync("/timing/fast");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("fast", await response.Content.ReadAsStringAsync());
        }

        // ── Slow handler triggers 504 ───────────────────────────────────

        [TestMethod]
        public async Task Cancellation_aware_slow_handler_returns_504_problem_json()
        {
            // Handler awaits Task.Delay with the request's cancellation
            // token, so when the deadline trips it cancels cleanly.
            using var host = await BuildHostAsync(opt => opt.DefaultTimeoutMs = 100);
            var client = host.GetTestClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = await client.GetAsync("/timing/cancellable-slow");

            Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.AreEqual("application/problem+json",
                response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.AreEqual(504, doc.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual("Gateway Timeout", doc.RootElement.GetProperty("title").GetString());
            Assert.AreEqual(100, doc.RootElement.GetProperty("timeoutMs").GetInt32());
        }

        [TestMethod]
        public async Task Path_override_applies_per_endpoint()
        {
            using var host = await BuildHostAsync(opt =>
            {
                opt.DefaultTimeoutMs = 100;
                opt.PathOverrides["/timing/cancellable-slow-export"] = 5_000;
            });
            var client = host.GetTestClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            // /timing/cancellable-slow-export awaits 200 ms — would 504
            // under the 100 ms default but should pass under the 5 s
            // override.
            var response = await client.GetAsync("/timing/cancellable-slow-export");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // ── Path exclusion ──────────────────────────────────────────────

        [TestMethod]
        public async Task Excluded_path_bypasses_timeout()
        {
            using var host = await BuildHostAsync(opt =>
            {
                opt.DefaultTimeoutMs = 50;
                opt.PathExclusions = new List<string> { "/healthz" };
            });
            var client = host.GetTestClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            // /healthz handler awaits 200 ms — without exclusion would
            // 504 under 50 ms default; with exclusion must complete OK.
            var response = await client.GetAsync("/healthz");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // ── Default disabled ────────────────────────────────────────────

        [TestMethod]
        public async Task Default_timeout_zero_disables_global_deadline()
        {
            using var host = await BuildHostAsync(opt => opt.DefaultTimeoutMs = 0);
            var client = host.GetTestClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            // Slow handler that would normally 504 — should now pass.
            var response = await client.GetAsync("/timing/cancellable-slow-export");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // ── Extension argument validation ───────────────────────────────

        [TestMethod]
        public void UseWtmRequestTimeouts_null_configure_throws()
        {
            var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmRequestTimeoutsExtension.UseWtmRequestTimeouts(builder, null!));
        }

        // ── Scaffolding ─────────────────────────────────────────────────

        private static (WtmRequestTimeoutsMiddleware mw, WtmRequestTimeoutsOptions opt) BuildMiddleware(
            Action<WtmRequestTimeoutsOptions> configure)
        {
            var opt = new WtmRequestTimeoutsOptions();
            configure(opt);
            var mw = new WtmRequestTimeoutsMiddleware(
                _ => Task.CompletedTask,
                opt,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WtmRequestTimeoutsMiddleware>.Instance);
            return (mw, opt);
        }

        private static async Task<IHost> BuildHostAsync(
            Action<WtmRequestTimeoutsOptions>? configure = null)
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
                            app.UseWtmRequestTimeouts(configure);
                        }
                        else
                        {
                            app.UseWtmRequestTimeouts();
                        }

                        app.Run(async ctx =>
                        {
                            var path = ctx.Request.Path.Value ?? "";
                            if (path.StartsWith("/timing/fast", StringComparison.OrdinalIgnoreCase))
                            {
                                await ctx.Response.WriteAsync("fast");
                                return;
                            }
                            if (path.StartsWith("/timing/cancellable-slow-export", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    await Task.Delay(200, ctx.RequestAborted);
                                    await ctx.Response.WriteAsync("export-ok");
                                }
                                catch (OperationCanceledException)
                                {
                                    // Re-throw so the middleware can convert to 504.
                                    throw;
                                }
                                return;
                            }
                            if (path.StartsWith("/timing/cancellable-slow", StringComparison.OrdinalIgnoreCase))
                            {
                                await Task.Delay(5_000, ctx.RequestAborted);
                                await ctx.Response.WriteAsync("would-be-slow");
                                return;
                            }
                            if (path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase))
                            {
                                await Task.Delay(200);
                                await ctx.Response.WriteAsync("ok");
                                return;
                            }

                            await ctx.Response.WriteAsync("default");
                        });
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }
    }
}
