#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Registration helpers for
    /// <see cref="RefreshTokenRetentionService"/>. Opt-in — apps must
    /// explicitly call <c>services.AddWtmRefreshTokenRetention()</c> to
    /// enable scheduled deletion of old <c>FrameworkRefreshTokens</c> rows.
    /// Introduced by issue #757.
    /// </summary>
    public static class RefreshTokenRetentionExtension
    {
        /// <summary>
        /// Register the background retention service with default options
        /// (ExpiredDays=30, RevokedDays=30, runs at local 04:00, batched
        /// 5000 rows per DELETE). See
        /// <see cref="RefreshTokenRetentionOptions"/> for per-setting details.
        /// </summary>
        public static IServiceCollection AddWtmRefreshTokenRetention(
            this IServiceCollection services,
            Action<RefreshTokenRetentionOptions>? configure = null)
        {
            services.AddOptions<RefreshTokenRetentionOptions>();
            if (configure != null)
            {
                services.Configure(configure);
            }
            services.AddHostedService<RefreshTokenRetentionService>();
            return services;
        }
    }
}
