#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Dashboard;

/// <summary>
/// Issue #957: <c>CanAccess</c> authorizes viewing a dashboard's content — it is not
/// credential-view authorization. Every widget's
/// <see cref="RestWidgetDataSourceOptions.Headers"/> is exactly where an operator is
/// documented to put <c>Authorization</c>, <c>Cookie</c>, or API-key values (see that
/// property's own XML doc); before this fix, <c>_DashboardController.Get</c> returned the
/// entire persisted <see cref="DashboardDefinition"/>, including those header values
/// verbatim, to every viewer who merely passed <c>CanAccess</c> — and the shipped viewer
/// UI (<c>framework_dashboard.js</c>'s <c>DashboardManager._loadDashboard</c>) actively
/// fetches and holds that response in the browser. A department dashboard being
/// legitimately readable by the whole department does not mean the whole department
/// should hold the API key behind it.
/// </summary>
public static class DashboardCredentialMasking
{
    /// <summary>
    /// Sentinel substituted for a REST widget header's VALUE when a
    /// <see cref="DashboardDefinition"/> is returned to a viewer (<see cref="MaskForRead"/>).
    /// Header NAMES are never masked — only values, so a future editor UI can still list
    /// which headers are configured. <see cref="ReconcileWidgetHeaders"/> recognizes this
    /// exact value coming back on a write and treats it as "caller round-tripped a masked
    /// value, not a real credential" rather than persisting the literal sentinel.
    /// </summary>
    /// <remarks>
    /// Adversarial review finding F6 (#960): deliberately <c>static readonly</c>, not
    /// <c>const</c>. WTM ships as a NuGet package — a <c>const</c> is inlined into every
    /// downstream assembly's IL at THEIR compile time. If this sentinel's literal value ever
    /// changed in a future WTM release, an already-compiled downstream binary would keep
    /// comparing header values against the OLD literal forever (until recompiled against the
    /// new package), and — worse — would start persisting the NEW sentinel value it receives
    /// from a live server as if it were a literal credential, since its own compiled-in
    /// comparison would never match it. <c>static readonly</c> is resolved at the CONSUMER's
    /// runtime against whatever WTM binary is actually loaded, closing that drift window.
    /// </remarks>
    public static readonly string MaskedHeaderValue = "••••••••";

