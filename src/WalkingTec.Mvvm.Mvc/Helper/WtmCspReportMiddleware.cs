#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Receives browser-posted CSP violation reports at the path
    /// configured in <see cref="WtmCspReportOptions.Path"/> (default
    /// <c>/_csp/report</c>). Closes #845.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Browsers POST the report as <c>application/csp-report</c> or
    /// <c>application/json</c>. The body is a JSON object shaped
    /// <c>{"csp-report": { ... }}</c>; the middleware parses the
    /// inner record, validates it, optionally rate-limits the source
    /// IP, and dispatches to either an operator-supplied callback or
    /// a structured Serilog warning. The HTTP response is always
    /// <c>204 No Content</c> — there's nothing meaningful to return
    /// to the browser. Non-matching paths fall through unchanged.
    /// </para>
    /// <para>
    /// Defenses against amplification:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Body cap</b> — reads at most
    /// <see cref="WtmCspReportOptions.MaxBodyBytes"/> bytes; oversized
    /// bodies return 413 without parsing.</item>
    /// <item><b>Per-IP rate limit</b> — a sliding 60-second window per
    /// resolved client IP, default 10 reports/min, returns 429 when
    /// exceeded. Prevents a hostile page from amplifying CSP-induced
    /// violations into a log-flooding DoS.</item>
    /// <item><b>Method gate</b> — only POST is accepted; GET / OPTIONS
    /// / etc. fall through to the next middleware so the path stays
    /// available for any other handler that wants it.</item>
    /// </list>
    /// </remarks>
    public class WtmCspReportMiddleware
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            // CSP report keys are kebab-case (document-uri,
            // violated-directive, etc.) — exact mapping is on the
            // CspViolationReport properties via [JsonPropertyName].
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly RequestDelegate _next;
        private readonly WtmCspReportOptions _options;
        private readonly ILogger<WtmCspReportMiddleware> _logger;

        // Per-IP sliding-window counters. ConcurrentDictionary so racy
        // updates don't corrupt the counter — under contention we may
        // briefly under-throttle by one report; that is the right
        // trade-off vs. taking a global lock per request.
        private readonly ConcurrentDictionary<string, RateBucket> _buckets = new();

        public WtmCspReportMiddleware(
            RequestDelegate next,
            WtmCspReportOptions options,
            ILogger<WtmCspReportMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!IsMatchingPath(context))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Only POST is meaningful for csp-report. Anything else
            // falls through so the path can host other handlers if
            // an app re-uses it.
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            if (!CheckAndRecordRate(context))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            // Bound body read: declared Content-Length wins fast
            // rejection; for chunked bodies we stop reading at the cap.
            if (context.Request.ContentLength is long len && len > _options.MaxBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            string raw;
            using (var ms = new MemoryStream(_options.MaxBodyBytes))
            {
                var buffer = new byte[4096];
                int total = 0;
                int read;
                while ((read = await context.Request.Body.ReadAsync(
                           buffer, 0, buffer.Length, context.RequestAborted).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > _options.MaxBodyBytes)
                    {
                        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        return;
                    }
                    ms.Write(buffer, 0, read);
                }
                raw = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }

            CspViolationReport? report;
            try
            {
                report = ParseReport(raw);
            }
            catch (JsonException)
            {
                // Malformed JSON — log and shrug; never blow up.
                _logger.LogDebug("WtmCspReport: rejected malformed body from {Ip}",
                    LogSanitizer.Sanitize(context.GetRemoteIpAddress()));
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (report == null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (_options.OnReport != null)
            {
                try
                {
                    await _options.OnReport(report).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Callback exceptions must not propagate; the
                    // browser doesn't care and propagation would mask
                    // the violation under an exception trace.
                    _logger.LogWarning(ex,
                        "WtmCspReport: OnReport callback threw for {Directive}",
                        LogSanitizer.Sanitize(report.ViolatedDirective ?? ""));
                }
            }
            else
            {
                _logger.LogWarning(
                    "CspViolation Document={Doc} Directive={Directive} Blocked={Blocked} Source={Source}:{Line}",
                    LogSanitizer.Sanitize(report.DocumentUri ?? ""),
                    LogSanitizer.Sanitize(report.ViolatedDirective ?? report.EffectiveDirective ?? ""),
                    LogSanitizer.Sanitize(report.BlockedUri ?? ""),
                    LogSanitizer.Sanitize(report.SourceFile ?? ""),
                    report.LineNumber);
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        private bool IsMatchingPath(HttpContext context)
        {
            var p = _options.Path;
            if (string.IsNullOrWhiteSpace(p)) { return false; }
            return context.Request.Path.Equals(p, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Tracks per-IP request count over the last 60 s. Returns
        /// <c>true</c> when the request fits inside the limit and
        /// records it; <c>false</c> when it would exceed.
        /// Public for unit-test determinism.
        /// </summary>
        public bool CheckAndRecordRate(HttpContext context)
        {
            if (_options.RateLimitPerMinute <= 0) { return true; }
            var ip = context.GetRemoteIpAddress();
            if (string.IsNullOrEmpty(ip)) { return true; }
            var now = DateTimeOffset.UtcNow;

            // Opportunistic eviction: 1-in-N requests we sweep the dictionary
            // for buckets that are empty AND have not been touched in 60 s.
            // Random sample keeps cost O(1) per request on average.
            if (_buckets.Count > 64 && Random.Shared.Next(32) == 0)
            {
                EvictStaleBuckets(now);
            }

            var bucket = _buckets.GetOrAdd(ip, _ => new RateBucket());
            lock (bucket)
            {
                bucket.Trim(now);
                if (bucket.Count >= _options.RateLimitPerMinute)
                {
                    return false;
                }
                bucket.Add(now);
                return true;
            }
        }

        /// <summary>
        /// Removes buckets that have no in-window hits and were last touched
        /// over 60 s ago. Exposed internal for unit-test determinism.
        /// </summary>
        internal void EvictStaleBuckets(DateTimeOffset now)
        {
            var cutoff = now.AddSeconds(-60);
            foreach (var kvp in _buckets)
            {
                var b = kvp.Value;
                lock (b)
                {
                    b.Trim(now);
                    if (b.Count == 0 && b.LastHit < cutoff)
                    {
                        // ConcurrentDictionary.TryRemove(KeyValuePair) only
                        // removes if the value reference still matches.
                        _buckets.TryRemove(new System.Collections.Generic.KeyValuePair<string, RateBucket>(kvp.Key, b));
                    }
                }
            }
        }

        /// <summary>
        /// Parses a CSP report body. Supports the canonical
        /// <c>{"csp-report": { ... }}</c> envelope and the looser
        /// flat-object shape some non-Chromium browsers / proxies
        /// emit. Public for unit-test determinism.
        /// </summary>
        public static CspViolationReport? ParseReport(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) { return null; }
            using var doc = JsonDocument.Parse(raw);
            JsonElement target = doc.RootElement;
            if (target.ValueKind == JsonValueKind.Object
                && target.TryGetProperty("csp-report", out var inner)
                && inner.ValueKind == JsonValueKind.Object)
            {
                target = inner;
            }
            return JsonSerializer.Deserialize<CspViolationReport>(target.GetRawText(), JsonOptions);
        }

        /// <summary>
        /// Per-IP rate bucket — list of timestamps within the last 60 s.
        /// Trimmed lazily on each access.
        /// </summary>
        private sealed class RateBucket
        {
            private readonly System.Collections.Generic.Queue<DateTimeOffset> _hits = new();
            public DateTimeOffset LastHit { get; private set; }

            public int Count => _hits.Count;

            public void Trim(DateTimeOffset now)
            {
                var cutoff = now.AddSeconds(-60);
                while (_hits.Count > 0 && _hits.Peek() < cutoff)
                {
                    _hits.Dequeue();
                }
            }

            public void Add(DateTimeOffset now)
            {
                _hits.Enqueue(now);
                LastHit = now;
            }
        }
    }
}
