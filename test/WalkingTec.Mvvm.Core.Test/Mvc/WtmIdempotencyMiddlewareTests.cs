#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Auth;
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
        public void BuildCacheKey_scopes_by_user_method_and_path()
        {
            // Method normalisation
            var a = WtmIdempotencyMiddleware.BuildCacheKey("post", "/api/orders", "abc", "alice");
            var b = WtmIdempotencyMiddleware.BuildCacheKey("POST", "/api/orders", "abc", "alice");
            Assert.AreEqual(a, b, "Method comparison must be upper-invariant.");

            // Different path
            var c = WtmIdempotencyMiddleware.BuildCacheKey("POST", "/api/charges", "abc", "alice");
            Assert.AreNotEqual(a, c, "Different path must yield different cache key.");

            // Different method
            var d = WtmIdempotencyMiddleware.BuildCacheKey("PUT",  "/api/orders", "abc", "alice");
            Assert.AreNotEqual(a, d, "Different method must yield different cache key.");

            // SECURITY: different users with the same key MUST NOT share a cache entry
            var e = WtmIdempotencyMiddleware.BuildCacheKey("POST", "/api/orders", "abc", "bob");
            Assert.AreNotEqual(a, e, "Different users must yield different cache keys (no cross-user replay).");

            // Same user, same inputs → stable deterministic key (idempotent replay works)
            var f = WtmIdempotencyMiddleware.BuildCacheKey("POST", "/api/orders", "abc", "alice");
            Assert.AreEqual(a, f, "Same user+method+path+key must always produce the same cache key.");
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
            using var client = ClientForUser(host, "alice");

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
            using var client = ClientForUser(host, "alice");

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
            using var client = ClientForUser(host, "alice");

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
            using var client = ClientForUser(host, "alice");

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
            using var client = ClientForUser(host, "alice");

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
            using var client = ClientForUser(host, "alice");

            var response = await PostAsync(client, "/idem/gated", "body", key: "has space");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [TestMethod]
        public async Task Oversized_key_returns_400()
        {
            using var host = await BuildHostAsync();
            using var client = ClientForUser(host, "alice");

            var big = new string('a', 500);
            var response = await PostAsync(client, "/idem/gated", "body", key: big);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [TestMethod]
        public async Task Missing_key_on_non_required_endpoint_runs_normally_not_cached()
        {
            using var host = await BuildHostAsync();
            using var client = ClientForUser(host, "alice");

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
            using var client = ClientForUser(host, "alice");

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
            using var client = ClientForUser(host, "alice");

            // GETs are already safe+idempotent per RFC 9110 and caching
            // them would mask a bug where the same URI now returns
            // different data. Expect every call to hit the action.
            var req1 = new HttpRequestMessage(HttpMethod.Get, "/idem/get-gated");
            req1.Headers.Add("Idempotency-Key", "abc");
            req1.Headers.Add("X-Test-User", "alice");
            var req2 = new HttpRequestMessage(HttpMethod.Get, "/idem/get-gated");
            req2.Headers.Add("Idempotency-Key", "abc");
            req2.Headers.Add("X-Test-User", "alice");

            var a = await client.SendAsync(req1);
            var b = await client.SendAsync(req2);

            Assert.IsFalse(a.Headers.Contains("Idempotency-Replay"));
            Assert.IsFalse(b.Headers.Contains("Idempotency-Replay"));
        }

        // ── User-scope security tests (Issue #110) ───────────────────────

        [TestMethod]
        public async Task Same_user_same_key_replays_response()
        {
            // Verify same-user idempotency still works after the fix.
            using var host = await BuildHostAsync();
            using var client = ClientForUser(host, "alice");

            var first  = await PostAsync(client, "/idem/gated", "body1", key: "same-user-key");
            var second = await PostAsync(client, "/idem/gated", "body2", key: "same-user-key");

            var firstBody  = await first.Content.ReadAsStringAsync();
            var secondBody = await second.Content.ReadAsStringAsync();

            Assert.AreEqual(firstBody, secondBody,
                "Same user with same key must still replay the cached response.");
            Assert.IsTrue(second.Headers.Contains("Idempotency-Replay"),
                "Replay header must be set on the second call from the same user.");
        }

        [TestMethod]
        public async Task Different_users_same_key_do_not_share_cached_response()
        {
            // SECURITY: this is the cross-user replay test (Issue #110).
            // Alice and Bob each submit the same Idempotency-Key to the same endpoint.
            // Bob must NOT receive Alice's cached response body.
            using var host = await BuildHostAsync();
            using var aliceClient = ClientForUser(host, "alice");
            using var bobClient   = ClientForUser(host, "bob");

            // Alice's request is served fresh and cached under her user scope.
            var aliceFirst = await PostAsync(aliceClient, "/idem/gated", "body", key: "cross-user-key");
            Assert.AreEqual(HttpStatusCode.OK, aliceFirst.StatusCode);
            Assert.IsFalse(aliceFirst.Headers.Contains("Idempotency-Replay"),
                "Alice's first call must be fresh (no cache hit).");

            // Bob's request with the same key must NOT be a cache replay.
            var bobFirst = await PostAsync(bobClient, "/idem/gated", "body", key: "cross-user-key");
            Assert.AreEqual(HttpStatusCode.OK, bobFirst.StatusCode);
            Assert.IsFalse(bobFirst.Headers.Contains("Idempotency-Replay"),
                "Bob must NOT receive Alice's cached response — no cross-user replay.");

            // The bodies must be different (each backed by a fresh Guid from the action).
            var aliceBody = await aliceFirst.Content.ReadAsStringAsync();
            var bobBody   = await bobFirst.Content.ReadAsStringAsync();
            Assert.AreNotEqual(aliceBody, bobBody,
                "Cross-user responses must be independent — no shared cache entry.");
        }

        [TestMethod]
        public async Task Unauthenticated_request_passes_through_without_caching()
        {
            // Unauthenticated requests (no X-Test-User header → no user identity claim)
            // must NOT be cached. The same key on successive anonymous calls must invoke
            // the action each time rather than replaying a cached body.
            using var host = await BuildHostAsync();
            // Deliberately use a plain client with NO X-Test-User header.
            using var anonClient = host.GetTestClient();

            var first  = await PostAsync(anonClient, "/idem/gated", "body", key: "anon-key");
            var second = await PostAsync(anonClient, "/idem/gated", "body", key: "anon-key");

            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
            Assert.IsFalse(first.Headers.Contains("Idempotency-Replay"),
                "Unauthenticated first call must not be a replay.");
            Assert.IsFalse(second.Headers.Contains("Idempotency-Replay"),
                "Unauthenticated second call must NOT replay a cached body — " +
                "caching is skipped for anonymous requests to prevent shared-bucket poisoning.");

            var firstBody  = await first.Content.ReadAsStringAsync();
            var secondBody = await second.Content.ReadAsStringAsync();
            Assert.AreNotEqual(firstBody, secondBody,
                "Each unauthenticated call must hit the action freshly (no anonymous caching).");
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

        /// <summary>
        /// Posts to <paramref name="path"/> optionally with an Idempotency-Key header
        /// and optionally impersonating a specific user (via the test auth scheme).
        /// </summary>
        private static async Task<HttpResponseMessage> PostAsync(
            HttpClient client, string path, string body,
            string? key = null, string? userId = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            if (key != null) { req.Headers.Add("Idempotency-Key", key); }
            // Convey user identity to the test authentication handler via a
            // custom header that WtmTestAuthHandler reads.
            if (userId != null) { req.Headers.Add("X-Test-User", userId); }
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
                        // Register the lightweight test authentication scheme so
                        // integration tests can simulate authenticated requests
                        // without a real JWT stack.
                        services.AddAuthentication("Test")
                                .AddScheme<AuthenticationSchemeOptions, WtmTestAuthHandler>(
                                    "Test", _ => { });
                        services.AddAuthorization();
                        services.AddControllers()
                                .AddApplicationPart(typeof(WtmIdempotencyMiddlewareTests).Assembly);
                    });
                    w.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseWtmIdempotency();
                        app.UseEndpoints(e => e.MapControllers());
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }

        /// <summary>
        /// Returns a client pre-configured to authenticate as <paramref name="userId"/>
        /// on every request by injecting a default <c>X-Test-User</c> header.
        /// </summary>
        private static HttpClient ClientForUser(IHost host, string userId)
        {
            var client = host.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Test-User", userId);
            return client;
        }
    }

    // ── Test authentication handler ─────────────────────────────────────

    /// <summary>
    /// Lightweight authentication handler for integration tests.
    /// Reads the user identity from the <c>X-Test-User</c> header and creates
    /// a <see cref="ClaimsPrincipal"/> with an <c>itcode</c> claim (matching
    /// <see cref="WalkingTec.Mvvm.Core.Auth.AuthConstants.JwtClaimTypes.Subject"/>)
    /// so the middleware's user-scoping logic can be exercised without a real JWT.
    /// Requests without the header are treated as unauthenticated.
    /// </summary>
    public class WtmTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public WtmTestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = Request.Headers["X-Test-User"].FirstOrDefault();
            if (string.IsNullOrEmpty(userId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                // Use the same claim name as WTMContext / AuthConstants.JwtClaimTypes.Subject
                new Claim(AuthConstants.JwtClaimTypes.Subject, userId),
            };
            var identity = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
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
