#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
///   <item>HTTPS by default; plain HTTP requires <see cref="RestWidgetDataSourceOptions.AllowHttp"/>
///         set server-side in <see cref="WidgetSourceDefinition.RestOptions"/>.</item>
///   <item>SSRF guard: resolved URL host must not map to private, loopback,
///         link-local (incl. cloud IMDS), CGNAT (100.64/10), or multicast IPs unless
///         <see cref="RestWidgetDataSourceOptions.AllowPrivateNetwork"/> is set server-side.</item>
///   <item>DNS pinning via <c>SocketsHttpHandler.ConnectCallback</c>: the IP validated at
///         connect time by <see cref="PinnedConnectAsync"/> is the IP that the socket actually
///         connects to, eliminating DNS rebinding / TOCTOU windows. TLS SNI and server-certificate
///         validation use the original hostname URI (not an IP rewrite), so HTTPS works correctly.</item>
///   <item>Redirects disabled: the named HttpClient <see cref="HttpClientName"/> is registered
///         with <c>AllowAutoRedirect=false</c> — 302 redirects cannot bypass the SSRF guard.</item>
///   <item>Response body capped at <see cref="RestWidgetDataSourceOptions.MaxResponseBytes"/>
///         (default 1 MiB, hard limit <see cref="MaxResponseBytesHardLimit"/> 10 MiB)
///         to prevent memory exhaustion.</item>
///   <item>Request timeout bounded by <see cref="RestWidgetDataSourceOptions.TimeoutSeconds"/>
///         (clamped to [1, 60]).</item>
/// </list>
/// </remarks>
public class RestWidgetDataSource : IWidgetDataSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;

    // Shared options for case-insensitive JSON deserialization (avoids per-call allocation).
    private static readonly JsonSerializerOptions _caseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>Named HttpClient key — registered with <c>AllowAutoRedirect=false</c> and
    /// <c>ConnectCallback = <see cref="PinnedConnectAsync"/></c>.</summary>
    public const string HttpClientName = "WtmRestWidget";

    /// <summary>Hard upper limit on <see cref="RestWidgetDataSourceOptions.MaxResponseBytes"/> (10 MiB).</summary>
    public const long MaxResponseBytesHardLimit = 10 * 1024 * 1024;

    /// <summary>
    /// <see cref="HttpRequestOptions"/> key used to pass the per-request
    /// <c>AllowPrivateNetwork</c> policy to <see cref="PinnedConnectAsync"/>.
    /// </summary>
    internal const string AllowPrivateNetworkOptionKey = "WtmRestWidget.AllowPrivateNetwork";

    private const int TimeoutSecondsMin = 1;
    private const int TimeoutSecondsMax = 60;
    private const int MaxResponseBytesMin = 1024;           // 1 KB
    private const int MaxResponseBytesDefault = 1024 * 1024; // 1 MiB

    public string Name => "rest";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Rest;

    public RestWidgetDataSource(IHttpClientFactory httpClientFactory, IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, CancellationToken ct = default)
    {
        var options = ParseOptions(request.Parameters);

        // Fast-fail pre-check: validate URL scheme and, for public-only mode, verify
        // that the host resolves to a non-blocked IP. This surfaces friendly error
        // messages before we even attempt the TCP connection.
        // The authoritative TOCTOU-safe check happens again at actual connect time
        // inside PinnedConnectAsync via SocketsHttpHandler.ConnectCallback.
        await ValidateUrlAsync(options, ct).ConfigureAwait(false);

        var cacheKey = BuildCacheKey(options);
        if (options.CacheTtlSeconds > 0 && _cache.TryGetValue(cacheKey, out WidgetDataResult? cached) && cached != null)
        {
            return cached;
        }

        var json = await FetchJsonAsync(options, ct).ConfigureAwait(false);
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

    // ── URL validation + SSRF guard (fast-fail pre-check) ───────────────

    /// <summary>
    /// Fast-fail pre-check: validates URL scheme and resolves the host to verify
    /// no resolved IP is in a blocked range. Throws <see cref="InvalidOperationException"/>
    /// with a descriptive message on failure.
    /// </summary>
    /// <remarks>
    /// This is a best-effort early check. The authoritative TOCTOU-safe enforcement
    /// happens at actual connect time in <see cref="PinnedConnectAsync"/>.
    /// </remarks>
    internal static async Task ValidateUrlAsync(
        RestWidgetDataSourceOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            throw new InvalidOperationException("REST widget: Url is required.");
        }
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("REST widget: Url is not a well-formed absolute URI: " + options.Url);
        }
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            // always allowed
        }
        else if (uri.Scheme == Uri.UriSchemeHttp)
        {
            if (!options.AllowHttp)
            {
                throw new InvalidOperationException(
                    "REST widget: http:// URLs are rejected by default. " +
                    "Set AllowHttp=true in the server-side RestOptions to permit plain HTTP " +
                    "(typically for internal endpoints).");
            }
        }
        else
        {
            throw new InvalidOperationException(
                "REST widget: only http / https URL schemes are supported. Got: " + uri.Scheme);
        }

        if (options.AllowPrivateNetwork)
        {
            // Private network explicitly allowed — skip SSRF pre-check.
            return;
        }

        // Resolve host and check every returned IP.
        IPAddress[] ips;
        if (IPAddress.TryParse(uri.Host, out var literalIp))
        {
            ips = new[] { literalIp };
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

        foreach (var ip in ips)
        {
            if (IsBlockedIp(ip))
            {
                throw new InvalidOperationException(
                    $"REST widget: URL host '{uri.Host}' resolves to a blocked IP range ({ip}). " +
                    "Set AllowPrivateNetwork=true in the server-side RestOptions to permit " +
                    "internal endpoints (SSRF mitigation).");
            }
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
    /// <see cref="SelectConnectableIp"/>, and opens the socket directly to that IP.
    /// </para>
    /// <para>
    /// The per-request <c>AllowPrivateNetwork</c> policy is read from
    /// <see cref="HttpRequestMessage.Options"/> using <see cref="AllowPrivateNetworkOptionKey"/>.
    /// </para>
    /// </remarks>
    internal static async ValueTask<Stream> PinnedConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        // Read the per-request AllowPrivateNetwork policy injected by FetchJsonAsync.
        context.InitialRequestMessage.Options.TryGetValue(
            new HttpRequestOptionsKey<bool>(AllowPrivateNetworkOptionKey),
            out var allowPrivateNetwork);

        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

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

        var chosen = SelectConnectableIp(candidates, allowPrivateNetwork);
        if (chosen == null)
        {
            // All resolved IPs are in blocked ranges. Throw a generic message
            // so no host/IP details leak through the 502 response.
            throw new InvalidOperationException(
                "REST widget: connection refused — target resolved to a blocked IP range.");
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

    // ── HTTP fetch ───────────────────────────────────────────────────────

    private async Task<string> FetchJsonAsync(
        RestWidgetDataSourceOptions options, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        using var req = new HttpRequestMessage(new HttpMethod(options.Method.ToUpperInvariant()), options.Url);
        foreach (var h in options.Headers)
        {
            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        if (!string.IsNullOrEmpty(options.Body) &&
            (options.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
             options.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase)))
        {
            req.Content = new StringContent(options.Body, Encoding.UTF8, "application/json");
        }

        // Pass the per-request AllowPrivateNetwork policy to PinnedConnectAsync via
        // HttpRequestMessage.Options. The ConnectCallback reads this key to decide
        // whether to permit private-range IPs at actual connect time.
        req.Options.Set(
            new HttpRequestOptionsKey<bool>(AllowPrivateNetworkOptionKey),
            options.AllowPrivateNetwork);

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

    // ── Cache key ────────────────────────────────────────────────────────

    private static string BuildCacheKey(RestWidgetDataSourceOptions o)
    {
        return $"WtmRestWidget::{o.Method.ToUpperInvariant()}::{o.Url}::{o.Body ?? ""}::{o.JsonPath}";
    }
}
