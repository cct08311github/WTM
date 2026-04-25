#nullable enable
using System;
using Microsoft.AspNetCore.Builder;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for <see cref="WtmCspReportMiddleware"/>
    /// (#845). Opt-in — apps must explicitly call
    /// <c>app.UseWtmCspReport()</c>. Pair with
    /// <see cref="WtmCspOptions.ReportUri"/> set to the same path so
    /// the browser actually sends reports here.
    /// </summary>
    /// <remarks>
    /// Place this middleware <em>before</em>
    /// <see cref="WtmCspExtension.UseWtmContentSecurityPolicy"/> in the
    /// pipeline so the report endpoint itself does not have to live
    /// inside the CSP it serves (the browser would never POST
    /// <c>application/csp-report</c> through a CSP'd endpoint anyway,
    /// but ordering keeps the responsibility clear).
    /// </remarks>
    public static class WtmCspReportExtension
    {
        /// <summary>
        /// Registers the middleware with default options
        /// (<c>/_csp/report</c> path, 10 reports per IP per minute,
        /// 8 KiB body cap, log-to-Serilog dispatch).
        /// </summary>
        public static IApplicationBuilder UseWtmCspReport(this IApplicationBuilder app)
            => app.UseMiddleware<WtmCspReportMiddleware>(new WtmCspReportOptions());

        /// <summary>
        /// Registers the middleware, applying <paramref name="configure"/>
        /// to a fresh <see cref="WtmCspReportOptions"/> instance before
        /// middleware construction.
        /// </summary>
        public static IApplicationBuilder UseWtmCspReport(
            this IApplicationBuilder app,
            Action<WtmCspReportOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var opts = new WtmCspReportOptions();
            configure(opts);
            return app.UseMiddleware<WtmCspReportMiddleware>(opts);
        }
    }
}
