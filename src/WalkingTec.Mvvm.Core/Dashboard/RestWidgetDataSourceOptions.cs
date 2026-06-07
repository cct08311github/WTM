#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.Dashboard;

/// <summary>
/// Config contract for <see cref="RestWidgetDataSource"/>. Admin puts this
/// (JSON-serialized) into the Dashboard widget's <c>dataSource.options</c>
/// node — no C# code required to wire up an external HTTP data source.
/// Introduced by issue #824.
/// </summary>
public class RestWidgetDataSourceOptions
{
    /// <summary>
    /// Target URL. <c>https://</c> required by default; set
    /// <see cref="AllowHttp"/> = <c>true</c> to permit <c>http://</c>.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// HTTP method. Only <c>GET</c> and <c>POST</c> are supported here —
    /// widget data sources should be idempotent reads.
    /// Default: <c>GET</c>.
    /// </summary>
    public string Method { get; set; } = "GET";

    /// <summary>
    /// Custom request headers. Avoid putting long-lived secrets here —
    /// the Dashboard config is stored in plain JSON.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// Optional request body for POST. Empty / null means no body.
    /// </summary>
    public string? Body { get; set; }

    /// <summary>
    /// Simple dot-path into the JSON response.
    /// Examples: <c>$.data</c>, <c>$.result.items</c>, <c>$</c> (root).
    /// Full JSONPath (wildcards, filters) is intentionally not supported —
    /// apps needing that expressiveness should implement a Custom
    /// <see cref="IWidgetDataSource"/>. Default <c>$</c> selects the root.
    /// </summary>
    public string JsonPath { get; set; } = "$";

    /// <summary>
    /// Cache TTL for the fetched response, keyed by URL + method + body.
    /// Default 60 s. Set to 0 to disable caching.
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 60;

    /// <summary>
    /// HTTP request timeout in seconds. Default 10 s. Bounded by the
    /// Dashboard controller's own timeout budget.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum response body size in bytes. Prevents memory exhaustion
    /// from unexpectedly large upstream responses. Default 1 MiB.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// SSRF mitigation: when <c>false</c> (default), URLs resolving to
    /// private IP ranges (RFC 1918), loopback, link-local (including AWS
    /// IMDS 169.254.169.254), IPv6 ULA, or multicast are rejected.
    /// Opt in to <c>true</c> only for intentional internal-network widgets.
    /// </summary>
    public bool AllowPrivateNetwork { get; set; } = false;

    /// <summary>
    /// When <c>false</c> (default) only <c>https://</c> URLs are allowed.
    /// Set <c>true</c> for plain-HTTP endpoints (typically internal-only,
    /// combined with <see cref="AllowPrivateNetwork"/>).
    /// </summary>
    public bool AllowHttp { get; set; } = false;

    /// <summary>
    /// Port allowlist for SSRF mitigation. When non-null and non-empty, only
    /// the listed destination ports are allowed. Default: {80, 443, 8080, 8443}.
    /// Set to <c>null</c> or empty to disable port restriction (not recommended
    /// unless <see cref="AllowPrivateNetwork"/> is also <c>true</c> for intentional
    /// internal-network scanning use-cases).
    /// </summary>
    public int[]? AllowedPorts { get; set; } = new[] { 80, 443, 8080, 8443 };
}
