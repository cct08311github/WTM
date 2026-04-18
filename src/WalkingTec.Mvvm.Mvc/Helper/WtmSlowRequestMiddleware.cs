#nullable enable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Measures request pipeline latency with <see cref="Stopwatch"/> and
    /// emits a structured log entry when it exceeds
    /// <see cref="WtmSlowRequestOptions.ThresholdMs"/>. Introduced by
    /// issue #840 so SRE / on-call teams can pivot slow-endpoint triage on
    /// existing log aggregators without wiring an APM.
    /// </summary>
    /// <remarks>
    /// Under-threshold requests cost only the <c>Stopwatch.Start/Stop</c>
    /// + a single prefix comparison (path exclusions) + an integer
    /// comparison — effectively free.
    /// <para>
    /// Emitted Serilog template:
    /// <c>SlowRequest Path={Path} Method={Method} Status={StatusCode} ElapsedMs={Elapsed} User={User} ClientIp={ClientIp}</c>
    /// </para>
    /// <para>
    /// Path / IP / query string values are routed through <see cref="LogSanitizer.Sanitize(string)"/>
    /// to neutralise CR/LF / control-char log-injection attempts coming
    /// via untrusted URL segments.
    /// </para>
    /// </remarks>
    public class WtmSlowRequestMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly WtmSlowRequestOptions _options;
        private readonly ILogger<WtmSlowRequestMiddleware> _logger;

        public WtmSlowRequestMiddleware(
            RequestDelegate next,
            WtmSlowRequestOptions options,
            ILogger<WtmSlowRequestMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Short-circuit exclusions before starting the stopwatch so
            // excluded paths pay zero measurement cost.
            if (IsExcluded(context.Request.Path))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            finally
            {
                sw.Stop();

                var elapsed = sw.ElapsedMilliseconds;
                if (elapsed >= _options.ThresholdMs && _logger.IsEnabled(_options.LogLevel))
                {
                    EmitSlowRequestLog(context, elapsed);
                }
            }
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

        private void EmitSlowRequestLog(HttpContext context, long elapsedMs)
        {
            var req = context.Request;
            var pathValue = req.Path.HasValue ? req.Path.Value! : "/";
            var path = _options.IncludeQueryString && req.QueryString.HasValue
                ? pathValue + req.QueryString.Value
                : pathValue;

            var user = context.User?.Identity?.IsAuthenticated == true
                ? context.User.Identity.Name
                : null;

            var clientIp = _options.IncludeClientIp
                ? context.GetRemoteIpAddress()
                : null;

            _logger.Log(
                _options.LogLevel,
                "SlowRequest Path={Path} Method={Method} Status={StatusCode} ElapsedMs={Elapsed} User={User} ClientIp={ClientIp}",
                LogSanitizer.Sanitize(path),
                LogSanitizer.Sanitize(req.Method),
                context.Response.StatusCode,
                elapsedMs,
                LogSanitizer.Sanitize(user ?? ""),
                LogSanitizer.Sanitize(clientIp ?? ""));
        }
    }
}
