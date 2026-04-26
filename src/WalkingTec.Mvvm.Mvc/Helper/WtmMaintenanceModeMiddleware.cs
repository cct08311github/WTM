#nullable enable
using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Returns <see cref="WtmMaintenanceModeOptions.StatusCode"/> (default 503)
    /// for all non-allow-listed traffic while maintenance mode is active.
    /// Health probes, framework assets, and the admin plane continue to
    /// flow through so k8s / ALB keep the pod attached and operators can
    /// flip the switch back once the outage clears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decision order per request:
    /// </para>
    /// <list type="number">
    /// <item>Is maintenance mode active?
    /// (<see cref="WtmMaintenanceModeOptions.IsEnabled"/> if set,
    /// otherwise <see cref="WtmMaintenanceModeOptions.Enabled"/>).
    /// If <c>false</c>, pass through.</item>
    /// <item>Does the request path start with any
    /// <see cref="WtmMaintenanceModeOptions.AllowedPathPrefixes"/> entry?
    /// If yes, pass through.</item>
    /// <item>Is the resolved client IP in
    /// <see cref="WtmMaintenanceModeOptions.AllowedClientIps"/>? If yes, pass through.</item>
    /// <item>Otherwise write the configured error response and short-circuit.</item>
    /// </list>
    /// <para>
    /// When <see cref="WtmMaintenanceModeOptions.Enabled"/> is <c>false</c>
    /// and no dynamic provider is set, the middleware performs a single
    /// bool check before delegating — effectively free.
    /// </para>
    /// </remarks>
    public class WtmMaintenanceModeMiddleware
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly RequestDelegate _next;
        private readonly WtmMaintenanceModeOptions _options;
        private readonly ILogger<WtmMaintenanceModeMiddleware> _logger;

        public WtmMaintenanceModeMiddleware(
            RequestDelegate next,
            WtmMaintenanceModeOptions options,
            ILogger<WtmMaintenanceModeMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task InvokeAsync(HttpContext context)
        {
            if (!ResolveActive(context))
            {
                return _next(context);
            }

            if (IsPathAllowed(context.Request.Path))
            {
                return _next(context);
            }

            if (IsClientAllowed(context))
            {
                return _next(context);
            }

            return WriteMaintenanceResponseAsync(context);
        }

        private bool ResolveActive(HttpContext context)
        {
            var provider = _options.IsEnabled;
            if (provider != null)
            {
                try
                {
                    return provider(context);
                }
                catch (Exception ex)
                {
                    // A faulty provider must not bring down production — log
                    // and fall back to the static flag so the system keeps
                    // serving the safer default.
                    _logger.LogWarning(ex, "WtmMaintenanceMode IsEnabled delegate threw; falling back to Options.Enabled={Enabled}",
                        _options.Enabled);
                }
            }
            return _options.Enabled;
        }

        private bool IsPathAllowed(PathString path)
        {
            if (!path.HasValue) { return false; }
            var value = path.Value!;
            foreach (var prefix in _options.AllowedPathPrefixes)
            {
                if (string.IsNullOrEmpty(prefix)) { continue; }
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsClientAllowed(HttpContext context)
        {
            if (_options.AllowedClientIps.Count == 0) { return false; }
            var ip = context.GetRemoteIpAddress();
            if (string.IsNullOrEmpty(ip) || ip == "unknown") { return false; }
            foreach (var allowed in _options.AllowedClientIps)
            {
                if (string.IsNullOrEmpty(allowed)) { continue; }
                if (string.Equals(allowed, ip, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private Task WriteMaintenanceResponseAsync(HttpContext context)
        {
            if (context.Response.HasStarted)
            {
                // Something downstream already began writing — do not
                // attempt to rewrite headers or body. Log and give up.
                _logger.LogWarning("WtmMaintenanceMode: response already started, cannot short-circuit Path={Path}",
                    LogSanitizer.Sanitize(context.Request.Path.Value ?? "/"));
                return Task.CompletedTask;
            }

            context.Response.StatusCode = _options.StatusCode;
            context.Response.ContentType = _options.ContentType;
            if (_options.RetryAfterSeconds is int retry && retry >= 0)
            {
                context.Response.Headers["Retry-After"] = retry.ToString(CultureInfo.InvariantCulture);
            }

            var body = BuildBody(context);
            return context.Response.WriteAsync(body);
        }

        private string BuildBody(HttpContext context)
        {
            if (_options.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                if (_options.HtmlBodyFactory != null)
                {
                    return _options.HtmlBodyFactory(context);
                }
                return BuildDefaultHtmlBody(_options);
            }

            var payload = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.6.4",
                title = _options.Title,
                status = _options.StatusCode,
                detail = _options.Message,
                retryAfterSeconds = _options.RetryAfterSeconds,
                traceId = context.TraceIdentifier,
            };
            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        private static string BuildDefaultHtmlBody(WtmMaintenanceModeOptions options)
        {
            // Minimal, self-contained fallback. Keeps the framework free of
            // a Razor / templating dependency for this middleware while still
            // giving browser callers something reasonable to render.
            return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"/>" +
                   "<title>" + HtmlEncode(options.Title) + "</title></head>" +
                   "<body><h1>" + HtmlEncode(options.Title) + "</h1>" +
                   "<p>" + HtmlEncode(options.Message) + "</p></body></html>";
        }

        private static string HtmlEncode(string value)
            => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    }
}
