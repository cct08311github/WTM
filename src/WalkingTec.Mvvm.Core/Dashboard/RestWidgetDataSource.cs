#nullable enable
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace WalkingTec.Mvvm.Core.Dashboard;

/// <summary>
/// Fetches JSON from an external HTTP endpoint and maps the result into
/// <see cref="WidgetDataResult"/> shape. Introduced by issue #824 to let
/// operators wire up REST-backed widgets via Dashboard config JSON
/// without writing C#.
/// </summary>
/// <remarks>
/// Security model:
/// <list type="bullet">
///   <item>HTTPS and public (non-private-range) IPs by default. Reaching a plain
///         <c>http://</c> URL, or an IP in a private/loopback/link-local/CGNAT/multicast
///         range, now ALWAYS requires a registered <see cref="IDashboardEgressPolicy"/> to
///         approve that specific resolved destination (issue #948) —
///         <see cref="RestWidgetDataSourceOptions.AllowHttp"/> and
///         <see cref="RestWidgetDataSourceOptions.AllowPrivateNetwork"/> are no longer
///         sufficient on their own, because both live inside the widget definition that
///         <c>_DashboardController.Create</c>/<c>Update</c> and
///         <c>_DashboardDesignerController.Preview</c> accept directly from the caller —
///         see <see cref="IDashboardEgressPolicy"/>'s own XML doc for the full rationale.</item>
///   <item>SSRF guard: resolved URL host must not map to private, loopback,
///         link-local (incl. cloud IMDS), CGNAT (100.64/10), or multicast IPs unless
///         <see cref="IDashboardEgressPolicy.IsAllowedAsync"/> approves the specific
///         resolved IP.</item>
///   <item>DNS pinning via <c>SocketsHttpHandler.ConnectCallback</c>: the IP validated at
///         connect time by <see cref="PinnedConnectAsync"/> is the IP that the socket actually
///         connects to, eliminating DNS rebinding / TOCTOU windows. TLS SNI and server-certificate
///         validation use the original hostname URI (not an IP rewrite), so HTTPS works correctly.
///         The egress policy (when registered) is consulted again at this authoritative,
///         connect-time check — not just at the earlier fast pre-check — using the SAME
///         freshly-resolved IP the socket is about to connect to.</item>
///   <item>Redirects disabled: the named HttpClient <see cref="HttpClientName"/> is registered
///         with <c>AllowAutoRedirect=false</c> — 302 redirects cannot bypass the SSRF guard.</item>
///   <item>Response body capped at <see cref="RestWidgetDataSourceOptions.MaxResponseBytes"/>
///         (default 1 MiB, hard limit <see cref="MaxResponseBytesHardLimit"/> 10 MiB)
///         to prevent memory exhaustion.</item>
///   <item>Request timeout bounded by <see cref="RestWidgetDataSourceOptions.TimeoutSeconds"/>
///         (clamped to [1, 60]).</item>
///   <item>Cache key (issue #952): <c>IMemoryCache</c> key is <c>SHA256(tenant + canonical
///         JSON of the ENTIRE options object)</c> — see <see cref="BuildCacheKey"/> for why
///         this is a hash of the whole object rather than a hand-picked field list.</item>
///   <item>Header hardening (issue #956): a fixed set of hop-by-hop/framing header names
///         (<c>Host</c>, <c>Transfer-Encoding</c>, <c>Content-Length</c>, <c>Connection</c>,
///         <c>Upgrade</c>, <c>TE</c>, <c>Trailer</c>, <c>Expect</c>, anything starting with
///         <c>Proxy-</c>) is rejected outright, plus a header-count and total-size cap — see
///         <see cref="ValidateHeaders"/>, enforced at both write time
///         (<c>JsonFileDashboardService</c>/<c>EfCoreDashboardService.ValidateWidgetConfigs</c>)
///         and send time (<see cref="FetchJsonAsync"/>).</item>
/// </list>
/// </remarks>
public class RestWidgetDataSource : IWidgetDataSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IDashboardEgressPolicy? _egressPolicy;

    // Shared options for case-insensitive JSON deserialization (avoids per-call allocation).
    private static readonly JsonSerializerOptions _caseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>Named HttpClient key — registered with <c>AllowAutoRedirect=false</c> and
    /// <c>ConnectCallback = <see cref="PinnedConnectAsync"/></c>.</summary>
    public const string HttpClientName = "WtmRestWidget";

    /// <summary>Hard upper limit on <see cref="RestWidgetDataSourceOptions.MaxResponseBytes"/> (10 MiB).</summary>
    public const long MaxResponseBytesHardLimit = 10 * 1024 * 1024;

    /// <summary>
    /// <see cref="HttpRequestOptions"/> key used to pass the resolved
    /// <see cref="IDashboardEgressPolicy"/> instance (may be <c>null</c>) to
    /// <see cref="PinnedConnectAsync"/>, so the authoritative connect-time check can
    /// consult the same policy the fast pre-check used (issue #948). This carries the
    /// policy reference itself, not a caller-controlled boolean — see that interface's
    /// XML doc for why a boolean was insufficient.
    /// </summary>
    internal const string EgressPolicyOptionKey = "WtmRestWidget.EgressPolicy";

    /// <summary>
    /// <see cref="HttpRequestOptions"/> key used to pass the <see cref="RestWidgetRequestContext"/>
    /// built by <see cref="BuildRequestContext"/> from <see cref="FetchJsonAsync"/> to
    /// <see cref="PinnedConnectAsync"/> (issue #948-F8). The pre-check
    /// (<see cref="ValidateUrlAsync"/>) builds its own context from the same inputs
    /// (<c>options</c>/tenantId/dashboardId/widgetId) via the same function — this key only
    /// exists to carry that value across the async boundary into the connect-time callback,
    /// which has no other way to see it.
    /// </summary>
    internal const string RequestContextOptionKey = "WtmRestWidget.RequestContext";

    private const int TimeoutSecondsMin = 1;
    private const int TimeoutSecondsMax = 60;
    private const int MaxResponseBytesMin = 1024;           // 1 KB
    private const int MaxResponseBytesDefault = 1024 * 1024; // 1 MiB

    // ── Header hardening (issue #956) ───────────────────────────────────

    /// <summary>
    /// Header names <see cref="RestWidgetDataSource"/> rejects outright — never configurable,
    /// not subject to any <see cref="IDashboardEgressPolicy"/> override. See
    /// <see cref="ValidateHeaders"/> for the full rationale.
    /// </summary>
    internal static readonly FrozenSet<string> HardRejectedHeaderNames =
        new[] { "Host", "Transfer-Encoding", "Content-Length", "Connection", "Upgrade", "TE", "Trailer", "Expect" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum number of custom headers a REST widget may configure (issue #956).</summary>
    internal const int MaxHeaderCount = 20;

    /// <summary>
    /// Maximum total UTF-8 byte length of all header names plus values combined
    /// (issue #956).
    /// </summary>
    internal const int MaxHeaderTotalBytes = 8 * 1024; // 8 KB

    public string Name => "rest";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Rest;

    /// <param name="httpClientFactory">Used to create the named <see cref="HttpClientName"/> client.</param>
    /// <param name="cache">Backs the per-widget response cache (see <see cref="RestWidgetDataSourceOptions.CacheTtlSeconds"/>).</param>
    /// <param name="egressPolicy">
    /// Optional host-owned egress policy (issue #948). Resolved via DI as an ordinary
    /// nullable constructor parameter — <c>null</c> when the host has not registered
    /// one (<c>services.AddWtmDashboardEgressPolicy&lt;T&gt;()</c>), which is the default
    /// and means every private-network/plain-HTTP destination is rejected. See
    /// <see cref="IDashboardEgressPolicy"/>.
    /// </param>
    public RestWidgetDataSource(IHttpClientFactory httpClientFactory, IMemoryCache cache, IDashboardEgressPolicy? egressPolicy = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _egressPolicy = egressPolicy;
    }

    public async Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, CancellationToken ct = default)
    {
        var options = ParseOptions(request.Parameters);

        // Fast-fail pre-check: validate URL scheme and, for public-only mode, verify
        // that the host resolves to a non-blocked IP. This surfaces friendly error
        // messages before we even attempt the TCP connection.
        // The authoritative TOCTOU-safe check happens again at actual connect time
        // inside PinnedConnectAsync via SocketsHttpHandler.ConnectCallback.
        // #948-F8: tenantId/dashboardId/widgetId are threaded through so the destination a
        // policy sees carries this fetch's identity (see BuildRequestContext/BuildDestination).
        await ValidateUrlAsync(options, ct, _egressPolicy, request.TenantId, request.DashboardId, request.WidgetId)
            .ConfigureAwait(false);

        // #952: cache key is a hash of tenant + the ENTIRE options object — see BuildCacheKey.
        var cacheKey = BuildCacheKey(options, request.TenantId);
        if (options.CacheTtlSeconds > 0 && _cache.TryGetValue(cacheKey, out WidgetDataResult? cached) && cached != null)
        {
            return cached;
        }

        var json = await FetchJsonAsync(options, ct, request.TenantId, request.DashboardId, request.WidgetId)
            .ConfigureAwait(false);
        var node = ExtractJsonPath(json, options.JsonPath);
        var result = MapToWidgetResult(node);

        if (options.CacheTtlSeconds > 0)
        {
            _cache.Set(cacheKey, result, TimeSpan.FromSeconds(options.CacheTtlSeconds));
        }
        return result;
    }

    // ── Parsing ──────────────────────────────────────────────────────────

    internal static RestWidgetDataSourceOptions ParseOptions(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("options", out var json) || string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                "REST widget request missing required parameter 'options' (JSON-serialized RestWidgetDataSourceOptions).");
        }
        RestWidgetDataSourceOptions opts;
        try
        {
            opts = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(
                json,
                _caseInsensitiveOptions)
                ?? throw new InvalidOperationException("REST widget options JSON deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("REST widget options JSON is malformed: " + ex.Message, ex);
        }

        // Clamp security-bounded values to prevent DoS from extreme inputs.
        // Negative or zero TimeoutSeconds → floor to minimum; above max → cap.
        opts.TimeoutSeconds = Math.Clamp(opts.TimeoutSeconds, TimeoutSecondsMin, TimeoutSecondsMax);

        // MaxResponseBytes: clamp to [1 KB, 10 MiB]. Zero or negative → use default 1 MiB.
        if (opts.MaxResponseBytes <= 0)
        {
            opts.MaxResponseBytes = MaxResponseBytesDefault;
        }
        else
        {
            opts.MaxResponseBytes = (int)Math.Clamp((long)opts.MaxResponseBytes, MaxResponseBytesMin, MaxResponseBytesHardLimit);
        }

        return opts;
    }

    /// <summary>
    /// The set of HTTP methods permitted for widget data sources.
    /// Widget fetches must be idempotent reads; only GET and POST are accepted.
    /// </summary>
    // Perf(#663): private and read-only after initialization (only .Contains is ever
    // called) - FrozenSet<T> is a drop-in, faster read path. OrdinalIgnoreCase must be
    // passed explicitly to ToFrozenSet (it does not inherit the source comparer).
    private static readonly FrozenSet<string> _allowedMethods =
        new[] { "GET", "POST" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // ── URL validation + SSRF guard (fast-fail pre-check) ───────────────

    /// <summary>
    /// Fast-fail pre-check: validates URL scheme and resolves the host to verify at
    /// least one resolved IP is either public (over HTTPS) or explicitly approved by
    /// <paramref name="egressPolicy"/> for that specific resolved IP (issue #948).
    /// Throws <see cref="InvalidOperationException"/> with a descriptive message on
    /// failure.
    /// </summary>
    /// <remarks>
    /// This is a best-effort early check. The authoritative TOCTOU-safe enforcement
    /// happens at actual connect time in <see cref="PinnedConnectAsync"/>, which consults
    /// the SAME <paramref name="egressPolicy"/> (threaded through via
    /// <see cref="EgressPolicyOptionKey"/>) against a freshly-resolved IP, not whatever
    /// this pre-check happened to see. Both layers now share exactly the same
    /// "is any candidate connectable" decision — <see cref="SelectConnectableIpAsync"/> —
    /// so there is no separate ALL-must-pass-vs-ANY-must-pass semantic to keep in sync by
    /// hand (issue #955 review finding F7: an earlier version of this method rejected the
    /// whole DNS answer set if ANY resolved IP was unapproved, while the connect-time
    /// layer only ever needed ONE approved candidate — a legitimate multi-A-record host
    /// where the policy approves only one specific IP would fail here but would have
    /// connected fine downstream).
    /// </remarks>
    internal static async Task ValidateUrlAsync(
        RestWidgetDataSourceOptions options, CancellationToken ct = default, IDashboardEgressPolicy? egressPolicy = null,
        string? tenantId = null, string? dashboardId = null, string? widgetId = null)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            throw new InvalidOperationException("REST widget: Url is required.");
        }
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("REST widget: Url is not a well-formed absolute URI: " + options.Url);
        }

        bool isPlainHttp;
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            isPlainHttp = false;
        }
        else if (uri.Scheme == Uri.UriSchemeHttp)
        {
            isPlainHttp = true;
        }
        else
        {
            throw new InvalidOperationException(
                "REST widget: only http / https URL schemes are supported. Got: " + uri.Scheme);
        }

        // S5: Port allowlist — blocks probing of Redis/ES/DB ports for any caller that
        // leaves AllowedPorts at its default or narrows it. Unlike AllowPrivateNetwork/
        // AllowHttp, this is NOT gated behind the egress policy below — it is a hard
        // block regardless of what any policy would approve. That is only meaningful
        // because AllowedPorts, like AllowPrivateNetwork/AllowHttp, lives on the same
        // caller-controlled RestOptions object: a caller who could simply send
        // "allowedPorts": null would turn this block off entirely, which is why
        // ValidateWidgetConfigs (JsonFileDashboardService/EfCoreDashboardService) now
        // rejects a null/empty AllowedPorts from the caller at write time (issue #955
        // review finding F5) — this check only holds because that one does. -1 means the
        // URI uses the default port for its scheme (443 for https, 80 for http), which is
        // always permitted.
        if (options.AllowedPorts is { Length: > 0 } allowedPorts && uri.Port != -1)
        {
            if (!Array.Exists(allowedPorts, p => p == uri.Port))
            {
                throw new InvalidOperationException(
                    $"REST widget: port {uri.Port} is not in the AllowedPorts list. " +
                    "Configure AllowedPorts on the widget's RestOptions to permit additional ports.");
            }
        }
        var resolvedPort = uri.Port != -1 ? uri.Port : (isPlainHttp ? 80 : 443);

        // #948: no fast path to skip DNS resolution anymore — a caller-supplied
        // AllowPrivateNetwork=true (or AllowHttp=true) no longer bypasses the check by
        // itself; every resolved IP must be either public-over-HTTPS by default, or
        // individually approved by egressPolicy below.
        IPAddress[] ips;
        if (IPAddress.TryParse(uri.Host, out var literalIp))
        {
            ips = new[] { literalIp };
        }
        else if (isPlainHttp && egressPolicy == null)
        {
            // No policy registered at all: a plain-http:// hostname can never be approved
            // (no candidate IP would ever pass), so resolving DNS just to prove that would
            // be pure overhead (and would turn an otherwise network-independent rejection
            // into one that depends on DNS actually working). Reject immediately —
            // identical failure mode to the https/private-IP path below when egressPolicy
            // is null and every resolved IP is blocked.
            throw new InvalidOperationException(
                "REST widget: http:// URLs are rejected by default. Register an " +
                "IDashboardEgressPolicy (services.AddWtmDashboardEgressPolicy<T>()) that " +
                "approves this specific destination to permit plain HTTP.");
        }
        else
        {
            try
            {
                ips = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(
                    "REST widget: DNS lookup failed for host: " + uri.Host, ex);
            }
        }

        // Delegate the actual accept/reject decision to the SAME function PinnedConnectAsync
        // uses at connect time — see this method's own remarks for why that (not two
        // hand-synchronized implementations) is what makes the ANY-one-candidate-suffices
        // semantics actually match between the two layers.
        // #948-F8: build the request context (tenant/dashboard/widget identity + method/header
        // names/has-body) via the SAME function FetchJsonAsync uses, so the destination this
        // pre-check hands to egressPolicy is field-for-field identical to what the connect-time
        // check will hand it for the same fetch — see BuildRequestContext/BuildDestination.
        var requestContext = BuildRequestContext(options, tenantId, dashboardId, widgetId);
        var approvedIp = await SelectConnectableIpAsync(ips, uri, resolvedPort, isPlainHttp, egressPolicy, ct, requestContext)
            .ConfigureAwait(false);
        if (approvedIp == null)
        {
            var anyPrivate = Array.Exists(ips, IsBlockedIp);
            throw new InvalidOperationException(
                $"REST widget: URL host '{uri.Host}' resolves to {ips.Length} destination(s), " +
                "none of which are reachable — every candidate is blocked by the default-safe " +
                "egress policy" +
                (anyPrivate ? " (at least one resolves to a private/blocked IP range)" : "") +
                (isPlainHttp ? " (plain HTTP)" : "") +
                (egressPolicy == null
                    ? ". No IDashboardEgressPolicy is registered."
                    : ", and the registered IDashboardEgressPolicy did not approve any of them.") +
                " Register an IDashboardEgressPolicy (services.AddWtmDashboardEgressPolicy<T>()) " +
                "that approves the specific resolved destination to permit it — " +
                "AllowPrivateNetwork/AllowHttp on RestOptions no longer grant access by themselves.");
        }
    }

    internal static bool IsBlockedIp(IPAddress ip)
    {
        // Unwrap IPv4-mapped IPv6 (e.g. ::ffff:169.254.169.254) first so that the IPv4 rules
        // below apply correctly. Without this step, an attacker could bypass the check by
        // supplying the IPv4-mapped form.
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip)) { return true; }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 10.0.0.0/8 (RFC 1918 private)
            if (bytes[0] == 10) { return true; }
            // 172.16.0.0/12 (RFC 1918 private)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) { return true; }
            // 192.168.0.0/16 (RFC 1918 private)
            if (bytes[0] == 192 && bytes[1] == 168) { return true; }
            // 169.254.0.0/16 (link-local, includes AWS IMDS 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) { return true; }
            // 100.64.0.0/10 (CGNAT / shared address space, RFC 6598)
            if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) { return true; }
            // 127.0.0.0/8 (loopback — already caught by IsLoopback, but explicit for clarity)
            if (bytes[0] == 127) { return true; }
            // 224.0.0.0/4 (multicast)
            if (bytes[0] >= 224 && bytes[0] <= 239) { return true; }
            // 0.0.0.0/8 (invalid source)
            if (bytes[0] == 0) { return true; }
            // 240.0.0.0/4 (reserved)
            if (bytes[0] >= 240) { return true; }
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 is already caught by IPAddress.IsLoopback above.
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) { return true; }
            // ULA fc00::/7
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) { return true; }

            // Transitional IPv6 forms that embed an IPv4 address inside them.
            // Without decoding these, an attacker can bypass the SSRF guard by
            // resolving/supplying a host to one of these forms instead of the
            // raw (blocked) IPv4 address — e.g. 64:ff9b::169.254.169.254 (NAT64)
            // reaches the AWS/GCP IMDS endpoint even though 169.254.169.254 itself
            // is blocked. Extract the embedded IPv4 and re-apply the IPv4 rules.
            // (IPv4-mapped ::ffff:0:0/96 is already unwrapped by IsIPv4MappedToIPv6
            // at the top of this method, so it never reaches this branch.)
            byte[]? embeddedV4 = null;

            // NAT64 well-known prefix 64:ff9b::/96 (RFC 6052):
            // bytes[0..3] = 00 64 ff 9b, bytes[4..11] = 0, bytes[12..15] = IPv4.
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B
                && bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0
                && bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0)
            {
                embeddedV4 = new[] { bytes[12], bytes[13], bytes[14], bytes[15] };
            }
            // 6to4 2002::/16 (RFC 3056): bytes[0..1] = 20 02, bytes[2..5] = IPv4.
            else if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                embeddedV4 = new[] { bytes[2], bytes[3], bytes[4], bytes[5] };
            }
            // IPv4-compatible IPv6 ::/96 (deprecated, RFC 4291): high 96 bits zero,
            // low 32 bits = embedded IPv4 (e.g. ::10.0.0.1, ::169.254.169.254).
            else if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0
                && bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0
                && bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0)
            {
                embeddedV4 = new[] { bytes[12], bytes[13], bytes[14], bytes[15] };
            }

            if (embeddedV4 != null)
            {
                return IsBlockedIp(new IPAddress(embeddedV4));
            }
        }
        return false;
    }

    // ── DNS-pinning ConnectCallback ──────────────────────────────────────

    /// <summary>
    /// <c>SocketsHttpHandler.ConnectCallback</c> implementation that performs an
    /// authoritative, TOCTOU-safe SSRF check at the moment of actual TCP connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request URI is kept as the original hostname so that TLS SNI and
    /// server-certificate validation use the correct hostname — HTTPS works correctly.
    /// This callback resolves DNS, selects an allowed IP via
    /// <see cref="SelectConnectableIpAsync"/>, and opens the socket directly to that IP.
    /// </para>
    /// <para>
    /// The <see cref="IDashboardEgressPolicy"/> resolved for this request (may be
    /// <c>null</c>) is read from <see cref="HttpRequestMessage.Options"/> using
    /// <see cref="EgressPolicyOptionKey"/> — see that constant's XML doc for why the
    /// policy reference itself is threaded through rather than a boolean (#948).
    /// </para>
    /// </remarks>
    internal static async ValueTask<Stream> PinnedConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        // Read the per-request egress policy injected by FetchJsonAsync.
        context.InitialRequestMessage.Options.TryGetValue(
            new HttpRequestOptionsKey<IDashboardEgressPolicy?>(EgressPolicyOptionKey),
            out var egressPolicy);

        // #948-F8: read back the SAME context FetchJsonAsync built via BuildRequestContext —
        // guarantees this connect-time destination matches the pre-check's field-for-field.
        // Delegates to ReadRequestContext (rather than inlining the TryGetValue call here) so a
        // test can exercise the EXACT readback logic directly — SocketsHttpConnectionContext has
        // no public constructor (see ReadRequestContext's own doc comment), so nothing can drive
        // this method itself in a unit test; only this one-line delegation stays unpinned.
        var requestContext = ReadRequestContext(context.InitialRequestMessage);

        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var requestUri = context.InitialRequestMessage.RequestUri;
        var isPlainHttp = string.Equals(requestUri?.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        // Resolve the host to candidate IPs (literal IPs resolve instantly from OS).
        IPAddress[] candidates;
        if (IPAddress.TryParse(host, out var literalIp))
        {
            candidates = new[] { literalIp };
        }
        else
        {
            candidates = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }

        var chosen = await SelectConnectableIpAsync(
            candidates, requestUri ?? new Uri($"{(isPlainHttp ? "http" : "https")}://{host}:{port}/"),
            port, isPlainHttp, egressPolicy, ct, requestContext).ConfigureAwait(false);
        if (chosen == null)
        {
            // All resolved IPs are in blocked ranges (and none was approved by
            // egressPolicy, if one is registered). Throw a generic message so no
            // host/IP details leak through the 502 response.
            throw new InvalidOperationException(
                "REST widget: connection refused — target resolved to a blocked destination.");
        }

        var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(chosen, port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Selects the first candidate IP that is allowed given the
    /// <paramref name="allowPrivateNetwork"/> policy.
    /// </summary>
    /// <returns>
    /// The first connectable <see cref="IPAddress"/>, or <c>null</c> if all
    /// candidates are blocked.
    /// </returns>
    /// <remarks>
    /// #948: kept as a pure, synchronous, IP-only primitive — used internally by
    /// <see cref="SelectConnectableIpAsync"/> as the fast path for the common
    /// public-IP-over-HTTPS case (no policy round-trip needed). Production code no
    /// longer calls this directly with a caller-controlled <paramref name="allowPrivateNetwork"/>
    /// value; see <see cref="IDashboardEgressPolicy"/> for why a boolean is no longer
    /// sufficient for that decision.
    /// </remarks>
    internal static IPAddress? SelectConnectableIp(IPAddress[] candidates, bool allowPrivateNetwork)
    {
        foreach (var ip in candidates)
        {
            if (allowPrivateNetwork || !IsBlockedIp(ip))
            {
                return ip;
            }
        }
        return null;
    }

    /// <summary>
    /// #948: async, policy-aware IP selection used by <see cref="PinnedConnectAsync"/>.
    /// First tries the SSRF-safe fast path — the first candidate that is both a public IP
    /// and not plain-HTTP needs no policy call at all (delegates to
    /// <see cref="SelectConnectableIp"/> with <c>allowPrivateNetwork: false</c>). Only when
    /// that fast path finds nothing (every candidate is private and/or the scheme is plain
    /// HTTP) does it fall through to asking <paramref name="egressPolicy"/>, once per
    /// candidate IP, in order.
    /// </summary>
    /// <returns>The first connectable/approved <see cref="IPAddress"/>, or <c>null</c> if none is.</returns>
    internal static async ValueTask<IPAddress?> SelectConnectableIpAsync(
        IPAddress[] candidates, Uri requestUri, int port, bool isPlainHttp,
        IDashboardEgressPolicy? egressPolicy, CancellationToken ct, RestWidgetRequestContext? requestContext = null)
    {
        if (!isPlainHttp)
        {
            var fast = SelectConnectableIp(candidates, allowPrivateNetwork: false);
            if (fast != null)
            {
                return fast;
            }
        }

        if (egressPolicy == null)
        {
            return null;
        }

        foreach (var ip in candidates)
        {
            // #948-F8: the ONE place a DashboardEgressDestination is ever constructed — both
            // ValidateUrlAsync (pre-check) and PinnedConnectAsync (connect-time) reach this same
            // call via this same method, so the two never risk building it differently.
            var destination = BuildDestination(requestUri, ip, port, isPlainHttp, requestContext);
            if (await egressPolicy.IsAllowedAsync(destination, ct).ConfigureAwait(false))
            {
                return ip;
            }
        }
        return null;
    }

    /// <summary>
    /// Issue #948-F8: the single function that constructs a <see cref="DashboardEgressDestination"/>
    /// — called only from <see cref="SelectConnectableIpAsync"/>'s policy-consultation loop, which
    /// in turn is the only code <see cref="ValidateUrlAsync"/> (pre-check) and
    /// <see cref="PinnedConnectAsync"/> (connect-time) both go through. Do not construct
    /// <see cref="DashboardEgressDestination"/> anywhere else — a second construction site is
    /// exactly the "two hand-synchronized copies" shape this repo has already shipped bugs from
    /// (see <see cref="ValidateUrlAsync"/>'s own remarks on why it delegates to
    /// <see cref="SelectConnectableIpAsync"/> instead of re-implementing candidate selection).
    /// </summary>
    internal static DashboardEgressDestination BuildDestination(
        Uri requestUri, IPAddress resolvedIp, int port, bool isPlainHttp, RestWidgetRequestContext? context)
    {
        return new DashboardEgressDestination
        {
            RequestUri = requestUri,
            ResolvedAddress = resolvedIp,
            Port = port,
            IsPrivateNetwork = IsBlockedIp(resolvedIp),
            IsPlainHttp = isPlainHttp,
            TenantId = context?.TenantId,
            DashboardId = context?.DashboardId,
            WidgetId = context?.WidgetId,
            Method = context?.Method,
            HeaderNames = context?.HeaderNames,
            HasBody = context?.HasBody ?? false,
        };
    }

    /// <summary>
    /// Issue #948-F8: everything about a REST widget fetch that an
    /// <see cref="IDashboardEgressPolicy"/> might want to see about the CALLER — as opposed to
    /// the network destination itself, which <see cref="DashboardEgressDestination"/> already
    /// carried before this issue. Built by <see cref="BuildRequestContext"/>, called once by
    /// <see cref="ValidateUrlAsync"/> (pre-check) and once by <see cref="FetchJsonAsync"/> (which
    /// threads its result to <see cref="PinnedConnectAsync"/> via <see cref="RequestContextOptionKey"/>)
    /// — same function, same inputs, so both calls produce field-equal contexts for the same fetch.
    /// </summary>
    internal sealed record RestWidgetRequestContext(
        string? TenantId,
        string? DashboardId,
        string? WidgetId,
        string Method,
        IReadOnlyCollection<string>? HeaderNames,
        bool HasBody);

    /// <summary>
    /// Issue #948-F8: the single function that builds a <see cref="RestWidgetRequestContext"/>
    /// from a fetch's options and identity. Deterministic and side-effect-free — calling it
    /// twice with the same arguments (as <see cref="ValidateUrlAsync"/> and
    /// <see cref="FetchJsonAsync"/> each independently do) always produces field-equal results.
    /// <see cref="RestWidgetRequestContext.HeaderNames"/> carries <paramref name="options"/>'s
    /// header KEYS only — see that property's own doc comment on <see cref="DashboardEgressDestination.HeaderNames"/>
    /// for why values must never appear here (a policy is host code and may log what it receives).
    /// </summary>
    internal static RestWidgetRequestContext BuildRequestContext(
        RestWidgetDataSourceOptions options, string? tenantId, string? dashboardId, string? widgetId)
    {
        var method = string.IsNullOrWhiteSpace(options.Method) ? "GET" : options.Method.Trim().ToUpperInvariant();
        var headerNames = options.Headers is { Count: > 0 }
            ? (IReadOnlyCollection<string>)options.Headers.Keys.ToArray()
            : null;
        return new RestWidgetRequestContext(
            tenantId, dashboardId, widgetId, method, headerNames, !string.IsNullOrEmpty(options.Body));
    }

    /// <summary>
    /// Issue #948-F8: reads back the <see cref="RestWidgetRequestContext"/> <see cref="FetchJsonAsync"/>
    /// stored on <paramref name="message"/>'s <see cref="HttpRequestMessage.Options"/> via
    /// <see cref="RequestContextOptionKey"/>. <c>null</c> if none was set (e.g. a message built
    /// outside <see cref="FetchJsonAsync"/>).
    /// </summary>
    /// <remarks>
    /// Extracted specifically so a test can call it directly with a real
    /// <see cref="HttpRequestMessage"/> — <see cref="PinnedConnectAsync"/> calls this same method
    /// on <c>context.InitialRequestMessage</c>, but <see cref="PinnedConnectAsync"/> itself cannot
    /// be driven from a unit test: its <c>SocketsHttpConnectionContext</c> parameter has no public
    /// constructor (confirmed empirically, not assumed — see the #948-F8 test file). Extracting
    /// this lookup means a test exercises the EXACT readback logic <see cref="PinnedConnectAsync"/>
    /// depends on, not a hand-duplicated copy of it; only the one-line delegation inside
    /// <see cref="PinnedConnectAsync"/> that calls this method remains outside what a test can
    /// reach directly.
    /// </remarks>
    internal static RestWidgetRequestContext? ReadRequestContext(HttpRequestMessage message)
    {
        message.Options.TryGetValue(
            new HttpRequestOptionsKey<RestWidgetRequestContext?>(RequestContextOptionKey),
            out var requestContext);
        return requestContext;
    }

    // ── Header hardening (issue #956) ───────────────────────────────────

    /// <summary>
    /// Issue #956: rejects a REST widget's <see cref="RestWidgetDataSourceOptions.Headers"/>
    /// outright when it contains a hard-rejected name, too many headers, or too much total
    /// name+value length. Returns a descriptive error message on rejection, or <c>null</c>
    /// when <paramref name="headers"/> is acceptable (including <c>null</c>/empty).
    /// </summary>
    /// <remarks>
    /// <para><b>What is hard-rejected and why.</b> <c>Host</c>, <c>Transfer-Encoding</c>,
    /// <c>Content-Length</c>, <c>Connection</c>, <c>Upgrade</c>, <c>TE</c>, <c>Trailer</c>,
    /// <c>Expect</c>, and anything starting with <c>Proxy-</c> are never configurable — not
    /// even via a permissive <see cref="IDashboardEgressPolicy"/> — because this specific set
    /// breaks the invariants issue #948's own DNS-pinning/connect-time SSRF guard depends on.
    /// <c>Host</c> is the sharpest example: <see cref="PinnedConnectAsync"/> validates and
    /// connects to a specific resolved IP while deliberately keeping the outgoing request's URI
    /// (and therefore its default <c>Host</c> header) as the original, already-approved
    /// hostname — see that method's own remarks on why, and <see cref="RestWidgetDataSource"/>'s
    /// class-level remarks for why HTTPS correctness depends on it. A caller-supplied <c>Host</c>
    /// header override would let an operator-approved connection (this IP, this port) present as
    /// a DIFFERENT virtual host to the server actually answering the socket — the URL the policy
    /// checked never changes, but what the server treats the request as being FOR does. The other
    /// names in the set are the standard hop-by-hop/framing headers .NET's own
    /// <c>HttpRequestHeaders.Add</c> either special-cases or that would let a caller desynchronize
    /// request framing (<c>Transfer-Encoding</c>/<c>Content-Length</c> smuggling), pin a raw
    /// socket open (<c>Connection</c>/<c>Upgrade</c>/<c>TE</c>/<c>Trailer</c>), or manipulate
    /// proxy-only semantics (<c>Proxy-*</c>) this client was never meant to expose.</para>
    /// <para><b>What stays legal, deliberately.</b> <c>Authorization</c>, <c>X-Api-Key</c>, and
    /// any other custom header are NOT rejected by this method — "a REST widget calling an
    /// external service that needs an API key" is the explicit, supported use case this feature
    /// exists to serve (see <see cref="RestWidgetDataSourceOptions.Headers"/>'s own doc comment
    /// and issue #957's credential-masking feature, which exists because real credentials are
    /// expected to live here).</para>
    /// <para><b>Count and size caps.</b> At most <see cref="MaxHeaderCount"/> headers, and at
    /// most <see cref="MaxHeaderTotalBytes"/> combined UTF-8 bytes across every name and value —
    /// a bound on how much a caller-controlled widget definition can inflate the outgoing
    /// request, independent of any single header's own content.</para>
    /// <para><b>Enforced at BOTH write time and send time — this is the one shared
    /// implementation both call.</b> <c>JsonFileDashboardService</c>/<c>EfCoreDashboardService
    /// .ValidateWidgetConfigs</c> call this at Create/Update (Preview goes through the same
    /// <c>CreateAsync</c>); <see cref="FetchJsonAsync"/> calls it again on every fetch. Write-time
    /// only would leave a widget persisted before this method existed — or edited directly in
    /// the JSON file store — permanently unprotected, since <c>ValidateWidgetConfigs</c> never
    /// re-runs against already-persisted data. Send-time only would still work correctly, but an
    /// operator reviewing a widget definition (or a future admin UI listing them) would have no
    /// signal that a definition is invalid until someone actually views the widget.</para>
    /// </remarks>
    internal static string? ValidateHeaders(Dictionary<string, string>? headers)
    {
        if (headers == null || headers.Count == 0)
        {
            return null;
        }

        if (headers.Count > MaxHeaderCount)
        {
            return $"REST widget: header count {headers.Count} exceeds the maximum of {MaxHeaderCount}.";
        }

        long totalBytes = 0;
        foreach (var (name, value) in headers)
        {
            if (!string.IsNullOrEmpty(name) &&
                (HardRejectedHeaderNames.Contains(name) || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)))
            {
                return $"REST widget: header '{name}' is not permitted. Hop-by-hop/framing " +
                       "headers (Host, Transfer-Encoding, Content-Length, Connection, Upgrade, " +
                       "TE, Trailer, Expect, Proxy-*) are never configurable — they can break " +
                       "the SSRF guard's own invariants (see RestWidgetDataSource.ValidateHeaders).";
            }
            totalBytes += Encoding.UTF8.GetByteCount(name ?? "") + Encoding.UTF8.GetByteCount(value ?? "");
        }

        if (totalBytes > MaxHeaderTotalBytes)
        {
            return $"REST widget: total header name+value length {totalBytes} bytes exceeds the " +
                   $"maximum of {MaxHeaderTotalBytes} bytes.";
        }

        return null;
    }

    // ── HTTP fetch ───────────────────────────────────────────────────────

    private async Task<string> FetchJsonAsync(
        RestWidgetDataSourceOptions options, CancellationToken ct,
        string? tenantId = null, string? dashboardId = null, string? widgetId = null)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        // S1: HTTP method injection guard — only GET and POST are permitted.
        // new HttpMethod(string) accepts arbitrary strings; without this check an
        // operator could supply "DELETE" or "CONNECT" through config.
        var normalizedMethod = (options.Method ?? "GET").Trim().ToUpperInvariant();
        if (!_allowedMethods.Contains(normalizedMethod))
        {
            throw new InvalidOperationException(
                $"REST widget: HTTP method '{options.Method}' is not allowed. " +
                "Only GET and POST are supported for widget data sources.");
        }

        // #956 send-time enforcement: independent of, and in addition to, the write-time check
        // in JsonFileDashboardService/EfCoreDashboardService.ValidateWidgetConfigs — a widget
        // persisted before this check shipped (or edited directly in the JSON store) is only
        // ever protected by THIS call. See ValidateHeaders for the full rationale.
        var headerValidationError = ValidateHeaders(options.Headers);
        if (headerValidationError != null)
        {
            throw new InvalidOperationException(headerValidationError);
        }

        using var req = new HttpRequestMessage(new HttpMethod(normalizedMethod), options.Url);
        // #957: Headers is now nullable (see its own XML doc) — a persisted widget with no
        // configured headers deserializes to null, not an empty dictionary.
        if (options.Headers != null)
        {
            foreach (var h in options.Headers)
            {
                // S3: Header injection / CRLF guard — use the validating Add() instead of
                // TryAddWithoutValidation(). HttpRequestHeaders.Add() throws FormatException
                // when a name or value contains CRLF sequences or other invalid characters,
                // preventing HTTP request-splitting via operator-controlled header config.
                try
                {
                    req.Headers.Add(h.Key, h.Value);
                }
                catch (Exception ex) when (ex is FormatException || ex is InvalidOperationException)
                {
                    // #961: do NOT chain `ex` as InnerException. For a parser-backed header
                    // (Authorization is the notable one) .NET's own FormatException.Message
                    // embeds the FULL raw attempted value — e.g. "The format of value 'Bearer
                    // <secret>' is invalid." — and this exception is later handed whole to
                    // ILogger.LogWarning by _DashboardController / _DashboardDesignerController
                    // (their catch (InvalidOperationException ex) blocks). A logging sink that
                    // renders Exception.ToString() (the .NET default) walks the InnerException
                    // chain, so chaining `ex` here would put the secret value in application
                    // logs — reachable with no attack at all, just an operator mistyping a
                    // credential (an embedded newline from a paste, a stray control character).
                    // Keep the header NAME (an operator needs it to find the misconfigured
                    // entry) and the exception TYPE (diagnostic value); never the value, and
                    // never the original exception object.
                    throw new InvalidOperationException(
                        $"REST widget: header '{h.Key}' was rejected by the HTTP stack " +
                        $"(possible invalid characters or CRLF in name/value; underlying error: {ex.GetType().Name}).");
                }
            }
        }
        if (!string.IsNullOrEmpty(options.Body) &&
            string.Equals(normalizedMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            req.Content = new StringContent(options.Body, Encoding.UTF8, "application/json");
        }

        // #948: pass the resolved IDashboardEgressPolicy (may be null) to PinnedConnectAsync
        // via HttpRequestMessage.Options. The ConnectCallback consults it — at actual
        // connect time, against a freshly-resolved IP — to decide whether to permit a
        // private-range and/or plain-HTTP destination. options.AllowPrivateNetwork/AllowHttp
        // are deliberately NOT read here — see IDashboardEgressPolicy's XML doc.
        req.Options.Set(
            new HttpRequestOptionsKey<IDashboardEgressPolicy?>(EgressPolicyOptionKey),
            _egressPolicy);

        // #948-F8: pass the SAME request context ValidateUrlAsync built (same function, same
        // inputs) to PinnedConnectAsync via HttpRequestMessage.Options, so the connect-time
        // destination a policy sees carries identical tenant/dashboard/widget/method/header-name/
        // has-body values to what the pre-check already showed it.
        req.Options.Set(
            new HttpRequestOptionsKey<RestWidgetRequestContext?>(RequestContextOptionKey),
            BuildRequestContext(options, tenantId, dashboardId, widgetId));

        // Note: the request URI is kept as the original hostname URL.
        // TLS SNI and server-certificate validation derive from the URI host (hostname),
        // so HTTPS works correctly. PinnedConnectAsync handles the actual IP selection.

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                     .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // Avoid leaking the target URL or status details to the caller.
            // The controller catches InvalidOperationException and returns a generic 502.
            throw new InvalidOperationException(
                "REST widget: upstream returned an unsuccessful HTTP status code.");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        long total = 0;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > options.MaxResponseBytes)
            {
                throw new InvalidOperationException(
                    $"REST widget: response exceeds MaxResponseBytes ({options.MaxResponseBytes} bytes).");
            }
            limited.Write(buffer, 0, read);
        }
        // Rewind and use StreamReader to decode UTF-8 without allocating an intermediate byte[].
        limited.Position = 0;
        using var reader = new StreamReader(limited, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: -1, leaveOpen: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    // ── JSON parsing ─────────────────────────────────────────────────────

    internal static JsonNode? ExtractJsonPath(string json, string jsonPath)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "REST widget: upstream response is not valid JSON: " + ex.Message, ex);
        }

        if (string.IsNullOrWhiteSpace(jsonPath) || jsonPath == "$") { return node; }

        var path = jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath.Substring(2) : jsonPath;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
            {
                node = next;
            }
            else
            {
                // Segment miss → null result is legitimate (widget can render empty).
                return null;
            }
        }
        return node;
    }

    internal static WidgetDataResult MapToWidgetResult(JsonNode? node)
    {
        if (node == null)
        {
            return new WidgetDataResult { Value = null };
        }

        // Scalar → Value
        if (node is JsonValue value)
        {
            return new WidgetDataResult { Value = JsonValueToObject(value) };
        }

        // Array of objects → Rows + Columns
        if (node is JsonArray arr)
        {
            var rows = new List<Dictionary<string, object?>>();
            var columnSet = new List<string>();
            var columnSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in arr)
            {
                if (item is JsonObject row)
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var kv in row)
                    {
                        if (columnSeen.Add(kv.Key)) { columnSet.Add(kv.Key); }
                        dict[kv.Key] = kv.Value is JsonValue jv ? JsonValueToObject(jv) : kv.Value?.ToJsonString();
                    }
                    rows.Add(dict);
                }
                else if (item is JsonValue jv)
                {
                    // array of primitives — one-column table
                    if (columnSeen.Add("value")) { columnSet.Add("value"); }
                    rows.Add(new Dictionary<string, object?> { ["value"] = JsonValueToObject(jv) });
                }
            }
            return new WidgetDataResult { Rows = rows, Columns = columnSet };
        }

        // Object → single-row table OR scalar-Value (depends on caller shape;
        // default to single row so stat widgets reading Value via Rows[0] work).
        if (node is JsonObject singleObj)
        {
            var dict = new Dictionary<string, object?>();
            var columns = new List<string>();
            foreach (var kv in singleObj)
            {
                columns.Add(kv.Key);
                dict[kv.Key] = kv.Value is JsonValue jv ? JsonValueToObject(jv) : kv.Value?.ToJsonString();
            }
            return new WidgetDataResult
            {
                Rows = new List<Dictionary<string, object?>> { dict },
                Columns = columns
            };
        }

        return new WidgetDataResult { Value = node.ToJsonString() };
    }

    private static object? JsonValueToObject(JsonValue v)
    {
        // Preserve numeric / boolean types; fall back to string.
        if (v.TryGetValue<long>(out var l)) { return l; }
        if (v.TryGetValue<double>(out var d)) { return d; }
        if (v.TryGetValue<bool>(out var b)) { return b; }
        return v.ToString();
    }

    // ── Cache key (issue #952) ──────────────────────────────────────────

    /// <summary>Cache-key format version — bumped whenever the canonicalization algorithm
    /// itself changes, so a key computed by an old algorithm can never collide with one
    /// computed by a new one.</summary>
    internal const string CacheKeyPrefix = "WtmRestWidget::v2::";

    /// <summary>
    /// Sentinel substituted for a <c>null</c> tenant when building the canonical cache-key
    /// input. The leading space makes it distinguishable from any tenant code this framework's
    /// <c>TenantCode</c> convention would produce, so a null-tenant fetch can never collide
    /// with a real tenant whose code happens to be the literal text <c>"null"</c>.
    /// </summary>
    private const string NullTenantSentinel = " null";

    private static readonly JsonSerializerOptions _cacheKeyJsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Builds the <see cref="IMemoryCache"/> key for one REST widget fetch (issue #952).
    /// </summary>
    /// <remarks>
    /// <para><b>The point is not "add Headers to the key" — the key is structurally incapable
    /// of omitting a field.</b> Issue #952 exists because the pre-fix key
    /// (<c>$"...{Method}::{Url}::{Body}::{JsonPath}"</c>) hand-enumerated four fields and
    /// omitted <see cref="RestWidgetDataSourceOptions.Headers"/> entirely — two requests
    /// differing only in <c>Headers</c> (e.g. one carrying a real <c>Authorization</c>, one
    /// carrying none) shared a cache entry, so an unauthenticated second widget could read back
    /// the first widget's authorized response for the whole TTL window. Re-enumerating the
    /// fields (add <c>Headers</c>, keep the rest hand-listed) would only relocate the same
    /// failure to the next field someone adds — during this issue's own design review, a
    /// proposed fix that did exactly that itself omitted
    /// <see cref="RestWidgetDataSourceOptions.AllowedPorts"/>, which is the concrete proof this
    /// class of fix is not safe to repeat. Instead, this method reflection-serializes the ENTIRE
    /// <paramref name="options"/> object — every current and future public property of
    /// <see cref="RestWidgetDataSourceOptions"/> is automatically part of the key with no
    /// per-field code anywhere in this method to keep in sync.</para>
    /// <para><b>Determinism.</b> Default <see cref="JsonSerializer"/> property ordering
    /// currently follows declaration order but that is not a documented, version-stable
    /// contract, so this method does not rely on it: the serialized <see cref="JsonNode"/> tree
    /// is recursively re-sorted by property name (<see cref="StringComparer.Ordinal"/>) at every
    /// level via <see cref="CanonicalizeNode"/> before hashing. This is also what sorts
    /// <see cref="RestWidgetDataSourceOptions.Headers"/> by key — <see cref="CanonicalizeNode"/>
    /// is a generic JSON-object canonicalizer with no <c>Headers</c>-specific code; it would sort
    /// any future <c>Dictionary&lt;string,string&gt;</c> property the same way, automatically.</para>
    /// <para><b>Cache fragmentation is accepted, deliberately</b> (this repo's stated priority
    /// order is Compatibility &gt; Security &gt; Quality &gt; Performance). Two widgets differing
    /// only in, say, <see cref="RestWidgetDataSourceOptions.MaxResponseBytes"/> no longer share a
    /// cache entry even though that field cannot affect the response body — the direct, accepted
    /// cost of structural completeness over a smaller, hand-picked key.</para>
    /// <para><b>No HMAC, no random salt — deliberate, not an oversight.</b> (a) Any actor able to
    /// enumerate <see cref="IMemoryCache"/> keys in-process can equally read the plaintext
    /// <c>Headers</c> straight out of the widget-definition cache; an HMAC keyed by a secret in
    /// that SAME process would not raise the bar against that threat model, only add cost.
    /// (b) A random per-process salt would make "two tenants get different keys" a tautology that
    /// cannot fail any test — a documented failure mode in this repo (a proposed random-salt
    /// design during this issue's own review was rejected for exactly this reason).</para>
    /// <para><b>Tenant, not user/principal.</b> <paramref name="tenantId"/> is included; no
    /// user/principal identifier is, for three independent reasons: (1) authorization
    /// (<c>CanAccess</c>) already runs in the controller before <see cref="RestWidgetDataSource"/>
    /// is ever reached, so by the time this method runs the caller is already known to be allowed
    /// to view this widget; (2) <c>DashboardAlertHostedService.EvaluateAllAsync</c> calls
    /// <c>GetWidgetDataAsync(..., null, summary.TenantId, ct)</c> — there is no user on that path
    /// at all; (3) since issue #955 finding F6, fetch-time <paramref name="options"/> always comes from
    /// the widget's own persisted <c>Source.RestOptions</c> (never from a caller-supplied request
    /// parameter), so the response is a pure function of (tenant, options) — partitioning by user
    /// would buy zero additional isolation while destroying the cache for every dashboard shared
    /// across a team.</para>
    /// <para><b><c>Headers == null</c> vs <c>Headers == {}</c> (issue #957 made these two
    /// distinguishable at the model level): deliberately NOT canonicalized to the same key.</b>
    /// Both currently produce a byte-identical outgoing request — <see cref="FetchJsonAsync"/>'s
    /// <c>if (options.Headers != null) foreach (...)</c> adds zero headers either way — so
    /// merging them into one cache-key representation would be a safe, valid optimization today.
    /// This method deliberately does not, because doing so would require it to know and depend on
    /// that fetch-time behaviour, re-introducing the exact per-field special-casing this whole
    /// redesign exists to remove — this method stays a pure, generic canonicalizer of whatever
    /// <paramref name="options"/> actually is. The cost is one avoidable cache miss the first time a
    /// widget configured with <c>Headers: {}</c> and one with no configured headers would
    /// otherwise have collided — accepted under the same performance-last priority as the
    /// <c>MaxResponseBytes</c> fragmentation above.</para>
    /// </remarks>
    private static string BuildCacheKey(RestWidgetDataSourceOptions options, string? tenantId)
    {
        var node = JsonSerializer.SerializeToNode(options, typeof(RestWidgetDataSourceOptions), _cacheKeyJsonOptions);
        var canonicalNode = CanonicalizeNode(node);
        var canonicalJson = canonicalNode?.ToJsonString(_cacheKeyJsonOptions) ?? "null";

        var canonical = (tenantId ?? NullTenantSentinel) + canonicalJson;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return CacheKeyPrefix + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Recursively rebuilds <paramref name="node"/> with every <see cref="JsonObject"/>'s
    /// properties re-ordered by key (<see cref="StringComparer.Ordinal"/>) at every nesting
    /// level. <see cref="JsonArray"/> element order is left untouched — array element order is
    /// part of the value being canonicalized, not an artifact of serialization. Generic: knows
    /// nothing about <see cref="RestWidgetDataSourceOptions"/> or <c>Headers</c> specifically —
    /// any current or future object-valued (including dictionary-valued) property gets the same
    /// deterministic treatment automatically.
    /// </summary>
    private static JsonNode? CanonicalizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    // node[key] is still parented to `obj`; DeepClone() before handing it to a
                    // new parent — a JsonNode can only ever have one parent at a time.
                    sorted.Add(key, CanonicalizeNode(obj[key]?.DeepClone()));
                }
                return sorted;
            case JsonArray arr:
                var items = new JsonArray();
                foreach (var item in arr)
                {
                    items.Add(CanonicalizeNode(item?.DeepClone()));
                }
                return items;
            default:
                return node?.DeepClone();
        }
    }
}
