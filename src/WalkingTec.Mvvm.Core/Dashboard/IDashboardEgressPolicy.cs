#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;

/// <summary>
/// Issue #948: host-owned policy that decides whether a REST widget may reach a
/// specific, already-resolved network destination that the SSRF-safe default would
/// otherwise block (a private/loopback/link-local address, or a plain-<c>http://</c>
/// scheme).
/// <para>
/// <b>Why this exists.</b> Before this seam, the only way to unblock a private-network
/// or plain-HTTP REST widget destination was
/// <see cref="RestWidgetDataSourceOptions.AllowPrivateNetwork"/> /
/// <see cref="RestWidgetDataSourceOptions.AllowHttp"/> on
/// <see cref="WidgetSourceDefinition.RestOptions"/> — data that lives inside the widget
/// <em>definition</em> itself. <c>_DashboardController.Create</c>/<c>Update</c> and
/// <c>_DashboardDesignerController.Preview</c> all accept a caller-supplied
/// <see cref="WidgetDefinition"/> (or the containing <see cref="DashboardDefinition"/>)
/// as their request body, so <c>RestOptions</c> — despite being framed as "server-side"
/// in earlier comments — was, on every write path this framework ships, the
/// <em>caller's</em> data. A single boolean the caller can set is not a policy: it
/// cannot express "only THIS internal host, on THIS port", it cannot consult
/// configuration or a store, and it cannot differ per tenant or per destination. This
/// interface replaces that boolean as the actual authorization: the two
/// <c>RestWidgetDataSourceOptions</c> fields are kept (for JSON back-compat with
/// already-persisted widget definitions and for informational/back-compat purposes
/// only) but <see cref="RestWidgetDataSource"/> no longer trusts them by themselves —
/// see that class's XML doc for exactly where they used to be read.
/// </para>
/// <para>
/// <b>Async by design.</b> Per this repo's own conventions
/// (<c>.claude/rules/dotnet-conventions.md</c>: never <c>.GetAwaiter().GetResult()</c>
/// on a request path), and because a real policy will typically need to consult
/// configuration, a database allowlist, or an external service — not just compare
/// against a static in-memory list.
/// </para>
/// <para>
/// <b>Default (nothing registered): every call is denied.</b> No private network, no
/// plain HTTP, for any destination, unless a host explicitly registers an
/// implementation via <c>services.AddWtmDashboardEgressPolicy&lt;T&gt;()</c>
/// (<see cref="DashboardServiceCollectionExtensions"/>) whose
/// <see cref="IsAllowedAsync"/> approves that specific resolved destination. This is a
/// deliberately small surface — a single predicate over the resolved destination, not a
/// second sprawling per-hook authorization interface alongside
/// <c>IWtmFrameworkEndpointAuthorizer</c> (Issue #827), which answers a different
/// question ("may this caller touch this resource") and is itself still unreleased at
/// the time this seam was added.
/// </para>
/// <para>
/// <b>Called more than once per logical request.</b> <see cref="RestWidgetDataSource"/>
/// consults this policy at two points for the same widget fetch: a fast pre-check
/// (before any HTTP attempt, for a friendly error message) and the authoritative,
/// TOCTOU-safe check at actual TCP-connect time (<c>SocketsHttpHandler.ConnectCallback</c>
/// — DNS can rebind between the two). An implementation must be side-effect-free (or at
/// least idempotent) with respect to being asked about the same destination more than
/// once in quick succession.
/// </para>
/// <para>
/// <b>Registered Singleton, must be thread-safe.</b>
/// <c>services.AddWtmDashboardEgressPolicy&lt;T&gt;()</c> registers the implementation as
/// a Singleton — see that method's own XML doc for exactly why (the short version: its
/// only consumer, <see cref="RestWidgetDataSource"/>, ends up built once from the root
/// container because it is captured into a Singleton <see cref="IDashboardService"/>'s
/// constructor dependency list; a <c>Scoped</c> registration is a captive-dependency
/// error there, not a stricter lifetime). An implementation that needs a scoped resource
/// (a <c>DbContext</c> resolved via DI, request-scoped state) must obtain it itself —
/// typically via an injected <c>IDbContextFactory&lt;T&gt;</c> or
/// <c>IServiceScopeFactory</c> — rather than taking it as a constructor dependency.
/// </para>
/// </summary>
public interface IDashboardEgressPolicy
{
    /// <summary>
    /// Returns <c>true</c> only if <paramref name="destination"/> — a specific,
    /// already-resolved network destination — may be reached even though it would
    /// otherwise be blocked by the SSRF-safe default (private/loopback/link-local IP,
    /// and/or plain <c>http://</c>). Never called for a destination that is already
    /// safe by default (a public IP over <c>https://</c>) — see
    /// <see cref="DashboardEgressDestination.IsPrivateNetwork"/> /
    /// <see cref="DashboardEgressDestination.IsPlainHttp"/>, at least one of which is
    /// always <c>true</c> on any call this method receives.
    /// </summary>
    Task<bool> IsAllowedAsync(DashboardEgressDestination destination, CancellationToken ct = default);
}

