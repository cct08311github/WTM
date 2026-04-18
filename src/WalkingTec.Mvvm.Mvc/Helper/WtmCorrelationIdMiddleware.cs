#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Correlation-ID preservation middleware. Reads an inbound
    /// <c>X-Correlation-Id</c>-style header (configurable), validates
    /// and adopts it as <see cref="HttpContext.TraceIdentifier"/> so
    /// Serilog scope, <c>Activity.Current</c>, and
    /// <c>WtmProblemDetails.traceId</c> all see the same value. Echoes
    /// the chosen ID on the response so callers can cross-reference
    /// server logs by their own request ID. Introduced by issue #830.
    /// </summary>
    public sealed class WtmCorrelationIdMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly WtmCorrelationIdOptions _options;

        public WtmCorrelationIdMiddleware(RequestDelegate next, WtmCorrelationIdOptions options)
        {
            ArgumentNullException.ThrowIfNull(next);
            ArgumentNullException.ThrowIfNull(options);
            _next = next;
            _options = options;
        }

        public Task InvokeAsync(HttpContext context)
        {
            var chosenId = ResolveCorrelationId(context);
            context.TraceIdentifier = chosenId;

            if (_options.EmitOutbound)
            {
                var headerName = _options.HeaderName;
                context.Response.OnStarting(() =>
                {
                    if (!context.Response.Headers.ContainsKey(headerName))
                    {
                        context.Response.Headers[headerName] = chosenId;
                    }
                    return Task.CompletedTask;
                });
            }

            return _next(context);
        }

        private string ResolveCorrelationId(HttpContext context)
        {
            if (_options.AdoptInbound &&
                context.Request.Headers.TryGetValue(_options.HeaderName, out var values))
            {
                var first = values.ToString();
                if (IsSafeCorrelationId(first, _options.MaxLength))
                {
                    return first;
                }
            }
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Validates an inbound correlation ID candidate. Exposed
        /// <c>internal</c> for unit tests; rules documented at
        /// <see cref="WtmCorrelationIdOptions.MaxLength"/>.
        /// </summary>
        public static bool IsSafeCorrelationId(string? candidate, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(candidate)) { return false; }
            if (candidate.Length > maxLength) { return false; }
            foreach (var c in candidate)
            {
                // Allow alphanumeric + dash + underscore + dot. Covers
                // UUIDs (hex+dash), UUIDs without dashes, W3C Trace
                // Context (hex), Heroku-style slug IDs, and dotted
                // namespace IDs — but rejects control chars, CR/LF
                // (log injection), commas / semicolons (header split
                // confusion), and non-ASCII.
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == '-' || c == '_' || c == '.')
                {
                    continue;
                }
                return false;
            }
            return true;
        }
    }
}
