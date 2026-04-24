#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Configuration for <see cref="WtmMaintenanceModeMiddleware"/>. Controls
    /// whether maintenance mode is active, which callers bypass it, and the
    /// shape of the "go away" response.
    /// </summary>
    /// <remarks>
    /// Maintenance mode is intended for planned outages (deploys, schema
    /// migrations, data backfills) where the app must keep accepting TCP
    /// connections for health probes / the admin plane but must reject all
    /// user traffic with a deterministic 503. Opt-in — apps must explicitly
    /// call <c>app.UseWtmMaintenanceMode()</c>.
    /// </remarks>
    public class WtmMaintenanceModeOptions
    {
        /// <summary>
        /// Master switch. When <c>false</c> (default) the middleware is a
        /// zero-cost no-op. When <c>true</c> every non-allow-listed request
        /// receives <see cref="StatusCode"/>. Ignored if
        /// <see cref="IsEnabled"/> is set.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Dynamic override for <see cref="Enabled"/>. When set, the
        /// delegate is invoked on every request and its result wins over
        /// the static flag — this lets operators toggle maintenance mode
        /// via a feature flag, Redis key, admin controller, or
        /// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>
        /// snapshot without restarting the app.
        /// </summary>
        public Func<HttpContext, bool>? IsEnabled { get; set; }

        /// <summary>
        /// HTTP status code returned when maintenance mode is active.
        /// Default <c>503</c> (Service Unavailable). The RFC 7231 semantic
        /// "server is currently unable to handle the request due to a
        /// temporary overload or scheduled maintenance" — load balancers,
        /// browsers, and SDKs all treat 503 as transient/retryable, which
        /// is exactly the signal we want.
        /// </summary>
        public int StatusCode { get; set; } = StatusCodes.Status503ServiceUnavailable;

        /// <summary>
        /// <c>Retry-After</c> header value in seconds. <c>null</c> omits
        /// the header. Default 60 — conservative hint for automated
        /// retries and CDN edge caches.
        /// </summary>
        public int? RetryAfterSeconds { get; set; } = 60;

        /// <summary>
        /// Response body <c>detail</c>. Keep operator-friendly and
        /// free of PII / tenant specifics — this body is returned to
        /// anonymous callers.
        /// </summary>
        public string Message { get; set; } = "Service temporarily unavailable for scheduled maintenance.";

        /// <summary>
        /// Response body <c>title</c>. Default "Service Unavailable".
        /// </summary>
        public string Title { get; set; } = "Service Unavailable";

        /// <summary>
        /// Response <c>Content-Type</c>. Default
        /// <c>application/problem+json</c> to align with
        /// <see cref="WtmProblemDetailsExtension"/>. Apps that expose an
        /// HTML-first surface may switch to <c>text/html</c> and pair with
        /// <see cref="HtmlBodyFactory"/>.
        /// </summary>
        public string ContentType { get; set; } = "application/problem+json";

        /// <summary>
        /// Optional HTML body factory invoked when <see cref="ContentType"/>
        /// starts with <c>text/html</c>. Receives the resolved
        /// <see cref="HttpContext"/> and must return the full response body.
        /// When <c>null</c> and <see cref="ContentType"/> is HTML, the
        /// middleware emits a minimal inline HTML page.
        /// </summary>
        public Func<HttpContext, string>? HtmlBodyFactory { get; set; }

        /// <summary>
        /// Path prefixes that bypass maintenance mode. Matching is
        /// case-insensitive prefix check against <c>HttpContext.Request.Path</c>.
        /// Defaults keep the health-probe + admin plane reachable so ops can
        /// (a) keep k8s readiness probes green and (b) toggle maintenance
        /// mode off again once the outage clears.
        /// </summary>
        public IList<string> AllowedPathPrefixes { get; set; } = new List<string>
        {
            "/healthz",
            "/_framework",
            "/_js",
            "/_content",
            "/_admin",
        };

        /// <summary>
        /// Client IPs that bypass maintenance mode — typically the ops
        /// bastion / jump-host subnet so operators can continue to drive
        /// the app while external traffic is blocked. Matched against the
        /// string form of <see cref="HttpContextExtention.GetRemoteIpAddress"/>
        /// so reverse-proxy <c>X-Forwarded-For</c> is honoured the same way
        /// as everywhere else in the framework.
        /// </summary>
        public IList<string> AllowedClientIps { get; set; } = new List<string>();
    }
}
