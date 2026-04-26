#nullable enable
using System;
using Microsoft.AspNetCore.Builder;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for <see cref="WtmETagMiddleware"/>.
    /// Opt-in — apps must explicitly call <c>app.UseWtmETag()</c>.
    /// Place after <c>UseRouting</c> and (for consistency with the
    /// body-buffering pattern) before compression middleware so the
    /// hash is computed over the uncompressed representation.
    /// </summary>
    public static class WtmETagExtension
    {
        /// <summary>
        /// Registers the middleware with default options (GET + HEAD
        /// eligible, 2 MiB buffer cap, strong ETag, default path
        /// exclusions).
        /// </summary>
        public static IApplicationBuilder UseWtmETag(this IApplicationBuilder app)
            => app.UseMiddleware<WtmETagMiddleware>(new WtmETagOptions());

        /// <summary>
        /// Registers the middleware, applying <paramref name="configure"/>
        /// to a fresh <see cref="WtmETagOptions"/> instance before
        /// middleware construction.
        /// </summary>
        public static IApplicationBuilder UseWtmETag(
            this IApplicationBuilder app,
            Action<WtmETagOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var opts = new WtmETagOptions();
            configure(opts);
            return app.UseMiddleware<WtmETagMiddleware>(opts);
        }
    }
}
