#nullable enable
using System;
using System.Globalization;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Per-endpoint rate limit — decorate a controller class or action
    /// method to enforce a stricter per-IP quota on top of the global
    /// limiter installed by <c>AddWtmRateLimiting</c>. Introduced by
    /// issue #828 to protect brute-force-sensitive endpoints (login,
    /// password reset, forgot password) without lowering the global
    /// ceiling for normal traffic.
    /// </summary>
    /// <remarks>
    /// At app startup, <c>AddWtmRateLimiting</c> scans loaded assemblies
    /// for this attribute and registers one named policy per unique
    /// <c>(permits, window, queue)</c> tuple. At action-model build time,
    /// an <c>IActionModelConvention</c> injects the corresponding ASP.NET
    /// Core <c>EnableRateLimitingAttribute</c> into each decorated
    /// action's metadata, so the built-in rate-limiter middleware wires
    /// the policy automatically.
    /// <para>
    /// The per-policy partition key is <c>{policyName}:{clientIp}</c>,
    /// so decorated actions enforce <b>per-IP-per-endpoint</b> quota
    /// independent of the global per-IP quota.
    /// </para>
    /// <para>
    /// Usage:
    /// <code>
    /// [WtmRateLimit(5, 60)]                 // 5 reqs per IP per 60 s
    /// [HttpPost("/Login/Login")]
    /// public IActionResult Login(LoginDTO vm) { ... }
    /// </code>
    /// </para>
    /// <para>
    /// Direct inheritance from <c>EnableRateLimitingAttribute</c> is not
    /// possible because the ASP.NET Core type is sealed — hence the
    /// convention-based composition described above.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class WtmRateLimitAttribute : Attribute
    {
        /// <summary>Max requests allowed in the window per IP.</summary>
        public int Permits { get; }

        /// <summary>Window duration in seconds.</summary>
        public int WindowSeconds { get; }

        /// <summary>
        /// Max queued requests waiting for a slot. Default 0 — rejects
        /// immediately once the limit is hit (simplest / lowest latency
        /// for brute-force protection; queuing doesn't help the attacker
        /// scenario anyway).
        /// </summary>
        public int QueueLimit { get; }

        public WtmRateLimitAttribute(int permits, int windowSeconds, int queueLimit = 0)
        {
            ValidateTuple(permits, windowSeconds, queueLimit);

            Permits = permits;
            WindowSeconds = windowSeconds;
            QueueLimit = queueLimit;
        }

        /// <summary>
        /// Shared validation for a <c>(permits, windowSeconds, queueLimit)</c>
        /// config tuple. Issue #759: extracted so the attribute constructor
        /// and <see cref="WtmRateLimitingOptions.RegisterPolicy"/> (explicit
        /// programmatic policy registration, no controller attribute
        /// required) enforce byte-identical guards and cannot drift apart.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="permits"/> is not positive, <paramref name="windowSeconds"/>
        /// is not positive, or <paramref name="queueLimit"/> is negative.
        /// </exception>
        internal static void ValidateTuple(int permits, int windowSeconds, int queueLimit)
        {
            if (permits <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(permits), permits,
                    "[WtmRateLimit] Permits must be > 0.");
            }
            if (windowSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSeconds), windowSeconds,
                    "[WtmRateLimit] WindowSeconds must be > 0.");
            }
            if (queueLimit < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(queueLimit), queueLimit,
                    "[WtmRateLimit] QueueLimit must be >= 0.");
            }
        }

        /// <summary>
        /// Deterministic policy name for a given config tuple. Exposed so
        /// <c>AddWtmRateLimiting</c> and the action-model convention
        /// agree on the name used to connect attribute → registered
        /// policy.
        /// </summary>
        public static string BuildPolicyName(int permits, int windowSeconds, int queueLimit) =>
            string.Create(CultureInfo.InvariantCulture, $"wtm_rl_{permits}_{windowSeconds}_{queueLimit}");

        /// <summary>
        /// Policy name for this attribute instance.
        /// </summary>
        public string PolicyName => BuildPolicyName(Permits, WindowSeconds, QueueLimit);
    }
}
