#nullable enable
namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Options for <see cref="WtmCorrelationIdMiddleware"/>.
    /// Introduced by issue #830.
    /// </summary>
    public class WtmCorrelationIdOptions
    {
        /// <summary>
        /// Request header to read an inbound correlation ID from, and
        /// to write the echo value into on the response. Default
        /// <c>X-Correlation-Id</c>. Common alternatives: <c>X-Request-Id</c>
        /// (Heroku / Rails), <c>X-Trace-Id</c>.
        /// </summary>
        public string HeaderName { get; set; } = "X-Correlation-Id";

        /// <summary>
        /// Maximum accepted length of an inbound correlation ID. Longer
        /// values are treated as invalid and replaced with a freshly
        /// generated UUID. Default 128 — enough for UUIDs (32 hex),
        /// composite slugs, W3C Trace Context IDs, but prevents
        /// log-injection / memory abuse via arbitrarily long headers.
        /// </summary>
        public int MaxLength { get; set; } = 128;

        /// <summary>
        /// When <c>true</c> (default), a valid inbound header value is
        /// adopted as the request's correlation ID. Set to <c>false</c>
        /// in zero-trust environments where upstream trust is suspect —
        /// the middleware then always generates a fresh ID regardless
        /// of inbound header content.
        /// </summary>
        public bool AdoptInbound { get; set; } = true;

        /// <summary>
        /// When <c>true</c> (default), the chosen ID is echoed into the
        /// response header. Set to <c>false</c> if downstream consumers
        /// never want the ID exposed (rare — normally echoing is desired
        /// so callers can match server logs).
        /// </summary>
        public bool EmitOutbound { get; set; } = true;
    }
}
