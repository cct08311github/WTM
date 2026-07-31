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
    /// Issue #948-F8: <see cref="DashboardEgressDestination"/>'s new
    /// TenantId/DashboardId/WidgetId/Method/HeaderNames/HasBody fields, and the guarantee that
    /// the pre-check (<see cref="RestWidgetDataSource.ValidateUrlAsync"/>) and the connect-time
    /// check (<see cref="RestWidgetDataSource.PinnedConnectAsync"/>, exercised here via the same
    /// <see cref="RestWidgetDataSource.SelectConnectableIpAsync"/>/<see cref="RestWidgetDataSource.BuildDestination"/>
    /// both funnel through) see a field-identical destination for the same fetch.
    /// </summary>
    /// <remarks>
    /// <b>Coverage boundary, stated precisely (heterogeneous review, 2026-07-31):</b>
    /// <see cref="RestWidgetDataSource.PinnedConnectAsync"/> itself cannot be driven from a unit
    /// test — its <c>SocketsHttpConnectionContext</c> parameter has no public constructor
    /// (confirmed with a throwaway probe program, not assumed). What CAN and IS pinned here: (1)
    /// <see cref="RestWidgetDataSource.FetchJsonAsync"/>'s <c>req.Options.Set(...)</c> — exercised
    /// for real via <see cref="FetchJsonAsync_sets_the_request_context_option_matching_BuildRequestContext"/>,
    /// which drives a real <c>GetDataAsync</c> call and inspects the actual
    /// <see cref="HttpRequestMessage"/> the mock handler receives; (2) the readback logic
    /// <see cref="RestWidgetDataSource.PinnedConnectAsync"/> depends on, extracted into
    /// <see cref="RestWidgetDataSource.ReadRequestContext"/> specifically so the same test can call
    /// it directly rather than duplicating its <c>TryGetValue</c> call by hand. The one line that
    /// remains genuinely unpinned is the single delegating call inside
    /// <see cref="RestWidgetDataSource.PinnedConnectAsync"/> itself
    /// (<c>var requestContext = ReadRequestContext(context.InitialRequestMessage);</c>) — see
    /// <c>docs/production-readiness.md</c>'s #948-F8 entry for why that residual is accepted.
    /// </remarks>
    [TestClass]
    public class RestWidgetRequestContextTests
    {
        /// <summary>
        /// The pre-check builds its context via <c>RestWidgetDataSource.ValidateUrlAsync</c>
        /// (production code, exercised for real). The "connect-time" side is NOT
        /// <c>PinnedConnectAsync</c> itself — it is a direct call to
        /// <c>SelectConnectableIpAsync</c> (the shared function <c>PinnedConnectAsync</c> calls),
        /// the same technique this repo's own #955-F3 test group already uses, because
        /// <c>SocketsHttpConnectionContext</c> has no public constructor a test can use to drive
        /// <c>PinnedConnectAsync</c> itself. The context fed to this call is built via
        /// <c>BuildRequestContext</c> with the exact same inputs <c>FetchJsonAsync</c> would use —
        /// the same function, the same arguments — which is the actual guarantee issue #948-F8
        /// asks for. (Renamed 2026-07-31, heterogeneous review: the previous name,
        /// "..._simulated_ConnectTime_...", read as if <c>PinnedConnectAsync</c> itself had been
        /// exercised. See <see cref="FetchJsonAsync_sets_the_request_context_option_matching_BuildRequestContext"/>
        /// for what closes part of that remaining gap, and this class's own remarks for the exact
        /// boundary of what is and is not pinned.)
        /// </summary>
        [TestMethod]
        public async Task PreCheck_and_a_direct_SelectConnectableIpAsync_call_produce_field_equal_destinations_for_the_same_fetch()
        {
            var options = new RestWidgetDataSourceOptions
            {
                Url = "https://10.0.0.1/internal-api",
                Method = "POST",
                Body = "{\"x\":1}",
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret" }
            };
            const string tenantId = "tenant-xyz";
            const string dashboardId = "dash-1";
            const string widgetId = "widget-1";

            var preCheckPolicy = new CapturingDenyingEgressPolicy();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => RestWidgetDataSource.ValidateUrlAsync(
                    options, CancellationToken.None, preCheckPolicy, tenantId, dashboardId, widgetId));

            var connectTimeContext = RestWidgetDataSource.BuildRequestContext(options, tenantId, dashboardId, widgetId);
            var connectTimePolicy = new CapturingDenyingEgressPolicy();
            var candidates = new[] { IPAddress.Parse("10.0.0.1") };
            var uri = new Uri(options.Url);
            await RestWidgetDataSource.SelectConnectableIpAsync(
                candidates, uri, port: 443, isPlainHttp: false, connectTimePolicy, CancellationToken.None, connectTimeContext);

            preCheckPolicy.LastDestination.Should().NotBeNull("the pre-check must have consulted the policy for the blocked private IP");
            connectTimePolicy.LastDestination.Should().NotBeNull();
            var preCheck = preCheckPolicy.LastDestination!;
            var connectTime = connectTimePolicy.LastDestination!;

            preCheck.RequestUri.Should().Be(connectTime.RequestUri);
            preCheck.ResolvedAddress.Should().Be(connectTime.ResolvedAddress);
            preCheck.Port.Should().Be(connectTime.Port);
            preCheck.IsPrivateNetwork.Should().Be(connectTime.IsPrivateNetwork);
            preCheck.IsPlainHttp.Should().Be(connectTime.IsPlainHttp);

            preCheck.TenantId.Should().Be(connectTime.TenantId);
            preCheck.TenantId.Should().Be(tenantId);
            preCheck.DashboardId.Should().Be(connectTime.DashboardId);
            preCheck.DashboardId.Should().Be(dashboardId);
            preCheck.WidgetId.Should().Be(connectTime.WidgetId);
            preCheck.WidgetId.Should().Be(widgetId);
            preCheck.Method.Should().Be(connectTime.Method);
            preCheck.Method.Should().Be("POST");
            preCheck.HasBody.Should().Be(connectTime.HasBody);
            preCheck.HasBody.Should().BeTrue();
            preCheck.HeaderNames.Should().BeEquivalentTo(connectTime.HeaderNames);
            preCheck.HeaderNames.Should().BeEquivalentTo(new[] { "Authorization" });
        }

        /// <summary>
        /// Issue #948-F8 (heterogeneous review follow-up, 2026-07-31): pins the request-context
        /// Set/Get round trip for real, without a socket. Drives a genuine <c>GetDataAsync</c>
        /// call (so <c>FetchJsonAsync</c>'s <c>req.Options.Set(...)</c> — the line at issue —
        /// executes for real, not simulated) through a mock handler that captures the actual
        /// <see cref="HttpRequestMessage"/> sent to <c>client.SendAsync</c>, then reads it back
        /// via <c>RestWidgetDataSource.ReadRequestContext</c> — the exact same method
        /// <c>PinnedConnectAsync</c> itself calls, not a hand-duplicated <c>TryGetValue</c>.
        /// Deleting the <c>req.Options.Set(...)</c> call in <c>FetchJsonAsync</c>, or deleting
        /// <c>ReadRequestContext</c>'s body, both turn this red (<c>readBack</c> becomes
        /// <c>null</c> or loses fields). What this does NOT reach: the one-line delegation inside
        /// <c>PinnedConnectAsync</c> that calls <c>ReadRequestContext</c> — see this class's own
        /// remarks for why that specific line cannot be driven from a unit test.
        /// </summary>
        [TestMethod]
        public async Task FetchJsonAsync_sets_the_request_context_option_matching_BuildRequestContext()
        {
            HttpRequestMessage? captured = null;
            var handler = new MockHttpHandler(req =>
            {
                captured = req;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
                };
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var options = new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/data",
                Method = "POST",
                Body = "{\"x\":1}",
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret" },
                CacheTtlSeconds = 0
            };
            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string> { ["options"] = JsonSerializer.Serialize(options) },
                TenantId = "tenant-x",
                DashboardId = "dash-1",
                WidgetId = "widget-1"
            };

            await source.GetDataAsync(req, CancellationToken.None);

            captured.Should().NotBeNull("the request must have reached the HTTP handler");
            var readBack = RestWidgetDataSource.ReadRequestContext(captured!);

            readBack.Should().NotBeNull("FetchJsonAsync must set the request-context Option before sending");
            readBack!.TenantId.Should().Be("tenant-x");
            readBack.DashboardId.Should().Be("dash-1");
            readBack.WidgetId.Should().Be("widget-1");
            readBack.Method.Should().Be("POST");
            readBack.HasBody.Should().BeTrue();
            readBack.HeaderNames.Should().BeEquivalentTo(new[] { "Authorization" });
        }

        /// <summary>
        /// HeaderNames carries names only — never values. A policy is host code and may log
        /// what it receives; if a header value ever leaked into this collection, logging
        /// HeaderNames anywhere would leak the credential.
        /// </summary>
        [TestMethod]
        public void BuildRequestContext_HeaderNames_contains_names_and_no_values_for_a_secret_bearing_header()
        {
            var options = new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/data",
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer super-secret-value-must-not-appear-anywhere",
                    ["X-Api-Key"] = "another-secret-abc123"
                }
            };

            var context = RestWidgetDataSource.BuildRequestContext(options, "t1", "d1", "w1");

            context.HeaderNames.Should().BeEquivalentTo(new[] { "Authorization", "X-Api-Key" });
            context.HeaderNames.Should().NotContain(n => n.Contains("secret", StringComparison.OrdinalIgnoreCase),
                "HeaderNames must carry header NAMES only — never the header VALUES");
        }

        [TestMethod]
        public void BuildRequestContext_no_headers_yields_null_HeaderNames()
        {
            var options = new RestWidgetDataSourceOptions { Url = "https://8.8.8.8/data" };
            var context = RestWidgetDataSource.BuildRequestContext(options, null, null, null);
            context.HeaderNames.Should().BeNull();
            context.HasBody.Should().BeFalse();
            context.Method.Should().Be("GET");
        }

        [TestMethod]
        public void BuildDestination_with_null_context_defaults_to_no_identity_and_no_body()
        {
            var uri = new Uri("https://10.0.0.1/data");
            var destination = RestWidgetDataSource.BuildDestination(uri, IPAddress.Parse("10.0.0.1"), 443, isPlainHttp: false, context: null);

            destination.TenantId.Should().BeNull();
            destination.DashboardId.Should().BeNull();
            destination.WidgetId.Should().BeNull();
            destination.Method.Should().BeNull();
            destination.HeaderNames.Should().BeNull();
            destination.HasBody.Should().BeFalse();
            destination.IsPrivateNetwork.Should().BeTrue("10.0.0.1 is an RFC 1918 address");
        }

        private sealed class CapturingDenyingEgressPolicy : IDashboardEgressPolicy
        {
            public DashboardEgressDestination? LastDestination { get; private set; }

            public Task<bool> IsAllowedAsync(DashboardEgressDestination destination, CancellationToken ct = default)
            {
                LastDestination = destination;
                return Task.FromResult(false);
            }
        }
    }
}
