#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for
    /// <see cref="ActionLogRetentionService"/>. Opt-in — apps must
    /// explicitly call <c>services.AddWtmActionLogRetention()</c> to
    /// enable scheduled deletion of old <c>ActionLogs</c> rows.
    /// Introduced by issue #832.
    /// </summary>
    public static class ActionLogRetentionExtension
    {
        /// <summary>
        /// Register the background retention service with default options
        /// (Normal=90d, Exception=365d, Debug=30d, Job=90d, runs at local
        /// 03:00, batched 5000 rows per DELETE). See
        /// <see cref="ActionLogRetentionOptions"/> for per-setting details.
        /// </summary>
        public static IServiceCollection AddWtmActionLogRetention(
            this IServiceCollection services,
            Action<ActionLogRetentionOptions>? configure = null)
        {
            services.AddOptions<ActionLogRetentionOptions>();
            if (configure != null)
            {
                services.Configure(configure);
            }
            services.AddHostedService<ActionLogRetentionService>();
            return services;
        }
    }
}
