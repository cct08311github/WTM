#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Configuration for <see cref="WtmRequestTimeoutsMiddleware"/>:
    /// per-app default deadline, path-prefix overrides, and exclusions.
    /// </summary>
    /// <remarks>
    /// Pairs naturally with <c>UseWtmSlowRequestLogging()</c> (#840):
    /// the latter logs slow outliers; this kills runaways. Set the
    /// timeout strictly above the slow-request threshold so a transient
    /// blip is logged once before being killed.
    /// </remarks>
    public class WtmRequestTimeoutsOptions
    {
        /// <summary>
        /// Default per-request deadline in milliseconds. Default 30 s —
        /// generous enough that ordinary CRUD / report endpoints clear
        /// it comfortably, tight enough that a runaway query / external
        /// call doesn't park a thread for minutes. Set to 0 or negative
        /// to disable the default and rely solely on
        /// <see cref="PathOverrides"/> (anything not listed will pass
        /// through unbounded).
        /// </summary>
        public int DefaultTimeoutMs { get; set; } = 30_000;

        /// <summary>
        /// Path-prefix-keyed timeout overrides. The longest-matching
        /// prefix wins, so callers can scope a more permissive deadline
        /// to specific endpoints — typical examples: <c>/api/export</c>
        /// (5 minutes for CSV / Excel writes), <c>/api/report</c>
        /// (2 minutes for cross-tenant aggregates), <c>/api/admin/migrate</c>
        /// (10 minutes for one-shot back-fills). A value ≤ 0 in this
        /// dictionary disables the timeout for the matched path entirely
        /// (handle-with-care; intended for SSE / WebSocket / streaming
        /// endpoints).
        /// </summary>
        public IDictionary<string, int> PathOverrides { get; set; } =
            new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Path prefixes that bypass the timeout entirely. Defaults
        /// match the rest of the WTM middleware bundle: <c>/healthz</c>,
        /// <c>/_framework</c>, <c>/_js</c>, <c>/_content</c>,
        /// <c>/favicon.ico</c>. Static assets / health probes don't
        /// need a deadline and the bypass keeps the cost flat for them.
        /// </summary>
        public IList<string> PathExclusions { get; set; } = new List<string>
        {
            "/healthz",
            "/_framework",
            "/_js",
            "/_content",
            "/favicon.ico",
        };
    }
}
