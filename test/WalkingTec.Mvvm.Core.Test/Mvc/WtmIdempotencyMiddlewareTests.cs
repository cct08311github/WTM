#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
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
    /// Tests for <see cref="WtmIdempotencyMiddleware"/>: key validation,
    /// replay semantics, path/method scoping, body-size cap,
    /// RequireKey enforcement, error-path non-caching, and
    /// UseWtmIdempotency() argument guards.
    /// </summary>
    [TestClass]
    public class WtmIdempotencyMiddlewareTests
    {
        // ── Pure helper ──────────────────────────────────────────────────

        [TestMethod]
        public void BuildCacheKey_scopes_by_method_and_path()
        {
            var a = WtmIdempotencyMiddleware.BuildCacheKey("post", "/api/orders", "abc");
            var b = WtmIdempotencyMiddleware.BuildCacheKey("POST", "/api/orders", "abc");
            var c = WtmIdempotencyMiddleware.BuildCacheKey("POST", "/api/charges", "abc");
            var d = WtmIdempotencyMiddleware.BuildCacheKey("PUT",  "/api/orders", "abc");

            Assert.AreEqual(a, b, "Method comparison must be upper-invariant.");
            Assert.AreNotEqual(a, c, "Different path must yield different cache key.");
            Assert.AreNotEqual(a, d, "Different method must yield different cache key.");
        }

        [TestMethod]
        public void IsAsciiTokenSafe_accepts_common_id_shapes_and_rejects_unsafe_chars()
        {
            Assert.IsTrue(WtmIdempotencyMiddleware.IsAsciiTokenSafe("550e8400-e29b-41d4-a716-446655440000"));
            Assert.IsTrue(WtmIdempotencyMiddleware.IsAsciiTokenSafe("01ARZ3NDEKTSV4RRFFQ69G5FAV"));
            Assert.IsTrue(WtmIdempotencyMiddleware.IsAsciiTokenSafe("key_abc.123:xyz"));

            Assert.IsFalse(WtmIdempotencyMiddleware.IsAsciiTokenSafe("has space"));
            Assert.IsFalse(WtmIdempotencyMiddleware.IsAsciiTokenSafe("new\nline"));
            Assert.IsFalse(WtmIdempotencyMiddleware.IsAsciiTokenSafe("slash/here"));
            Assert.IsFalse(WtmIdempotencyMiddleware.IsAsciiTokenSafe("utf8-ok-é"));
        }

        // ── Ungated endpoint ─────────────────────────────────────────────

        [TestMethod]
        public async Task Ungated_endpoint_is_transparent_to_middleware()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var a = await PostAsync(client, "/idem/open", "body1", key: "dup-key");
            var b = await PostAsync(client, "/idem/open", "body2", key: "dup-key");

            Assert.AreEqual(HttpStatusCode.OK, a.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, b.StatusCode);
            Assert.AreEqual("open-1", await a.Content.ReadAsStringAsync());
            Assert.AreEqual("open-2", await b.Content.ReadAsStringAsync(),
                "Ungated endpoint must execute every call, ignoring keys.");
        }

        // ── Replay behaviour ─────────────────────────────────────────────

        [TestMethod]
        public async Task Duplicate_key_replays_cached_response_with_replay_header()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var first = await PostAsync(client, "/idem/gated", "body1", key: "abc-123");
            var second = await PostAsync(client, "/idem/gated", "body2", key: "abc-123");

            var firstBody = await first.Content.ReadAsStringAsync();
            var secondBody = await second.Content.ReadAsStringAsync();

            Assert.AreEqual(firstBody, secondBody,
                "Second call with same key must return the exact cached body.");
            Assert.IsFalse(first.Headers.Contains("Idempotency-Replay"),
                "First call (fresh) must NOT carry the replay marker.");
            Assert.IsTrue(second.Headers.Contains("Idempotency-Replay"),
                "Second call (cache hit) must carry Idempotency-Replay: true.");
            Assert.AreEqual("true", second.Headers.GetValues("Idempotency-Replay").First());
        }

        [TestMethod]
        public async Task Different_keys_result_in_separate_invocations()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var a = await PostAsync(client, "/idem/gated", "body1", key: "key-A");
            var b = await PostAsync(client, "/idem/gated", "body2", key: "key-B");

            var aBody = await a.Content.ReadAsStringAsync();
            var bBody = await b.Content.ReadAsStringAsync();
            Assert.AreNotEqual(aBody, bBody,
                "Different keys must hit the action twice, not replay.");
        }

        [TestMethod]
        public async Task Same_key_on_different_path_does_not_replay()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var orders = await PostAsync(client, "/idem/gated", "body1", key: "shared");
            var charges = await PostAsync(client, "/idem/other", "body1", key: "shared");

            Assert.IsFalse(charges.Headers.Contains("Idempotency-Replay"),
                "Cache must be scoped per-endpoint; same key on different path is a fresh call.");
        }

        // ── Key validation ───────────────────────────────────────────────

        [TestMethod]
        public async Task RequireKey_missing_key_returns_400_problem_json()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            // /idem/required uses [WtmIdempotent(RequireKey = true)].
            var response = await PostAsync(client, "/idem/required", "body");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "Idempotency-Key");
        }

        [TestMethod]
        public async Task Malformed_key_returns_400()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await PostAsync(client, "/idem/gated", "body", key: "has space");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [TestMethod]
        public async Task Oversized_key_returns_400()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var big = new string('a', 500);
            var response = await PostAsync(client, "/idem/gated", "body", key: big);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [TestMethod]
        public async Task Missing_key_on_non_required_endpoint_runs_normally_not_cached()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var a = await PostAsync(client, "/idem/gated", "body1");
            var b = await PostAsync(client, "/idem/gated", "body2");

            var aBody = await a.Content.ReadAsStringAsync();
            var bBody = await b.Content.ReadAsStringAsync();
            Assert.AreNotEqual(aBody, bBody,
                "Keyless requests must run the action normally (no caching without a key).");
        }

        // ── Status-code gating ───────────────────────────────────────────

        [TestMethod]
        public async Task Non_2xx_response_is_not_cached()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var first = await PostAsync(client, "/idem/fail", "body", key: "fail-key");
            Assert.AreEqual(HttpStatusCode.InternalServerError, first.StatusCode);

            // If caching ran on the 500, a second call would replay the 500.
            // We want it to re-invoke the action instead so a transient
            // failure doesn't poison the cache slot.
            var second = await PostAsync(client, "/idem/fail", "body", key: "fail-key");
            Assert.IsFalse(second.Headers.Contains("Idempotency-Replay"),
                "5xx responses must not populate the cache — retry must hit the action.");
        }

        // ── Method eligibility ───────────────────────────────────────────

        [TestMethod]
        public async Task Get_request_is_not_cached_even_when_decorated_with_Idempotent()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            // GETs are already safe+idempotent per RFC 9110 and caching
            // them would mask a bug where the same URI now returns
            // different data. Expect every call to hit the action.
            var req1 = new HttpRequestMessage(HttpMethod.Get, "/idem/get-gated");
            req1.Headers.Add("Idempotency-Key", "abc");
            var req2 = new HttpRequestMessage(HttpMethod.Get, "/idem/get-gated");
            req2.Headers.Add("Idempotency-Key", "abc");

            var a = await client.SendAsync(req1);
            var b = await client.SendAsync(req2);

            Assert.IsFalse(a.Headers.Contains("Idempotency-Replay"));
            Assert.IsFalse(b.Headers.Contains("Idempotency-Replay"));
        }

        // ── UseWtmIdempotency argument validation ────────────────────────

        [TestMethod]
        public void UseWtmIdempotency_null_configure_throws()
        {
            var services = new ServiceCollection();
            services.AddMemoryCache();
            var builder = new ApplicationBuilder(services.BuildServiceProvider());
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmIdempotencyExtension.UseWtmIdempotency(builder, null!));
        }

        [TestMethod]
        public void UseWtmIdempotency_without_MemoryCache_throws_informative_message()
        {
            var services = new ServiceCollection(); // no AddMemoryCache
            var builder = new ApplicationBuilder(services.BuildServiceProvider());
            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                WtmIdempotencyExtension.UseWtmIdempotency(builder));
            StringAssert.Contains(ex.Message, "AddMemoryCache");
        }

        // ── Scaffolding ──────────────────────────────────────────────────

        private static async Task<HttpResponseMessage> PostAsync(
            HttpClient client, string path, string body, string? key = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            if (key != null) { req.Headers.Add("Idempotency-Key", key); }
            return await client.SendAsync(req);
        }

        private static async Task<IHost> BuildHostAsync()
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.ConfigureServices(services =>
                    {
                        services.AddMemoryCache();
                        services.AddLogging();
                        services.AddControllers()
                                .AddApplicationPart(typeof(WtmIdempotencyMiddlewareTests).Assembly);
                    });
                    w.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseWtmIdempotency();
                        app.UseEndpoints(e => e.MapControllers());
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }
    }

    // ── Fixture controllers ─────────────────────────────────────────────

    [ApiController]
    [Route("idem")]
    public class WtmIdempotencyFixtureController : ControllerBase
    {
        private static int _counter;

        [HttpPost("open")]
        public IActionResult Open()
        {
            var n = Interlocked.Increment(ref _counter);
            return Ok($"open-{n}");
        }

        [HttpPost("gated")]
        [WtmIdempotent]
        public IActionResult Gated()
        {
            var n = Guid.NewGuid().ToString("N");
            return Ok($"gated-{n}");
        }

        [HttpPost("other")]
        [WtmIdempotent]
        public IActionResult Other()
        {
            var n = Guid.NewGuid().ToString("N");
            return Ok($"other-{n}");
        }

        [HttpPost("required")]
        [WtmIdempotent(RequireKey = true)]
        public IActionResult Required() => Ok("should-not-run");

        [HttpPost("fail")]
        [WtmIdempotent]
        public IActionResult Fail() => StatusCode(500, "boom");

        [HttpGet("get-gated")]
        [WtmIdempotent]
        public IActionResult GetGated()
        {
            var n = Guid.NewGuid().ToString("N");
            return Ok($"get-{n}");
        }
    }
}
