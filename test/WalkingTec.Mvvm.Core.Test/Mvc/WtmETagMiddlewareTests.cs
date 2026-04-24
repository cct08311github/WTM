#nullable enable
using System;
using System.IO;
using System.Linq;
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
    /// Tests for <see cref="WtmETagMiddleware"/>: pure hash / matching
    /// functions, fresh-vs-304 response shapes, method + path
    /// exclusions, upstream-ETag preservation, oversize bypass, and
    /// weak-comparison semantics.
    /// </summary>
    [TestClass]
    public class WtmETagMiddlewareTests
    {
        // ── Pure functions ──────────────────────────────────────────────

        [TestMethod]
        public void ComputeShortHash_is_deterministic_for_same_bytes()
        {
            var a = Hash("hello-world");
            var b = Hash("hello-world");
            Assert.AreEqual(a, b);
        }

        [TestMethod]
        public void ComputeShortHash_differs_for_different_bytes()
        {
            var a = Hash("hello-world");
            var b = Hash("hello-world!");
            Assert.AreNotEqual(a, b);
        }

        [TestMethod]
        public void ComputeShortHash_is_base64url_safe_chars_only()
        {
            var h = Hash("some content");
            foreach (var ch in h)
            {
                bool ok = (ch >= 'A' && ch <= 'Z')
                       || (ch >= 'a' && ch <= 'z')
                       || (ch >= '0' && ch <= '9')
                       || ch is '-' or '_';
                Assert.IsTrue(ok, $"Unexpected ETag character '{ch}' in hash {h}");
            }
        }

        [TestMethod]
        public void IsMatch_wildcard_always_matches()
        {
            Assert.IsTrue(WtmETagMiddleware.IsMatch("*", "\"abc\""));
            Assert.IsTrue(WtmETagMiddleware.IsMatch("  *  ", "\"abc\""));
        }

        [TestMethod]
        public void IsMatch_exact_and_list_match()
        {
            Assert.IsTrue(WtmETagMiddleware.IsMatch("\"abc\"", "\"abc\""));
            Assert.IsTrue(WtmETagMiddleware.IsMatch("\"x\", \"abc\", \"y\"", "\"abc\""));
            Assert.IsFalse(WtmETagMiddleware.IsMatch("\"x\", \"y\"", "\"abc\""));
        }

        [TestMethod]
        public void IsMatch_weak_prefix_ignored_on_both_sides()
        {
            Assert.IsTrue(WtmETagMiddleware.IsMatch("W/\"abc\"", "\"abc\""));
            Assert.IsTrue(WtmETagMiddleware.IsMatch("\"abc\"", "W/\"abc\""));
            Assert.IsTrue(WtmETagMiddleware.IsMatch("W/\"abc\"", "W/\"abc\""));
        }

        [TestMethod]
        public void IsMatch_empty_or_null_header_returns_false()
        {
            Assert.IsFalse(WtmETagMiddleware.IsMatch(null, "\"abc\""));
            Assert.IsFalse(WtmETagMiddleware.IsMatch("", "\"abc\""));
            Assert.IsFalse(WtmETagMiddleware.IsMatch("   ", "\"abc\""));
        }

        // ── Fresh GET emits ETag ────────────────────────────────────────

        [TestMethod]
        public async Task Fresh_GET_receives_strong_etag()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/data");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsNotNull(response.Headers.ETag);
            Assert.IsFalse(response.Headers.ETag!.IsWeak, "Default is strong ETag.");
            Assert.IsFalse(string.IsNullOrEmpty(response.Headers.ETag.Tag));
            Assert.IsTrue(response.Headers.ETag.Tag.StartsWith("\"") && response.Headers.ETag.Tag.EndsWith("\""));
        }

        [TestMethod]
        public async Task Weak_etag_option_prefixes_with_W_slash()
        {
            using var host = await BuildHostAsync(opt => opt.EmitWeakETag = true);
            var client = host.GetTestClient();

            var response = await client.GetAsync("/data");
            Assert.IsTrue(response.Headers.ETag!.IsWeak,
                "EmitWeakETag=true must produce W/\"...\" form.");
        }

        [TestMethod]
        public async Task Same_body_yields_same_etag_across_calls()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var a = await client.GetAsync("/data");
            var b = await client.GetAsync("/data");

            Assert.AreEqual(a.Headers.ETag!.Tag, b.Headers.ETag!.Tag,
                "Deterministic hashing: identical bodies must share an ETag.");
        }

        // ── If-None-Match flow ──────────────────────────────────────────

        [TestMethod]
        public async Task Matching_if_none_match_returns_304_empty_body()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var first = await client.GetAsync("/data");
            var etag = first.Headers.ETag!.Tag;

            var req = new HttpRequestMessage(HttpMethod.Get, "/data");
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            var second = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.NotModified, second.StatusCode);
            Assert.AreEqual(etag, second.Headers.ETag!.Tag,
                "304 must echo the same ETag.");
            var body = await second.Content.ReadAsByteArrayAsync();
            Assert.AreEqual(0, body.Length, "304 must have no body.");
        }

        [TestMethod]
        public async Task Wildcard_if_none_match_returns_304()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/data");
            req.Headers.TryAddWithoutValidation("If-None-Match", "*");
            var response = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.NotModified, response.StatusCode);
        }

        [TestMethod]
        public async Task Non_matching_if_none_match_returns_fresh_200()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/data");
            req.Headers.TryAddWithoutValidation("If-None-Match", "\"some-stale-tag\"");
            var response = await client.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsNotNull(response.Headers.ETag);
        }

        // ── Exclusions & skips ──────────────────────────────────────────

        [TestMethod]
        public async Task POST_response_is_not_etagged()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.PostAsync("/data",
                new StringContent("x", Encoding.UTF8, "text/plain"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsNull(response.Headers.ETag);
        }

        [TestMethod]
        public async Task Excluded_path_is_not_etagged()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/healthz");
            Assert.IsNull(response.Headers.ETag,
                "Default /healthz exclusion must suppress ETag emission.");
        }

        [TestMethod]
        public async Task Non_2xx_response_is_not_etagged()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/boom");
            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.IsNull(response.Headers.ETag);
        }

        [TestMethod]
        public async Task Upstream_set_etag_is_preserved()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/preset-etag");
            Assert.AreEqual("\"upstream-tag\"", response.Headers.ETag!.Tag);
        }

        [TestMethod]
        public async Task Oversize_response_is_not_etagged()
        {
            using var host = await BuildHostAsync(opt => opt.MaxBufferedBytes = 10);
            var client = host.GetTestClient();

            var response = await client.GetAsync("/data"); // returns 25+ bytes
            Assert.IsNull(response.Headers.ETag,
                "Response larger than MaxBufferedBytes must fall through without ETag.");
            var body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Length > 10, "Body still delivered to client despite no caching.");
        }

        // ── Extension arg validation ────────────────────────────────────

        [TestMethod]
        public void UseWtmETag_null_configure_throws()
        {
            var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmETagExtension.UseWtmETag(builder, null!));
        }

        // ── Scaffolding ─────────────────────────────────────────────────

        private static string Hash(string s)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(s));
            return WtmETagMiddleware.ComputeShortHash(ms);
        }

        private static async Task<IHost> BuildHostAsync(
            Action<WtmETagOptions>? configure = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.Configure(app =>
                    {
                        if (configure != null)
                        {
                            app.UseWtmETag(configure);
                        }
                        else
                        {
                            app.UseWtmETag();
                        }

                        app.Run(async ctx =>
                        {
                            var path = ctx.Request.Path.Value ?? "";
                            if (path.StartsWith("/preset-etag", StringComparison.OrdinalIgnoreCase))
                            {
                                ctx.Response.Headers["ETag"] = "\"upstream-tag\"";
                                await ctx.Response.WriteAsync("preset");
                                return;
                            }
                            if (path.StartsWith("/boom", StringComparison.OrdinalIgnoreCase))
                            {
                                ctx.Response.StatusCode = 500;
                                await ctx.Response.WriteAsync("oops");
                                return;
                            }

                            await ctx.Response.WriteAsync("deterministic-payload-v1");
                        });
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }
    }
}
