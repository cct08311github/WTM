#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Replays cached 2xx responses for duplicate
    /// <see cref="WtmIdempotencyOptions.HeaderName"/> values, so
    /// mobile-client / flaky-network retries do not re-execute a POST
    /// handler that already charged the customer / created the order.
    /// Skips any endpoint that isn't decorated with
    /// <see cref="WtmIdempotentAttribute"/>; keyless requests on
    /// decorated endpoints run normally unless
    /// <see cref="WtmIdempotentAttribute.RequireKey"/> is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Body-buffering strategy: the middleware swaps
    /// <c>Response.Body</c> with a bounded <see cref="MemoryStream"/>,
    /// runs the pipeline, and copies the captured bytes back to the
    /// original stream. Only 2xx responses are cached — 4xx / 5xx are
    /// passed through unchanged so a transient validation failure
    /// doesn't poison the cache slot.
    /// </para>
    /// <para>
    /// Cache backing is <see cref="IMemoryCache"/> — in-process, single
    /// node. Multi-instance deployments needing cross-node idempotency
    /// should register an <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
    /// and wrap it with an <c>IMemoryCache</c>-shaped adapter, or gate
    /// the endpoints behind a session-sticky load balancer.
    /// </para>
    /// <para>
    /// Security notes:
    /// </para>
    /// <list type="bullet">
    /// <item>Keys are length-capped and scoped by <c>method + path + key</c>
    /// so an attacker cannot reuse the same key to grab a cached
    /// response from a different endpoint.</item>
    /// <item>Only the body + status + content-type are cached. Response
    /// headers such as <c>Set-Cookie</c> are intentionally NOT replayed
    /// (would leak a previous user's session into a new caller).</item>
    /// <item>Replayed responses carry an <c>Idempotency-Replay: true</c>
    /// header so clients / tests can distinguish a cache hit from a
    /// fresh invocation.</item>
    /// </list>
    /// </remarks>
    public class WtmIdempotencyMiddleware
    {
        /// <summary>Header written on replays so tests / clients can distinguish cache hits.</summary>
        public const string ReplayHeaderName = "Idempotency-Replay";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly RequestDelegate _next;
        private readonly WtmIdempotencyOptions _options;
        private readonly IMemoryCache _cache;
        private readonly ILogger<WtmIdempotencyMiddleware> _logger;

        public WtmIdempotencyMiddleware(
            RequestDelegate next,
            WtmIdempotencyOptions options,
            IMemoryCache cache,
            ILogger<WtmIdempotencyMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var attr = context.GetEndpoint()?.Metadata.GetMetadata<WtmIdempotentAttribute>();
            if (attr == null)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            if (!_options.EligibleMethods.Contains(context.Request.Method))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var rawKey = context.Request.Headers[_options.HeaderName].ToString();
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                if (attr.RequireKey)
                {
                    await WriteBadRequestAsync(context,
                        $"{_options.HeaderName} header is required for this endpoint.")
                        .ConfigureAwait(false);
                    return;
                }
                await _next(context).ConfigureAwait(false);
                return;
            }

            if (rawKey.Length > _options.MaxKeyLength || !IsAsciiTokenSafe(rawKey))
            {
                await WriteBadRequestAsync(context,
                    $"{_options.HeaderName} header value is invalid or too long.")
                    .ConfigureAwait(false);
                return;
            }

            var cacheKey = BuildCacheKey(context.Request.Method, context.Request.Path, rawKey);
            if (_cache.TryGetValue(cacheKey, out CachedResponse? cached) && cached != null)
            {
                await ReplayAsync(context, cached).ConfigureAwait(false);
                return;
            }

            await RunAndMaybeCacheAsync(context, attr, cacheKey).ConfigureAwait(false);
        }

        private async Task RunAndMaybeCacheAsync(
            HttpContext context, WtmIdempotentAttribute attr, string cacheKey)
        {
            var originalBody = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await _next(context).ConfigureAwait(false);

                var status = context.Response.StatusCode;
                if (status >= 200 && status < 300)
                {
                    var len = buffer.Length;
                    if (len <= _options.MaxCachedBodyBytes)
                    {
                        buffer.Position = 0;
                        var bytes = buffer.ToArray();
                        var entry = new CachedResponse(status, context.Response.ContentType, bytes);
                        var window = attr.WindowSeconds > 0
                            ? attr.WindowSeconds
                            : _options.DefaultWindowSeconds;
                        _cache.Set(cacheKey, entry,
                            TimeSpan.FromSeconds(window));
                    }
                    else
                    {
                        _logger.LogInformation(
                            "WtmIdempotency: skipped caching — body {Bytes} B exceeds cap {Cap} B for {Path}",
                            len, _options.MaxCachedBodyBytes,
                            LogSanitizer.Sanitize(context.Request.Path.Value ?? ""));
                    }
                }

                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = originalBody;
            }
        }

        private static Task ReplayAsync(HttpContext context, CachedResponse cached)
        {
            context.Response.StatusCode = cached.StatusCode;
            if (!string.IsNullOrEmpty(cached.ContentType))
            {
                context.Response.ContentType = cached.ContentType;
            }
            context.Response.Headers[ReplayHeaderName] = "true";
            return context.Response.Body.WriteAsync(cached.Body, 0, cached.Body.Length);
        }

        private static Task WriteBadRequestAsync(HttpContext context, string detail)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/problem+json";
            var payload = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                title = "Bad Request",
                status = StatusCodes.Status400BadRequest,
                detail,
                traceId = context.TraceIdentifier,
            };
            return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>Deterministic cache-key scoping by method + path + user-supplied idempotency key. Public for unit-test determinism.</summary>
        public static string BuildCacheKey(string method, PathString path, string idempotencyKey)
        {
            // Scoping by method + path prevents an attacker from reusing
            // a stolen key against a different endpoint (e.g. trying the
            // same key against /api/admin/* that worked on /api/orders).
            var p = path.HasValue ? path.Value : "/";
            return string.Concat("wtm:idem:", method.ToUpperInvariant(), ":", p, ":", idempotencyKey);
        }

        /// <summary>Allow-list check against a conservative ASCII subset (letters / digits / <c>-_.:</c>). Public for unit-test determinism.</summary>
        public static bool IsAsciiTokenSafe(string value)
        {
            // Allow-list: ASCII letters, digits, '-' '_' '.' ':' (UUID
            // and ULID shapes) — enough to satisfy Stripe-style UUID v4
            // keys and all common shapes without opening the door to
            // control chars / UTF-8 / log-injection.
            foreach (var ch in value)
            {
                if (ch is >= 'A' and <= 'Z') { continue; }
                if (ch is >= 'a' and <= 'z') { continue; }
                if (ch is >= '0' and <= '9') { continue; }
                if (ch is '-' or '_' or '.' or ':') { continue; }
                return false;
            }
            return true;
        }

        internal sealed class CachedResponse
        {
            public int StatusCode { get; }
            public string? ContentType { get; }
            public byte[] Body { get; }
            public CachedResponse(int statusCode, string? contentType, byte[] body)
            {
                StatusCode = statusCode;
                ContentType = contentType;
                Body = body;
            }
        }
    }
}
