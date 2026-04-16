#nullable enable
using System;
using Microsoft.AspNetCore.Builder;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for <see cref="WtmCspMiddleware"/>. Opt-in -
    /// apps must explicitly call <c>app.UseWtmContentSecurityPolicy()</c>
    /// for the header to be emitted.
    /// </summary>
    /// <remarks>
    /// See issue #789 for the full eval-elimination effort that makes
    /// enforcing <c>script-src</c> without <c>'unsafe-eval'</c> safe for
    /// framework-owned code. Apps that still call eval() in their own JS
    /// must migrate or widen the policy via
    /// <see cref="WtmCspOptions.ScriptSrc"/> before enabling this middleware.
    /// </remarks>
    public static class WtmCspExtension
    {
        /// <summary>
        /// Registers the CSP middleware with the default policy
        /// (<c>default-src 'self'; script-src 'self' 'unsafe-inline'; ...</c>),
        /// optionally customized via the configure callback.
        /// </summary>
        public static IApplicationBuilder UseWtmContentSecurityPolicy(
            this IApplicationBuilder app,
            Action<WtmCspOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(app);
            var options = new WtmCspOptions();
            configure?.Invoke(options);
            return app.UseMiddleware<WtmCspMiddleware>(options);
        }
    }
}
