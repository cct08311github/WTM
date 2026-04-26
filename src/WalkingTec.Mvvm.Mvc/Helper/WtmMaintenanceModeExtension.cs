#nullable enable
using System;
using Microsoft.AspNetCore.Builder;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for <see cref="WtmMaintenanceModeMiddleware"/>.
    /// Opt-in — apps must explicitly call <c>app.UseWtmMaintenanceMode()</c>.
    /// Place the call early in the pipeline (after
    /// <c>UseRouting</c> but before auth / MVC) so the 503 short-circuits
    /// before expensive middleware runs.
    /// </summary>
    public static class WtmMaintenanceModeExtension
    {
        /// <summary>
        /// Registers the middleware with a default-constructed
        /// <see cref="WtmMaintenanceModeOptions"/>. With defaults,
        /// <see cref="WtmMaintenanceModeOptions.Enabled"/> is <c>false</c>
        /// so the middleware is a zero-cost no-op until toggled.
        /// </summary>
        public static IApplicationBuilder UseWtmMaintenanceMode(this IApplicationBuilder app)
            => app.UseMiddleware<WtmMaintenanceModeMiddleware>(new WtmMaintenanceModeOptions());

        /// <summary>
        /// Registers the middleware, applying <paramref name="configure"/>
        /// to a fresh <see cref="WtmMaintenanceModeOptions"/> instance
        /// before middleware construction.
        /// </summary>
        public static IApplicationBuilder UseWtmMaintenanceMode(
            this IApplicationBuilder app,
            Action<WtmMaintenanceModeOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var opts = new WtmMaintenanceModeOptions();
            configure(opts);
            return app.UseMiddleware<WtmMaintenanceModeMiddleware>(opts);
        }

        /// <summary>
        /// Registers the middleware with a pre-built options instance.
        /// Useful when the host resolves <see cref="WtmMaintenanceModeOptions"/>
        /// from configuration binding (<c>appsettings.json</c> →
        /// <see cref="WtmMaintenanceModeOptions"/>).
        /// </summary>
        public static IApplicationBuilder UseWtmMaintenanceMode(
            this IApplicationBuilder app,
            WtmMaintenanceModeOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return app.UseMiddleware<WtmMaintenanceModeMiddleware>(options);
        }
    }
}
