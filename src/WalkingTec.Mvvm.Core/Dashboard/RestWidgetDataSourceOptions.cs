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
    /// Target URL. <c>https://</c> required by default.
    /// </summary>
    /// <remarks>
    /// Issue #948 correction: setting <see cref="AllowHttp"/> = <c>true</c> is no longer,
    /// by itself, sufficient to permit <c>http://</c> — see that property's own remarks.
    /// </remarks>
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
    /// </summary>
    /// <remarks>
    /// <b>Issue #948: setting this to <c>true</c> is no longer sufficient by itself to
    /// permit private-network egress.</b> This field lives inside the widget
    /// <em>definition</em> that <c>_DashboardController.Create</c>/<c>Update</c> and
    /// <c>_DashboardDesignerController.Preview</c> all accept directly from the caller —
    /// it is caller data on every write path this framework ships, not a trusted
    /// server-side setting. <see cref="RestWidgetDataSource"/> now requires a
    /// host-registered <see cref="IDashboardEgressPolicy"/> to explicitly approve the
    /// specific resolved destination (see that interface's XML doc). The field is kept
    /// deserializable for JSON back-compat with already-persisted widget JSON — it is not
    /// itself read by <see cref="RestWidgetDataSource"/> as a grant, and (issue #955
    /// review correction) <see cref="DashboardEgressDestination"/> carries no widget,
    /// dashboard, or tenant identifier a policy could use to look this field's originating
    /// widget back up even if it wanted to — an earlier version of this remark claimed
    /// otherwise; that capability does not exist.
    /// </remarks>
    public bool AllowPrivateNetwork { get; set; } = false;

    /// <summary>
    /// When <c>false</c> (default) only <c>https://</c> URLs are allowed.
    /// </summary>
    /// <remarks>
    /// <b>Issue #948: setting this to <c>true</c> is no longer sufficient by itself to
    /// permit plain-HTTP egress</b> — same rationale as <see cref="AllowPrivateNetwork"/>;
    /// a registered <see cref="IDashboardEgressPolicy"/> must approve the specific
    /// resolved destination.
    /// </remarks>
    public bool AllowHttp { get; set; } = false;

    /// <summary>
    /// Port allowlist for SSRF mitigation. When non-null and non-empty, only
    /// the listed destination ports are allowed. Default: {80, 443, 8080, 8443}.
    /// </summary>
    /// <remarks>
    /// Issue #955 review correction: a <c>null</c> or empty value disables port
    /// restriction entirely — this is the same "caller can just turn the guard off"
    /// shape as <see cref="AllowPrivateNetwork"/>/<see cref="AllowHttp"/>, since this
    /// field lives on the same caller-controlled object. Unlike those two fields, there
    /// is no host-owned policy override for ports — <c>JsonFileDashboardService</c> and
    /// <c>EfCoreDashboardService</c>'s <c>ValidateWidgetConfigs</c> reject a "rest"
    /// widget definition whose <see cref="AllowedPorts"/> is <c>null</c> or empty at
    /// write time, so a caller cannot disable this check at all (an earlier version of
    /// this doc recommended <c>null</c>/empty "for intentional internal-network scanning
    /// use-cases", which described a capability the caller-data threat model this
    /// interface now assumes cannot safely be caller-controlled).
    /// </remarks>
    public int[]? AllowedPorts { get; set; } = new[] { 80, 443, 8080, 8443 };
}
