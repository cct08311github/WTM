#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>
        /// Issue #759: per-endpoint rate-limit policy tuples registered
        /// programmatically, independent of any <see cref="WtmRateLimitAttribute"/>
        /// on a controller. Populated via <see cref="RegisterPolicy"/>.
        /// </summary>
        internal HashSet<(int Permits, int WindowSeconds, int QueueLimit)> ExplicitPolicies { get; } = new();

        /// <summary>
        /// Registers the named ASP.NET Core rate-limiter policy
        /// <c>wtm_rl_{permits}_{windowSeconds}_{queueLimit}</c> even when
        /// no controller/action carries a matching <see cref="WtmRateLimitAttribute"/>.
        /// Pair this with <see cref="WtmRateLimitEndpointExtension.RequireWtmRateLimit"/>
        /// to attach the same policy to minimal-API / <c>MapGet</c>-style
        /// endpoints, which cannot carry the MVC-only attribute.
        /// </summary>
        /// <remarks>
        /// Without this, a minimal-API endpoint calling
        /// <c>RequireWtmRateLimit(permits, windowSeconds)</c> for a tuple
        /// that no controller attribute also declares would reference a
        /// policy name that was never registered with
        /// <c>AddRateLimiter</c> — ASP.NET Core's rate-limiting middleware
        /// then throws <see cref="InvalidOperationException"/> the first
        /// time the endpoint is hit. This incident (BMS <c>/live</c>
        /// / <c>/ready</c> minimal-API health endpoints) is why this API
        /// exists: it guarantees the policy exists up front, independent
        /// of any controller attribute.
        /// </remarks>
        /// <param name="permits">Max requests allowed in the window per IP. Must be &gt; 0.</param>
        /// <param name="windowSeconds">Window duration in seconds. Must be &gt; 0.</param>
        /// <param name="queueLimit">Max queued requests waiting for a slot. Must be &gt;= 0. Default 0.</param>
        /// <returns>This options instance, for fluent chaining.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="permits"/> is not positive, <paramref name="windowSeconds"/>
        /// is not positive, or <paramref name="queueLimit"/> is negative.
        /// </exception>
        public WtmRateLimitingOptions RegisterPolicy(int permits, int windowSeconds, int queueLimit = 0)
        {
            WtmRateLimitAttribute.ValidateTuple(permits, windowSeconds, queueLimit);
            ExplicitPolicies.Add((permits, windowSeconds, queueLimit));
            return this;
        }
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

            // Issue #759: fold in explicitly-registered policies (via
            // opt.RegisterPolicy(...)) so they get a named policy even
            // when no controller/action attribute carries the same tuple.
            // Both sides are HashSet<(int, int, int)>, so an identical
            // tuple registered both ways (attribute + RegisterPolicy)
            // collapses to a single entry — AddPolicy below only runs
            // once per tuple, avoiding ASP.NET Core's duplicate-name throw.
            perEndpointConfigs.UnionWith(options.ExplicitPolicies);

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
        /// <para>
        /// Issue #759: per-member attribute lookups are further guarded by
        /// <see cref="SafeGetRateLimitAttributes"/>. <c>GetCustomAttributes&lt;T&gt;</c>
        /// is lazily materialized — the underlying attribute-instantiation
        /// throw only surfaces when the returned sequence is enumerated, so
        /// a bare call site is not enough; every enumeration is wrapped
        /// per-member so one poisoned type/method (e.g. an MSTest
        /// test-adapter assembly whose attribute materialization throws
        /// <see cref="TypeLoadException"/>) can't abort the whole scan.
        /// </para>
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
                    CollectFromAttributes(SafeGetRateLimitAttributes(type), results);

                    MethodInfo[] methods;
                    try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                    catch { continue; }

                    foreach (var method in methods)
                    {
                        CollectFromAttributes(SafeGetRateLimitAttributes(method), results);
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// Issue #759: guarded wrapper around
        /// <c>MemberInfo.GetCustomAttributes&lt;WtmRateLimitAttribute&gt;(inherit: true)</c>.
        /// <c>GetCustomAttributes&lt;T&gt;</c> returns a lazily-materialized
        /// sequence — attribute instantiation (and any exception it throws)
        /// happens on enumeration, not on the call itself — so the
        /// <c>.ToArray()</c> that forces materialization must stay inside
        /// the <c>try</c>. Direct calls inside an MSTest host can otherwise
        /// hit test-adapter assemblies whose attribute materialization
        /// throws <see cref="TypeLoadException"/>, aborting the whole scan.
        /// </summary>
        /// <param name="member">The type or method to inspect.</param>
        /// <returns>
        /// The member's <see cref="WtmRateLimitAttribute"/> instances, or an
        /// empty array if attribute materialization failed for this member.
        /// </returns>
        private static WtmRateLimitAttribute[] SafeGetRateLimitAttributes(MemberInfo member)
        {
            try
            {
                return member.GetCustomAttributes<WtmRateLimitAttribute>(inherit: true).ToArray();
            }
            catch (TypeLoadException)
            {
                return Array.Empty<WtmRateLimitAttribute>();
            }
            catch (FileNotFoundException)
            {
                return Array.Empty<WtmRateLimitAttribute>();
            }
            catch (ReflectionTypeLoadException)
            {
                return Array.Empty<WtmRateLimitAttribute>();
            }
            catch
            {
                return Array.Empty<WtmRateLimitAttribute>();
            }
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
