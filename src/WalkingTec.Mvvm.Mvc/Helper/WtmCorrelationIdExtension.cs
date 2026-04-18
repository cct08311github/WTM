#nullable enable
using System;
using Microsoft.AspNetCore.Builder;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for <see cref="WtmCorrelationIdMiddleware"/>.
    /// Opt-in — apps must explicitly call
    /// <c>app.UseWtmCorrelationId()</c> to enable correlation-ID
    /// preservation. Introduced by issue #830.
    /// </summary>
    public static class WtmCorrelationIdExtension
    {
        /// <summary>
        /// Register the middleware with default options
        /// (<c>X-Correlation-Id</c> header, 128 byte limit,
        /// adopt inbound, echo outbound).
        /// </summary>
        public static IApplicationBuilder UseWtmCorrelationId(this IApplicationBuilder app)
        {
            return app.UseMiddleware<WtmCorrelationIdMiddleware>(new WtmCorrelationIdOptions());
        }

        /// <summary>
        /// Register the middleware with a configured options callback.
        /// </summary>
        public static IApplicationBuilder UseWtmCorrelationId(
            this IApplicationBuilder app,
            Action<WtmCorrelationIdOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var options = new WtmCorrelationIdOptions();
            configure(options);
            return app.UseMiddleware<WtmCorrelationIdMiddleware>(options);
        }
    }
}
