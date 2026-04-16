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
    /// </remarks>
    public class WtmCspMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly string _headerName;
        private readonly string _headerValue;

        public WtmCspMiddleware(RequestDelegate next, WtmCspOptions options)
        {
            ArgumentNullException.ThrowIfNull(next);
            ArgumentNullException.ThrowIfNull(options);

            _next = next;
            _headerName = options.ReportOnly
                ? "Content-Security-Policy-Report-Only"
                : "Content-Security-Policy";
            _headerValue = BuildHeaderValue(options);
        }

        public Task InvokeAsync(HttpContext context)
        {
            var headerName = _headerName;
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
