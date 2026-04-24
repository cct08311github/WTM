#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Attaches a W3C <c>Server-Timing</c> response header
    /// (<c>name;dur=ms[;desc="..."]</c>) to every non-excluded request so
    /// browser devtools can display per-request backend latency without
    /// wiring an APM. Emits in the response-starting phase so the header
    /// lands even when downstream middleware short-circuits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Complementary to <see cref="WtmSlowRequestMiddleware"/>:
    /// <c>UseWtmSlowRequestLogging()</c> logs to the backend aggregator
    /// when elapsed ≥ threshold; <c>UseWtmServerTiming()</c> surfaces
    /// elapsed as an in-band response header so front-end devs see it in
    /// Chrome DevTools → Network → Timing → "Server Timing".
    /// </para>
    /// <para>
    /// Short-circuit exclusions fire before the stopwatch starts, so
    /// static-asset / health-probe responses pay zero measurement cost.
    /// </para>
    /// <para>
    /// First-writer-wins at the header level: any <c>Server-Timing</c>
    /// value already written by upstream middleware (reverse proxy, CDN
    /// edge, another framework in a polyglot pipeline) is preserved; the
    /// middleware <c>.Append(...)</c>s its own metric so multiple layers
    /// can coexist per the W3C spec's comma-separated-list semantics.
    /// </para>
    /// </remarks>
    public class WtmServerTimingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly WtmServerTimingOptions _options;

        public WtmServerTimingMiddleware(RequestDelegate next, WtmServerTimingOptions options)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public Task InvokeAsync(HttpContext context)
        {
            // Exclusions short-circuit before the stopwatch to keep
            // excluded paths free.
            if (IsExcluded(context.Request.Path))
            {
                return _next(context);
            }

            var sw = Stopwatch.StartNew();

            context.Response.OnStarting(static state =>
            {
                var payload = (State)state!;
                payload.Stopwatch.Stop();
                var elapsedMs = payload.Stopwatch.Elapsed.TotalMilliseconds;
                if (elapsedMs < payload.Options.MinDurationMs)
                {
                    return Task.CompletedTask;
                }

                var value = BuildHeaderValue(payload.Options, elapsedMs);
                if (!string.IsNullOrEmpty(value))
                {
                    payload.Response.Headers.Append("Server-Timing", value);
                }
                return Task.CompletedTask;
            }, new State(sw, _options, context.Response));

            return _next(context);
        }

        private bool IsExcluded(PathString path)
        {
            if (!path.HasValue) { return false; }
            var value = path.Value!;
            foreach (var prefix in _options.PathExclusions)
            {
                if (string.IsNullOrEmpty(prefix)) { continue; }
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Assembles the header value <c>name;dur=ms[;desc="..."]</c>.
        /// Public for unit-test determinism. Returns empty string when
        /// <see cref="WtmServerTimingOptions.MetricName"/> is whitespace
        /// or contains characters that would break the W3C token grammar
        /// (spaces, separators, control chars) — a defective caller
        /// value must never produce a malformed response header.
        /// </summary>
        public static string BuildHeaderValue(WtmServerTimingOptions options, double elapsedMs)
        {
            ArgumentNullException.ThrowIfNull(options);

            var name = SanitizeToken(options.MetricName);
            if (string.IsNullOrEmpty(name)) { return string.Empty; }

            var dur = elapsedMs.ToString("0.##", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(options.Description))
            {
                return $"{name};dur={dur}";
            }

            var escapedDesc = options.Description!
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            return $"{name};dur={dur};desc=\"{escapedDesc}\"";
        }

        private static string SanitizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return string.Empty; }
            // W3C Server-Timing names follow RFC 7230 "token" grammar —
            // a conservative ASCII subset keeps the header well-formed.
            // Reject anything else rather than half-escape.
            foreach (var ch in value)
            {
                if (ch <= 0x20 || ch >= 0x7F) { return string.Empty; }
                switch (ch)
                {
                    case '(': case ')': case '<': case '>': case '@':
                    case ',': case ';': case ':': case '\\': case '"':
                    case '/': case '[': case ']': case '?': case '=':
                    case '{': case '}':
                        return string.Empty;
                }
            }
            return value!;
        }

        private sealed record State(Stopwatch Stopwatch, WtmServerTimingOptions Options, HttpResponse Response);
    }
}
