#nullable enable
using System;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace WalkingTec.Mvvm.Mvc
{
    public class WtmRateLimitingOptions
    {
        public int PermitLimit { get; set; } = 100;
        public int WindowSeconds { get; set; } = 60;
        public int QueueLimit { get; set; } = 0;
        public Action<RateLimiterOptions>? CustomConfig { get; set; }
    }

    public static class WtmRateLimitingExtension
    {
        public static IServiceCollection AddWtmRateLimiting(
            this IServiceCollection services,
            Action<WtmRateLimitingOptions>? configure = null)
        {
            var options = new WtmRateLimitingOptions();
            configure?.Invoke(options);

            services.AddRateLimiter(limiterOptions =>
            {
                limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                limiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    httpContext =>
                    {
                        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ =>
                            new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = options.PermitLimit,
                                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                                QueueLimit = options.QueueLimit
                            });
                    });

                options.CustomConfig?.Invoke(limiterOptions);
            });

            return services;
        }

        public static IApplicationBuilder UseWtmRateLimiting(this IApplicationBuilder app)
        {
            app.UseRateLimiter();
            return app;
        }
    }
}
