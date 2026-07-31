#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace WalkingTec.Mvvm.Core.Dashboard;

/// <summary>
/// Issue #948 (review finding F2): minimal, config-driven <see cref="IDashboardEgressPolicy"/>
/// so a host that only needs to allowlist a small, fixed set of internal REST widget
/// destinations does not have to write a custom <see cref="IDashboardEgressPolicy"/>
/// implementation in C#. This exists specifically for the compatibility story of
/// upgrading to this fix: a deployment that had a working <c>rest</c> widget pointing at
/// an internal host via <c>RestOptions.AllowPrivateNetwork = true</c> before this issue's
/// fix landed can restore it with configuration alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately simple matching — exact host/IP, optional port list, no CIDR.</b> A
/// host that needs subnet-level or wildcard matching should implement its own
/// <see cref="IDashboardEgressPolicy"/>; keeping this built-in policy's matching logic to
/// two comparisons (resolved IP equality, or original-hostname equality) keeps it
/// auditable at a glance, which matters more here than expressiveness — this is the
/// policy a deployment reaches for specifically because it does not want to reason about
/// custom authorization code.
/// </para>
/// <para>
/// <b>Registration:</b>
/// <code>
/// services.AddWtmDashboard();
/// services.Configure&lt;DashboardEgressAllowlistOptions&gt;(opt =>
/// {
///     opt.Entries.Add(new DashboardEgressAllowlistEntry
///     {
///         Host = "10.1.2.3",           // matched against the RESOLVED IP
///         Ports = new[] { 8080 }        // null/empty = any port
///     });
/// });
/// services.AddWtmDashboardEgressPolicy&lt;ConfiguredAllowlistDashboardEgressPolicy&gt;();
/// </code>
/// Uses <see cref="IOptionsMonitor{TOptions}"/> (not <see cref="IOptions{TOptions}"/>) so
/// this policy is safe under the Singleton lifetime <c>AddWtmDashboardEgressPolicy&lt;T&gt;()</c>
/// requires (see that method's XML doc) and picks up configuration reloads without
/// needing a captive scoped dependency.
/// </para>
/// <para>
/// Default (no entries configured): denies everything, same as no policy registered at
/// all — this built-in policy does not change the safe-by-default posture on its own; it
/// only gives an operator a config-only path to opt specific destinations back in.
/// </para>
/// </remarks>
public sealed class ConfiguredAllowlistDashboardEgressPolicy : IDashboardEgressPolicy
{
    private readonly IOptionsMonitor<DashboardEgressAllowlistOptions> _options;

    public ConfiguredAllowlistDashboardEgressPolicy(IOptionsMonitor<DashboardEgressAllowlistOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<bool> IsAllowedAsync(DashboardEgressDestination destination, CancellationToken ct = default)
    {
        var entries = _options.CurrentValue.Entries;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Host))
            {
                continue;
            }

            var hostMatches =
                (IPAddress.TryParse(entry.Host, out var literalIp) && literalIp.Equals(destination.ResolvedAddress)) ||
                string.Equals(entry.Host, destination.RequestUri.Host, StringComparison.OrdinalIgnoreCase);
            if (!hostMatches)
            {
                continue;
            }

            if (entry.Ports is { Length: > 0 } ports && Array.IndexOf(ports, destination.Port) < 0)
            {
                continue;
            }

            // #948-F8: Method — null/empty entry.Methods means "any method" (back-compat: an
            // entry configured before this field existed keeps matching exactly as it did).
            if (entry.Methods is { Length: > 0 } methods &&
                !Array.Exists(methods, m => string.Equals(m, destination.Method, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // #948-F8: TenantId — null/empty entry.TenantId means "any tenant" (same back-compat
            // reasoning as Methods above). A configured entry.TenantId only matches a destination
            // that actually carries that same tenant; a destination with no tenant (e.g. a
            // background alert evaluation with no TenantId) never matches a tenant-scoped entry.
            if (!string.IsNullOrEmpty(entry.TenantId) &&
                !string.Equals(entry.TenantId, destination.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}

/// <summary>
/// Config contract for <see cref="ConfiguredAllowlistDashboardEgressPolicy"/>.
/// </summary>
public sealed class DashboardEgressAllowlistOptions
{
    public List<DashboardEgressAllowlistEntry> Entries { get; set; } = new();
}

/// <summary>One allowlisted destination for <see cref="ConfiguredAllowlistDashboardEgressPolicy"/>.</summary>
public sealed class DashboardEgressAllowlistEntry
{
    /// <summary>
    /// Either an exact IP literal (matched against the destination's RESOLVED address —
    /// the TOCTOU-safe comparison) or an exact hostname (matched case-insensitively
    /// against the original request URI's host). No CIDR or wildcard support.
    /// </summary>
    public string Host { get; set; } = "";

    /// <summary>Allowed destination ports for this entry. Null or empty means any port.</summary>
    public int[]? Ports { get; set; }

    /// <summary>
    /// Issue #948-F8: allowed HTTP methods for this entry (e.g. <c>["GET"]</c>), matched
    /// case-insensitively against <see cref="DashboardEgressDestination.Method"/>. Null or
    /// empty (the default) means any method — an entry configured before this field existed
    /// keeps matching exactly as it did.
    /// </summary>
    public string[]? Methods { get; set; }

    /// <summary>
    /// Issue #948-F8: when set, this entry only approves a destination whose
    /// <see cref="DashboardEgressDestination.TenantId"/> equals this value
    /// (case-insensitive). Null or empty (the default) means any tenant, including a
    /// destination with no tenant at all — an entry configured before this field existed
    /// keeps matching exactly as it did.
    /// </summary>
    public string? TenantId { get; set; }
}
