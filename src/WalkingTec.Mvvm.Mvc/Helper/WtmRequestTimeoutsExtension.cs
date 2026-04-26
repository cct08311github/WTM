#nullable enable
using System;
using Microsoft.AspNetCore.Builder;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for <see cref="WtmRequestTimeoutsMiddleware"/>.
    /// Opt-in — apps must explicitly call
    /// <c>app.UseWtmRequestTimeouts()</c>. Place early in the pipeline
    /// (immediately after <c>UseRouting</c>) so the deadline covers
    /// auth, model binding, and the action body — anywhere a runaway
    /// can park a worker.
    /// </summary>
    public static class WtmRequestTimeoutsExtension
    {
        /// <summary>
        /// Registers the middleware with default options (30 s default
        /// deadline, no path overrides, default exclusions).
        /// </summary>
        public static IApplicationBuilder UseWtmRequestTimeouts(this IApplicationBuilder app)
            => app.UseMiddleware<WtmRequestTimeoutsMiddleware>(new WtmRequestTimeoutsOptions());

        /// <summary>
        /// Registers the middleware, applying <paramref name="configure"/>
        /// to a fresh <see cref="WtmRequestTimeoutsOptions"/> instance
        /// before middleware construction.
        /// </summary>
        public static IApplicationBuilder UseWtmRequestTimeouts(
            this IApplicationBuilder app,
            Action<WtmRequestTimeoutsOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var opts = new WtmRequestTimeoutsOptions();
            configure(opts);
            return app.UseMiddleware<WtmRequestTimeoutsMiddleware>(opts);
        }
    }
}
