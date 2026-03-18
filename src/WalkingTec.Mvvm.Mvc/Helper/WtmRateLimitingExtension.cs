#nullable enable
using System;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

                limiterOptions.OnRejected = (context, cancellationToken) =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetService<ILoggerFactory>()?.CreateLogger("WalkingTec.Mvvm.Mvc.RateLimiting");
                    var clientIp = context.HttpContext.GetRemoteIpAddress();
                    var path = context.HttpContext.Request.Path.Value ?? "/";
                    logger?.LogWarning(
                        "Rate limit exceeded. ClientIp={ClientIp} Path={Path} Method={Method}",
                        clientIp, path, context.HttpContext.Request.Method);
                    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return ValueTask.CompletedTask;
                };

                limiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    httpContext =>
                    {
                        var clientIp = httpContext.GetRemoteIpAddress();
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
