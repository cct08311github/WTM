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
///   <item>HTTPS by default; plain HTTP requires <see cref="RestWidgetDataSourceOptions.AllowHttp"/>.</item>
///   <item>SSRF guard: resolved URL host must not map to private, loopback,
///         link-local (incl. cloud IMDS), or multicast IPs unless
///         <see cref="RestWidgetDataSourceOptions.AllowPrivateNetwork"/> is set.</item>
///   <item>Response body capped at <see cref="RestWidgetDataSourceOptions.MaxResponseBytes"/>
///         (default 1 MiB) to prevent memory exhaustion.</item>
///   <item>Request timeout bounded by <see cref="RestWidgetDataSourceOptions.TimeoutSeconds"/>
///         (default 10 s).</item>
/// </list>
/// </remarks>
public class RestWidgetDataSource : IWidgetDataSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;

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
        ValidateUrl(options);

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

    private static RestWidgetDataSourceOptions ParseOptions(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("options", out var json) || string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                "REST widget request missing required parameter 'options' (JSON-serialized RestWidgetDataSourceOptions).");
        }
        try
        {
            var opts = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return opts ?? throw new InvalidOperationException("REST widget options JSON deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("REST widget options JSON is malformed: " + ex.Message, ex);
        }
    }

    // ── URL validation + SSRF guard ──────────────────────────────────────

    internal static void ValidateUrl(RestWidgetDataSourceOptions options)
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
                    "Set AllowHttp=true in the options to permit plain HTTP (typically for internal endpoints).");
            }
        }
        else
        {
            throw new InvalidOperationException(
                "REST widget: only http / https URL schemes are supported. Got: " + uri.Scheme);
        }

        if (options.AllowPrivateNetwork) { return; }

        // Resolve host and check every returned IP. A hostname that could
        // resolve to both a private and public IP would be rejected — safe
        // default for SSRF protection.
        IPAddress[] ips;
        if (IPAddress.TryParse(uri.Host, out var literalIp))
        {
            ips = new[] { literalIp };
        }
        else
        {
            try
            {
                ips = Dns.GetHostAddresses(uri.Host);
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
                    "Set AllowPrivateNetwork=true in the options to permit internal endpoints (SSRF mitigation).");
            }
        }
    }

    internal static bool IsBlockedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) { return true; }
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) { return true; }
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) { return true; }
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) { return true; }
            // 169.254.0.0/16 (link-local, includes AWS IMDS 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) { return true; }
            // 127.0.0.0/8 (already caught by IsLoopback, but explicit)
            if (bytes[0] == 127) { return true; }
            // 224.0.0.0/4 (multicast)
            if (bytes[0] >= 224 && bytes[0] <= 239) { return true; }
            // 0.0.0.0/8 (invalid source)
            if (bytes[0] == 0) { return true; }
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) { return true; }
            // ULA fc00::/7
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) { return true; }
        }
        return false;
    }

    // ── HTTP fetch ───────────────────────────────────────────────────────

    private async Task<string> FetchJsonAsync(RestWidgetDataSourceOptions options, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("WtmRestWidget");
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

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                     .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"REST widget: upstream returned {(int)resp.StatusCode} {resp.StatusCode} for {options.Url}");
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
        return Encoding.UTF8.GetString(limited.ToArray());
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
