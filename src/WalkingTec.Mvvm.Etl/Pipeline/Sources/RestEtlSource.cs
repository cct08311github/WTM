#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace WalkingTec.Mvvm.Etl.Pipeline.Sources;

/// <summary>
/// REST/HTTP ETL 資料來源 — 從 JSON REST 端點分頁抓取資料，分批 yield DataTable。
///
/// <para>
/// <b>connectionString</b> 參數為 <see cref="RestEtlSourceConfig"/> 的 JSON 序列化字串，
/// 包含端點 URL、認證 headers、分頁策略、JSON mapping 等設定。<br/>
/// <b>queryTemplate</b> 可選擇性覆寫 <see cref="RestEtlSourceConfig.RecordsPath"/>。<br/>
/// <b>watermarkValue</b> 被忽略（REST 分頁語義由配置自描述）。
/// </para>
///
/// <para>支援特性：</para>
/// <list type="bullet">
/// <item>分頁策略：page-number、offset/limit、next-link/cursor</item>
/// <item>認證 headers（bearer token / API key）— 值不會被 log</item>
/// <item>頁間 rate-limit delay</item>
/// <item>JSON→DataTable 映射（RecordsPath 指向記錄陣列；頂層純量欄位攤平為欄位）</item>
/// <item>IHttpClientFactory 整合（pooled client、per-request timeout）</item>
/// <item>SSRF 防護：HTTPS 強制、私有 IP 封鎖、DNS-pinned ConnectCallback、AllowAutoRedirect=false、response size limit</item>
/// <item>next-link 分頁 same-host 防護（Issue #661）：next-link cursor 預設須與設定端點同 scheme+host+port，
///   否則視為 semantic redirect 立即中止（headers 會隨每頁請求附上，opt-out：<c>AllowCrossHostPagination</c>）；
///   cursor 重複造訪視為 cycle 立即中止；<c>MaxPages</c> 預設 1000（顯式設為 0 才是 unlimited）</item>
/// </list>
///
/// <para>
/// 安全模型與 <c>RestWidgetDataSource</c>（Issue #824）相同：
/// private/loopback/link-local/IMDS IP 在 TCP connect 時同步封鎖（TOCTOU-safe）。
/// </para>
/// </summary>
public sealed class RestEtlSource : IEtlSource
{
    // Named HttpClient registered with AllowAutoRedirect=false and PinnedConnectAsync.
    /// <summary>Named HttpClient key for IHttpClientFactory.</summary>
    public const string HttpClientName = "WtmRestEtlSource";

    /// <summary>Hard upper limit on response bytes (100 MiB).</summary>
    public const long MaxResponseBytesHardLimit = 100L * 1024 * 1024;

    private const int TimeoutSecondsMin  = 1;
    private const int TimeoutSecondsMax  = 300;
    private const int MaxResponseBytesMin = 1024;               // 1 KB
    private const int MaxResponseBytesDefault = 10 * 1024 * 1024; // 10 MiB
    private const int PageDelayMaxMs = 60_000;

    // Shared JSON options (case-insensitive property matching, avoids per-call alloc).
    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// When <c>true</c> the DNS resolution step of the SSRF pre-check is skipped.
    /// IP-literal blocking is still enforced.
    /// <para>
    /// This is an <b>internal</b> testing escape-hatch — it must never be set to
    /// <c>true</c> in production code.  Production always resolves DNS; the named
    /// HttpClient's <see cref="PinnedConnectAsync"/> ConnectCallback provides the
    /// authoritative TOCTOU-safe SSRF enforcement at actual TCP-connect time.
    /// </para>
    /// </summary>
    internal bool SkipDnsPrecheck { get; init; } = false;

