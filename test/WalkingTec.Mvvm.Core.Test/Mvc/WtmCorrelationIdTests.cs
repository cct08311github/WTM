#nullable enable
using System;
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
    /// Tests for issue #830: X-Correlation-Id middleware — inbound
    /// adoption, sanitization, response echo, auto-generation.
    /// </summary>
    [TestClass]
    public class WtmCorrelationIdTests
    {
        private const string DefaultHeader = "X-Correlation-Id";

        // ── IsSafeCorrelationId — pure unit coverage ─────────────────────

        [TestMethod]
        [DataRow("abc-123")]
        [DataRow("a1b2c3d4e5f6a7b8")]
        [DataRow("550e8400-e29b-41d4-a716-446655440000")]   // UUID
        [DataRow("550e8400e29b41d4a716446655440000")]        // UUID-no-dashes
        [DataRow("my.service.trace-0123")]
        [DataRow("_")]
        [DataRow("X")]
        [DataRow("Z1")]
        public void IsSafeCorrelationId_accepts_alphanumeric_dash_underscore_dot(string candidate)
        {
            Assert.IsTrue(WtmCorrelationIdMiddleware.IsSafeCorrelationId(candidate, 128));
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("abc\r\ninjected: line")]   // CRLF injection attempt
        [DataRow("abc;DROP TABLE")]           // semicolon (header split)
        [DataRow("abc def")]                   // space
        [DataRow("abc,def")]                   // comma
        [DataRow("中文")]                       // non-ASCII
        [DataRow("abc/def")]                   // slash
        [DataRow("<script>alert(1)</script>")]
        [DataRow("foo\u0000bar")]              // embedded null
        public void IsSafeCorrelationId_rejects_unsafe_chars(string candidate)
        {
            Assert.IsFalse(WtmCorrelationIdMiddleware.IsSafeCorrelationId(candidate, 128));
        }

        [TestMethod]
        public void IsSafeCorrelationId_rejects_null()
        {
            Assert.IsFalse(WtmCorrelationIdMiddleware.IsSafeCorrelationId(null, 128));
        }

        [TestMethod]
        public void IsSafeCorrelationId_enforces_max_length()
        {
            var ok = new string('a', 64);
            var tooLong = new string('a', 129);
            Assert.IsTrue(WtmCorrelationIdMiddleware.IsSafeCorrelationId(ok, 128));
            Assert.IsFalse(WtmCorrelationIdMiddleware.IsSafeCorrelationId(tooLong, 128));
            // Custom max still respected
            Assert.IsFalse(WtmCorrelationIdMiddleware.IsSafeCorrelationId(ok, 32));
        }

        // ── Options validation in ctor ──────────────────────────────────

        [TestMethod]
        public void Ctor_rejects_null_options()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                new WtmCorrelationIdMiddleware(_ => Task.CompletedTask, null!));
        }

        [TestMethod]
        public void Ctor_rejects_null_next()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                new WtmCorrelationIdMiddleware(null!, new WtmCorrelationIdOptions()));
        }

        // ── Integration via TestServer ──────────────────────────────────

        [TestMethod]
        public async Task Inbound_valid_header_is_adopted_and_echoed()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            using var req = new HttpRequestMessage(HttpMethod.Get, "/echo");
            req.Headers.Add(DefaultHeader, "caller-abc-123");
            var resp = await client.SendAsync(req);

            Assert.IsTrue(resp.Headers.Contains(DefaultHeader));
            Assert.AreEqual("caller-abc-123",
                string.Join("", resp.Headers.GetValues(DefaultHeader)));
            // Body echoes the adopted TraceIdentifier — confirms the
            // middleware replaced it.
            var body = await resp.Content.ReadAsStringAsync();
            Assert.AreEqual("caller-abc-123", body);
        }

        [TestMethod]
        public async Task Inbound_missing_header_auto_generates_UUID()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var resp = await client.GetAsync("/echo");
            Assert.IsTrue(resp.Headers.Contains(DefaultHeader));
            var emitted = string.Join("", resp.Headers.GetValues(DefaultHeader));
            Assert.AreEqual(32, emitted.Length, "Auto-gen UUID 'N' format is 32 hex chars.");
            foreach (var c in emitted)
            {
                Assert.IsTrue((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                    $"UUID 'N' format must be hex; found '{c}'.");
            }
        }

        [TestMethod]
        public async Task Inbound_invalid_header_falls_back_to_auto_generated()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            using var req = new HttpRequestMessage(HttpMethod.Get, "/echo");
            // Invalid: contains non-allowed char
            req.Headers.Add(DefaultHeader, "abc def");
            var resp = await client.SendAsync(req);

            var emitted = string.Join("", resp.Headers.GetValues(DefaultHeader));
            Assert.AreNotEqual("abc def", emitted,
                "Invalid inbound must not be echoed as-is.");
            Assert.AreEqual(32, emitted.Length,
                "Fell back to auto-generated UUID.");
        }

        [TestMethod]
        public async Task AdoptInbound_false_always_generates_fresh_id()
        {
            using var host = await BuildHostAsync(o => o.AdoptInbound = false);
            var client = host.GetTestClient();

            using var req = new HttpRequestMessage(HttpMethod.Get, "/echo");
            req.Headers.Add(DefaultHeader, "caller-wants-this-id");
            var resp = await client.SendAsync(req);

            var emitted = string.Join("", resp.Headers.GetValues(DefaultHeader));
            Assert.AreNotEqual("caller-wants-this-id", emitted);
            Assert.AreEqual(32, emitted.Length);
        }

        [TestMethod]
        public async Task EmitOutbound_false_omits_response_header()
        {
            using var host = await BuildHostAsync(o => o.EmitOutbound = false);
            var client = host.GetTestClient();

            using var req = new HttpRequestMessage(HttpMethod.Get, "/echo");
            req.Headers.Add(DefaultHeader, "caller-abc-123");
            var resp = await client.SendAsync(req);

            Assert.IsFalse(resp.Headers.Contains(DefaultHeader),
                "EmitOutbound=false must suppress response header.");
            // But the server still adopted the inbound ID — body confirms.
            var body = await resp.Content.ReadAsStringAsync();
            Assert.AreEqual("caller-abc-123", body);
        }

        [TestMethod]
        public async Task Custom_HeaderName_is_respected()
        {
            using var host = await BuildHostAsync(o => o.HeaderName = "X-Request-Id");
            var client = host.GetTestClient();

            using var req = new HttpRequestMessage(HttpMethod.Get, "/echo");
            req.Headers.Add("X-Request-Id", "req-99");
            var resp = await client.SendAsync(req);

            Assert.IsTrue(resp.Headers.Contains("X-Request-Id"));
            Assert.IsFalse(resp.Headers.Contains(DefaultHeader));
            var body = await resp.Content.ReadAsStringAsync();
            Assert.AreEqual("req-99", body);
        }

        // ── Host builder ─────────────────────────────────────────────────

        private static async Task<IHost> BuildHostAsync(Action<WtmCorrelationIdOptions>? configure = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.Configure(app =>
                    {
                        if (configure == null) { app.UseWtmCorrelationId(); }
                        else { app.UseWtmCorrelationId(configure); }

                        // Echo the middleware-adopted TraceIdentifier back
                        // in the response body so tests can verify the
                        // HttpContext state, not just headers.
                        app.Run(async ctx =>
                        {
                            await ctx.Response.WriteAsync(ctx.TraceIdentifier);
                        });
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }
    }
}
