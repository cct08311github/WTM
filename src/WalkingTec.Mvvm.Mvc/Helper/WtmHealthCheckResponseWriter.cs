#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// JSON response writer for <c>UseHealthChecks</c>. Emits per-check
    /// duration, status, description, and error message so SRE tooling
    /// (Kubernetes, Prometheus, Datadog) can scrape structured data
    /// instead of parsing the ASP.NET Core default plain-text output (#836).
    /// </summary>
    /// <remarks>
    /// <strong>Security note:</strong> the <c>exception</c> field in the JSON output is
    /// redacted (omitted) by default. Raw exception messages may contain connection-string
    /// fragments or hostnames that should not be exposed to unauthenticated callers.
    /// Enable exception detail only in development environments by passing
    /// <c>includeExceptionDetail: true</c> to <see cref="WriteJsonResponse(HttpContext,HealthReport,bool)"/>
    /// or by using <see cref="CreateWriter"/> with an <c>IWebHostEnvironment</c> check.
    /// </remarks>
    public static class WtmHealthCheckResponseWriter
    {
        // Static options for hot-path — thread-safe, reusable.
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            // DefaultIgnoreCondition keeps payload small — null description /
            // exception / data just disappear rather than emitting `"x": null`
            // everywhere.
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// Creates a <see cref="Func{HttpContext,HealthReport,Task}"/> delegate suitable for
        /// <c>HealthCheckOptions.ResponseWriter</c> that includes exception detail only when
        /// <paramref name="includeExceptionDetail"/> is <c>true</c>.
        /// <para>
        /// Typical usage in <c>UseWtmHealthChecks</c>:
        /// </para>
        /// <code>
        /// var isDev = env.IsDevelopment();
        /// options.ResponseWriter = WtmHealthCheckResponseWriter.CreateWriter(isDev);
        /// </code>
        /// </summary>
        public static Func<HttpContext, HealthReport, Task> CreateWriter(bool includeExceptionDetail)
            => (ctx, report) => WriteJsonResponse(ctx, report, includeExceptionDetail);

        /// <summary>
        /// Writes a JSON body describing the overall and per-check health status.
        /// The <c>exception</c> field is emitted only when <paramref name="includeExceptionDetail"/>
        /// is <c>true</c>; in all other cases it is redacted to <c>null</c> (omitted from JSON
        /// by <c>DefaultIgnoreCondition = WhenWritingNull</c>).
        /// <para>Shape:</para>
        /// <code>
        /// {
        ///   "status": "Healthy",
        ///   "totalDurationMs": 42,
        ///   "checks": [
        ///     { "name": "self", "status": "Healthy", "durationMs": 0 },
        ///     { "name": "datacontext", "status": "Unhealthy", "durationMs": 2004, "description": "..." }
        ///   ]
        /// }
        /// </code>
        /// </summary>
        /// <param name="httpContext">The current HTTP context.</param>
        /// <param name="report">The health report to serialise.</param>
        /// <param name="includeExceptionDetail">
        /// When <c>true</c>, includes <c>entry.Exception.Message</c> in the JSON output.
        /// Default is <c>false</c>; set to <c>true</c> only in development environments.
        /// </param>
        public static Task WriteJsonResponse(
            HttpContext httpContext, HealthReport report, bool includeExceptionDetail = false)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentNullException.ThrowIfNull(report);

            httpContext.Response.ContentType = "application/json; charset=utf-8";

            var checks = new List<CheckEntry>(report.Entries.Count);
            foreach (var kv in report.Entries)
            {
                var entry = kv.Value;
                checks.Add(new CheckEntry
                {
                    Name = kv.Key,
                    Status = entry.Status.ToString(),
                    DurationMs = (long)entry.Duration.TotalMilliseconds,
                    Description = string.IsNullOrEmpty(entry.Description) ? null : entry.Description,
                    // Redact exception message in non-development environments to prevent
                    // leaking connection strings or hostnames to unauthenticated callers.
                    Exception = includeExceptionDetail ? entry.Exception?.Message : null,
                    Data = entry.Data.Count == 0 ? null : new Dictionary<string, object?>(entry.Data),
                    Tags = entry.Tags.Any() ? new List<string>(entry.Tags) : null,
                });
            }

            var payload = new ReportPayload
            {
                Status = report.Status.ToString(),
                TotalDurationMs = (long)report.TotalDuration.TotalMilliseconds,
                Checks = checks,
            };

            return JsonSerializer.SerializeAsync(httpContext.Response.Body, payload, JsonOptions);
        }

        // POCO wire shapes — intentionally public so apps can reference the
        // contract from their own tests / dashboards.

        public sealed class ReportPayload
        {
            public string Status { get; set; } = string.Empty;
            public long TotalDurationMs { get; set; }
            public List<CheckEntry> Checks { get; set; } = new();
        }

        public sealed class CheckEntry
        {
            public string Name { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public long DurationMs { get; set; }
            public string? Description { get; set; }
            public string? Exception { get; set; }
            public IDictionary<string, object?>? Data { get; set; }
            public IList<string>? Tags { get; set; }
        }
    }
}
