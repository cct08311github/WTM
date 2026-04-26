#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Configuration for <see cref="WtmServerTimingMiddleware"/>. Controls
    /// which requests receive a <c>Server-Timing</c> response header and
    /// the shape of the emitted metric.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>Server-Timing</c> header is a W3C specification
    /// (<see href="https://www.w3.org/TR/server-timing/"/>) that browsers
    /// (Chrome DevTools → Network → Timing) surface as a per-request
    /// backend-timing breakdown. It is complementary to the framework's
    /// existing <c>UseWtmSlowRequestLogging()</c> (#840): the former
    /// tells the front-end "here's how long I took", the latter logs to
    /// the aggregator when things go slow.
    /// </para>
    /// <para>
    /// Surfacing a backend-duration number in a public response header is
    /// generally considered safe (standard practice on GitHub, Cloudflare,
    /// Netlify). If fingerprinting is a concern for a given deployment,
    /// disable the middleware or set <see cref="MinDurationMs"/> high
    /// enough that the header only emits on slow outliers (same signal
    /// as <c>UseWtmSlowRequestLogging()</c> but client-side).
    /// </para>
    /// </remarks>
    public class WtmServerTimingOptions
    {
        /// <summary>
        /// Metric name used in the <c>Server-Timing</c> header
        /// (<c>name;dur=ms</c>). Default <c>"app"</c>. Must be a token
        /// per RFC 7230 (no spaces, no control chars) — callers supplying
        /// an invalid name will simply see the middleware emit the default.
        /// </summary>
        public string MetricName { get; set; } = "app";

        /// <summary>
        /// Optional human-readable description rendered as
        /// <c>name;desc="..."</c>. Default <c>null</c> (omits the desc).
        /// DevTools shows this on hover, so keep it short and operator-free.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Minimum elapsed milliseconds before the header is emitted.
        /// Default <c>0</c> — header always emitted. Set higher (e.g.
        /// 200) to only tag slow requests, which also reduces header-byte
        /// overhead on hot-path static-ish responses.
        /// </summary>
        public int MinDurationMs { get; set; }

        /// <summary>
        /// Path prefixes that bypass the timing middleware entirely.
        /// Defaults match <c>UseWtmSlowRequestLogging()</c> exclusions so
        /// static-asset / health-probe responses stay header-clean.
        /// Matching is case-insensitive prefix check against
        /// <c>HttpContext.Request.Path</c>.
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
