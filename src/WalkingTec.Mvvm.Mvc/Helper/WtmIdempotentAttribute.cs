#nullable enable
using System;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Marks a controller or action as idempotency-key-aware. When the
    /// client sends the same <c>Idempotency-Key</c> header value within
    /// the configured window, <see cref="WtmIdempotencyMiddleware"/>
    /// replays the cached successful response without re-invoking the
    /// action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is an industry-standard pattern for retry-safe mutating
    /// endpoints (Stripe, PayPal, AWS services all expose an
    /// <c>Idempotency-Key</c> header). The typical scenario: a mobile
    /// client submits a payment / order over a flaky network, doesn't
    /// receive the response, retries — without idempotency the user is
    /// charged twice.
    /// </para>
    /// <para>
    /// Decoration:
    /// <code>
    /// [HttpPost("/api/orders")]
    /// [WtmIdempotent(WindowSeconds = 600)]
    /// public async Task&lt;IActionResult&gt; Create(OrderDto dto) { ... }
    /// </code>
    /// </para>
    /// <para>
    /// Requires <c>app.UseWtmIdempotency()</c> in the pipeline (before
    /// MVC). When the middleware is not registered the attribute is a
    /// no-op — there's no "fail closed" here because the protection is
    /// purely a performance/retry optimisation; the underlying action
    /// must still be written to tolerate duplicate submission.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class WtmIdempotentAttribute : Attribute
    {
        /// <summary>
        /// Window during which a repeated key replays the cached
        /// response. Default 300 s (5 minutes) — long enough to cover
        /// mobile-app retry storms, short enough that a user who
        /// genuinely changes their mind and re-submits after an hour
        /// isn't silently blocked. Set to 0 or negative to fall back to
        /// the middleware's <c>Options.DefaultWindowSeconds</c>.
        /// </summary>
        public int WindowSeconds { get; set; } = 300;

        /// <summary>
        /// Require the client to send an <c>Idempotency-Key</c> header —
        /// missing / blank key returns <c>400 Bad Request</c>. Default
        /// <c>false</c> (best-effort; keyless requests run normally).
        /// Opt in for endpoints where double-submit is genuinely
        /// dangerous and the client is known to be updated to send keys.
        /// </summary>
        public bool RequireKey { get; set; }
    }
}
