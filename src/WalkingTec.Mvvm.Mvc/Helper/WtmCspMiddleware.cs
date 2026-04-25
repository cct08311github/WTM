#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Sets a Content-Security-Policy response header on every request.
    /// Introduced in issue #789 Phase 3D — opt-in via
    /// <see cref="WtmCspExtension.UseWtmContentSecurityPolicy"/>.
    /// </summary>
    /// <remarks>
    /// The header value is assembled once in the constructor and reused per
    /// request. Uses <see cref="HttpResponse.OnStarting(Func{Task})"/> so the
    /// header is appended just before the response is flushed, regardless of
    /// which downstream middleware writes the body.
    ///
    /// First-writer-wins: if another middleware (or a reverse proxy upstream)
    /// has already set the header, this middleware does not overwrite it.
    /// That lets apps layer a stricter per-endpoint policy on top of the
    /// framework default.
    ///
    /// Mode resolution (#846): <see cref="WtmCspOptions.Mode"/> wins when
    /// non-default; the legacy <see cref="WtmCspOptions.ReportOnly"/> bool
    /// is consulted only when <c>Mode == Enforce</c> (the default) so
    /// callers that haven't migrated to the enum keep their previous
    /// ReportOnly toggle semantics. <see cref="WtmCspMode.Disabled"/>
    /// turns the middleware into a zero-cost pass-through.
    /// </remarks>
    public class WtmCspMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly string? _headerName;   // null => Disabled
        private readonly string _headerValue;

        public WtmCspMiddleware(RequestDelegate next, WtmCspOptions options)
        {
            ArgumentNullException.ThrowIfNull(next);
            ArgumentNullException.ThrowIfNull(options);

            _next = next;
            _headerName = ResolveHeaderName(options);
            _headerValue = BuildHeaderValue(options);
        }

        public Task InvokeAsync(HttpContext context)
        {
            var headerName = _headerName;
            if (headerName == null)
            {
                // Mode == Disabled: skip OnStarting registration entirely.
                return _next(context);
            }
            var headerValue = _headerValue;
            context.Response.OnStarting(() =>
            {
                if (!context.Response.Headers.ContainsKey(headerName))
                {
                    context.Response.Headers.Append(headerName, headerValue);
                }
                return Task.CompletedTask;
            });
            return _next(context);
        }

        /// <summary>
        /// Pick the header name (or null = no-op) per <see cref="WtmCspOptions.Mode"/>,
        /// falling back to the legacy <see cref="WtmCspOptions.ReportOnly"/> bool when
        /// <c>Mode</c> is left at its <see cref="WtmCspMode.Enforce"/> default. Public
        /// for unit-test determinism.
        /// </summary>
        public static string? ResolveHeaderName(WtmCspOptions o)
        {
            ArgumentNullException.ThrowIfNull(o);
            switch (o.Mode)
            {
                case WtmCspMode.Disabled:
                    return null;
                case WtmCspMode.ReportOnly:
                    return "Content-Security-Policy-Report-Only";
                case WtmCspMode.Enforce:
#pragma warning disable CS0618 // legacy ReportOnly bool retained for back-compat
                    return o.ReportOnly
                        ? "Content-Security-Policy-Report-Only"
                        : "Content-Security-Policy";
#pragma warning restore CS0618
                default:
                    return "Content-Security-Policy";
            }
        }

        public static string BuildHeaderValue(WtmCspOptions o)
        {
            var sb = new StringBuilder(256);
            AppendDirective(sb, "default-src", o.DefaultSrc);
            AppendDirective(sb, "script-src", o.ScriptSrc);
            AppendDirective(sb, "style-src", o.StyleSrc);
            AppendDirective(sb, "img-src", o.ImgSrc);
            AppendDirective(sb, "font-src", o.FontSrc);
            AppendDirective(sb, "connect-src", o.ConnectSrc);
            AppendDirective(sb, "frame-src", o.FrameSrc);
            // frame-ancestors (#844) — modern clickjacking defense, distinct from
            // frame-src (which is outward). Emitted only when non-null/empty so
            // apps can opt out by setting null without re-instantiating Options.
            if (!string.IsNullOrWhiteSpace(o.FrameAncestors))
            {
                AppendDirective(sb, "frame-ancestors", o.FrameAncestors);
            }
            AppendDirective(sb, "object-src", o.ObjectSrc);
            AppendDirective(sb, "base-uri", o.BaseUri);
            AppendDirective(sb, "form-action", o.FormAction);
            if (!string.IsNullOrWhiteSpace(o.ReportUri))
            {
                AppendDirective(sb, "report-uri", o.ReportUri);
            }
            return sb.ToString();
        }

        private static void AppendDirective(StringBuilder sb, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return; }
            if (sb.Length > 0) { sb.Append("; "); }
            sb.Append(name).Append(' ').Append(value);
        }
    }
}
