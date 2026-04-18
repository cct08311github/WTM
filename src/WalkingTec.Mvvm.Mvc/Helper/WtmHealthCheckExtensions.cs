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
        /// Resolves <see cref="IDataContext"/> from DI. Apps must have
        /// registered one (typically via <c>AddWtmContext</c>) before
        /// calling this — otherwise the check activates but fails with a
        /// DI resolution error on first probe.
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
