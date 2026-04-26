#nullable enable
using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Enforces a per-request deadline by replacing
    /// <see cref="IHttpRequestLifetimeFeature"/> with a timeout-linked
    /// token; pipeline components that honour <c>HttpContext.RequestAborted</c>
    /// (EF Core, <see cref="System.Net.Http.HttpClient"/>,
    /// <c>Task.Delay(_, ct)</c>, etc.) cancel cooperatively. When the
    /// deadline trips before <see cref="HttpResponse.HasStarted"/>, the
    /// middleware writes a <c>504 Gateway Timeout</c> with an
    /// <c>application/problem+json</c> body and emits a structured
    /// <c>Warning</c> log entry. If the response has already started
    /// (e.g. a chunked stream) the connection is aborted instead — the
    /// only correct option since we cannot retroactively send a 504.
    /// </summary>
    /// <remarks>
    /// Pairs naturally with <c>UseWtmSlowRequestLogging()</c>: keep the
    /// timeout strictly above the slow-request threshold so a transient
    /// blip is logged once before being killed.
    /// </remarks>
    public class WtmRequestTimeoutsMiddleware
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly RequestDelegate _next;
        private readonly WtmRequestTimeoutsOptions _options;
        private readonly ILogger<WtmRequestTimeoutsMiddleware> _logger;

        public WtmRequestTimeoutsMiddleware(
            RequestDelegate next,
            WtmRequestTimeoutsOptions options,
            ILogger<WtmRequestTimeoutsMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (IsExcluded(context.Request.Path))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var timeoutMs = ResolveTimeout(context.Request.Path);
            if (timeoutMs <= 0)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var originalLifetime = context.Features.Get<IHttpRequestLifetimeFeature>();
            if (originalLifetime == null)
            {
                // Hosting environment didn't supply a lifetime feature —
                // the test harness on certain code paths can omit it.
                // Fall through unbounded rather than crash.
                await _next(context).ConfigureAwait(false);
                return;
            }

            using var timeoutFeature = new TimeoutLifetimeFeature(originalLifetime, timeoutMs);
            context.Features.Set<IHttpRequestLifetimeFeature>(timeoutFeature);

            try
            {
                await _next(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutFeature.IsTimeout)
            {
                await HandleTimeoutAsync(context, timeoutMs).ConfigureAwait(false);
            }
            finally
            {
                context.Features.Set<IHttpRequestLifetimeFeature>(originalLifetime);
            }

            // Edge: handler swallowed the cancellation and returned normally.
            // We still want to convert the response to 504 if nothing was
            // written and the deadline tripped — otherwise the client gets
            // a 200 with empty body.
            if (timeoutFeature.IsTimeout && !context.Response.HasStarted
                && context.Response.StatusCode is StatusCodes.Status200OK or 0
                && context.Response.ContentLength is null or 0)
            {
                await HandleTimeoutAsync(context, timeoutMs).ConfigureAwait(false);
            }
        }

        private async Task HandleTimeoutAsync(HttpContext context, int timeoutMs)
        {
            _logger.LogWarning(
                "RequestTimeout Path={Path} Method={Method} TimeoutMs={TimeoutMs}",
                LogSanitizer.Sanitize(context.Request.Path.Value ?? "/"),
                LogSanitizer.Sanitize(context.Request.Method),
                timeoutMs);

            if (context.Response.HasStarted)
            {
                // Cannot rewrite headers — abort the connection so the
                // client treats the response as truncated rather than
                // accepting a partial body as complete.
                context.Abort();
                return;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            context.Response.ContentType = "application/problem+json";

            var payload = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.6.5",
                title = "Gateway Timeout",
                status = StatusCodes.Status504GatewayTimeout,
                detail = "The server took too long to produce a response and the request was aborted.",
                timeoutMs,
                traceId = context.TraceIdentifier,
            };
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(payload, JsonOptions))
                .ConfigureAwait(false);
        }

        /// <summary>Resolve the timeout in ms for the request path, applying longest-prefix-match against <see cref="WtmRequestTimeoutsOptions.PathOverrides"/>. Public for unit-test determinism.</summary>
        public int ResolveTimeout(PathString path)
        {
            if (!path.HasValue || _options.PathOverrides.Count == 0)
            {
                return _options.DefaultTimeoutMs;
            }

            // Longest-prefix-wins so a more specific override (e.g.
            // /api/export/jobs/cancel) beats a parent (e.g. /api).
            string? bestPrefix = null;
            foreach (var kv in _options.PathOverrides)
            {
                var prefix = kv.Key;
                if (string.IsNullOrEmpty(prefix)) { continue; }
                if (!path.Value!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (bestPrefix == null || prefix.Length > bestPrefix.Length)
                {
                    bestPrefix = prefix;
                }
            }
            if (bestPrefix != null && _options.PathOverrides.TryGetValue(bestPrefix, out var ms))
            {
                return ms;
            }
            return _options.DefaultTimeoutMs;
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
        /// Linked lifetime feature: <see cref="RequestAborted"/> cancels
        /// either when the underlying connection drops <b>or</b> when
        /// the configured deadline elapses, whichever comes first.
        /// </summary>
        internal sealed class TimeoutLifetimeFeature : IHttpRequestLifetimeFeature, IDisposable
        {
            private readonly IHttpRequestLifetimeFeature _inner;
            private readonly CancellationTokenSource _timeoutCts;
            private readonly CancellationTokenSource _linkedCts;

            public TimeoutLifetimeFeature(IHttpRequestLifetimeFeature inner, int timeoutMs)
            {
                _inner = inner;
                _timeoutCts = new CancellationTokenSource();
                _timeoutCts.CancelAfter(timeoutMs);
                _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    inner.RequestAborted, _timeoutCts.Token);
            }

            public CancellationToken RequestAborted
            {
                get => _linkedCts.Token;
                set => _inner.RequestAborted = value;
            }

            public void Abort() => _inner.Abort();

            /// <summary>
            /// True when the deadline tripped <em>and</em> the original
            /// connection was not separately aborted by the client.
            /// Used by the middleware to distinguish "deadline killed
            /// the handler" from "client gave up first" — only the
            /// former should produce a 504.
            /// </summary>
            public bool IsTimeout =>
                _timeoutCts.IsCancellationRequested
                && !_inner.RequestAborted.IsCancellationRequested;

            public void Dispose()
            {
                _linkedCts.Dispose();
                _timeoutCts.Dispose();
            }

            public override string ToString() =>
                FormattableString.Invariant($"TimeoutLifetimeFeature(timedOut={_timeoutCts.IsCancellationRequested})");
        }
    }
}
