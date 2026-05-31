#nullable enable
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for ASP.NET Core's built-in
    /// <see cref="ForwardedHeadersMiddleware"/> as a thin WTM-consistent wrapper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>When to use this:</b> if the app runs behind a reverse proxy
    /// (nginx, Kestrel behind an ALB / Cloudflare / k8s ingress, etc.) and you
    /// want <see cref="HttpContextExtention.GetRemoteIpAddress"/> to return the
    /// real client IP rather than the proxy's IP, call
    /// <c>services.AddWtmForwardedHeaders()</c> and
    /// <c>app.UseWtmForwardedHeaders()</c> early in the pipeline.
    /// </para>
    /// <para>
    /// <b>Security note:</b> always configure <c>KnownProxies</c> or
    /// <c>KnownNetworks</c> to restrict which peers are trusted to supply
    /// <c>X-Forwarded-For</c>.  Without this, any client can spoof the header
    /// and bypass IP-based controls.  The default
    /// <see cref="ForwardedHeadersOptions"/> only trusts loopback; adjust to
    /// match your actual proxy infrastructure.
    /// </para>
    /// <para>
    /// Once <c>UseWtmForwardedHeaders</c> is in the pipeline,
    /// <c>Connection.RemoteIpAddress</c> is rewritten to the validated client IP
    /// by the time any WTM middleware runs, so no other code change is needed.
    /// </para>
    /// <para>
    /// Issue #114: replaces the old raw-XFF-first behaviour that was
    /// unconditionally spoofable.
    /// </para>
    /// </remarks>
    public static class WtmForwardedHeadersExtension
    {
        /// <summary>
        /// Registers ASP.NET Core's <c>ForwardedHeadersMiddleware</c> services
        /// with WTM's default configuration (<c>ForwardedHeaders.XForwardedFor</c>).
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">
        /// Optional delegate to further configure <see cref="ForwardedHeadersOptions"/>.
        /// Use this to set <c>KnownProxies</c> / <c>KnownNetworks</c> for your
        /// infrastructure so that only headers from trusted proxies are honoured.
        /// </param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddWtmForwardedHeaders(
            this IServiceCollection services,
            Action<ForwardedHeadersOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.Configure<ForwardedHeadersOptions>(options =>
            {
                // Enable X-Forwarded-For processing only.
                // Callers can also enable X-Forwarded-Proto / X-Forwarded-Host
                // via the configure delegate if their proxy sets those.
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;

                // Apply caller overrides (KnownProxies, KnownNetworks, etc.).
                configure?.Invoke(options);
            });

            return services;
        }

        /// <summary>
        /// Adds ASP.NET Core's <c>ForwardedHeadersMiddleware</c> to the pipeline.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <returns>The application builder for chaining.</returns>
        /// <remarks>
        /// Place this call early in <c>Configure</c> / <c>app.Use…</c> ordering —
        /// before <c>UseRouting</c>, <c>UseAuthentication</c>, and any WTM
        /// middleware — so that <c>Connection.RemoteIpAddress</c> is already set
        /// to the real client IP when downstream handlers run.
        /// </remarks>
        public static IApplicationBuilder UseWtmForwardedHeaders(this IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);
            app.UseForwardedHeaders();
            return app;
        }
    }
}
