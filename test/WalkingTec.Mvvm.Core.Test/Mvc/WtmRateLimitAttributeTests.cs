#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for issue #828: per-endpoint [WtmRateLimit] attribute +
    /// policy registration + convention.
    /// </summary>
    [TestClass]
    public class WtmRateLimitAttributeTests
    {
        // ── Attribute validation ─────────────────────────────────────────

        [TestMethod]
        public void Ctor_rejects_zero_or_negative_permits()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new WtmRateLimitAttribute(0, 60));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new WtmRateLimitAttribute(-1, 60));
        }

        [TestMethod]
        public void Ctor_rejects_zero_or_negative_window()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new WtmRateLimitAttribute(5, 0));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new WtmRateLimitAttribute(5, -1));
        }

        [TestMethod]
        public void Ctor_rejects_negative_queue_limit()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new WtmRateLimitAttribute(5, 60, -1));
        }

        [TestMethod]
        public void Ctor_accepts_valid_config_and_computes_policy_name()
        {
            var a = new WtmRateLimitAttribute(5, 60);
            Assert.AreEqual(5, a.Permits);
            Assert.AreEqual(60, a.WindowSeconds);
            Assert.AreEqual(0, a.QueueLimit);
            Assert.AreEqual("wtm_rl_5_60_0", a.PolicyName);

            var b = new WtmRateLimitAttribute(3, 300, 2);
            Assert.AreEqual("wtm_rl_3_300_2", b.PolicyName);
        }

        [TestMethod]
        public void BuildPolicyName_is_deterministic_and_culture_invariant()
        {
            // Ensure the number formatting doesn't pick up a thread culture
            // quirk (e.g. some locales using non-ASCII digits).
            var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fa-IR");
                Assert.AreEqual("wtm_rl_10_60_0", WtmRateLimitAttribute.BuildPolicyName(10, 60, 0));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = originalCulture;
            }
        }

        // ── Assembly scan ────────────────────────────────────────────────

        [TestMethod]
        public void ScanWtmRateLimitAttributes_finds_decorated_types_in_current_assembly()
        {
            var configs = WtmRateLimitingExtension.ScanWtmRateLimitAttributes();
            // Test fixtures below decorate actions — the scan should find them.
            Assert.IsTrue(configs.Contains((3, 60, 0)),
                "Scan should find [WtmRateLimit(3, 60)] on the test fixture.");
            Assert.IsTrue(configs.Contains((2, 30, 0)),
                "Scan should find [WtmRateLimit(2, 30)] on the test fixture.");
        }

        [TestMethod]
        public void ScanWtmRateLimitAttributes_deduplicates_identical_tuples()
        {
            // Two test actions use the same (3, 60, 0) config — they must
            // share one tuple in the returned set.
            var configs = WtmRateLimitingExtension.ScanWtmRateLimitAttributes();
            var matching_3_60 = configs.Count(t => t.permits == 3 && t.window == 60 && t.queue == 0);
            Assert.AreEqual(1, matching_3_60,
                "Duplicate (permits, window, queue) tuples must be deduplicated by the scan.");
        }

        // ── Integration via TestServer ───────────────────────────────────

        [TestMethod]
        public async Task Decorated_endpoint_returns_429_after_exceeding_permit_limit()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            // Test fixture /ratelimit/strict has [WtmRateLimit(2, 30)].
            // First 2 requests should succeed, 3rd should 429.
            var r1 = await client.GetAsync("/ratelimit/strict");
            var r2 = await client.GetAsync("/ratelimit/strict");
            var r3 = await client.GetAsync("/ratelimit/strict");

            Assert.AreEqual(HttpStatusCode.OK, r1.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, r2.StatusCode);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, r3.StatusCode,
                "Third request within the window must be rejected with 429.");
        }

        [TestMethod]
        public async Task Global_limiter_still_allows_undecorated_endpoint_up_to_its_own_quota()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            // Global limiter in the test config is generous (500/60s).
            // An undecorated endpoint should happily serve 10 requests.
            for (int i = 0; i < 10; i++)
            {
                var resp = await client.GetAsync("/ratelimit/open");
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode, $"Request {i + 1} on /ratelimit/open should pass.");
            }
        }

        [TestMethod]
        public async Task Different_endpoints_have_independent_per_endpoint_quotas()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            // /ratelimit/strict = (2, 30), /ratelimit/medium = (3, 60)
            // Exhausting strict must NOT affect medium's quota.
            _ = await client.GetAsync("/ratelimit/strict");
            _ = await client.GetAsync("/ratelimit/strict");
            var strict3 = await client.GetAsync("/ratelimit/strict");
            Assert.AreEqual(HttpStatusCode.TooManyRequests, strict3.StatusCode);

            var medium1 = await client.GetAsync("/ratelimit/medium");
            Assert.AreEqual(HttpStatusCode.OK, medium1.StatusCode,
                "Strict's exhausted quota must not affect medium's independent bucket.");
        }

        // ── Test host / fixtures ─────────────────────────────────────────

        private static async Task<IHost> BuildHostAsync()
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.ConfigureServices(services =>
                    {
                        services.AddWtmRateLimiting(opt =>
                        {
                            // Generous global so it doesn't mask per-endpoint effects in tests.
                            opt.PermitLimit = 500;
                            opt.WindowSeconds = 60;
                        });
                        services.AddControllers()
                                .AddApplicationPart(typeof(WtmRateLimitAttributeTests).Assembly);
                    });
                    w.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseWtmRateLimiting();
                        app.UseEndpoints(e => e.MapControllers());
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }
    }

    // ── Fixture controller exercised by the integration tests ───────────

    [ApiController]
    [Route("ratelimit")]
    public class WtmRateLimitTestFixtureController : ControllerBase
    {
        [HttpGet("strict")]
        [WtmRateLimit(2, 30)]
        public IActionResult Strict() => Ok("strict");

        [HttpGet("medium")]
        [WtmRateLimit(3, 60)]
        public IActionResult Medium() => Ok("medium");

        [HttpGet("open")]
        public IActionResult Open() => Ok("open");

        [HttpGet("alt-3-60")]
        [WtmRateLimit(3, 60)]
        public IActionResult AltSameConfig() => Ok("alt"); // dedup check: same (3, 60) as Medium
    }
}
