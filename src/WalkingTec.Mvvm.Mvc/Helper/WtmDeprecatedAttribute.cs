#nullable enable
using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Marks an API endpoint (controller class or action method) as
    /// deprecated. Emits the IETF-standard <c>Deprecation: true</c>
    /// response header on every invocation, plus optional
    /// <c>Sunset</c> (RFC 8594) and <c>Link</c> (RFC 8288) headers so
    /// machine consumers can schedule a migration before the endpoint is
    /// removed. An <c>Information</c>-level log line is emitted on every
    /// hit (opt-out via <see cref="LogHit"/>) so operators can
    /// instrument "who's still calling the deprecated surface?" from the
    /// existing log pipeline without wiring bespoke telemetry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attribute implements <see cref="IAsyncResultFilter"/>, so
    /// ASP.NET Core MVC discovers it automatically when applied — no
    /// registration in <c>Program.cs</c> is required. Headers are
    /// attached via <see cref="HttpResponse.OnStarting(Func{Task})"/> to
    /// survive short-circuiting result writers. First-writer-wins: any
    /// header already set by upstream middleware or a reverse proxy is
    /// preserved as-is.
    /// </para>
    /// <para>
    /// Usage:
    /// <code>
    /// [WtmDeprecated(Message = "Use /api/v2/orders instead.",
    ///                Sunset = "2026-12-31",
    ///                Link = "&lt;/api/v2/orders&gt;; rel=\"successor-version\"")]
    /// [HttpGet("/api/v1/orders")]
    /// public IActionResult GetOrdersV1() { ... }
    /// </code>
    /// </para>
    /// <para>
    /// References:
    /// <list type="bullet">
    /// <item>draft-ietf-httpapi-deprecation-header — <c>Deprecation</c> header.</item>
    /// <item>RFC 8594 — <c>Sunset</c> HTTP header field.</item>
    /// <item>RFC 8288 — Web Linking (<c>Link</c> header).</item>
    /// </list>
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class WtmDeprecatedAttribute : Attribute, IAsyncResultFilter
    {
        private string? _cachedSunsetHeaderValue;
        private bool _sunsetParsed;
        private readonly object _sunsetLock = new();

        /// <summary>
        /// Human-readable deprecation message, emitted in the log line
        /// and available via reflection for OpenAPI doc-generators. Keep
        /// operator-friendly — don't leak tenant / customer specifics.
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// Sunset date — any format <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
        /// accepts (<c>yyyy-MM-dd</c>, ISO 8601, RFC 3339). Emitted as
        /// an RFC 1123 HTTP-date in the <c>Sunset</c> response header
        /// per RFC 8594. A value that fails to parse is silently dropped
        /// (no header emitted) and logged once at <c>Warning</c>
        /// level — a malformed attribute value should never crash an
        /// action.
        /// </summary>
        public string? Sunset { get; set; }

        /// <summary>
        /// Full <c>Link</c> header value (RFC 8288). Typical use is to
        /// point callers at the successor endpoint, e.g.
        /// <c>&lt;/api/v2/orders&gt;; rel="successor-version"</c>.
        /// Emitted verbatim — callers are responsible for correct
        /// link-format quoting.
        /// </summary>
        public string? Link { get; set; }

        /// <summary>
        /// Free-form marker for when / which version deprecated this
        /// endpoint. Not emitted as a header (there is no standardised
        /// HTTP field for this) — consumed by doc-generators and log
        /// lines only. Example: <c>"10.4.0"</c>.
        /// </summary>
        public string? Since { get; set; }

        /// <summary>
        /// Whether to emit an <c>Information</c>-level log entry on each
        /// invocation. Default <c>true</c> so the moment you annotate an
        /// endpoint, "who's still calling it?" becomes queryable via the
        /// normal log pipeline. Flip off for endpoints that remain
        /// high-traffic intentionally during a long grace period.
        /// </summary>
        public bool LogHit { get; set; } = true;

        public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            var http = context.HttpContext;

            http.Response.OnStarting(() =>
            {
                ApplyHeaders(http.Response);
                return Task.CompletedTask;
            });

            if (LogHit)
            {
                var logger = http.RequestServices
                    .GetService<ILoggerFactory>()
                    ?.CreateLogger("WalkingTec.Mvvm.Mvc.WtmDeprecated");
                logger?.LogInformation(
                    "DeprecatedEndpointHit Path={Path} Method={Method} Message={Message} Since={Since}",
                    LogSanitizer.Sanitize(http.Request.Path.Value ?? ""),
                    LogSanitizer.Sanitize(http.Request.Method),
                    LogSanitizer.Sanitize(Message ?? ""),
                    LogSanitizer.Sanitize(Since ?? ""));
            }

            return next();
        }

        private void ApplyHeaders(HttpResponse response)
        {
            var headers = response.Headers;

            if (!headers.ContainsKey("Deprecation"))
            {
                headers.Append("Deprecation", "true");
            }

            var sunsetValue = GetCachedSunsetHeaderValue(response);
            if (!string.IsNullOrEmpty(sunsetValue) && !headers.ContainsKey("Sunset"))
            {
                headers.Append("Sunset", sunsetValue);
            }

            if (!string.IsNullOrEmpty(Link))
            {
                headers.Append("Link", Link);
            }
        }

        private string? GetCachedSunsetHeaderValue(HttpResponse response)
        {
            if (_sunsetParsed) { return _cachedSunsetHeaderValue; }
            lock (_sunsetLock)
            {
                if (_sunsetParsed) { return _cachedSunsetHeaderValue; }
                _sunsetParsed = true;

                if (string.IsNullOrWhiteSpace(Sunset))
                {
                    _cachedSunsetHeaderValue = null;
                    return null;
                }

                if (DateTimeOffset.TryParse(
                        Sunset,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var parsed))
                {
                    _cachedSunsetHeaderValue = parsed.ToUniversalTime()
                        .ToString("R", CultureInfo.InvariantCulture);
                }
                else
                {
                    _cachedSunsetHeaderValue = null;

                    // Warn once — misconfigured Sunset should be visible
                    // but must not spam every request.
                    var logger = response.HttpContext.RequestServices
                        .GetService<ILoggerFactory>()
                        ?.CreateLogger("WalkingTec.Mvvm.Mvc.WtmDeprecated");
                    logger?.LogWarning(
                        "WtmDeprecated: Sunset value {Sunset} could not be parsed as a date; header omitted",
                        LogSanitizer.Sanitize(Sunset));
                }

                return _cachedSunsetHeaderValue;
            }
        }
    }
}
