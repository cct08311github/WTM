#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// Issue #956: the framework's own hard-rejected-header-name / count / total-size guard —
    /// <see cref="RestWidgetDataSource.ValidateHeaders"/> — plus its SEND-time wiring into
    /// <see cref="RestWidgetDataSource.FetchJsonAsync"/> (reached via <c>GetDataAsync</c>).
    /// The WRITE-time wiring (JsonFileDashboardService/EfCoreDashboardService.ValidateWidgetConfigs)
    /// is covered separately in DashboardReliabilityTests.cs /
    /// EfCoreDashboardServiceTests.cs — deliberately kept apart so neither guard's tests can
    /// hide the other's absence.
    /// </summary>
    [TestClass]
    public class RestWidgetHeaderHardeningTests
    {
        // ── ValidateHeaders — direct, exhaustive per-name/count/size coverage ──────────

        [TestMethod]
        public void ValidateHeaders_null_or_empty_is_accepted()
        {
            Assert.IsNull(RestWidgetDataSource.ValidateHeaders(null));
            Assert.IsNull(RestWidgetDataSource.ValidateHeaders(new Dictionary<string, string>()));
        }

        [TestMethod]
        [DataRow("Host")]
        [DataRow("host")]
        [DataRow("Transfer-Encoding")]
        [DataRow("Content-Length")]
        [DataRow("Connection")]
        [DataRow("Upgrade")]
        [DataRow("TE")]
        [DataRow("Trailer")]
        [DataRow("Expect")]
        public void ValidateHeaders_rejects_hard_rejected_name_case_insensitively(string headerName)
        {
            var error = RestWidgetDataSource.ValidateHeaders(new Dictionary<string, string> { [headerName] = "x" });
            error.Should().NotBeNull();
            error.Should().Contain(headerName);
        }

        [TestMethod]
        [DataRow("Proxy-Authorization")]
        [DataRow("Proxy-Connection")]
        [DataRow("proxy-anything")]
        public void ValidateHeaders_rejects_any_Proxy_prefixed_name(string headerName)
        {
            var error = RestWidgetDataSource.ValidateHeaders(new Dictionary<string, string> { [headerName] = "x" });
            error.Should().NotBeNull();
        }

        [TestMethod]
        public void ValidateHeaders_rejects_more_than_MaxHeaderCount_headers()
        {
            var headers = new Dictionary<string, string>();
            for (int i = 0; i < RestWidgetDataSource.MaxHeaderCount + 1; i++) { headers[$"X-{i}"] = "v"; }

            var error = RestWidgetDataSource.ValidateHeaders(headers);
            error.Should().NotBeNull();
            error.Should().Contain("header count");
        }

        [TestMethod]
        public void ValidateHeaders_accepts_exactly_MaxHeaderCount_headers()
        {
            var headers = new Dictionary<string, string>();
            for (int i = 0; i < RestWidgetDataSource.MaxHeaderCount; i++) { headers[$"X-{i}"] = "v"; }

            RestWidgetDataSource.ValidateHeaders(headers).Should().BeNull(
                "the count cap is a maximum, not an off-by-one exclusive bound");
        }

        [TestMethod]
        public void ValidateHeaders_rejects_total_name_plus_value_length_over_the_cap()
        {
            var headers = new Dictionary<string, string> { ["X-Big"] = new string('v', RestWidgetDataSource.MaxHeaderTotalBytes) };

            var error = RestWidgetDataSource.ValidateHeaders(headers);
            error.Should().NotBeNull();
            error.Should().Contain("total header");
        }

        [TestMethod]
        public void ValidateHeaders_accepts_total_length_at_the_cap()
        {
            // "X-A" (3 bytes) + value sized so name+value == exactly MaxHeaderTotalBytes.
            var headers = new Dictionary<string, string> { ["X-A"] = new string('v', RestWidgetDataSource.MaxHeaderTotalBytes - 3) };

            RestWidgetDataSource.ValidateHeaders(headers).Should().BeNull("the size cap is a maximum, not exclusive");
        }

        /// <summary>Positive control: Authorization and an arbitrary custom header are legal.</summary>
        [TestMethod]
        public void ValidateHeaders_accepts_Authorization_and_custom_header()
        {
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer real-token",
                ["X-Api-Key"] = "real-key"
            };
            RestWidgetDataSource.ValidateHeaders(headers).Should().BeNull();
        }

        // ── Send-time wiring (GetDataAsync → FetchJsonAsync → ValidateHeaders) ─────────
        // Deleting the "ValidateHeaders(options.Headers)" call (or the "throw" that follows a
        // non-null result) in RestWidgetDataSource.FetchJsonAsync turns every test in this
        // group red — see
        // test/mutants/entries/956-restwidget-header-send-time-guard-neutralize.json.
        // These tests assert the underlying HTTP handler is NEVER invoked — proving the
        // rejection happens before any bytes leave the process, not merely that some
        // exception eventually surfaces.

        // "Content-Length" is deliberately NOT in this DataRow set. Verified empirically while
        // RED-verifying this group (stashing this method's own guard): with ValidateHeaders'
        // send-time call neutralized, a "Content-Length" case still throws
        // InvalidOperationException and callCount stays 0 — but for a DIFFERENT reason, unrelated
        // to this guard. .NET's own HttpRequestHeaders.Add rejects "Content-Length" as a
        // content-only header (it belongs on HttpContent.Headers, never HttpRequestMessage
        // .Headers), and the pre-existing S3 CRLF-injection try/catch a few lines below
        // (catch (Exception ex) when (ex is FormatException || ex is InvalidOperationException))
        // wraps that BCL rejection in the same InvalidOperationException type this guard throws.
        // A "Content-Length" test case here would therefore stay green even if this guard's own
        // "if (headerValidationError != null) throw" were deleted — decoration, not coverage,
        // for THIS guard specifically. "Content-Length" write-time rejection is still covered
        // (and RED-verified) by DashboardWidgetConfigValidationTests/EfCoreDashboardServiceTests
        // in DashboardReliabilityTests.cs / EfCoreDashboardServiceTests.cs, where no such
        // confound exists (ValidateWidgetConfigs never touches HttpRequestHeaders).
        [TestMethod]
        [DataRow("Host")]
        [DataRow("Transfer-Encoding")]
        [DataRow("Connection")]
        [DataRow("Upgrade")]
        [DataRow("TE")]
        [DataRow("Trailer")]
        [DataRow("Expect")]
        [DataRow("Proxy-Authorization")]
        public async Task GetDataAsync_rejects_hard_rejected_header_at_send_time_without_invoking_handler(string headerName)
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
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/data",
                        Headers = new Dictionary<string, string> { [headerName] = "x" },
                        CacheTtlSeconds = 0
                    })
                }
            };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => source.GetDataAsync(req, CancellationToken.None));
            Assert.AreEqual(0, callCount,
                $"the HTTP handler must never be invoked when '{headerName}' is configured — rejection happens before any request leaves the process");
        }

        [TestMethod]
        public async Task GetDataAsync_rejects_too_many_headers_at_send_time_without_invoking_handler()
        {
            int callCount = 0;
            var handler = new MockHttpHandler(_ => { callCount++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }; });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var headers = new Dictionary<string, string>();
            for (int i = 0; i < 21; i++) { headers[$"X-Custom-{i}"] = "v"; }

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/data",
                        Headers = headers,
                        CacheTtlSeconds = 0
                    })
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => source.GetDataAsync(req, CancellationToken.None));
            StringAssert.Contains(ex.Message, "header count");
            Assert.AreEqual(0, callCount);
        }

        [TestMethod]
        public async Task GetDataAsync_rejects_headers_exceeding_total_size_at_send_time_without_invoking_handler()
        {
            int callCount = 0;
            var handler = new MockHttpHandler(_ => { callCount++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }; });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/data",
                        Headers = new Dictionary<string, string> { ["X-Big"] = new string('v', 9 * 1024) },
                        CacheTtlSeconds = 0
                    })
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => source.GetDataAsync(req, CancellationToken.None));
            StringAssert.Contains(ex.Message, "total header");
            Assert.AreEqual(0, callCount);
        }

        /// <summary>
        /// Positive control (issue #956), send-time: Authorization and a custom header are
        /// not merely "not rejected" — they must actually reach the outgoing HTTP request.
        /// </summary>
        [TestMethod]
        public async Task GetDataAsync_forwards_Authorization_and_custom_header_to_the_outgoing_request()
        {
            HttpRequestMessage? captured = null;
            var handler = new MockHttpHandler(req =>
            {
                captured = req;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
                };
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
                        Url = "https://8.8.8.8/data",
                        Headers = new Dictionary<string, string>
                        {
                            ["Authorization"] = "Bearer real-token",
                            ["X-Api-Key"] = "real-key"
                        },
                        CacheTtlSeconds = 0
                    })
                }
            };

            await source.GetDataAsync(req, CancellationToken.None);

            Assert.IsNotNull(captured);
            captured!.Headers.TryGetValues("Authorization", out var authVals).Should().BeTrue();
            authVals!.Should().Contain("Bearer real-token");
            captured.Headers.TryGetValues("X-Api-Key", out var apiKeyVals).Should().BeTrue();
            apiKeyVals!.Should().Contain("real-key");
        }
    }
}