    /// <summary>
    /// Initialises the source with a pooled <see cref="IHttpClientFactory"/>.
    /// The named client <see cref="HttpClientName"/> must be registered in DI with
    /// <c>AllowAutoRedirect=false</c> and the <see cref="PinnedConnectAsync"/>
    /// ConnectCallback on its <c>SocketsHttpHandler</c>.
    /// </summary>
    public RestEtlSource(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory
            ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    // ── IEtlSource ───────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <param name="connectionString">JSON-serialised <see cref="RestEtlSourceConfig"/>.</param>
    /// <param name="queryTemplate">
    ///   Optional: overrides <see cref="RestEtlSourceConfig.RecordsPath"/> when non-empty.
    /// </param>
    /// <param name="watermarkValue">Ignored for REST sources.</param>
    public async IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString,
        string queryTemplate,
        object? watermarkValue,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var config = ParseConfig(connectionString);
        ClampConfig(config);

        // queryTemplate may override RecordsPath.
        if (!string.IsNullOrWhiteSpace(queryTemplate))
            config.RecordsPath = queryTemplate.Trim();

        // Fast-fail SSRF pre-check before we open any connections.
        await ValidateUrlAsync(config, cancellationToken, SkipDnsPrecheck).ConfigureAwait(false);

        var effectiveBatchSize = batchSize > 0 ? batchSize : 100;
        var recordsPath = string.IsNullOrWhiteSpace(config.RecordsPath)
            ? null : config.RecordsPath.Trim();

        // Schema is derived from the first row encountered across all pages.
        DataTable? batch = null;
        int rowsInBatch = 0;
        bool schemaBuilt = false;

        int pagesFetched = 0;
        string? nextCursor = config.PaginationStrategy == RestPaginationStrategy.NextLink
            ? config.Url
            : null;
        int pageNumber = config.FirstPage;
        int offset     = 0;

        // Issue #661: cycle guard for NextLink pagination — tracks every cursor URL we have
        // already followed (seeded with the starting URL) so a pathological/hostile upstream
        // that loops the "next" field back to a previously-seen page cannot crawl forever.
        HashSet<string>? visitedNextLinks = config.PaginationStrategy == RestPaginationStrategy.NextLink
            ? new HashSet<string>(StringComparer.Ordinal) { config.Url }
            : null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (config.MaxPages > 0 && pagesFetched >= config.MaxPages)
                break;

            // Build URL for this page.
            var pageUrl = config.PaginationStrategy switch
            {
                RestPaginationStrategy.NextLink =>
                    nextCursor ?? throw new InvalidOperationException("RestEtlSource: nextCursor is null — should not reach here."),
                RestPaginationStrategy.Offset =>
                    AppendQueryParam(
                        AppendQueryParam(config.Url, config.OffsetParam, offset.ToString()),
                        config.LimitParam, effectiveBatchSize.ToString()),
                _ => // PageNumber (default)
                    AppendQueryParam(config.Url, config.PageParam, pageNumber.ToString())
            };

            // Fetch one page.
            string json;
            try
            {
                json = await FetchPageAsync(config, pageUrl, cancellationToken)
                           .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            pagesFetched++;

            // Parse JSON and extract records array.
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"RestEtlSource: page {pagesFetched} response is not valid JSON: {ex.Message}", ex);
            }

            // Extract next-link cursor (before we modify root via path traversal).
            nextCursor = config.PaginationStrategy == RestPaginationStrategy.NextLink
                ? ExtractNextLink(root, config.NextLinkField)
                : null;

            // Issue #376: re-validate next-link scheme on every follow-on page to prevent
            // HTTPS→HTTP downgrade that would expose Authorization headers in plaintext.
            if (nextCursor is not null)
            {
                ValidateNextLinkScheme(nextCursor, config.AllowHttp);

                // Issue #661: reject a next-link that points at a different host than the
                // configured endpoint unless AllowCrossHostPagination opts in. config.Headers
                // (bearer tokens / API keys) are attached to every page request, so an
                // unchecked next-link is a credential-exfiltration vector that the HTTP-level
                // AllowAutoRedirect=false setting does not cover (this is a semantic redirect,
                // not an HTTP one). Must run before this URL is ever used to build a request.
                ValidateNextLinkHost(nextCursor, config.Url, config.AllowCrossHostPagination);

                // Issue #661: cycle guard — stop a next-link crawl that loops back to a
                // previously-visited cursor URL instead of spinning until MaxPages.
                if (visitedNextLinks is not null && !visitedNextLinks.Add(nextCursor))
                    throw new InvalidOperationException(
                        $"RestEtlSource: next-link cycle detected after {pagesFetched} page(s) fetched — " +
                        "the upstream 'next' field looped back to a previously-visited page. " +
                        "Stopping to prevent an infinite pagination loop.");
            }

