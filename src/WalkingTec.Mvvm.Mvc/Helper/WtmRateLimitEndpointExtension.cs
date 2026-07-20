#nullable enable
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Issue #759: attaches a WTM-style rate-limit policy to minimal-API /
    /// endpoint-routing builders (<c>MapGet</c>, <c>MapPost</c>, ...), which
    /// cannot carry the MVC-only <see cref="WtmRateLimitAttribute"/> (that
    /// attribute is wired via an <c>IActionModelConvention</c> that only
    /// runs for MVC action models — see <see cref="WtmRateLimitConvention"/>).
    /// </summary>
    public static class WtmRateLimitEndpointExtension
    {
        /// <summary>
        /// Attaches the ASP.NET Core rate-limiter policy named
        /// <c>wtm_rl_{permits}_{windowSeconds}_{queueLimit}</c> — the same
        /// deterministic name <see cref="WtmRateLimitAttribute"/> computes —
        /// to the endpoint(s) produced by <paramref name="builder"/>.
        /// </summary>
        /// <remarks>
        /// <b>This call attaches endpoint metadata only — it does not
        /// register the policy.</b> The <c>(permits, windowSeconds,
        /// queueLimit)</c> tuple must <i>also</i> be registered, either via
        /// <see cref="WtmRateLimitingOptions.RegisterPolicy"/> (passed to
        /// <c>AddWtmRateLimiting</c>) or by an existing
        /// <see cref="WtmRateLimitAttribute"/> somewhere in the scanned
        /// assemblies that carries the identical tuple.
        /// <para>
        /// If neither registers the policy, ASP.NET Core's rate-limiting
        /// middleware throws <see cref="InvalidOperationException"/> the
        /// first time a request reaches the endpoint — loudly, at request
        /// time, not silently. This method deliberately does <b>not</b>
        /// auto-register the policy via static state or side effects; call
        /// <c>opt.RegisterPolicy(permits, windowSeconds, queueLimit)</c>
        /// explicitly inside your <c>AddWtmRateLimiting</c> configuration
        /// callback.
        /// </para>
        /// <para>
        /// Usage:
        /// <code>
        /// services.AddWtmRateLimiting(opt =&gt; opt.RegisterPolicy(2, 30));
        /// // ...
        /// app.MapGet("/live", () =&gt; Results.Ok()).RequireWtmRateLimit(2, 30);
        /// </code>
        /// </para>
        /// </remarks>
        /// <typeparam name="TBuilder">The endpoint convention builder type — matches the signature style of ASP.NET Core's own <c>RequireRateLimiting</c>.</typeparam>
        /// <param name="builder">The endpoint convention builder (e.g. the result of <c>MapGet</c>).</param>
        /// <param name="permits">Max requests allowed in the window per IP. Must be &gt; 0.</param>
        /// <param name="windowSeconds">Window duration in seconds. Must be &gt; 0.</param>
        /// <param name="queueLimit">Max queued requests waiting for a slot. Must be &gt;= 0. Default 0.</param>
        /// <returns><paramref name="builder"/>, for fluent chaining.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="permits"/> is not positive, <paramref name="windowSeconds"/>
        /// is not positive, or <paramref name="queueLimit"/> is negative.
        /// </exception>
        public static TBuilder RequireWtmRateLimit<TBuilder>(
            this TBuilder builder,
            int permits,
            int windowSeconds,
            int queueLimit = 0)
            where TBuilder : IEndpointConventionBuilder
        {
            WtmRateLimitAttribute.ValidateTuple(permits, windowSeconds, queueLimit);
            var name = WtmRateLimitAttribute.BuildPolicyName(permits, windowSeconds, queueLimit);
            return builder.RequireRateLimiting(name);
        }
    }
}
