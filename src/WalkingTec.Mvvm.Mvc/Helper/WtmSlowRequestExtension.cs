#nullable enable
using System;
using Microsoft.AspNetCore.Builder;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for <see cref="WtmSlowRequestMiddleware"/> (#840).
    /// Opt-in — apps must explicitly call <c>app.UseWtmSlowRequestLogging()</c>.
    /// </summary>
    public static class WtmSlowRequestExtension
    {
        /// <summary>
        /// Registers the middleware with default options (threshold 1000 ms,
        /// Warning level, default path exclusions).
        /// </summary>
        public static IApplicationBuilder UseWtmSlowRequestLogging(this IApplicationBuilder app)
            => app.UseMiddleware<WtmSlowRequestMiddleware>(new WtmSlowRequestOptions());

        /// <summary>
        /// Registers the middleware with the supplied configuration callback
        /// applied to the default options.
        /// </summary>
        public static IApplicationBuilder UseWtmSlowRequestLogging(
            this IApplicationBuilder app,
            Action<WtmSlowRequestOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var opts = new WtmSlowRequestOptions();
            configure(opts);
            return app.UseMiddleware<WtmSlowRequestMiddleware>(opts);
        }
    }
}
