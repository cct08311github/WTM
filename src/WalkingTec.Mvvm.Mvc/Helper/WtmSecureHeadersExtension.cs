#nullable enable
using System;
using Microsoft.AspNetCore.Builder;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for <see cref="WtmSecureHeadersMiddleware"/>
    /// (#838). Opt-in — apps must explicitly call
    /// <c>app.UseWtmSecureHeaders()</c>.
    /// </summary>
    public static class WtmSecureHeadersExtension
    {
        /// <summary>
        /// Registers the middleware with OWASP-aligned defaults (see
        /// <see cref="WtmSecureHeadersOptions"/>). HSTS remains opt-in —
        /// call the <c>configure</c> overload to enable it.
        /// </summary>
        public static IApplicationBuilder UseWtmSecureHeaders(this IApplicationBuilder app)
            => app.UseMiddleware<WtmSecureHeadersMiddleware>(new WtmSecureHeadersOptions());

        /// <summary>
        /// Registers the middleware, applying <paramref name="configure"/>
        /// to the default option instance before middleware construction.
        /// </summary>
        public static IApplicationBuilder UseWtmSecureHeaders(
            this IApplicationBuilder app,
            Action<WtmSecureHeadersOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var opts = new WtmSecureHeadersOptions();
            configure(opts);
            return app.UseMiddleware<WtmSecureHeadersMiddleware>(opts);
        }
    }
}