    /// <summary>
    /// Returns a <see cref="DashboardDefinition"/> safe to serialize into an HTTP response:
    /// every widget's <see cref="RestWidgetDataSourceOptions.Headers"/> VALUES are replaced
    /// with <see cref="MaskedHeaderValue"/>; keys are preserved unchanged.
    /// </summary>
    /// <remarks>
    /// Does not mutate <paramref name="source"/> or any object it references. This matters
    /// because <c>JsonFileDashboardService</c>'s definition cache (<c>_defCache</c>) can hand
    /// back the SAME cached <see cref="DashboardDefinition"/> instance to multiple callers —
    /// masking in place would permanently corrupt what a subsequent <c>GetAsync</c> call, or
    /// an internal widget-data fetch that needs the REAL header value to call the upstream
    /// REST API, would see. When no widget in <paramref name="source"/> has any headers to
    /// mask, this returns <paramref name="source"/> itself unchanged (no clone, no
    /// allocation) — dashboards with no REST-widget credentials in play are unaffected byte
    /// for byte.
    /// </remarks>
    /// <remarks>
    /// Adversarial review finding F7 (#960), disclosed rather than fixed here (scope decision
    /// deferred to the maintainer): this masks <see cref="RestWidgetDataSourceOptions.Headers"/>
    /// ONLY. <see cref="RestWidgetDataSourceOptions.Url"/> and <see cref="RestWidgetDataSourceOptions.Body"/>
    /// are copied to the masked response completely unmasked, and both are at least as common a
    /// place to put a credential — e.g. userinfo-in-URL (<c>https://user:pass@host/...</c>) or an
    /// API key in a query string (<c>...?api_key=...</c>) for <c>Url</c>; a bearer token or
    /// session id in a POST payload for <c>Body</c>. Any <c>CanAccess</c> viewer still receives
    /// both in full — see <c>docs/production-readiness.md</c>'s #957 entry for the full
    /// disclosure.
    /// </remarks>
    public static DashboardDefinition MaskForRead(DashboardDefinition source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (source.Widgets == null || source.Widgets.Count == 0)
            return source;

        Dictionary<string, WidgetDefinition>? maskedWidgets = null;
        foreach (var (widgetId, widget) in source.Widgets)
        {
            var masked = MaskWidgetHeaders(widget);
            if (!ReferenceEquals(masked, widget))
            {
                // First widget that actually needs masking: snapshot the original
                // dictionary (shallow copy of the key/value pairs) so we never write
                // into source.Widgets itself.
                maskedWidgets ??= new Dictionary<string, WidgetDefinition>(source.Widgets);
                maskedWidgets[widgetId] = masked;
            }
        }

        if (maskedWidgets == null)
            return source; // nothing had headers to mask — safe to return the same instance

        return new DashboardDefinition
        {
            SchemaVersion = source.SchemaVersion,
            Id = source.Id,
            Title = source.Title,
            Owner = source.Owner,
            TenantId = source.TenantId,
            Sharing = source.Sharing,
            RefreshInterval = source.RefreshInterval,
            Layout = source.Layout,
            Widgets = maskedWidgets,
            Filters = source.Filters,
            Links = source.Links,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    private static WidgetDefinition MaskWidgetHeaders(WidgetDefinition widget)
    {
        var headers = widget.Source?.RestOptions?.Headers;
        if (headers == null || headers.Count == 0)
            return widget; // nothing to mask — safe to share this reference

        var maskedHeaders = new Dictionary<string, string>(headers.Count);
        foreach (var key in headers.Keys)
        {
            maskedHeaders[key] = MaskedHeaderValue;
        }

        var originalRestOptions = widget.Source!.RestOptions!;
        var maskedRestOptions = new RestWidgetDataSourceOptions
        {
            Url = originalRestOptions.Url,
            Method = originalRestOptions.Method,
            Headers = maskedHeaders,
            Body = originalRestOptions.Body,
            JsonPath = originalRestOptions.JsonPath,
            CacheTtlSeconds = originalRestOptions.CacheTtlSeconds,
            TimeoutSeconds = originalRestOptions.TimeoutSeconds,
            MaxResponseBytes = originalRestOptions.MaxResponseBytes,
            AllowPrivateNetwork = originalRestOptions.AllowPrivateNetwork,
            AllowHttp = originalRestOptions.AllowHttp,
            AllowedPorts = originalRestOptions.AllowedPorts
        };

        var maskedSource = new WidgetSourceDefinition
        {
            Kind = widget.Source.Kind,
            Name = widget.Source.Name,
            ListVmType = widget.Source.ListVmType,
            Dimensions = widget.Source.Dimensions,
            Measures = widget.Source.Measures,
            Filters = widget.Source.Filters,
            RestOptions = maskedRestOptions
        };

        return new WidgetDefinition
        {
            Type = widget.Type,
            Title = widget.Title,
            Source = maskedSource,
            Config = widget.Config,
            Thresholds = widget.Thresholds,
            DrillDown = widget.DrillDown
        };
    }

    /// <summary>
    /// Issue #957 (F1, adversarial review #960): one REST widget whose stored headers were NOT
    /// carried over to a save because its <c>Url</c>'s destination (scheme+host+port) changed
    /// from what the stored headers were bound to. <see cref="ReconcileWidgetHeaders"/> returns
    /// these rather than logging directly — it has no <see cref="ILogger"/> dependency of its
    /// own, staying a pure static utility — so the caller (a controller, which already owns a
    /// scoped logger) logs them with structured parameters via <see cref="LogWarnings"/>.
    /// </summary>
    public sealed record HeaderDriftWarning(
        string WidgetId, string OldDestination, string NewDestination, int DroppedHeaderCount);

    /// <summary>
    /// Logs each <see cref="HeaderDriftWarning"/> with structured parameters. Convenience so
    /// the three call sites (<c>_DashboardController.Create</c>/<c>Update</c>,
    /// <c>_DashboardDesignerController.Preview</c>) do not each hand-roll the same format
    /// string.
    /// </summary>
    public static void LogWarnings(ILogger logger, IReadOnlyList<HeaderDriftWarning> warnings)
    {
        foreach (var w in warnings)
        {
            logger.LogWarning(
                "[Dashboard] REST widget '{WidgetId}' destination changed from {OldDestination} " +
                "to {NewDestination} — {DroppedHeaderCount} stored header(s) were not carried over.",
                LogSanitizer.Sanitize(w.WidgetId), w.OldDestination, w.NewDestination, w.DroppedHeaderCount);
        }
    }

    /// <summary>
    /// Issue #957: reconciles a caller-submitted widget dictionary's REST configuration against
    /// the previously PERSISTED widgets before the caller's definition is written to storage.
    /// Because <see cref="MaskForRead"/> never returns a header's real value, a client that
    /// fetched a dashboard, left a header untouched in whatever editor UI it uses, and saved the
    /// definition back would otherwise silently overwrite that header's real value with the
    /// literal masked sentinel — or, for the shipped designer (which has no headers-editing UI
    /// at all and never sends a <c>headers</c> key), silently wipe the header entirely. This
    /// reconciliation makes both safe.
    /// </summary>
    /// <param name="incomingWidgets">
    /// The widgets from the request body about to be persisted. Mutated in place: each widget's
    /// <c>Source.RestOptions</c>/<c>.Headers</c> may be rewritten to its final, reconciled
    /// value. Safe to mutate because these are freshly model-bound request objects, never a
    /// shared/cached instance.
    /// </param>
    /// <param name="existingWidgets">
    /// The widgets currently persisted for this dashboard (<c>null</c>/empty on Create, where
    /// nothing exists yet to preserve). Never mutated; anything preserved from here is copied
    /// into a new object/dictionary, not aliased.
    /// </param>
    /// <returns>
    /// <c>Error</c> is <c>null</c> on success. A non-null, caller-displayable message when case
    /// (e) below fires — the caller is expected to return that message as a 400 and not proceed
    /// to persist anything (and should NOT log <c>Warnings</c> in that case — nothing was
    /// actually saved), mirroring how <c>ValidateWidgetConfigs</c> rejects (rather than silently
    /// strips) other caller-controlled <see cref="RestWidgetDataSourceOptions"/> fields elsewhere
    /// in this codebase (issue #948). <c>Warnings</c> lists every widget whose stored headers
    /// were dropped due to a cross-destination save (F1) — log these on the SUCCESS path via
    /// <see cref="LogWarnings"/>.
    /// </returns>
    /// <remarks>
    /// Per widget:
    /// <list type="bullet">
    ///   <item>(f) The incoming widget's <c>Source.RestOptions</c> node itself is absent (not
    ///         just <c>Headers</c>) while <c>Source.Kind</c> is still <c>"rest"</c> → preserve
    ///         the ENTIRE existing <c>RestOptions</c> (URL and all) unchanged. Same principle as
    ///         (a) one level up: absence in a partial update means "the caller didn't touch
    ///         this," not "delete it." If <c>Kind</c> is anything other than <c>"rest"</c>,
    ///         dropping <c>RestOptions</c> is the CORRECT, intentional behaviour (the widget
    ///         genuinely stopped being a REST widget) — this case does not apply and nothing is
    ///         restored.</item>
    ///   <item>(a) <c>Headers</c> is <c>null</c> (key absent from the request JSON, or sent
    ///         explicitly as JSON <c>null</c>) → preserve ALL of that widget's existing headers
    ///         unchanged — <b>but only when the incoming <c>Url</c> resolves to the SAME
    ///         destination the stored headers were bound to (F1, see remarks below)</b>. This is
    ///         what makes the shipped designer — which never sends a <c>headers</c> key at all —
    ///         safe: without this case, every save through the designer silently wiped headers
    ///         (a live data-loss bug, not just a theoretical one).</item>
    ///   <item>(b) <c>Headers</c> is present and empty (<c>{}</c>) → clear all headers for that
    ///         widget. This is the only way a caller can intentionally remove every header.</item>
    ///   <item>(c) A key's value is anything other than <see cref="MaskedHeaderValue"/> → that
    ///         becomes the key's new stored value (a real, caller-supplied value) — regardless
    ///         of destination, since the caller is not carrying anything over, they are setting
    ///         it directly.</item>
    ///   <item>(d) A key's value equals <see cref="MaskedHeaderValue"/> AND the key already has
    ///         an existing stored value FOR THE SAME DESTINATION → preserve the EXISTING value
    ///         for that key (the caller round-tripped a value it read from a masked GET and did
    ///         not choose to change it).</item>
    ///   <item>(e) A key's value equals <see cref="MaskedHeaderValue"/> but the key has NO
    ///         existing stored value FOR THE SAME DESTINATION to fall back to → REJECTED. This
    ///         fires when the key genuinely never existed, when a header name was mistyped, OR
    ///         when the destination changed (F1) — there is no existing value THIS destination
    ///         can borrow, even if one exists for a different one. On Create, every widget's
    ///         <paramref name="existingWidgets"/> lookup always misses (nothing persisted yet),
    ///         so this case fires for ANY key whose value equals <see cref="MaskedHeaderValue"/>
    ///         on Create.</item>
    /// </list>
    /// A non-empty <c>Headers</c> dictionary is a full replacement set for that widget — a key
    /// present in the existing stored headers but absent from a non-empty incoming <c>Headers</c>
    /// is dropped, the same "whatever you send is the new state" semantics case (b) already
    /// implies.
    /// </remarks>
    /// <remarks>
    /// <b>F1 — credentials are host-scoped (adversarial review #960).</b> Pre-this-fix, the
    /// designer's silent header wipe was, accidentally, a fail-safe: an editor could not point a
    /// widget's real credential at an attacker-chosen host through the shipped UI, because the
    /// header would just vanish. Naive case-(a) "always preserve when absent" turns that into
    /// credential forwarding instead: an editor who merely retypes a widget's URL (the ONLY field
    /// the designer's REST-widget UI exposes) through the shipped designer — with no
    /// headers-editing UI to even show them what would happen — would otherwise carry the SAME
    /// real header value onto whatever new host they typed, with no confirmation and nothing
    /// logged, and the very next background/viewer fetch sends that credential to the new host. A
    /// public HTTPS destination takes <c>RestWidgetDataSource</c>'s fast path with no
    /// <see cref="IDashboardEgressPolicy"/> consultation, so this is not blocked by #948's egress
    /// gate either — that gate answers "may this application reach this network location," not
    /// "should this specific credential follow this specific widget to a new one." This is
    /// <b>not</b> a privilege escalation — an editor could already read the plaintext credential
    /// via <c>Get</c> before this fix shipped at all — but it means the read-side mask is one
    /// edit away from being defeated by anyone who already has edit rights.
    /// <para>
    /// Fix: before applying (a)/(d)'s "preserve/restore" semantics, this method compares the
    /// incoming widget's <c>RestOptions.Url</c> against the existing one using
    /// scheme+host+port (case-insensitive host, default ports normalized so
    /// <c>https://h</c> and <c>https://h:443</c> compare equal — see <see cref="TryGetDestination"/>).
    /// When they match, reconciliation proceeds exactly as before (the common case — path/query
    /// edits on the same host). When they differ — OR either URL cannot be parsed at all, which
    /// is treated as "not the same destination" rather than falling into the preserve branch on
    /// an ambiguous comparison — stored headers are NOT carried over: case (a) leaves
    /// <c>Headers</c> empty (silent drop, logged via a <see cref="HeaderDriftWarning"/>, since a
    /// legitimate URL edit through the designer must still be possible — the designer has no way
    /// to re-supply headers, so REJECTING the whole save here would permanently lock a widget's
    /// URL once it had a header), and case (d) is routed into case (e)'s REJECT (a caller
    /// explicitly round-tripping a sentinel for a specific key against a NEW destination gets a
    /// clear error rather than a silent drop, since that shape is not reachable through the
    /// shipped designer at all and warrants the stricter response). This deliberately mirrors
    /// what <see cref="System.Net.Http.HttpClient"/> itself does on a cross-host redirect —
    /// strips <c>Authorization</c> rather than forwarding it — and is the same reasoning #948
    /// already used to disable auto-redirect on <c>RestWidgetDataSource</c>'s named
    /// <c>HttpClient</c>: credentials do not automatically follow a destination change, on
    /// principle, not as a one-off heuristic for this endpoint.
    /// </para>
    /// <para>
    /// <b>Known residual, not closed by this fix</b>: an editor who changes a widget's host AND,
    /// in the SAME or a LATER save, explicitly re-supplies the SAME real header value for the
    /// new host (case (c) — a real, caller-typed value, not a round-tripped sentinel) succeeds,
    /// by design — that is indistinguishable from a legitimate credential rotation and this
    /// method has no way to tell the two apart, nor does any other layer in this endpoint. F1
    /// closes the SILENT, no-user-visible-step forwarding path (the sentinel/absent-header
    /// carry-over); it does not, and structurally cannot, stop an editor who is willing to
    /// deliberately retype the real credential from sending it wherever they choose to — that is
    /// unchanged from every editor's pre-existing <c>CanEdit</c> capability and is not a new gap
    /// this fix introduces.
    /// </para>
    /// </remarks>
    public static (string? Error, IReadOnlyList<HeaderDriftWarning> Warnings) ReconcileWidgetHeaders(
        IReadOnlyDictionary<string, WidgetDefinition>? incomingWidgets,
        IReadOnlyDictionary<string, WidgetDefinition>? existingWidgets)
    {
        var warnings = new List<HeaderDriftWarning>();
        if (incomingWidgets == null) return (null, warnings);

        foreach (var (widgetId, widget) in incomingWidgets)
        {
            WidgetDefinition? existingWidget = null;
            existingWidgets?.TryGetValue(widgetId, out existingWidget);

            var restOptions = widget.Source?.RestOptions;
            if (restOptions == null)
            {
                // (f) [F4]: RestOptions itself absent, not just Headers — the inverse of case
                // (a) one level up. Only preserve while the widget is still declared "rest";
                // if Kind changed away from it, dropping RestOptions is correct, not a defect.
                if (string.Equals(widget.Source?.Kind, "rest", StringComparison.OrdinalIgnoreCase) &&
                    existingWidget?.Source?.RestOptions != null)
                {
                    widget.Source!.RestOptions = CloneRestOptions(existingWidget.Source.RestOptions);
                }
                continue;
            }

            var existingRestOptions = existingWidget?.Source?.RestOptions;
            var existingHeaders = existingRestOptions?.Headers;

            // F1: credentials are host-scoped — see this method's own XML remarks for the full
            // rationale. Any URL that fails to parse on EITHER side is treated as "not the same
            // destination"; never let an ambiguous comparison fall into the preserve branch.
            var hasIncomingDestination = TryGetDestination(restOptions.Url, out var incomingDestination);
            var hasExistingDestination = TryGetDestination(existingRestOptions?.Url, out var existingDestination);
            var sameDestination = hasIncomingDestination && hasExistingDestination &&
                                   string.Equals(incomingDestination, existingDestination, StringComparison.Ordinal);

            if (!sameDestination && existingHeaders is { Count: > 0 })
            {
                warnings.Add(new HeaderDriftWarning(
                    widgetId,
                    hasExistingDestination ? existingDestination : "(unparseable or missing URL)",
                    hasIncomingDestination ? incomingDestination : "(unparseable or missing URL)",
                    existingHeaders.Count));
            }

            // Everything below reasons about "what is already stored for THIS destination" —
            // a cross-destination save reconciles as if nothing were stored at all, which is
            // what turns case (a) into a silent drop (not a forward) and routes a
            // cross-destination case (d) sentinel into case (e)'s reject.
            var effectiveExistingHeaders = sameDestination ? existingHeaders : null;

            if (restOptions.Headers == null)
            {
                // (a) absent / explicit null → preserve, scoped to the current destination only.
                restOptions.Headers = effectiveExistingHeaders != null
                    ? new Dictionary<string, string>(effectiveExistingHeaders)
                    : null;
                continue;
            }

            if (restOptions.Headers.Count == 0)
            {
                // (b) present, empty → clear. Nothing further to reconcile.
                continue;
            }

            var merged = new Dictionary<string, string>(restOptions.Headers.Count);
            foreach (var (key, value) in restOptions.Headers)
            {
                if (value == MaskedHeaderValue)
                {
                    if (effectiveExistingHeaders != null && effectiveExistingHeaders.TryGetValue(key, out var existingValue))
                    {
                        merged[key] = existingValue; // (d)
                    }
                    else
                    {
                        // (e) — also reached for a cross-destination save (F1): no value stored
                        // for THIS destination to restore, even if one exists for a different one.
                        return ($"Widget '{widgetId}': header '{key}' 的值等於遮罩後的預留值，" +
                               "但這個 widget 目前沒有這個 header 在這個目的地下的既存值可還原" +
                               "（可能是誤把畫面上顯示的遮罩字面值當成真實憑證送出、header 名稱打錯，或 " +
                               "REST 目的地已變更——既有的 header 值不會沿用到新的目的地）。" +
                               "請提供實際的 header 值，或移除這個 header。", warnings);
                    }
                }
                else
                {
                    merged[key] = value; // (c)
                }
            }
            restOptions.Headers = merged;
        }

        return (null, warnings);
    }

    /// <summary>
    /// Issue #957 (F1): resolves a REST widget URL to a normalized "destination" string
    /// (<c>scheme://host:port</c>, lower-invariant, default ports resolved to their concrete
    /// number so <c>https://h</c> and <c>https://h:443</c> compare equal) for host-scoped
    /// credential reconciliation in <see cref="ReconcileWidgetHeaders"/>. Returns <c>false</c> —
    /// never a destination string a caller could mistake for a real comparison target — for a
    /// null/empty/unparseable URL or a non-http(s) scheme, so an ambiguous URL can never
    /// accidentally satisfy a same-destination comparison.
    /// </summary>
    private static bool TryGetDestination(string? url, out string destination)
    {
        destination = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var isPlainHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isPlainHttp && !isHttps) return false;

        // Default-port normalization mirrors RestWidgetDataSource.ValidateUrlAsync's own
        // resolvedPort computation (kept in sync deliberately — see that method's own comment
        // for why uri.Port can be -1 here).
        var port = uri.Port != -1 ? uri.Port : (isPlainHttp ? 80 : 443);

        destination = $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}:{port}";
        return true;
    }

    /// <summary>
    /// Issue #957 (F4): full property-by-property clone of a persisted
    /// <see cref="RestWidgetDataSourceOptions"/>, used when preserving it wholesale (case (f)).
    /// Deliberately duplicates <see cref="MaskWidgetHeaders"/>'s clone shape rather than sharing
    /// it — that one masks <c>Headers</c> for an HTTP response, this one keeps the real value for
    /// persistence — kept in sync deliberately, the same convention this codebase already uses
    /// for <c>ValidateWidgetConfigs</c> across the two storage backends.
    /// </summary>
    private static RestWidgetDataSourceOptions CloneRestOptions(RestWidgetDataSourceOptions source) => new()
    {
        Url = source.Url,
        Method = source.Method,
        Headers = source.Headers != null ? new Dictionary<string, string>(source.Headers) : null,
        Body = source.Body,
        JsonPath = source.JsonPath,
        CacheTtlSeconds = source.CacheTtlSeconds,
        TimeoutSeconds = source.TimeoutSeconds,
        MaxResponseBytes = source.MaxResponseBytes,
        AllowPrivateNetwork = source.AllowPrivateNetwork,
        AllowHttp = source.AllowHttp,
        AllowedPorts = source.AllowedPorts?.ToArray()
    };
}
