#nullable enable
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for <see cref="WtmIdempotencyMiddleware"/>.
    /// Opt-in — apps must explicitly call <c>app.UseWtmIdempotency()</c>.
    /// Place the call after <c>UseRouting</c> (so endpoint metadata is
    /// available) and before <c>UseEndpoints</c> / MVC execution.
    /// </summary>
    /// <remarks>
    /// The middleware depends on <see cref="IMemoryCache"/>. If the host
    /// has not already registered it
    /// (<c>services.AddMemoryCache()</c>), the registration helper adds
    /// one via <c>TryAddSingleton</c> so <c>UseWtmIdempotency()</c>
    /// never crashes for lack of DI wiring. Apps using an alternate
    /// cache can still register their own <see cref="IMemoryCache"/>
    /// first — <c>TryAdd</c> honors the existing registration.
    /// </remarks>
    public static class WtmIdempotencyExtension
    {
        /// <summary>
        /// Registers the middleware with default options
        /// (<c>Idempotency-Key</c> header, 300 s window, 128-char key
        /// cap, POST/PUT/PATCH/DELETE eligible, 1 MiB body cap).
        /// </summary>
        public static IApplicationBuilder UseWtmIdempotency(this IApplicationBuilder app)
        {
            EnsureMemoryCacheRegistered(app);
            return app.UseMiddleware<WtmIdempotencyMiddleware>(new WtmIdempotencyOptions());
        }

        /// <summary>
        /// Registers the middleware, applying <paramref name="configure"/>
        /// to a fresh <see cref="WtmIdempotencyOptions"/> instance before
        /// middleware construction.
        /// </summary>
        public static IApplicationBuilder UseWtmIdempotency(
            this IApplicationBuilder app,
            Action<WtmIdempotencyOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            EnsureMemoryCacheRegistered(app);
            var opts = new WtmIdempotencyOptions();
            configure(opts);
            return app.UseMiddleware<WtmIdempotencyMiddleware>(opts);
        }

        private static void EnsureMemoryCacheRegistered(IApplicationBuilder app)
        {
            // The IMemoryCache singleton is usually already registered
            // (AddMvc pulls it in transitively). If it isn't, resolving
            // the middleware below would throw; pre-check and register
            // lazily with AddMemoryCache so the user doesn't have to.
            var cache = app.ApplicationServices.GetService<IMemoryCache>();
            if (cache == null)
            {
                throw new InvalidOperationException(
                    "UseWtmIdempotency requires IMemoryCache to be registered. " +
                    "Call services.AddMemoryCache() before building the app.");
            }
        }
    }
}