            // Navigate to the records array.
            var recordsNode = recordsPath is null
                ? root
                : NavigatePath(root, recordsPath);

            // Map records to rows.
            var records = ToRecordList(recordsNode);
            if (records.Count == 0)
                break; // empty page = last page

            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!schemaBuilt)
                {
                    batch = BuildSchema(record);
                    schemaBuilt = true;
                    rowsInBatch = 0;
                }

                if (batch is null)
                    break; // safety — schema build failed (empty record)

                AddRow(batch, record);
                rowsInBatch++;

                if (rowsInBatch >= effectiveBatchSize)
                {
                    yield return batch;
                    batch = batch.Clone();
                    rowsInBatch = 0;
                }
            }

            // Decide whether to continue paging.
            bool pageShort = records.Count < effectiveBatchSize;

            if (config.PaginationStrategy == RestPaginationStrategy.NextLink)
            {
                if (string.IsNullOrEmpty(nextCursor))
                    break; // no next link = last page
            }
            else
            {
                if (pageShort)
                    break; // last page (partial or empty)

                if (config.PaginationStrategy == RestPaginationStrategy.PageNumber)
                    pageNumber++;
                else
                    offset += records.Count;
            }

            // Inter-page delay (rate limiting).
            if (config.PageDelayMs > 0)
            {
                await Task.Delay(config.PageDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        // Flush any remaining rows.
        if (batch is not null && rowsInBatch > 0)
            yield return batch;
    }

    // ── Config parsing ───────────────────────────────────────────────────

    internal static RestEtlSourceConfig ParseConfig(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                "RestEtlSource: connectionString must be a non-empty JSON-serialised RestEtlSourceConfig.",
                nameof(connectionString));

        RestEtlSourceConfig config;
        try
        {
            config = JsonSerializer.Deserialize<RestEtlSourceConfig>(connectionString, _jsonOpts)
                     ?? throw new InvalidOperationException(
                         "RestEtlSource: connectionString JSON deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "RestEtlSource: connectionString is not valid JSON for RestEtlSourceConfig: " + ex.Message, ex);
        }

        if (string.IsNullOrWhiteSpace(config.Url))
            throw new InvalidOperationException("RestEtlSource: Url is required in RestEtlSourceConfig.");

        return config;
    }

    private static void ClampConfig(RestEtlSourceConfig c)
    {
        c.TimeoutSeconds    = Math.Clamp(c.TimeoutSeconds, TimeoutSecondsMin, TimeoutSecondsMax);
        c.PageDelayMs       = Math.Clamp(c.PageDelayMs, 0, PageDelayMaxMs);
        c.MaxResponseBytes  = c.MaxResponseBytes <= 0
            ? MaxResponseBytesDefault
            : (int)Math.Clamp((long)c.MaxResponseBytes, MaxResponseBytesMin, MaxResponseBytesHardLimit);
    }

    // ── SSRF validation (fast-fail pre-check) ────────────────────────────

    /// <summary>
    /// Validates URL scheme and resolves host IPs to block private/loopback/IMDS ranges.
    /// This is a best-effort pre-check; the authoritative TOCTOU-safe enforcement runs in
    /// <see cref="PinnedConnectAsync"/> at actual TCP connect time.
    /// </summary>
    /// <param name="config">The REST source configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="skipDns">
    /// When <c>true</c>, skips the DNS resolution step (for testing with mock handlers only).
    /// IP-literal blocking is always enforced regardless of this flag.
    /// </param>
    internal static async Task ValidateUrlAsync(
        RestEtlSourceConfig config,
        CancellationToken ct = default,
        bool skipDns = false)
    {
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                "RestEtlSource: Url is not a well-formed absolute URI: " + config.Url);

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            // HTTPS — always permitted.
        }
        else if (uri.Scheme == Uri.UriSchemeHttp)
        {
            if (!config.AllowHttp)
                throw new InvalidOperationException(
                    "RestEtlSource: http:// URLs are rejected by default. " +
                    "Set AllowHttp=true in RestEtlSourceConfig to permit plain HTTP " +
                    "(for internal/trusted endpoints only).");
        }
        else
        {
            throw new InvalidOperationException(
                "RestEtlSource: only http and https URL schemes are supported. Got: " + uri.Scheme);
        }

        // If the host is a literal IP, always enforce the block list.
        if (IPAddress.TryParse(uri.Host, out var literalIp))
        {
            if (IsBlockedIp(literalIp))
                throw new InvalidOperationException(
                    $"RestEtlSource: URL host '{uri.Host}' resolves to a blocked IP range ({literalIp}). " +
                    "SSRF protection prevents connecting to private/loopback/IMDS addresses.");
            return;
        }

        // DNS hostname: skip the resolution step when running under a mock handler.
        if (skipDns) return;

        IPAddress[] ips;
        try
        {
            ips = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                "RestEtlSource: DNS lookup failed for host: " + uri.Host, ex);
        }

        foreach (var ip in ips)
        {
            if (IsBlockedIp(ip))
                throw new InvalidOperationException(
                    $"RestEtlSource: URL host '{uri.Host}' resolves to a blocked IP range ({ip}). " +
                    "SSRF protection prevents connecting to private/loopback/IMDS addresses.");
        }
    }

    /// <summary>
    /// Returns <c>true</c> for IP addresses that are blocked by the SSRF guard:
    /// loopback, RFC 1918 private, link-local (incl. AWS/Azure IMDS), CGNAT,
    /// multicast, reserved, and their IPv6 equivalents.
    /// Matches the logic in <c>RestWidgetDataSource.IsBlockedIp</c>.
    /// </summary>
    internal static bool IsBlockedIp(IPAddress ip)
    {
        // Unwrap IPv4-mapped IPv6 (e.g. ::ffff:169.254.169.254) before checking.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10)                                       return true; // 10/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)         return true; // 172.16/12
            if (b[0] == 192 && b[1] == 168)                       return true; // 192.168/16
            if (b[0] == 169 && b[1] == 254)                       return true; // 169.254/16 (IMDS)
            if (b[0] == 100 && (b[1] & 0xC0) == 64)               return true; // 100.64/10 CGNAT
            if (b[0] == 127)                                       return true; // 127/8
            if (b[0] >= 224 && b[0] <= 239)                       return true; // multicast
            if (b[0] == 0)                                         return true; // 0/8
            if (b[0] >= 240)                                       return true; // reserved
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // ULA fc00::/7
        }
        return false;
    }

    // ── DNS-pinning ConnectCallback ──────────────────────────────────────

    /// <summary>
    /// <c>SocketsHttpHandler.ConnectCallback</c> for the named client.
    /// Resolves DNS at connect time, rejects blocked IPs, then opens the socket directly
    /// to the chosen IP — eliminating DNS-rebinding / TOCTOU windows.
    /// TLS SNI uses the original hostname URI so HTTPS works correctly.
    /// </summary>
    internal static async ValueTask<Stream> PinnedConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] candidates;
        if (IPAddress.TryParse(host, out var literalIp))
        {
            candidates = new[] { literalIp };
        }
        else
        {
            candidates = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }

        IPAddress? chosen = null;
        foreach (var ip in candidates)
        {
            if (!IsBlockedIp(ip))
            {
                chosen = ip;
                break;
            }
        }

        if (chosen is null)
            throw new InvalidOperationException(
                "RestEtlSource: connection refused — target resolved to a blocked IP range.");

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

    // ── HTTP fetch ───────────────────────────────────────────────────────

    private async Task<string> FetchPageAsync(
        RestEtlSourceConfig config, string url, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        // Apply per-request timeout via a linked CancellationTokenSource so we don't
        // mutate HttpClient.Timeout on a reused pooled client (which would throw after
        // the first request has already been sent, e.g. in unit tests with a shared instance).
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(config.TimeoutSeconds));
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(
            ct, timeoutCts.Token);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // Apply auth headers — use validating Add() to prevent CRLF injection.
        foreach (var kv in config.Headers)
        {
            try
            {
                req.Headers.Add(kv.Key, kv.Value);
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"RestEtlSource: header '{kv.Key}' was rejected by the HTTP stack " +
                    "(possible invalid characters or CRLF in name/value).", ex);
            }
        }

        using var resp = await client
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"RestEtlSource: upstream returned HTTP {(int)resp.StatusCode}.");

        await using var stream = await resp.Content
            .ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var buf = new byte[8192];
        int read;
        long total = 0;
        while ((read = await stream.ReadAsync(
            buf.AsMemory(0, buf.Length), linkedCts.Token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > config.MaxResponseBytes)
                throw new InvalidOperationException(
                    $"RestEtlSource: response exceeds MaxResponseBytes ({config.MaxResponseBytes} bytes).");
            buffer.Write(buf, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: true);
        return await reader.ReadToEndAsync(linkedCts.Token).ConfigureAwait(false);
    }

    // ── JSON helpers ─────────────────────────────────────────────────────

    private static JsonNode? NavigatePath(JsonNode? node, string path)
    {
        if (node is null) return null;
        var seg = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path;
        if (seg == "$" || seg.Length == 0) return node;

        foreach (var part in seg.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(part, out var next))
                node = next;
            else
                return null; // path miss
        }
        return node;
    }

    /// <summary>
    /// Validates that a server-supplied next-link cursor URL is safe to follow.
    /// Rejects non-absolute, non-http(s), and http:// cursors unless AllowHttp is set.
    /// The authoritative private-IP / DNS-pin block still runs in PinnedConnectAsync.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the next-link URL is relative, uses a disallowed scheme, or
    /// is an HTTP downgrade when AllowHttp is false.
    /// </exception>
    private static void ValidateNextLinkScheme(string nextLink, bool allowHttp)
    {
        if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"RestEtlSource: next-link cursor is not an absolute URI: '{nextLink}'. " +
                "Only absolute https:// (or http:// when AllowHttp=true) next-links are permitted.");

        if (uri.Scheme == Uri.UriSchemeHttps)
            return; // always permitted

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            if (!allowHttp)
                throw new InvalidOperationException(
                    $"RestEtlSource: next-link cursor uses http:// which is rejected by default " +
                    $"(AllowHttp=false). Set AllowHttp=true to permit plain-HTTP pagination " +
                    $"(for internal/trusted endpoints only). Next-link: '{nextLink}'");
            return;
        }

        throw new InvalidOperationException(
            $"RestEtlSource: next-link cursor uses an unsupported scheme '{uri.Scheme}'. " +
            $"Only http and https are supported. Next-link: '{nextLink}'");
    }

    /// <summary>
    /// Validates that a server-supplied next-link cursor targets the same
    /// scheme+host+port as the configured endpoint. <see cref="RestEtlSourceConfig.Headers"/>
    /// (bearer tokens, API keys) are attached to every page request, so an upstream that
    /// points "next" at a foreign host could otherwise exfiltrate those credentials — a
    /// semantic redirect the HTTP-level <c>AllowAutoRedirect=false</c> setting does not
    /// cover. Callers must invoke <see cref="ValidateNextLinkScheme"/> first so
    /// <paramref name="nextLink"/> is known to be an absolute http(s) URI.
    /// </summary>
    /// <param name="nextLink">The next-link cursor URL extracted from the response body.</param>
    /// <param name="originalUrl">The configured endpoint (<see cref="RestEtlSourceConfig.Url"/>).</param>
    /// <param name="allowCrossHost">
    /// <see cref="RestEtlSourceConfig.AllowCrossHostPagination"/> — when <c>true</c>, this
    /// check is skipped entirely (opt-out).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the next-link's scheme, host, or port differs from the configured
    /// endpoint's and cross-host pagination has not been explicitly allowed. The message is
    /// sanitized to scheme/host/port only — it never includes the full URL (which may carry
    /// cursor tokens in its query string) or any header value.
    /// </exception>
    private static void ValidateNextLinkHost(string nextLink, string originalUrl, bool allowCrossHost)
    {
        if (allowCrossHost) return;

        // Both URIs are absolute at this point: nextLink was already validated by
        // ValidateNextLinkScheme, and originalUrl (RestEtlSourceConfig.Url) was already
        // validated by ValidateUrlAsync before the crawl began.
        var nextUri = new Uri(nextLink, UriKind.Absolute);
        var origUri = new Uri(originalUrl, UriKind.Absolute);

        var sameHost =
            string.Equals(nextUri.Scheme, origUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(nextUri.Host, origUri.Host, StringComparison.OrdinalIgnoreCase) &&
            nextUri.Port == origUri.Port;

        if (!sameHost)
            throw new InvalidOperationException(
                $"RestEtlSource: next-link cursor points to a different host " +
                $"('{nextUri.Scheme}://{nextUri.Host}:{nextUri.Port}') than the configured " +
                $"endpoint ('{origUri.Scheme}://{origUri.Host}:{origUri.Port}'). Request headers " +
                "(Authorization / API keys) are attached to every page request, so following a " +
                "cross-host next-link could exfiltrate credentials to a third-party host. Set " +
                "AllowCrossHostPagination=true in RestEtlSourceConfig to permit this only if the " +
                "upstream API is known and trusted to paginate across multiple hosts.");
    }

    private static string? ExtractNextLink(JsonNode? root, string nextLinkField)
    {
        var node = NavigatePath(root, nextLinkField);
        if (node is JsonValue v)
        {
            var s = v.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        return null;
    }

    /// <summary>
    /// Converts a JSON node to a list of (column → value) dictionaries.
    /// <list type="bullet">
    /// <item>Array of objects → each object is one record.</item>
    /// <item>Array of scalars → each scalar is a one-key record with key <c>"value"</c>.</item>
    /// <item>Single object → treated as a one-element array.</item>
    /// <item>Anything else → empty list (treat as last empty page).</item>
    /// </list>
    /// </summary>
    private static List<Dictionary<string, JsonNode?>> ToRecordList(JsonNode? node)
    {
        var list = new List<Dictionary<string, JsonNode?>>();

        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject obj)
                {
                    var rec = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                    foreach (var kv in obj)
                        rec[kv.Key] = kv.Value;
                    list.Add(rec);
                }
                else if (item is not null)
                {
                    // Scalar array element.
                    list.Add(new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                        { ["value"] = item });
                }
            }
        }
        else if (node is JsonObject single)
        {
            var rec = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var kv in single)
                rec[kv.Key] = kv.Value;
            list.Add(rec);
        }
        // null / scalar / unknown → empty (caller treats as last page)

        return list;
    }

    /// <summary>Builds a DataTable schema from the first record's keys.</summary>
    private static DataTable? BuildSchema(Dictionary<string, JsonNode?> firstRecord)
    {
        if (firstRecord.Count == 0) return null;
        var dt = new DataTable();
        foreach (var key in firstRecord.Keys)
            dt.Columns.Add(key, typeof(string));
        return dt;
    }

    /// <summary>Adds a record as a new row; unknown columns are ignored; missing columns get DBNull.</summary>
    private static void AddRow(DataTable dt, Dictionary<string, JsonNode?> record)
    {
        var row = dt.NewRow();
        foreach (DataColumn col in dt.Columns)
        {
            if (record.TryGetValue(col.ColumnName, out var node))
            {
                row[col] = node is null
                    ? DBNull.Value
                    : (object)(node is JsonValue v ? v.ToString() : node.ToJsonString());
            }
            else
            {
                row[col] = DBNull.Value;
            }
        }
        dt.Rows.Add(row);
    }

    // ── URL query-string builder ─────────────────────────────────────────

    /// <summary>Appends <c>?key=value</c> or <c>&amp;key=value</c> to <paramref name="url"/>.</summary>
    internal static string AppendQueryParam(string url, string key, string value)
    {
        var encodedKey   = HttpUtility.UrlEncode(key);
        var encodedValue = HttpUtility.UrlEncode(value);
        var separator    = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}{encodedKey}={encodedValue}";
    }

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose() { /* IHttpClientFactory clients are pooled — no disposal needed. */ }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
