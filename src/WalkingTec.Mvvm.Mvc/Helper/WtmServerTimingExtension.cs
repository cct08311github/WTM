#nullable enable
using System;
using Microsoft.AspNetCore.Builder;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for <see cref="WtmServerTimingMiddleware"/>.
    /// Opt-in — apps must explicitly call <c>app.UseWtmServerTiming()</c>.
    /// Place the call as early as practical in the pipeline (right after
    /// <c>UseRouting</c>) so the stopwatch captures auth / MVC / DB cost.
    /// </summary>
    public static class WtmServerTimingExtension
    {
        /// <summary>
        /// Registers the middleware with default options (metric name
        /// <c>"app"</c>, zero threshold, default path exclusions).
        /// </summary>
        public static IApplicationBuilder UseWtmServerTiming(this IApplicationBuilder app)
            => app.UseMiddleware<WtmServerTimingMiddleware>(new WtmServerTimingOptions());

        /// <summary>
        /// Registers the middleware, applying <paramref name="configure"/>
        /// to a fresh <see cref="WtmServerTimingOptions"/> instance before
        /// middleware construction.
        /// </summary>
        public static IApplicationBuilder UseWtmServerTiming(
            this IApplicationBuilder app,
            Action<WtmServerTimingOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var opts = new WtmServerTimingOptions();
            configure(opts);
            return app.UseMiddleware<WtmServerTimingMiddleware>(opts);
        }
    }
}
