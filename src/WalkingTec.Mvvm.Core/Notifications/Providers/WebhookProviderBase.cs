#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Notifications.Providers;

/// <summary>
/// Shared base for all provider adapters.
/// Handles URL validation, HTTPS enforcement, host-allowlist check, SSRF guard
/// (blocks private/loopback IPs), pooled <see cref="IHttpClientFactory"/> usage,
/// and a simple retry loop on transient failures.
/// </summary>
internal abstract class WebhookProviderBase : IWtmWebhookSink
{
    internal const string HttpClientName = "WtmWebhookSink";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WtmWebhookOptions _options;
    private readonly ILogger _logger;

    // Clamping constants — keep the sink predictable even with extreme config.
    private const int TimeoutMin = 1;
    private const int TimeoutMax = 30;
    private const int RetriesMax = 3;

    protected WebhookProviderBase(
        IHttpClientFactory httpClientFactory,
        WtmWebhookOptions options,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ValidateOptions(options);
    }

    // ── Public contract ───────────────────────────────────────────────────

    public async Task SendAsync(WebhookMessage message, CancellationToken ct = default)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));

        var json = BuildPayload(message);
        var url = BuildRequestUrl(_options);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, TimeoutMin, TimeoutMax));
        var maxRetries = Math.Clamp(_options.MaxRetries, 0, RetriesMax);

        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PostAsync(url, json, timeout, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxRetries)
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2 s, 4 s, 8 s
                _logger.LogWarning(
                    "WtmWebhookSink: transient failure (attempt {Attempt}/{Max}), retrying in {Delay}s — {Message}",
                    attempt, maxRetries, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    // ── Abstract contract — each provider implements these ────────────────

    /// <summary>
    /// Serialises <paramref name="message"/> into the provider-specific JSON payload.
    /// </summary>
    protected abstract string BuildPayload(WebhookMessage message);

    /// <summary>
    /// Optionally appends signing parameters to the webhook URL.
    /// Default: returns the base URL unchanged.
    /// </summary>
    protected virtual string BuildRequestUrl(WtmWebhookOptions options) => options.WebhookUrl;

    // ── HTTP post ─────────────────────────────────────────────────────────

    private async Task PostAsync(string url, string json, TimeSpan timeout, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        // Per-request timeout via CancellationToken — avoids mutating shared HttpClient state.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"WtmWebhookSink: HTTP POST to webhook timed out after {timeout.TotalSeconds}s.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Read a limited body for diagnostics — do not log secrets from headers.
                var body = string.Empty;
                try
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    body = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
                }
                catch { /* best-effort */ }

                var statusCode = (int)response.StatusCode;
                // 5xx = transient (will be retried by caller); 4xx = permanent
                if (statusCode >= 500)
                {
                    throw new HttpRequestException(
                        $"WtmWebhookSink: webhook returned {statusCode}. Body (truncated): {body}",
                        null,
                        response.StatusCode);
                }
                throw new InvalidOperationException(
                    $"WtmWebhookSink: webhook returned non-retryable status {statusCode}. Body (truncated): {body}");
            }
        }
    }

    // ── Option validation — called once at construction time ──────────────

    internal static void ValidateOptions(WtmWebhookOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.WebhookUrl))
            throw new ArgumentException("WtmWebhookOptions.WebhookUrl must not be empty.", nameof(options));

        if (!Uri.TryCreate(options.WebhookUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException(
                $"WtmWebhookOptions.WebhookUrl is not a valid absolute URI: {options.WebhookUrl}", nameof(options));

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"WtmWebhookOptions.WebhookUrl must use https://. Got: {uri.Scheme}", nameof(options));

        // Host suffix allowlist — cheap string check (not a DNS check; main guard is below).
        if (options.AllowedHostSuffixes is { Count: > 0 } suffixes)
        {
            var host = uri.Host;
            var allowed = false;
            foreach (var suffix in suffixes)
            {
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                    break;
                }
            }
            if (!allowed)
                throw new ArgumentException(
                    $"WtmWebhookOptions.WebhookUrl host '{host}' is not in AllowedHostSuffixes.", nameof(options));
        }

        // SSRF guard: if the URL host is a literal IP address, block private ranges immediately.
        // For hostname-based URLs we rely on the SocketsHttpHandler ConnectCallback configured
        // in WebhookServiceCollectionExtensions to enforce the guard at connect time.
        if (IPAddress.TryParse(uri.Host, out var literalIp) && IsSsrfBlockedIp(literalIp))
        {
            throw new ArgumentException(
                $"WtmWebhookOptions.WebhookUrl resolves to a blocked (private/loopback) IP: {uri.Host}", nameof(options));
        }
    }

    // ── SSRF helpers (shared with the connect-callback below) ────────────

    /// <summary>
    /// Returns <c>true</c> for IPs in private, loopback, link-local, CGNAT,
    /// multicast, or reserved ranges.  Mirrors <c>RestWidgetDataSource.IsBlockedIp</c>.
    /// </summary>
    internal static bool IsSsrfBlockedIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;   // link-local / IMDS
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return true; // CGNAT
            if (b[0] == 127) return true;
            if (b[0] >= 224 && b[0] <= 239) return true;  // multicast
            if (b[0] == 0) return true;
            if (b[0] >= 240) return true;  // reserved
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // ULA fc00::/7
        }
        return false;
    }

    /// <summary>
    /// <c>SocketsHttpHandler.ConnectCallback</c> — enforces the SSRF guard at actual TCP
    /// connect time (TOCTOU-safe DNS pinning), analogous to
    /// <c>RestWidgetDataSource.PinnedConnectAsync</c>.
    /// </summary>
    internal static async ValueTask<System.IO.Stream> PinnedConnectAsync(
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
            if (!IsSsrfBlockedIp(ip)) { chosen = ip; break; }
        }
        if (chosen == null)
            throw new InvalidOperationException(
                "WtmWebhookSink: connection refused — target resolved to a blocked IP range.");

        var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(chosen, port), ct).ConfigureAwait(false);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    // ── Transient-failure classification ─────────────────────────────────

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } ||
        ex is HttpRequestException { StatusCode: null } ||   // network-level failure
        ex is TimeoutException ||
        ex is OperationCanceledException;
}
