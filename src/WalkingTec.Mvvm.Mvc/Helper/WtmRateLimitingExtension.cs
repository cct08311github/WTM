#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

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

            // Issue #828: scan loaded assemblies for [WtmRateLimit] on
            // controllers / actions; collect unique (permits, window, queue)
            // tuples. Registered lazily during AddRateLimiter() below so each
            // unique config gets a single policy regardless of how many
            // actions wear the attribute.
            var perEndpointConfigs = ScanWtmRateLimitAttributes();

            // Issue #828: register the MVC convention that translates
            // WtmRateLimitAttribute → EnableRateLimitingAttribute in action
            // metadata so the built-in rate-limiter middleware can match
            // endpoints to the named policies registered below.
            services.Configure<MvcOptions>(opt => opt.Conventions.Add(new WtmRateLimitConvention()));

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
                        LogSanitizer.Sanitize(clientIp), LogSanitizer.Sanitize(path), LogSanitizer.Sanitize(context.HttpContext.Request.Method));
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

                // Issue #828: register one named policy per unique per-endpoint
                // rate limit config discovered in the assembly scan above.
                // [WtmRateLimit(...)] derives the same policy name, so the
                // rate-limiter middleware matches attribute → policy by name.
                foreach (var (permits, windowSeconds, queueLimit) in perEndpointConfigs)
                {
                    var policyName = WtmRateLimitAttribute.BuildPolicyName(permits, windowSeconds, queueLimit);
                    var capturedPermits = permits;
                    var capturedWindow = windowSeconds;
                    var capturedQueue = queueLimit;

                    limiterOptions.AddPolicy<string>(policyName, httpContext =>
                    {
                        var clientIp = httpContext.GetRemoteIpAddress();
                        // Partition by policy + IP — separate bucket per IP
                        // per endpoint so brute-force on /Login doesn't
                        // exhaust quota from /ResetPassword, etc.
                        return RateLimitPartition.GetFixedWindowLimiter(
                            $"{policyName}:{clientIp}",
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = capturedPermits,
                                Window = TimeSpan.FromSeconds(capturedWindow),
                                QueueLimit = capturedQueue,
                            });
                    });
                }

                options.CustomConfig?.Invoke(limiterOptions);
            });

            return services;
        }

        public static IApplicationBuilder UseWtmRateLimiting(this IApplicationBuilder app)
        {
            app.UseRateLimiter();
            return app;
        }

        /// <summary>
        /// Issue #828: scan loaded assemblies for <see cref="WtmRateLimitAttribute"/>
        /// placements on classes or methods. Returns distinct
        /// <c>(permits, windowSeconds, queueLimit)</c> tuples.
        /// </summary>
        /// <remarks>
        /// Uses the exception-safe reflection pattern documented in the
        /// <c>dotnet-assembly-scan-partial-load</c> WTM skill:
        /// <see cref="Assembly.GetTypes"/> may throw
        /// <see cref="ReflectionTypeLoadException"/> when dependent assemblies
        /// fail to load; we fall back to <c>ex.Types</c> (partial list) so
        /// one problematic assembly doesn't blow up the whole scan.
        /// </remarks>
        public static HashSet<(int permits, int window, int queue)> ScanWtmRateLimitAttributes()
        {
            var results = new HashSet<(int, int, int)>();
            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch
            {
                return results;
            }

            foreach (var asm in assemblies)
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    CollectFromAttributes(type.GetCustomAttributes<WtmRateLimitAttribute>(inherit: true), results);

                    MethodInfo[] methods;
                    try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                    catch { continue; }

                    foreach (var method in methods)
                    {
                        CollectFromAttributes(method.GetCustomAttributes<WtmRateLimitAttribute>(inherit: true), results);
                    }
                }
            }
            return results;
        }

        private static void CollectFromAttributes(
            IEnumerable<WtmRateLimitAttribute> attrs,
            HashSet<(int, int, int)> target)
        {
            foreach (var attr in attrs)
            {
                target.Add((attr.Permits, attr.WindowSeconds, attr.QueueLimit));
            }
        }
    }
}
