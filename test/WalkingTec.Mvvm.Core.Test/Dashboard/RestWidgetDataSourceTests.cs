#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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

        /// <summary>
        /// #948: AllowHttp/AllowPrivateNetwork on RestOptions no longer grant access by
        /// themselves — both fields live inside the widget definition, which
        /// _DashboardController.Create/Update and _DashboardDesignerController.Preview all
        /// accept directly from the caller. Without a registered IDashboardEgressPolicy,
        /// setting them is a no-op; the request must still be rejected.
        /// This test replaces the pre-#948 version of the same name, which asserted the
        /// opposite (that AllowHttp=true alone was sufficient) — that was the vulnerability.
        /// </summary>
        [TestMethod]
        public async Task ValidateUrlAsync_rejects_http_even_when_AllowHttp_and_AllowPrivateNetwork_true_without_egress_policy()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "http://8.8.8.8/anything",
                AllowHttp = true,
                AllowPrivateNetwork = true
            };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => RestWidgetDataSource.ValidateUrlAsync(opts),
                "AllowHttp/AllowPrivateNetwork alone must not grant plain-HTTP egress (#948)");
        }

        /// <summary>
        /// #948: the same destination as above IS reachable once a host registers an
        /// IDashboardEgressPolicy that approves it — proving the seam is functional, not
        /// just a blanket deny.
        /// </summary>
        [TestMethod]
        public async Task ValidateUrlAsync_accepts_http_when_egress_policy_approves_the_resolved_destination()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "http://8.8.8.8/anything",
                AllowHttp = true,
                AllowPrivateNetwork = true
            };
            // should not throw — the policy approves this exact destination.
            await RestWidgetDataSource.ValidateUrlAsync(opts, CancellationToken.None, new AlwaysApproveEgressPolicy());
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

        /// <summary>
        /// #948: replaces the pre-#948 version of this test, which asserted that
        /// AllowPrivateNetwork=true alone (with no egress policy) was sufficient to reach a
        /// private-range destination — that was the vulnerability this issue closes.
        /// </summary>
        [TestMethod]
        public async Task ValidateUrlAsync_SSRF_still_blocks_private_ranges_when_AllowPrivateNetwork_true_but_no_egress_policy()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "https://10.0.0.1/api",
                AllowPrivateNetwork = true
            };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => RestWidgetDataSource.ValidateUrlAsync(opts),
                "AllowPrivateNetwork alone must not grant private-network egress (#948)");
        }

        /// <summary>
        /// #948: the same private-range destination becomes reachable once a host registers
        /// an IDashboardEgressPolicy that approves it.
        /// </summary>
        [TestMethod]
        public async Task ValidateUrlAsync_SSRF_allows_private_ranges_when_egress_policy_approves()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "https://10.0.0.1/api",
                AllowPrivateNetwork = true
            };
            // should not throw — the policy approves this exact destination.
            await RestWidgetDataSource.ValidateUrlAsync(opts, CancellationToken.None, new AlwaysApproveEgressPolicy());
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

        // ── #955 review finding F3: SelectConnectableIpAsync (the connect-time,
        // policy-aware IP selection PinnedConnectAsync's ConnectCallback actually uses)
        // had zero direct test coverage — every existing GetDataAsync_* test used a fake
        // IHttpClientFactory/HttpMessageHandler that never reaches SocketsHttpHandler's
        // ConnectCallback at all. These tests exercise the method directly.
        // Deleting the "if (egressPolicy == null) return null;" guard and the foreach
        // policy-consultation loop (test/mutants/entries/
        // 948-restwidget-selectconnectableipasync-policy-consultation-neutralize.json)
        // turns SelectConnectableIpAsync_ApprovingPolicy_* tests below red — with the
        // guard gone, an approved private/plain-HTTP destination can never be reached.

        private static readonly Uri TestRequestUri = new("https://example.test/data");
        private static readonly Uri TestPlainHttpRequestUri = new("http://example.test/data");

        [TestMethod]
        public async Task SelectConnectableIpAsync_AllPrivateCandidates_NoPolicy_ReturnsNull_ZeroPolicyCalls()
        {
            var candidates = new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("192.168.1.1") };
            var result = await RestWidgetDataSource.SelectConnectableIpAsync(
                candidates, TestRequestUri, port: 443, isPlainHttp: false, egressPolicy: null, CancellationToken.None);
            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task SelectConnectableIpAsync_AllPrivateCandidates_ApprovingPolicy_ReturnsFirstApproved()
        {
            var candidates = new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("192.168.1.1") };
            var policy = new StubEgressPolicy(approve: true);
            var result = await RestWidgetDataSource.SelectConnectableIpAsync(
                candidates, TestRequestUri, port: 8080, isPlainHttp: false, policy, CancellationToken.None);
            Assert.AreEqual(IPAddress.Parse("10.0.0.1"), result);
            Assert.IsTrue(policy.WasCalled);
            Assert.IsNotNull(policy.LastDestination);
            Assert.AreEqual(IPAddress.Parse("10.0.0.1"), policy.LastDestination!.ResolvedAddress);
            Assert.AreEqual(8080, policy.LastDestination.Port);
            Assert.IsTrue(policy.LastDestination.IsPrivateNetwork);
            Assert.IsFalse(policy.LastDestination.IsPlainHttp);
        }

        [TestMethod]
        public async Task SelectConnectableIpAsync_AllPrivateCandidates_DenyingPolicy_ReturnsNull_TriesEveryCandidate()
        {
            var candidates = new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("192.168.1.1") };
            var policy = new StubEgressPolicy(approve: false);
            var result = await RestWidgetDataSource.SelectConnectableIpAsync(
                candidates, TestRequestUri, port: 443, isPlainHttp: false, policy, CancellationToken.None);
            Assert.IsNull(result);
            Assert.AreEqual(candidates.Length, policy.CallCount,
                "a denying policy must be asked about every candidate before giving up");
        }

        [TestMethod]
        public async Task SelectConnectableIpAsync_MixedPrivateAndPublicCandidates_NoPolicy_ReturnsPublicIp_ZeroPolicyCalls()
        {
            // #955 review F7: the fast path must find the public candidate regardless of
            // its position in the candidate list, without ever consulting a policy —
            // this is the exact scenario ValidateUrlAsync used to get wrong (rejecting
            // outright on the first private candidate instead of trying the rest).
            var candidates = new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("8.8.8.8") };
            var result = await RestWidgetDataSource.SelectConnectableIpAsync(
                candidates, TestRequestUri, port: 443, isPlainHttp: false, egressPolicy: null, CancellationToken.None);
            Assert.AreEqual(IPAddress.Parse("8.8.8.8"), result);
        }

        [TestMethod]
        public async Task SelectConnectableIpAsync_PlainHttp_PublicCandidate_NoPolicy_ReturnsNull()
        {
            // Plain HTTP always needs policy approval, even to a public IP — the fast
            // path is scheme-gated, not just IP-gated.
            var candidates = new[] { IPAddress.Parse("8.8.8.8") };
            var result = await RestWidgetDataSource.SelectConnectableIpAsync(
                candidates, TestPlainHttpRequestUri, port: 80, isPlainHttp: true, egressPolicy: null, CancellationToken.None);
            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task SelectConnectableIpAsync_PlainHttp_PublicCandidate_ApprovingPolicy_ReturnsIt()
        {
            var candidates = new[] { IPAddress.Parse("8.8.8.8") };
            var policy = new StubEgressPolicy(approve: true);
            var result = await RestWidgetDataSource.SelectConnectableIpAsync(
                candidates, TestPlainHttpRequestUri, port: 80, isPlainHttp: true, policy, CancellationToken.None);
            Assert.AreEqual(IPAddress.Parse("8.8.8.8"), result);
            Assert.IsTrue(policy.WasCalled);
            Assert.IsTrue(policy.LastDestination!.IsPlainHttp);
            Assert.IsFalse(policy.LastDestination.IsPrivateNetwork);
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
            // #948: an approving IDashboardEgressPolicy (not AllowPrivateNetwork) is what
            // lets this request past the SSRF guard now. "localhost" is used instead of a
            // fake ".internal" hostname because ValidateUrlAsync must resolve DNS for any
            // hostname it cannot immediately classify as safe (it can no longer skip
            // resolution just because AllowPrivateNetwork was requested) — "localhost"
            // resolves instantly and deterministically via the OS with no real network
            // traffic, unlike a placeholder domain that would trigger a live (and likely
            // failing, in a sandboxed CI runner) DNS lookup.
            var source = new RestWidgetDataSource(factory, cache, new AlwaysApproveEgressPolicy());

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://localhost/v1/data",
                        CacheTtlSeconds = 0
                    })
                }
            };

            await source.GetDataAsync(req, CancellationToken.None);

            Assert.IsNotNull(capturedRequestUri, "Request should have been made");
            Assert.AreEqual("localhost", capturedRequestUri!.Host,
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

        // ── #948: GetDataAsync-level proof — a private-network destination never reaches
        // the HTTP layer without an approving IDashboardEgressPolicy, and does once one is
        // registered. Deleting the guard in RestWidgetDataSource.ValidateUrlAsync (or its
        // egressPolicy consultation) turns the first of these two tests red: the mock
        // handler would be invoked (callCount > 0) even though no policy was registered.

        [TestMethod]
        public async Task GetDataAsync_never_invokes_HTTP_handler_for_private_destination_without_egress_policy()
        {
            int callCount = 0;
            var handler = new MockHttpHandler(_ =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"secret\":\"leaked\"}", Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache); // no egress policy registered

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://169.254.169.254/latest/meta-data/iam/security-credentials/",
                        AllowPrivateNetwork = true, // attacker-controlled widget definition asks for this
                        CacheTtlSeconds = 0
                    })
                }
            };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(req, CancellationToken.None));
            Assert.AreEqual(0, callCount,
                "the HTTP handler must never be invoked for a blocked private-network destination — " +
                "AllowPrivateNetwork=true alone must not obtain egress (#948)");
        }

        [TestMethod]
        public async Task GetDataAsync_invokes_HTTP_handler_for_private_destination_when_egress_policy_approves()
        {
            int callCount = 0;
            var handler = new MockHttpHandler(_ =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache, new AlwaysApproveEgressPolicy());

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://10.0.0.1/internal-api",
                        AllowPrivateNetwork = true,
                        CacheTtlSeconds = 0
                    })
                }
            };

            var result = await source.GetDataAsync(req, CancellationToken.None);
            Assert.IsNotNull(result);
            Assert.AreEqual(1, callCount,
                "with a registered, approving IDashboardEgressPolicy the request must reach the HTTP layer");
        }

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

        // ── Cache key (issue #952) ────────────────────────────────────────
        // #952 exists because the pre-fix key was Method::Url::Body::JsonPath — no tenant, no
        // Headers. Deleting the tenant component of the new BuildCacheKey (i.e. reverting
        // "(tenantId ?? NullTenantSentinel) + canonicalJson" to just "canonicalJson") turns
        // GetDataAsync_cache_isolated_by_TenantId... red — see
        // test/mutants/entries/952-restwidget-cachekey-tenant-component-neutralize.json.

        private static RestWidgetDataSourceOptions AuthorizedFetchOptions(string url) => new()
        {
            Url = url,
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer victim-token" },
            CacheTtlSeconds = 60
        };

        private static RestWidgetDataSourceOptions UnauthenticatedFetchOptions(string url) => new()
        {
            Url = url,
            CacheTtlSeconds = 60
        };

        /// <summary>
        /// Two requests identical in Url/Method/Body/JsonPath but different TenantId must not
        /// share a cache entry. RED pre-fix: the old key had no tenant component at all, so the
        /// second call would be served from the first tenant's cached entry (callCount stays 1).
        /// </summary>
        [TestMethod]
        public async Task GetDataAsync_cache_NOT_shared_across_different_TenantId_for_identical_request()
        {
            int callCount = 0;
            var handler = new MockHttpHandler(_ =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"n\":1}", Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var optionsJson = JsonSerializer.Serialize(UnauthenticatedFetchOptions("https://8.8.8.8/shared-url"));
            var reqTenantA = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string> { ["options"] = optionsJson },
                TenantId = "tenant-a"
            };
            var reqTenantB = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string> { ["options"] = optionsJson },
                TenantId = "tenant-b"
            };

            await source.GetDataAsync(reqTenantA, CancellationToken.None);
            await source.GetDataAsync(reqTenantB, CancellationToken.None);

            Assert.AreEqual(2, callCount,
                "identical Url/Method/Body/JsonPath but different TenantId must NOT share a cache entry");
        }

        /// <summary>
        /// Within one tenant, two requests differing only in Headers must not share a cache
        /// entry. RED pre-fix: the old key had no Headers component, so an unauthenticated
        /// second request (no Headers) would be served the first (authorized) request's
        /// cached response.
        /// </summary>
        [TestMethod]
        public async Task GetDataAsync_cache_NOT_shared_across_different_Headers_within_same_tenant()
        {
            int callCount = 0;
            var handler = new MockHttpHandler(_ =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"n\":1}", Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var reqWithAuth = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(AuthorizedFetchOptions("https://8.8.8.8/shared-url"))
                },
                TenantId = "tenant-a"
            };
            var reqNoAuth = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(UnauthenticatedFetchOptions("https://8.8.8.8/shared-url"))
                },
                TenantId = "tenant-a"
            };

            await source.GetDataAsync(reqWithAuth, CancellationToken.None);
            await source.GetDataAsync(reqNoAuth, CancellationToken.None);

            Assert.AreEqual(2, callCount,
                "identical Url/Method/Body/JsonPath but different Headers must NOT share a cache entry, even within one tenant");
        }

        /// <summary>
        /// Positive control: two byte-identical requests DO still share a cache entry. Without
        /// this, a "fix" that simply disables caching entirely would also pass the two tests
        /// above.
        /// </summary>
        [TestMethod]
        public async Task GetDataAsync_cache_IS_shared_for_byte_identical_requests()
        {
            int callCount = 0;
            var handler = new MockHttpHandler(_ =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"n\":1}", Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var optionsJson = JsonSerializer.Serialize(AuthorizedFetchOptions("https://8.8.8.8/shared-url"));
            var req1 = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string> { ["options"] = optionsJson },
                TenantId = "tenant-a"
            };
            var req2 = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string> { ["options"] = optionsJson },
                TenantId = "tenant-a"
            };

            await source.GetDataAsync(req1, CancellationToken.None);
            await source.GetDataAsync(req2, CancellationToken.None);
            await source.GetDataAsync(req1, CancellationToken.None);

            Assert.AreEqual(1, callCount,
                "byte-identical requests (same tenant, same options) must still share a cache entry — " +
                "a fix that disables caching altogether would otherwise pass the isolation tests above for the wrong reason");
        }

        /// <summary>
        /// Attack reproduction, end to end (issue #952): a privileged widget with a real
        /// Authorization header completes a fetch and its response is cached. Within the TTL, a
        /// second widget with identical Url/Method/Body/JsonPath and NO headers calls
        /// GetWidgetData and must NOT receive the first widget's cached, authorized body.
        /// </summary>
        /// <remarks>
        /// The theft (pre-fix) is deterministic, not a race: an unauthenticated probe that gets
        /// a non-2xx response throws in FetchJsonAsync BEFORE <c>_cache.Set</c> is ever reached
        /// (see GetDataAsync's early-return-on-exception control flow), so a failed probe never
        /// poisons the key — an attacker could poll for free, with no window to miss, until the
        /// victim's authorized response lands and gets cached under the (pre-fix) shared key.
        /// This test does not need to model that polling window itself: the two-call sequence
        /// below (authorized fetch succeeds and caches; unauthenticated fetch follows within
        /// TTL) already is the moment the theft would be observed.
        /// </remarks>
        [TestMethod]
        public async Task GetDataAsync_unauthenticated_second_widget_does_not_receive_first_widgets_cached_authorized_response()
        {
            var handler = new MockHttpHandler(req =>
            {
                var isAuthorized = req.Headers.TryGetValues("Authorization", out var vals) &&
                                    vals.Contains("Bearer victim-token");
                var body = isAuthorized
                    ? "{\"secret\":\"vip-authorized-data\"}"
                    : "{\"secret\":\"public-unauthorized-data\"}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            // Same RestWidgetDataSource / same IMemoryCache instance for both widgets — this is
            // what production looks like: RestWidgetDataSource is registered Transient but the
            // IMemoryCache it's constructed with is Singleton (AddWtmDashboard/AddMemoryCache),
            // so unrelated widgets across the whole app genuinely share one cache backend.
            var source = new RestWidgetDataSource(factory, cache);

            const string sharedUrl = "https://8.8.8.8/vendor-api";
            var privilegedWidgetReq = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = sharedUrl,
                        Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer victim-token" },
                        JsonPath = "$.secret",
                        CacheTtlSeconds = 60
                    })
                },
                TenantId = "tenant-a"
            };
            var attackerWidgetReq = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = sharedUrl,
                        JsonPath = "$.secret",
                        CacheTtlSeconds = 60
                    })
                },
                TenantId = "tenant-a"
            };

            var privilegedResult = await source.GetDataAsync(privilegedWidgetReq, CancellationToken.None);
            var attackerResult = await source.GetDataAsync(attackerWidgetReq, CancellationToken.None);

            privilegedResult.Value.Should().Be("vip-authorized-data");
            attackerResult.Value.Should().Be("public-unauthorized-data",
                "the unauthenticated widget must get its OWN response, never the privileged widget's cached one");
            attackerResult.Value.Should().NotBe(privilegedResult.Value,
                "if these ever match, the cache leaked the authorized response to the unauthenticated request");
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

    /// <summary>
    /// #948: test double for <see cref="IDashboardEgressPolicy"/> that approves every
    /// destination it is asked about. Stands in for a host-authored policy that has
    /// already validated the destination against its own allowlist/config — tests use
    /// this only to prove the seam is wired up and functional, never as a substitute for
    /// exercising a real policy's own decision logic.
    /// </summary>
    internal sealed class AlwaysApproveEgressPolicy : IDashboardEgressPolicy
    {
        public Task<bool> IsAllowedAsync(DashboardEgressDestination destination, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    /// <summary>#948: test double for <see cref="IDashboardEgressPolicy"/> that denies every destination.</summary>
    internal sealed class AlwaysDenyEgressPolicy : IDashboardEgressPolicy
    {
        public Task<bool> IsAllowedAsync(DashboardEgressDestination destination, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
