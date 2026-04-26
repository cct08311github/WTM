#nullable enable
using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Configuration for <see cref="WtmCspReportMiddleware"/> (#845) —
    /// the server-side CSP-violation report endpoint that pairs with
    /// <see cref="WtmCspOptions.ReportUri"/>. Acts as a defensive
    /// receiver: drops malformed bodies, throttles per-IP, and emits a
    /// structured log entry for each accepted report so the existing
    /// log pipeline becomes the policy-violation observability surface.
    /// </summary>
    public class WtmCspReportOptions
    {
        /// <summary>
        /// Path the middleware listens on. Default <c>/_csp/report</c>.
        /// Must be the same value the operator sets in
        /// <see cref="WtmCspOptions.ReportUri"/> for the browser to
        /// post here. Setting <c>null</c> or whitespace effectively
        /// disables the middleware (no path matches).
        /// </summary>
        public string Path { get; set; } = "/_csp/report";

        /// <summary>
        /// Per-source-IP rate limit. Default 10 reports per minute.
        /// Browsers can fire many reports per session if a page has
        /// dozens of inline scripts; an attacker can amplify by
        /// triggering violations deliberately. The cap is the
        /// difference between a well-bounded log-stream and a DoS
        /// vector that crowds out useful signal.
        /// </summary>
        public int RateLimitPerMinute { get; set; } = 10;

        /// <summary>
        /// Maximum accepted body size in bytes. Default 8 KiB —
        /// generous for any realistic CSP report (most are 200‒800 B)
        /// while preventing a hostile client from forcing the
        /// middleware to allocate an unbounded buffer.
        /// </summary>
        public int MaxBodyBytes { get; set; } = 8 * 1024;

        /// <summary>
        /// Optional callback invoked with each accepted report. When
        /// <c>null</c> the middleware logs the report at
        /// <see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/>.
        /// Apps that already pipe CSP reports into Sentry / Datadog /
        /// custom telemetry register a callback to take ownership of
        /// the dispatch.
        /// </summary>
        public Func<CspViolationReport, Task>? OnReport { get; set; }
    }

    /// <summary>
    /// Parsed CSP violation report (subset of the
    /// <see href="https://www.w3.org/TR/CSP3/#deprecated-serialize-violation"/>
    /// CSP-2 / CSP-3 envelope). Browsers post a JSON object shaped
    /// like <c>{"csp-report": { ... }}</c>; this type unwraps the
    /// inner record. Null fields are normal — older / non-Chrome
    /// browsers omit certain keys.
    /// </summary>
    public sealed class CspViolationReport
    {
        [JsonPropertyName("document-uri")]      public string? DocumentUri { get; set; }
        [JsonPropertyName("referrer")]          public string? Referrer { get; set; }
        [JsonPropertyName("violated-directive")] public string? ViolatedDirective { get; set; }
        [JsonPropertyName("effective-directive")] public string? EffectiveDirective { get; set; }
        [JsonPropertyName("original-policy")]   public string? OriginalPolicy { get; set; }
        [JsonPropertyName("disposition")]       public string? Disposition { get; set; }
        [JsonPropertyName("blocked-uri")]       public string? BlockedUri { get; set; }
        [JsonPropertyName("status-code")]       public int? StatusCode { get; set; }
        [JsonPropertyName("script-sample")]     public string? ScriptSample { get; set; }
        [JsonPropertyName("line-number")]       public int? LineNumber { get; set; }
        [JsonPropertyName("column-number")]     public int? ColumnNumber { get; set; }
        [JsonPropertyName("source-file")]       public string? SourceFile { get; set; }
    }
}
