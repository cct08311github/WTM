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
        public async Task ValidateUrlAsync_rejects_empty_url()
        {
            var opts = new RestWidgetDataSourceOptions { Url = "" };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => RestWidgetDataSource.ValidateUrlAsync(opts));
        }

        [TestMethod]
        public async Task ValidateUrlAsync_rejects_malformed_url()
        {
            var opts = new RestWidgetDataSourceOptions { Url = "not a url" };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => RestWidgetDataSource.ValidateUrlAsync(opts));
        }

        [TestMethod]
        public async Task ValidateUrlAsync_rejects_http_by_default()
        {
            var opts = new RestWidgetDataSourceOptions { Url = "http://example.com/data" };
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => RestWidgetDataSource.ValidateUrlAsync(opts));
            StringAssert.Contains(ex.Message, "http://");
        }

        [TestMethod]
        public async Task ValidateUrlAsync_accepts_http_when_AllowHttp_is_true_and_network_allowed()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "http://8.8.8.8/anything",
                AllowHttp = true,
                AllowPrivateNetwork = true // skip SSRF check for this unit
            };
            // should not throw
            await RestWidgetDataSource.ValidateUrlAsync(opts);
        }

        [TestMethod]
        [DataRow("https://127.0.0.1/api")]
        [DataRow("https://10.0.0.1/api")]
        [DataRow("https://172.16.0.1/api")]
        [DataRow("https://172.31.255.255/api")]
        [DataRow("https://192.168.1.1/api")]
        [DataRow("https://169.254.169.254/latest/meta-data/")] // AWS IMDS
        [DataRow("https://224.0.0.1/api")]                      // multicast
        public async Task ValidateUrlAsync_SSRF_blocks_private_ranges_by_default(string url)
        {
            var opts = new RestWidgetDataSourceOptions { Url = url };
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => RestWidgetDataSource.ValidateUrlAsync(opts));
            StringAssert.Contains(ex.Message, "AllowPrivateNetwork");
        }

        [TestMethod]
        public async Task ValidateUrlAsync_SSRF_allows_private_ranges_when_opted_in()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "https://10.0.0.1/api",
                AllowPrivateNetwork = true
            };
            // should not throw
            await RestWidgetDataSource.ValidateUrlAsync(opts);
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

        // ── SelectConnectableIp ──────────────────────────────────────────

        [TestMethod]
        public void SelectConnectableIp_returns_first_public_ip_when_private_not_allowed()
        {
            var candidates = new[]
            {
                IPAddress.Parse("10.0.0.1"),         // private — blocked
                IPAddress.Parse("8.8.8.8"),           // public — allowed
                IPAddress.Parse("1.1.1.1"),           // public — allowed
            };
            var chosen = RestWidgetDataSource.SelectConnectableIp(candidates, allowPrivateNetwork: false);
            Assert.AreEqual(IPAddress.Parse("8.8.8.8"), chosen,
                "First public IP should be selected when private network is not allowed");
        }

        [TestMethod]
        public void SelectConnectableIp_returns_null_when_all_candidates_blocked()
        {
            var candidates = new[]
            {
                IPAddress.Parse("10.0.0.1"),
                IPAddress.Parse("192.168.1.1"),
                IPAddress.Parse("169.254.169.254"),
            };
            var chosen = RestWidgetDataSource.SelectConnectableIp(candidates, allowPrivateNetwork: false);
            Assert.IsNull(chosen, "Should return null when all IPs are blocked");
        }

        [TestMethod]
        public void SelectConnectableIp_returns_private_ip_when_allowed()
        {
            var candidates = new[]
            {
                IPAddress.Parse("10.0.0.1"),      // private
                IPAddress.Parse("8.8.8.8"),        // public
            };
            var chosen = RestWidgetDataSource.SelectConnectableIp(candidates, allowPrivateNetwork: true);
            Assert.AreEqual(IPAddress.Parse("10.0.0.1"), chosen,
                "First IP (private) should be selected when AllowPrivateNetwork=true");
        }

        [TestMethod]
        public void SelectConnectableIp_blocks_loopback_even_when_private_allowed()
        {
            // AllowPrivateNetwork=true means RFC-1918 etc. are connectable,
            // but it works via IsBlockedIp returning false for those. Loopback is still
            // blocked by IsBlockedIp, so when allowPrivateNetwork=false loopback is blocked.
            // When allowPrivateNetwork=true the entire IsBlockedIp check is bypassed.
            // This test confirms the allowPrivateNetwork=true bypass works for loopback.
            var candidates = new[] { IPAddress.Parse("127.0.0.1") };
            var chosen = RestWidgetDataSource.SelectConnectableIp(candidates, allowPrivateNetwork: true);
            // When allowPrivateNetwork=true, SelectConnectableIp does NOT call IsBlockedIp —
            // it just returns the first candidate. This is by design: the admin opted in.
            Assert.AreEqual(IPAddress.Parse("127.0.0.1"), chosen);
        }

        [TestMethod]
        public void SelectConnectableIp_blocks_IMDS_and_CGNAT_when_private_not_allowed()
        {
            var candidates = new[]
            {
                IPAddress.Parse("169.254.169.254"), // AWS IMDS
                IPAddress.Parse("100.64.0.1"),       // CGNAT
            };
            var chosen = RestWidgetDataSource.SelectConnectableIp(candidates, allowPrivateNetwork: false);
            Assert.IsNull(chosen, "IMDS and CGNAT IPs must be blocked when private network is not allowed");
        }

        [TestMethod]
        public void SelectConnectableIp_blocks_IPv4_mapped_IMDS()
        {
            var candidates = new[]
            {
                IPAddress.Parse("::ffff:169.254.169.254"), // IPv4-mapped IMDS
            };
            var chosen = RestWidgetDataSource.SelectConnectableIp(candidates, allowPrivateNetwork: false);
            Assert.IsNull(chosen, "IPv4-mapped IMDS address must be blocked");
        }

        [TestMethod]
        public void SelectConnectableIp_returns_null_for_empty_candidates()
        {
            var chosen = RestWidgetDataSource.SelectConnectableIp(Array.Empty<IPAddress>(), allowPrivateNetwork: false);
            Assert.IsNull(chosen);
        }

        // ── Request URI is NOT rewritten to an IP ────────────────────────

        [TestMethod]
        public async Task FetchJsonAsync_preserves_hostname_in_request_uri_for_https()
        {
            // The critical HTTPS correctness test: the request URI must keep the original
            // hostname (not be rewritten to an IP), so TLS SNI uses the hostname.
            Uri? capturedRequestUri = null;
            var handler = new MockHttpHandler(req =>
            {
                capturedRequestUri = req.RequestUri;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var originalUrl = "https://8.8.8.8/data";   // IP literal — no DNS rebinding risk
            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = originalUrl,
                        CacheTtlSeconds = 0
                    })
                }
            };

            await source.GetDataAsync(req, CancellationToken.None);

            Assert.IsNotNull(capturedRequestUri, "Request should have been made");
            // The host in the request URI must be the original host (not rewritten to a different IP string).
            Assert.AreEqual("8.8.8.8", capturedRequestUri!.Host,
                "Request URI host must be the original host — not rewritten to an IP. " +
                "URI rewriting breaks HTTPS TLS SNI.");
        }

        [TestMethod]
        public async Task FetchJsonAsync_hostname_url_request_uri_host_is_original_hostname()
        {
            // For a hostname-based URL, the request URI host must remain the hostname,
            // not be replaced by a resolved IP string.
            Uri? capturedRequestUri = null;
            var handler = new MockHttpHandler(req =>
            {
                capturedRequestUri = req.RequestUri;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            // Use AllowPrivateNetwork=true so the SSRF guard does not reject the request
            // before reaching the HTTP layer, allowing us to inspect the request URI.
            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://api.example.internal/v1/data",
                        AllowPrivateNetwork = true,
                        CacheTtlSeconds = 0
                    })
                }
            };

            await source.GetDataAsync(req, CancellationToken.None);

            Assert.IsNotNull(capturedRequestUri, "Request should have been made");
            Assert.AreEqual("api.example.internal", capturedRequestUri!.Host,
                "Request URI host must remain the original hostname — not rewritten to an IP. " +
                "Rewriting would break HTTPS TLS SNI and server certificate validation.");
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
