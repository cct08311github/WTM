#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for framework-owned health checks (#836).
    /// </summary>
    public static class WtmHealthCheckExtensions
    {
        /// <summary>
        /// Adds <see cref="WtmDataContextHealthCheck"/> under
        /// <paramref name="name"/> (default <c>datacontext</c>) with a
        /// bounded <paramref name="timeout"/> (default 2 s) and
        /// <paramref name="tags"/> (default <c>ready</c>).
        /// </summary>
        /// <remarks>
        /// Resolves the app's real DataContext via the optional, DI-injected
        /// <see cref="WTMContext"/> (<c>WTMContext.CreateDC()</c>) — the same
        /// connection-string/tenant-aware factory every other part of WTM uses — falling
        /// back to a directly DI-registered <see cref="IDataContext"/> for hosts/tests that
        /// register one without registering <see cref="WTMContext"/> (fix: #741, residual of
        /// #727 — a bare DI-injected <see cref="IDataContext"/> always resolves WTM's
        /// <c>NullContext</c> placeholder in real deployments and silently skips the probe).
        /// Apps should call <c>AddWtmContext</c> before calling this so <see cref="WTMContext"/>
        /// is registered; without it the check still activates but only ever probes the
        /// placeholder (reports "Healthy (skipped)").
        /// </remarks>
        public static IHealthChecksBuilder AddWtmDataContextCheck(
            this IHealthChecksBuilder builder,
            string name = "datacontext",
            TimeSpan? timeout = null,
            IEnumerable<string>? tags = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(2);
            var effectiveTags = tags ?? new[] { "ready" };

            return builder.AddTypeActivatedCheck<WtmDataContextHealthCheck>(
                name,
                failureStatus: HealthStatus.Unhealthy,
                tags: effectiveTags,
                args: new object[] { effectiveTimeout });
        }
    }
}