/// <summary>
/// The specific, already-resolved network destination an <see cref="IDashboardEgressPolicy"/>
/// is asked to approve or deny. Carries the resolved IP address (not just the hostname)
/// so a policy decision is bound to what the socket will actually connect to — the same
/// TOCTOU-safety property <see cref="RestWidgetDataSource"/>'s DNS pinning already
/// provides for the built-in blocklist.
/// </summary>
/// <remarks>
/// <b>Issue #948 review finding F8 (implemented): <see cref="TenantId"/>, <see cref="DashboardId"/>,
/// <see cref="WidgetId"/>, <see cref="Method"/>, <see cref="HeaderNames"/>, and
/// <see cref="HasBody"/> were added after the initial #948/#955 release.</b> The #955 review
/// deferred this on the theory that adding members later would be binary-breaking — false for
/// this type specifically: it is a <c>sealed class</c> constructed only by the framework
/// (<see cref="RestWidgetDataSource"/>) and only ever read by policy implementations, so new
/// non-<c>required</c> properties are purely additive for every existing caller and every
/// existing <see cref="IDashboardEgressPolicy"/> implementation. The real reason to add them now
/// is semantic freezing, not a binary-compat deadline: a policy author who never saw
/// <see cref="TenantId"/> on this type would have no way to write a tenant-scoped policy later
/// without a breaking interface change, and every release this type ships without it makes that
/// freeze more expensive to undo. All six new properties are constructed by a single function,
/// <see cref="RestWidgetDataSource.BuildDestination"/>, called from both the fast pre-check
/// (<see cref="RestWidgetDataSource.ValidateUrlAsync"/>) and the authoritative connect-time check
/// (<see cref="RestWidgetDataSource.PinnedConnectAsync"/>) — the same destination shape reaches a
/// policy regardless of which of the two checks is asking.
/// </remarks>
public sealed class DashboardEgressDestination
{
    /// <summary>The original request URI (hostname form — never rewritten to an IP).</summary>
    public required Uri RequestUri { get; init; }

    /// <summary>The specific IP address the socket is about to connect (or would connect) to.</summary>
    public required IPAddress ResolvedAddress { get; init; }

    /// <summary>The destination TCP port.</summary>
    public required int Port { get; init; }

    /// <summary>
    /// <c>true</c> when <see cref="ResolvedAddress"/> falls in a private, loopback,
    /// link-local, CGNAT, or multicast range (see <see cref="RestWidgetDataSource.IsBlockedIp"/>).
    /// </summary>
    public required bool IsPrivateNetwork { get; init; }

    /// <summary>
    /// <c>true</c> when <see cref="RequestUri"/>'s scheme is plain <c>http://</c>
    /// (as opposed to <c>https://</c>).
    /// </summary>
    public required bool IsPlainHttp { get; init; }

    /// <summary>
    /// Issue #948-F8: the tenant the fetching widget belongs to, when known — the same value
    /// threaded through <see cref="WidgetDataRequest.TenantId"/>. <c>null</c> for callers that
    /// never supplied one, including <c>DashboardAlertHostedService.EvaluateAllAsync</c>'s
    /// background evaluation path, which calls <c>GetWidgetDataAsync</c> with no user and,
    /// depending on the summary, potentially no tenant either.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Issue #948-F8: the id of the dashboard the fetching widget belongs to, when known.
    /// <c>null</c> for a <c>Preview</c>-only transient widget or any caller that did not
    /// supply one.
    /// </summary>
    public string? DashboardId { get; init; }

    /// <summary>
    /// Issue #948-F8: the id of the fetching widget itself, when known. <c>null</c> under the
    /// same conditions as <see cref="DashboardId"/>.
    /// </summary>
    public string? WidgetId { get; init; }

    /// <summary>
    /// Issue #948-F8: the normalized (upper-case) HTTP method this request will use
    /// (e.g. <c>"GET"</c>, <c>"POST"</c>).
    /// </summary>
    public string? Method { get; init; }

    /// <summary>
    /// Issue #948-F8: the request header NAMES this request will send — never their values.
    /// <c>null</c> or empty when the widget has no configured headers.
    /// </summary>
    /// <remarks>
    /// <b>Read both sentences before using this for anything security-relevant.</b> (1) It
    /// tells you which header names are ABOUT to be sent to this destination — useful, for
    /// example, for a policy that wants to log "an Authorization header is present" without
    /// ever touching the credential itself, since a policy is host code and anything it
    /// receives may end up in a log line. (2) Seeing a name here does **not** mean the
    /// framework has validated its value in any way, and does not mean the name itself passed
    /// <see cref="RestWidgetDataSource"/>'s own hard-rejected-header-name check (issue #956) —
    /// that check runs independently, before and after this policy is consulted, and rejects
    /// the request outright rather than filtering the name out of this collection. Do not infer
    /// "this header is safe" from its presence here.
    /// </remarks>
    public IReadOnlyCollection<string>? HeaderNames { get; init; }

    /// <summary>
    /// Issue #948-F8: <c>true</c> when this request carries a non-empty body (POST only —
    /// <see cref="RestWidgetDataSource"/> never sends a body on GET).
    /// </summary>
    public bool HasBody { get; init; }
}
