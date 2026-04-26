#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Writes the standard hardening HTTP response headers on every
    /// request — <c>X-Content-Type-Options</c>, <c>X-Frame-Options</c>,
    /// <c>Referrer-Policy</c>, <c>Permissions-Policy</c>, and optionally
    /// <c>Strict-Transport-Security</c>. CSP is a sibling middleware
    /// (<see cref="WtmCspMiddleware"/>); this one handles everything else
    /// in the OWASP Secure Headers bundle. Introduced by issue #838.
    /// </summary>
    /// <remarks>
    /// Each header is appended via <see cref="HttpResponse.OnStarting(Func{Task})"/>
    /// so it lands on every response regardless of which downstream
    /// middleware writes the body. First-writer-wins: any header already
    /// set by an upstream reverse proxy or by another piece of middleware
    /// is preserved as-is.
    /// <para>
    /// HSTS is only emitted when the incoming request is HTTPS — sending
    /// it over plain HTTP is ignored by browsers, but the middleware
    /// short-circuits anyway to keep the response minimal.
    /// </para>
    /// </remarks>
    public class WtmSecureHeadersMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly WtmSecureHeadersOptions _options;
        private readonly string? _hstsHeaderValue;

        public WtmSecureHeadersMiddleware(RequestDelegate next, WtmSecureHeadersOptions options)
        {
            ArgumentNullException.ThrowIfNull(next);
            ArgumentNullException.ThrowIfNull(options);

            _next = next;
            _options = options;
            _hstsHeaderValue = options.HstsEnabled ? BuildHstsValue(options) : null;
        }

        public Task InvokeAsync(HttpContext context)
        {
            var options = _options;
            var hstsValue = _hstsHeaderValue;

            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                var overwrite = options.Overwrite;

                SetHeader(headers, "X-Content-Type-Options", options.XContentTypeOptions, overwrite);
                SetHeader(headers, "X-Frame-Options", options.XFrameOptions, overwrite);
                SetHeader(headers, "Referrer-Policy", options.ReferrerPolicy, overwrite);
                SetHeader(headers, "Permissions-Policy", options.PermissionsPolicy, overwrite);

                if (hstsValue != null && context.Request.IsHttps)
                {
                    SetHeader(headers, "Strict-Transport-Security", hstsValue, overwrite);
                }

                return Task.CompletedTask;
            });

            return _next(context);
        }

        /// <summary>
        /// Writes <paramref name="value"/> to <paramref name="name"/> in
        /// <paramref name="headers"/>, choosing first-writer-wins
        /// (default, <paramref name="overwrite"/> = false) or
        /// always-overwrite (defense-in-depth, #843). Whitespace /
        /// null values are no-ops in either mode so toggling
        /// <see cref="WtmSecureHeadersOptions.Overwrite"/> never
        /// emits a header the caller explicitly disabled by setting it
        /// to null. Public for unit-test determinism.
        /// </summary>
        public static void SetHeader(IHeaderDictionary headers, string name, string? value, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(value)) { return; }
            if (overwrite)
            {
                headers[name] = value;
                return;
            }
            if (headers.ContainsKey(name)) { return; }
            headers.Append(name, value);
        }

        /// <summary>
        /// Assembles the HSTS header value:
        /// <c>max-age=N[; includeSubDomains][; preload]</c>. Public for
        /// testability. Guarantees <c>max-age</c> is non-negative.
        /// </summary>
        public static string BuildHstsValue(WtmSecureHeadersOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var sb = new StringBuilder();
            var maxAge = options.HstsMaxAgeSeconds < 0 ? 0 : options.HstsMaxAgeSeconds;
            sb.Append("max-age=").Append(maxAge);
            if (options.HstsIncludeSubDomains) { sb.Append("; includeSubDomains"); }
            if (options.HstsIncludePreload) { sb.Append("; preload"); }
            return sb.ToString();
        }
    }
}
