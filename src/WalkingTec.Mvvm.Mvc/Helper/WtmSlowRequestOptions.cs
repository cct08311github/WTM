#nullable enable
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Configuration for <see cref="WtmSlowRequestMiddleware"/> (#840).
    /// </summary>
    public class WtmSlowRequestOptions
    {
        /// <summary>
        /// Threshold in milliseconds. Requests taking longer than this
        /// emit a structured <see cref="LogLevel"/>-level log entry.
        /// Default 1000 ms.
        /// </summary>
        public int ThresholdMs { get; set; } = 1000;

        /// <summary>
        /// Log level used when a request exceeds the threshold.
        /// Default <see cref="LogLevel.Warning"/>.
        /// </summary>
        public LogLevel LogLevel { get; set; } = LogLevel.Warning;

        /// <summary>
        /// Whether to include the client IP address in the log entry.
        /// Default <c>true</c>. Uses the existing
        /// <see cref="Core.HttpContextExtensions"/> remote-IP helper so
        /// reverse-proxy <c>X-Forwarded-For</c> is resolved the same way
        /// as the rest of the framework.
        /// </summary>
        public bool IncludeClientIp { get; set; } = true;

        /// <summary>
        /// Whether to include the query string in the log entry.
        /// <b>Default <c>false</c></b> — query strings often carry tokens
        /// / IDs / emails and structured logs are frequently indexed
        /// (Loki, Seq, Elastic) where PII leakage is a real risk. Apps
        /// that already scrub their query strings may opt in.
        /// </summary>
        public bool IncludeQueryString { get; set; }

        /// <summary>
        /// Path prefixes to exclude from slow-request logging. Defaults
        /// cover static-asset and health-probe paths that commonly exceed
        /// low thresholds without being signal. Matching is case-insensitive
        /// prefix check against <c>HttpContext.Request.Path</c>.
        /// </summary>
        public IList<string> PathExclusions { get; set; } = new List<string>
        {
            "/healthz",
            "/_framework",
            "/_js",
            "/_content",
            "/favicon.ico",
        };
    }
}
