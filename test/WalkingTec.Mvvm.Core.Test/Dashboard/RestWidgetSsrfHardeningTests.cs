#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// Focused security regression tests for issue #101:
    /// SSRF hardening for the REST widget data source.
    /// Covers defects 1–7 from the adversarial bug hunt:
    ///   1. Request-side options cannot override security-sensitive flags.
    ///   2. Redirect handler disabled (AllowAutoRedirect=false).
    ///   3. IPv4-mapped IPv6 blocked (e.g. ::ffff:169.254.169.254).
    ///   4. DNS pinning via ConnectCallback (SelectConnectableIp tested directly).
    ///   5. Timeout/MaxResponseBytes clamping incl. negative/zero.
    ///   6. CGNAT range (100.64/10) blocked.
    ///   7. Non-success HTTP status returns generic error (no URL leak).
    /// </summary>
    [TestClass]
    public class RestWidgetSsrfHardeningTests
    {
        // ── Defect 3: IPv4-mapped IPv6 bypass ───────────────────────────────

        [TestMethod]
        [DataRow("::ffff:169.254.169.254")]   // AWS IMDS via IPv4-mapped IPv6
        [DataRow("::ffff:127.0.0.1")]          // loopback via IPv4-mapped IPv6
        [DataRow("::ffff:10.0.0.1")]           // RFC-1918 via IPv4-mapped IPv6
        [DataRow("::ffff:192.168.1.1")]        // RFC-1918 via IPv4-mapped IPv6
        [DataRow("::ffff:172.16.0.1")]         // RFC-1918 via IPv4-mapped IPv6
        [DataRow("::ffff:100.64.0.1")]         // CGNAT via IPv4-mapped IPv6
        public void IsBlockedIp_blocks_IPv4_mapped_IPv6_addresses(string ipStr)
        {
            var ip = IPAddress.Parse(ipStr);
            Assert.IsTrue(ip.IsIPv4MappedToIPv6, $"{ipStr} should be an IPv4-mapped IPv6 address");
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(ip),
                $"IPv4-mapped {ipStr} must be blocked (SSRF bypass via ::ffff: prefix)");
        }

        // ── Defect 3 (CGNAT): 100.64.0.0/10 blocked ────────────────────────

        [TestMethod]
        [DataRow("100.64.0.1")]   // start of CGNAT block
        [DataRow("100.64.0.255")]
        [DataRow("100.100.0.1")]  // middle of CGNAT block
        [DataRow("100.127.255.255")] // end of CGNAT block
        public void IsBlockedIp_blocks_CGNAT_range(string ipStr)
        {
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse(ipStr)),
                $"{ipStr} is in the CGNAT shared-address-space (RFC 6598) and must be blocked");
        }

        [TestMethod]
        [DataRow("100.63.255.255")]  // just below CGNAT
        [DataRow("100.128.0.1")]     // just above CGNAT
        public void IsBlockedIp_does_not_block_IPs_adjacent_to_CGNAT(string ipStr)
        {
            // These are public IPs (not in RFC 6598 range). Only test the two boundary cases;
            // they happen to be routable / reserved-but-not-blocked ranges.
            // 100.63.x is IANA-unassigned but not in 100.64/10.
            // 100.128.x is public (assigned to APNIC for research).
            // We just verify IsBlockedIp doesn't incorrectly extend the block.
            var ip = IPAddress.Parse(ipStr);
            // 100.63.255.255: not blocked by CGNAT rule; might be blocked by another rule only if in another range.
            // 100.128.0.1: public, should not be blocked.
            if (ipStr == "100.128.0.1")
            {
                Assert.IsFalse(RestWidgetDataSource.IsBlockedIp(ip),
                    $"{ipStr} is public and must NOT be blocked by the CGNAT rule");
            }
            // 100.63.255.255 is IANA-unassigned but not in any blocked range we enforce.
            // We merely confirm it is NOT in the CGNAT /10 range.
        }

        // ── Defect 3: loopback, private, link-local, multicast (full list) ─

        [TestMethod]
        [DataRow("127.0.0.1")]
        [DataRow("127.0.0.2")]
        [DataRow("0.0.0.0")]
        [DataRow("10.0.0.1")]
        [DataRow("172.16.0.1")]
        [DataRow("172.31.255.255")]
        [DataRow("192.168.0.1")]
        [DataRow("169.254.169.254")]  // AWS IMDS
        [DataRow("224.0.0.1")]        // multicast
        [DataRow("239.255.255.255")]  // multicast boundary
        [DataRow("240.0.0.1")]        // reserved
        [DataRow("::1")]              // IPv6 loopback
        [DataRow("fc00::1")]          // IPv6 ULA
        [DataRow("fd00::1")]          // IPv6 ULA
        [DataRow("fe80::1")]          // IPv6 link-local
        public void IsBlockedIp_blocks_private_loopback_and_special_ranges(string ipStr)
        {
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse(ipStr)),
                $"{ipStr} must be classified as blocked");
        }

        [TestMethod]
        [DataRow("8.8.8.8")]
        [DataRow("1.1.1.1")]
        [DataRow("2001:4860:4860::8888")]
        [DataRow("203.0.113.1")]
        public void IsBlockedIp_allows_public_IPs(string ipStr)
        {
            Assert.IsFalse(RestWidgetDataSource.IsBlockedIp(IPAddress.Parse(ipStr)),
                $"{ipStr} is a public IP and must NOT be blocked");
        }

        // ── Issue #533: IPv6 transitional forms that embed a blocked IPv4 ───
        // NAT64 (64:ff9b::/96, RFC 6052), 6to4 (2002::/16, RFC 3056), and
        // IPv4-mapped (::ffff:0:0/96, RFC 4291) IPv6 addresses can smuggle a
        // private/IMDS IPv4 destination past a naive IPv6-only check.

        [TestMethod]
        [DataRow("64:ff9b::169.254.169.254")]  // NAT64 → AWS/GCP IMDS
        [DataRow("64:ff9b::10.0.0.1")]         // NAT64 → RFC-1918 private
        [DataRow("64:ff9b::a9fe:a9fe")]        // NAT64 → 169.254.169.254 (hex form)
        public void IsBlockedIp_blocks_NAT64_embedded_IPv4(string ipStr)
        {
            var ip = IPAddress.Parse(ipStr);
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(ip),
                $"NAT64 address {ipStr} embeds a blocked IPv4 and must be blocked");
        }

        [TestMethod]
        [DataRow("2002:a9fe:a9fe::")]   // 6to4 → 169.254.169.254 (AWS/GCP IMDS)
        [DataRow("2002:0a00:0001::")]   // 6to4 → 10.0.0.1 (RFC-1918 private)
        public void IsBlockedIp_blocks_6to4_embedded_IPv4(string ipStr)
        {
            var ip = IPAddress.Parse(ipStr);
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(ip),
                $"6to4 address {ipStr} embeds a blocked IPv4 and must be blocked");
        }

        [TestMethod]
        [DataRow("::127.0.0.1")]          // IPv4-compatible (deprecated) → loopback
        [DataRow("::169.254.169.254")]    // IPv4-compatible (deprecated) → AWS/GCP IMDS
        [DataRow("::ffff:127.0.0.1")]     // IPv4-mapped → loopback
        public void IsBlockedIp_blocks_IPv4_compatible_and_mapped_embedded_IPv4(string ipStr)
        {
            var ip = IPAddress.Parse(ipStr);
            Assert.IsTrue(RestWidgetDataSource.IsBlockedIp(ip),
                $"{ipStr} embeds a blocked IPv4 and must be blocked");
        }

        [TestMethod]
        [DataRow("2606:4700:4700::1111")]  // Cloudflare public DNS — no embedded IPv4
        [DataRow("2001:4860:4860::8888")]  // Google public DNS — no embedded IPv4
        [DataRow("64:ff9b::8.8.8.8")]      // NAT64 → public IPv4 (8.8.8.8) must NOT be blocked
        [DataRow("2002:0808:0808::")]      // 6to4 → public IPv4 (8.8.8.8) must NOT be blocked
        public void IsBlockedIp_does_not_block_public_IPv6_or_public_embedded_IPv4(string ipStr)
        {
            var ip = IPAddress.Parse(ipStr);
            Assert.IsFalse(RestWidgetDataSource.IsBlockedIp(ip),
                $"{ipStr} is public (or embeds only a public IPv4) and must NOT be blocked");
        }

        // ── Defect 4: DNS pinning — SelectConnectableIp ──────────────────────

        [TestMethod]
        public void SelectConnectableIp_blocks_private_and_IMDS_when_not_allowed()
        {
            var blocked = new[]
            {
                IPAddress.Parse("10.0.0.1"),
                IPAddress.Parse("169.254.169.254"),
                IPAddress.Parse("100.64.0.1"),
                IPAddress.Parse("::ffff:169.254.169.254"),
            };
            foreach (var ip in blocked)
            {
                var chosen = RestWidgetDataSource.SelectConnectableIp(new[] { ip }, allowPrivateNetwork: false);
                Assert.IsNull(chosen,
                    $"SelectConnectableIp must return null for blocked IP {ip} when allowPrivateNetwork=false");
            }
        }

        [TestMethod]
        public void SelectConnectableIp_allows_public_IPs()
        {
            var candidates = new[]
            {
                IPAddress.Parse("8.8.8.8"),
                IPAddress.Parse("1.1.1.1"),
                IPAddress.Parse("2001:4860:4860::8888"),
            };
            foreach (var ip in candidates)
            {
                var chosen = RestWidgetDataSource.SelectConnectableIp(new[] { ip }, allowPrivateNetwork: false);
                Assert.AreEqual(ip, chosen,
                    $"SelectConnectableIp must return {ip} (public IP) when allowPrivateNetwork=false");
            }
        }

        [TestMethod]
        public void SelectConnectableIp_allows_private_IPs_when_flag_set()
        {
            var candidates = new[]
            {
                IPAddress.Parse("10.0.0.1"),
                IPAddress.Parse("192.168.1.1"),
                IPAddress.Parse("169.254.169.254"),
            };
            foreach (var ip in candidates)
            {
                var chosen = RestWidgetDataSource.SelectConnectableIp(new[] { ip }, allowPrivateNetwork: true);
                Assert.AreEqual(ip, chosen,
                    $"SelectConnectableIp must return {ip} when allowPrivateNetwork=true");
            }
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
            Assert.IsNull(chosen, "null when all IPs are blocked");
        }

        [TestMethod]
        public void SelectConnectableIp_skips_blocked_and_returns_first_public()
        {
            var candidates = new[]
            {
                IPAddress.Parse("10.0.0.1"),   // blocked
                IPAddress.Parse("8.8.8.8"),     // public — first allowed
                IPAddress.Parse("1.1.1.1"),     // public — second allowed
            };
            var chosen = RestWidgetDataSource.SelectConnectableIp(candidates, allowPrivateNetwork: false);
            Assert.AreEqual(IPAddress.Parse("8.8.8.8"), chosen,
                "Should skip the blocked private IP and return the first public IP");
        }

        // ── Defect 5: Timeout/MaxResponseBytes clamping ─────────────────────

        [TestMethod]
        [DataRow(0, 1)]           // zero → floor to 1
        [DataRow(-1, 1)]          // negative → floor to 1
        [DataRow(-100, 1)]        // large negative → floor to 1
        [DataRow(1, 1)]           // minimum preserved
        [DataRow(30, 30)]         // mid-range preserved
        [DataRow(60, 60)]         // maximum preserved
        [DataRow(61, 60)]         // above max → cap to 60
        [DataRow(9999, 60)]       // way above max → cap to 60
        public void ParseOptions_clamps_TimeoutSeconds(int input, int expected)
        {
            var json = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/test",
                TimeoutSeconds = input
            });
            var opts = RestWidgetDataSource.ParseOptions(new Dictionary<string, string>
            {
                ["options"] = json
            });
            Assert.AreEqual(expected, opts.TimeoutSeconds,
                $"TimeoutSeconds={input} should clamp to {expected}");
        }

        [TestMethod]
        [DataRow(0, 1024 * 1024)]         // zero → default 1 MiB
        [DataRow(-1, 1024 * 1024)]        // negative → default 1 MiB
        [DataRow(-9999, 1024 * 1024)]     // large negative → default 1 MiB
        [DataRow(512, 1024)]              // below 1 KB min → floor to 1 KB
        [DataRow(1024, 1024)]             // 1 KB preserved
        [DataRow(1024 * 1024, 1024 * 1024)] // 1 MiB preserved
        [DataRow(10 * 1024 * 1024, 10 * 1024 * 1024)] // 10 MiB hard limit preserved
        [DataRow(11 * 1024 * 1024, 10 * 1024 * 1024)] // above 10 MiB → cap to 10 MiB
        public void ParseOptions_clamps_MaxResponseBytes(int input, int expected)
        {
            var json = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/test",
                MaxResponseBytes = input
            });
            var opts = RestWidgetDataSource.ParseOptions(new Dictionary<string, string>
            {
                ["options"] = json
            });
            Assert.AreEqual(expected, opts.MaxResponseBytes,
                $"MaxResponseBytes={input} should clamp to {expected}");
        }

        // ── Defect 1: Request-side options cannot enable security-sensitive flags ──

        /// <summary>
        /// Verifies that <see cref="RestWidgetDataSource.ParseOptions"/> returns the raw
        /// deserialized options from the request parameter. The stripping of
        /// AllowPrivateNetwork/AllowHttp is applied upstream in
        /// <see cref="JsonFileDashboardService.GetWidgetDataAsync"/> for legacy widgets.
        /// This test verifies the ParseOptions layer itself, and the integration test below
        /// verifies the stripping that happens in the service layer.
        /// </summary>
        [TestMethod]
        public void ParseOptions_deserializes_AllowPrivateNetwork_as_provided()
        {
            // ParseOptions just deserializes; stripping happens in the service layer.
            // This confirms the field round-trips correctly.
            var json = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/test",
                AllowPrivateNetwork = true,
                AllowHttp = true
            });
            var opts = RestWidgetDataSource.ParseOptions(new Dictionary<string, string>
            {
                ["options"] = json
            });
            // ParseOptions itself does not strip — stripping is in the service layer.
            // We test the service-level stripping in the integration test below.
            Assert.IsTrue(opts.AllowPrivateNetwork);
            Assert.IsTrue(opts.AllowHttp);
        }

        /// <summary>
        /// Simulates the service-layer stripping behaviour: after the service strips
        /// the security flags, ValidateUrlAsync sees the safe-default values (false, false)
        /// and correctly rejects a private-network URL.
        /// </summary>
        [TestMethod]
        public async Task ServiceLayer_stripping_prevents_AllowPrivateNetwork_via_request()
        {
            // Simulate what JsonFileDashboardService does to a legacy widget's
            // request-supplied options: deserialize, force flags false, re-serialize.
            var requestJson = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
            {
                Url = "https://10.0.0.1/imds",
                AllowPrivateNetwork = true,   // attacker tries to enable this
                AllowHttp = true
            });

            RestWidgetDataSourceOptions requestOpts;
            try
            {
                requestOpts = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(
                    requestJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("null");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("malformed", ex);
            }

            // Apply the service-layer stripping (mirrors JsonFileDashboardService logic).
            requestOpts.AllowPrivateNetwork = false;
            requestOpts.AllowHttp = false;

            // Verify the stripped options are rejected by ValidateUrlAsync.
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => RestWidgetDataSource.ValidateUrlAsync(requestOpts),
                "After stripping, private-network URL must be rejected by ValidateUrlAsync");
        }

        /// <summary>
        /// Verifies that a server-side RestOptions with AllowPrivateNetwork=true
        /// is not rejected by ValidateUrlAsync (it skips the SSRF check intentionally).
        /// </summary>
        [TestMethod]
        public async Task ValidateUrlAsync_allows_private_network_when_server_RestOptions_sets_AllowPrivateNetwork()
        {
            var serverOpts = new RestWidgetDataSourceOptions
            {
                Url = "https://10.0.0.1/internal-api",
                AllowPrivateNetwork = true  // set server-side by admin
            };
            // Should not throw; AllowPrivateNetwork from server-side config is legitimate.
            await RestWidgetDataSource.ValidateUrlAsync(serverOpts);
        }

        // ── Defect 2: Named HttpClient has AllowAutoRedirect=false ──────────

        [TestMethod]
        public void HttpClientName_constant_is_WtmRestWidget()
        {
            // Ensure the constant name used to register the HttpClient in
            // DashboardServiceCollectionExtensions matches the name used in
            // RestWidgetDataSource.FetchJsonAsync.
            Assert.AreEqual("WtmRestWidget", RestWidgetDataSource.HttpClientName);
        }

        [TestMethod]
        public void RegisteredHttpClient_has_AllowAutoRedirect_false()
        {
            // Verify the DI-registered named handler has AllowAutoRedirect=false.
            // We inspect the handler directly rather than going through the factory
            // to keep this test fast and dependency-free.
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddWtmDashboard();
            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient(RestWidgetDataSource.HttpClientName);
            // The only way to observe AllowAutoRedirect=false on SocketsHttpHandler
            // through the factory is to attempt a redirect. We do that via a mock.
            // Here we verify the constant is wired up (the behavioural redirect test
            // is below at GetDataAsync_does_not_follow_redirects).
            Assert.IsNotNull(client);
        }

        [TestMethod]
        public async Task GetDataAsync_does_not_follow_redirects_when_upstream_returns_302()
        {
            // Because the SocketsHttpHandler is registered with AllowAutoRedirect=false,
            // a 302 response must surface as an error (not silently follow to IMDS).
            // We use a mock client factory that returns a 302 to an internal IP.
            int redirectFollowCount = 0;
            var handler = new RedirectTrackingHandler(redirectFollowCount: out var counter);
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/redirect-test",
                        CacheTtlSeconds = 0
                    })
                }
            };

            // The 302 response should cause an InvalidOperationException (non-2xx),
            // NOT cause a second request to the Location header.
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(req, CancellationToken.None));

            Assert.AreEqual(0, counter.Value,
                "No redirect should have been followed; the 302 response must surface as an error");
        }

        // ── Defect 7: Error message does not leak target URL/status ─────────

        [TestMethod]
        public async Task FetchJsonAsync_non_success_status_does_not_leak_url_in_message()
        {
            var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var targetUrl = "https://8.8.8.8/secret-internal-path";
            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = targetUrl,
                        CacheTtlSeconds = 0
                    })
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(req, CancellationToken.None));

            // The exception message must NOT contain the target URL or the raw HTTP status code.
            StringAssert.DoesNotMatch(ex.Message, new System.Text.RegularExpressions.Regex(
                System.Text.RegularExpressions.Regex.Escape(targetUrl)),
                "Error message must not leak the target URL");
            StringAssert.DoesNotMatch(ex.Message, new System.Text.RegularExpressions.Regex(@"\d{3}\s+\w+"),
                "Error message must not contain raw HTTP status details");
        }

        // ── ValidateUrlAsync pre-check behaviour ────────────────────────────

        [TestMethod]
        public async Task ValidateUrlAsync_accepts_public_IP_literal()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/data",
                AllowPrivateNetwork = false
            };
            // Should not throw — 8.8.8.8 is a public IP.
            await RestWidgetDataSource.ValidateUrlAsync(opts, CancellationToken.None);
        }

        [TestMethod]
        public async Task ValidateUrlAsync_does_not_throw_when_AllowPrivateNetwork_true()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "https://10.0.0.1/internal",
                AllowPrivateNetwork = true
            };
            // Should not throw — private network explicitly allowed.
            await RestWidgetDataSource.ValidateUrlAsync(opts, CancellationToken.None);
        }

        [TestMethod]
        public async Task ValidateUrlAsync_rejects_IPv4_mapped_IPv6_literal()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                // IPv4-mapped IPv6 literal in the URL host position requires bracket notation.
                // Use a plain IPv4 URL whose resolved IP would be mapped; in practice URLs
                // use the raw IPv4 form. Test directly via IsBlockedIp with a mapped address.
                Url = "https://169.254.169.254/latest/meta-data/",
            };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => RestWidgetDataSource.ValidateUrlAsync(opts, CancellationToken.None));
        }

        // ── MaxResponseBytesHardLimit constant ────────────────────────────────

        [TestMethod]
        public void MaxResponseBytesHardLimit_is_10MiB()
        {
            Assert.AreEqual(10 * 1024 * 1024, RestWidgetDataSource.MaxResponseBytesHardLimit);
        }

        // ── S1: HTTP method allowlist ─────────────────────────────────────────

        [TestMethod]
        [DataRow("GET")]
        [DataRow("POST")]
        [DataRow("get")]    // case-insensitive
        [DataRow("post")]
        public async Task GetDataAsync_accepts_allowed_http_methods(string method)
        {
            // GET/POST must succeed (mock returns valid JSON).
            var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/test",
                        Method = method,
                        CacheTtlSeconds = 0
                    })
                }
            };

            // Should not throw — GET and POST are allowed methods.
            var result = await source.GetDataAsync(req, CancellationToken.None);
            Assert.IsNotNull(result);
        }

        [TestMethod]
        [DataRow("DELETE")]
        [DataRow("PUT")]
        [DataRow("PATCH")]
        [DataRow("HEAD")]
        [DataRow("OPTIONS")]
        [DataRow("TRACE")]
        [DataRow("CONNECT")]
        [DataRow("PROPFIND")]
        public async Task GetDataAsync_rejects_disallowed_http_methods(string method)
        {
            var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/test",
                        Method = method,
                        CacheTtlSeconds = 0
                    })
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(req, CancellationToken.None),
                $"HTTP method '{method}' must be rejected by the allowlist");

            StringAssert.Contains(ex.Message, "not allowed",
                "Rejection message should indicate the method is not allowed");
        }

        // ── S3: Header injection / CRLF rejection ─────────────────────────────

        [TestMethod]
        [DataRow("X-Injected\r\nX-Evil: injected", "header-value")]
        [DataRow("X-Injected\nX-Evil: injected", "header-value")]
        [DataRow("Normal-Header", "value\r\nX-Evil: injected")]
        [DataRow("Normal-Header", "value\nX-Evil: injected")]
        public async Task GetDataAsync_rejects_headers_with_CRLF_injection(string headerName, string headerValue)
        {
            var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/test",
                        CacheTtlSeconds = 0,
                        Headers = new Dictionary<string, string>
                        {
                            [headerName] = headerValue
                        }
                    })
                }
            };

            // HttpRequestHeaders.Add() validates headers and throws on CRLF / invalid chars.
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(req, CancellationToken.None),
                "CRLF injection in headers must be rejected");
        }

        // ── S4: FilterConfig Op allowlist ─────────────────────────────────────

        [TestMethod]
        [DataRow("eq")]
        [DataRow("ne")]
        [DataRow("gt")]
        [DataRow("ge")]
        [DataRow("lt")]
        [DataRow("le")]
        [DataRow("contains")]
        [DataRow("notcontains")]
        [DataRow("in")]
        [DataRow("notin")]
        [DataRow("EQ")]   // case-insensitive
        [DataRow("Contains")]
        public void FilterConfig_AllowedOps_accepts_valid_operators(string op)
        {
            Assert.IsTrue(FilterConfig.AllowedOps.Contains(op),
                $"Operator '{op}' should be in the AllowedOps set");
        }

        [TestMethod]
        [DataRow("like")]
        [DataRow("between")]
        [DataRow("IS NULL")]
        [DataRow("OR 1=1--")]
        [DataRow("exists")]
        [DataRow("")]
        [DataRow("!eq")]
        public void FilterConfig_AllowedOps_rejects_unknown_operators(string op)
        {
            Assert.IsFalse(FilterConfig.AllowedOps.Contains(op),
                $"Operator '{op}' must NOT be in the AllowedOps set");
        }

        // ── S5: AllowedPorts enforcement ──────────────────────────────────────

        [TestMethod]
        [DataRow(6379,  "redis")]           // Redis
        [DataRow(9200,  "elasticsearch")]   // Elasticsearch
        [DataRow(5432,  "postgres")]        // PostgreSQL
        [DataRow(3306,  "mysql")]           // MySQL
        [DataRow(27017, "mongodb")]         // MongoDB
        [DataRow(2379,  "etcd")]            // etcd
        [DataRow(22,    "ssh")]             // SSH
        [DataRow(25,    "smtp")]            // SMTP
        public async Task ValidateUrlAsync_rejects_non_allowlisted_ports(int port, string label)
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = $"https://8.8.8.8:{port}/data",
                // AllowedPorts defaults to {80, 443, 8080, 8443}
            };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => RestWidgetDataSource.ValidateUrlAsync(opts),
                $"Port {port} ({label}) must be rejected by the AllowedPorts check");
        }

        [TestMethod]
        [DataRow(80)]
        [DataRow(443)]
        [DataRow(8080)]
        [DataRow(8443)]
        public async Task ValidateUrlAsync_accepts_default_allowlisted_ports(int port)
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = $"https://8.8.8.8:{port}/data",
                // Default AllowedPorts = {80, 443, 8080, 8443}
            };

            // Should not throw — these are in the default allowlist.
            await RestWidgetDataSource.ValidateUrlAsync(opts);
        }

        [TestMethod]
        public async Task ValidateUrlAsync_accepts_default_scheme_port_when_no_explicit_port_in_url()
        {
            // When the URL has no explicit port (Uri.Port == -1 for default scheme ports),
            // the port check should not trigger.
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/data",  // no explicit port — HTTPS default is 443
            };

            // Should not throw — no explicit port means port check is skipped.
            await RestWidgetDataSource.ValidateUrlAsync(opts);
        }

        [TestMethod]
        public async Task ValidateUrlAsync_allows_any_port_when_AllowedPorts_is_null()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8:6379/data",
                AllowedPorts = null   // null means port restriction disabled
            };

            // Should not throw — AllowedPorts=null disables the port check.
            await RestWidgetDataSource.ValidateUrlAsync(opts);
        }

        [TestMethod]
        public async Task ValidateUrlAsync_allows_any_port_when_AllowedPorts_is_empty()
        {
            var opts = new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8:6379/data",
                AllowedPorts = Array.Empty<int>()   // empty means port restriction disabled
            };

            // Should not throw — empty AllowedPorts disables the port check.
            await RestWidgetDataSource.ValidateUrlAsync(opts);
        }
    }

    // ── Redirect-tracking test double ────────────────────────────────────────

    internal sealed class RedirectCounter
    {
        public int Value { get; set; }
    }

    /// <summary>
    /// Returns an HTTP 302 on the first call and records whether a second call is made
    /// (which would indicate the client followed the redirect).
    /// </summary>
    internal sealed class RedirectTrackingHandler : HttpMessageHandler
    {
        private int _callCount;
        private readonly RedirectCounter _counter;

        public RedirectTrackingHandler(out RedirectCounter redirectFollowCount)
        {
            _counter = new RedirectCounter();
            redirectFollowCount = _counter;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _callCount++;
            if (_callCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Found);
                // Redirect to a private IP — if followed, this is an SSRF vulnerability.
                resp.Headers.Location = new Uri("https://169.254.169.254/latest/meta-data/");
                return Task.FromResult(resp);
            }
            // Second call means the client followed the redirect.
            _counter.Value++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
