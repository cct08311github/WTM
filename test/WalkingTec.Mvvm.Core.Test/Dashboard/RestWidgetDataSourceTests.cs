#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// Tests for issue #824: REST widget data source — HTTP fetch + JSON
    /// mapping + SSRF guard + cache.
    /// </summary>
    [TestClass]
    public class RestWidgetDataSourceTests
    {
        // ── URL validation / SSRF guard ──────────────────────────────────

        [TestMethod]
        public void ValidateUrl_rejects_empty_url()
        {
            var opts = new RestWidgetDataSourceOptions { Url = "" };
            Assert.ThrowsException<InvalidOperationException>(() => RestWidgetDataSource.ValidateUrl(opts));
        }

        [TestMethod]
        public void ValidateUrl_rejects_malformed_url()
        {
            var opts = new RestWidgetDataSourceOptions { Url = "not a url" };
            Assert.ThrowsException<InvalidOperationException>(() => RestWidgetDataSource.ValidateUrl(opts));
        }

        [TestMethod]
        public void ValidateUrl_rejects_http_by_default()
        {
            var opts = new RestWidgetDataSourceOptions { Url = "http://example.com/data" };
            var ex = Assert.ThrowsException<InvalidOperationException>(() => RestWidgetDataSource.ValidateUrl(opts));
            StringAssert.Contains(ex.Message, "http://");
        }

        [TestMethod]
        public void ValidateUrl_accepts_http_when_AllowHttp_is_true_and_network_allowed()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "http://8.8.8.8/anything",
                AllowHttp = true,
                AllowPrivateNetwork = true // skip SSRF check for this unit
            };
            RestWidgetDataSource.ValidateUrl(opts); // should not throw
        }

        [TestMethod]
        [DataRow("https://127.0.0.1/api")]
        [DataRow("https://10.0.0.1/api")]
        [DataRow("https://172.16.0.1/api")]
        [DataRow("https://172.31.255.255/api")]
        [DataRow("https://192.168.1.1/api")]
        [DataRow("https://169.254.169.254/latest/meta-data/")] // AWS IMDS
        [DataRow("https://224.0.0.1/api")]                      // multicast
        public void ValidateUrl_SSRF_blocks_private_ranges_by_default(string url)
        {
            var opts = new RestWidgetDataSourceOptions { Url = url };
            var ex = Assert.ThrowsException<InvalidOperationException>(() => RestWidgetDataSource.ValidateUrl(opts));
            StringAssert.Contains(ex.Message, "AllowPrivateNetwork");
        }

        [TestMethod]
        public void ValidateUrl_SSRF_allows_private_ranges_when_opted_in()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "https://10.0.0.1/api",
                AllowPrivateNetwork = true
            };
            RestWidgetDataSource.ValidateUrl(opts); // should not throw
        }

        [TestMethod]
        public void IsBlockedIp_classifies_ranges_correctly()
        {
            // Private
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("10.0.0.1")));
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("172.20.0.1")));
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("192.168.1.1")));
            // Loopback / link-local / multicast
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("127.0.0.1")));
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("169.254.169.254")));
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("224.0.0.1")));
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("::1")));
            // IPv6 ULA
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("fc00::1")));
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("fd12:3456::1")));
            // Public
            Assert.IsFalse(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("8.8.8.8")));
            Assert.IsFalse(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("1.1.1.1")));
            Assert.IsFalse(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse("2001:4860:4860::8888")));
        }

        // ── JSONPath extraction ──────────────────────────────────────────

        [TestMethod]
        public void ExtractJsonPath_root_returns_whole_node()
        {
            var node = RestWidgetDataSource.ExtractJsonPath("{\"a\":1}", "$");
            Assert.IsNotNull(node);
            Assert.IsInstanceOfType(node, typeof(JsonObject));
        }

        [TestMethod]
        public void ExtractJsonPath_nested_dot_path_descends()
        {
            var node = RestWidgetDataSource.ExtractJsonPath(
                "{\"data\":{\"items\":[1,2,3]}}",
                "$.data.items");
            Assert.IsNotNull(node);
            Assert.IsInstanceOfType(node, typeof(JsonArray));
            Assert.AreEqual(3, (node as JsonArray)!.Count);
        }

        [TestMethod]
        public void ExtractJsonPath_missing_segment_returns_null()
        {
            var node = RestWidgetDataSource.ExtractJsonPath(
                "{\"a\":1}",
                "$.nope.missing");
            Assert.IsNull(node);
        }

        [TestMethod]
        public void ExtractJsonPath_invalid_json_throws()
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                RestWidgetDataSource.ExtractJsonPath("not json", "$"));
        }

        // ── JSON → WidgetDataResult mapping ──────────────────────────────

        [TestMethod]
        public void MapToWidgetResult_scalar_populates_Value()
        {
            var r = RestWidgetDataSource.MapToWidgetResult(JsonNode.Parse("42"));
            Assert.AreEqual(42L, r.Value);
        }

        [TestMethod]
        public void MapToWidgetResult_array_of_objects_populates_Rows_and_Columns()
        {
            var r = RestWidgetDataSource.MapToWidgetResult(JsonNode.Parse(
                "[{\"name\":\"alice\",\"age\":30},{\"name\":\"bob\",\"age\":25}]"));
            Assert.IsNotNull(r.Rows);
            Assert.AreEqual(2, r.Rows!.Count);
            CollectionAssert.AreEqual(new[] { "name", "age" }, r.Columns!.ToArray());
            Assert.AreEqual("alice", r.Rows[0]["name"]);
            Assert.AreEqual(30L, r.Rows[0]["age"]);
        }

        [TestMethod]
        public void MapToWidgetResult_array_of_primitives_creates_value_column()
        {
            var r = RestWidgetDataSource.MapToWidgetResult(JsonNode.Parse("[10, 20, 30]"));
            Assert.IsNotNull(r.Rows);
            Assert.AreEqual(3, r.Rows!.Count);
            CollectionAssert.AreEqual(new[] { "value" }, r.Columns!.ToArray());
            Assert.AreEqual(10L, r.Rows[0]["value"]);
        }

        [TestMethod]
        public void MapToWidgetResult_single_object_produces_one_row()
        {
            var r = RestWidgetDataSource.MapToWidgetResult(JsonNode.Parse("{\"count\":5,\"label\":\"orders\"}"));
            Assert.IsNotNull(r.Rows);
            Assert.AreEqual(1, r.Rows!.Count);
            Assert.AreEqual(5L, r.Rows[0]["count"]);
            Assert.AreEqual("orders", r.Rows[0]["label"]);
        }

        [TestMethod]
        public void MapToWidgetResult_null_node_produces_null_Value()
        {
            var r = RestWidgetDataSource.MapToWidgetResult(null);
            Assert.IsNull(r.Value);
            Assert.IsNull(r.Rows);
        }

        // ── End-to-end with mocked HttpClient ────────────────────────────

        [TestMethod]
        public async Task GetDataAsync_end_to_end_with_public_URL_and_json_body()
        {
            var mockHandler = new MockHttpHandler(_ =>
            {
                var body = "{\"total\":42,\"items\":[{\"id\":1},{\"id\":2}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(mockHandler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/data", // public IP → passes SSRF
                        JsonPath = "$.items",
                        CacheTtlSeconds = 0
                    })
                }
            };

            var r = await source.GetDataAsync(req, CancellationToken.None);
            Assert.IsNotNull(r.Rows);
            Assert.AreEqual(2, r.Rows!.Count);
            Assert.AreEqual(1L, r.Rows[0]["id"]);
            Assert.AreEqual(2L, r.Rows[1]["id"]);
        }

        [TestMethod]
        public async Task GetDataAsync_caches_response_when_CacheTtlSeconds_gt_zero()
        {
            int calls = 0;
            var mockHandler = new MockHttpHandler(_ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"n\":1}", Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(mockHandler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/cached",
                        CacheTtlSeconds = 60
                    })
                }
            };

            await source.GetDataAsync(req, CancellationToken.None);
            await source.GetDataAsync(req, CancellationToken.None);
            await source.GetDataAsync(req, CancellationToken.None);

            Assert.AreEqual(1, calls, "Cache miss only once; subsequent calls served from cache.");
        }

        [TestMethod]
        public async Task GetDataAsync_throws_on_missing_options_parameter()
        {
            var factory = new SingleClientFactory(new MockHttpHandler(_ => throw new InvalidOperationException("unreachable")));
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest { Parameters = new Dictionary<string, string>() };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(req, CancellationToken.None));
        }

        [TestMethod]
        public async Task GetDataAsync_enforces_max_response_size()
        {
            var largeBody = new string('x', 2 * 1024 * 1024); // 2 MiB, default max is 1 MiB
            var mockHandler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(largeBody, Encoding.UTF8, "text/plain")
            });
            var factory = new SingleClientFactory(mockHandler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/big",
                        CacheTtlSeconds = 0
                    })
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(req, CancellationToken.None));
            StringAssert.Contains(ex.Message, "MaxResponseBytes");
        }
    }

    // ── Test doubles ─────────────────────────────────────────────────────

    internal sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    internal sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
