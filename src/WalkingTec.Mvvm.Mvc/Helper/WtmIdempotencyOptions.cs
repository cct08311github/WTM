#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Configuration for <see cref="WtmIdempotencyMiddleware"/>. Tunes
    /// header name, TTL, key size cap, and which HTTP methods are
    /// eligible for caching.
    /// </summary>
    public class WtmIdempotencyOptions
    {
        /// <summary>
        /// Request header that carries the idempotency key. Default
        /// <c>Idempotency-Key</c> (IETF draft-ietf-httpapi-idempotency-key-header
        /// and the de-facto convention used by Stripe / PayPal / AWS).
        /// </summary>
        public string HeaderName { get; set; } = "Idempotency-Key";

        /// <summary>
        /// Default replay window in seconds, used when the attribute
        /// specifies <c>WindowSeconds &lt;= 0</c>. Default 300 s.
        /// </summary>
        public int DefaultWindowSeconds { get; set; } = 300;

        /// <summary>
        /// Max idempotency-key length in characters. Defaults to 128 —
        /// enough for any UUID / ULID / base64-encoded hash while
        /// preventing pathologically large keys from ballooning the
        /// memory cache. Keys longer than this are treated as malformed
        /// and return <c>400 Bad Request</c>.
        /// </summary>
        public int MaxKeyLength { get; set; } = 128;

        /// <summary>
        /// HTTP methods eligible for idempotency caching. Default
        /// <c>POST</c>, <c>PUT</c>, <c>PATCH</c>, <c>DELETE</c> — the
        /// state-changing verbs where retry safety matters. <c>GET</c>
        /// / <c>HEAD</c> are intentionally excluded: they're already
        /// safe + idempotent per RFC 9110, and caching would mask a
        /// bug where the same URI now returns different data.
        /// </summary>
        public ISet<string> EligibleMethods { get; set; } = new HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase)
        {
            "POST", "PUT", "PATCH", "DELETE",
        };

        /// <summary>
        /// Max buffered response body size in bytes before caching is
        /// skipped (response is still returned to the original client
        /// — just not cached). Default 1 MiB. Prevents a rogue large
        /// response from bloating the process cache.
        /// </summary>
        public int MaxCachedBodyBytes { get; set; } = 1024 * 1024;
    }
}
