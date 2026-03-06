using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    public static class WtmProblemDetailsExtension
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public static IApplicationBuilder UseWtmProblemDetails(this IApplicationBuilder app)
        {
            app.Use(async (context, next) =>
            {
                try
                {
                    await next();
                }
                catch (Exception ex)
                {
                    if (ShouldHandleAsApi(context.Request))
                    {
                        await WriteExceptionProblemDetails(context, ex);
                        return;
                    }

                    throw;
                }

                if (!context.Response.HasStarted
                    && context.Response.StatusCode >= 400
                    && context.Response.ContentLength == null
                    && string.IsNullOrEmpty(context.Response.ContentType)
                    && ShouldHandleAsApi(context.Request))
                {
                    await WriteStatusProblemDetails(context);
                }
            });

            return app;
        }

        internal static bool ShouldHandleAsApi(HttpRequest request)
        {
            if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                return true;

            var accept = request.Headers.Accept.ToString();
            if (!string.IsNullOrEmpty(accept)
                && accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static async Task WriteExceptionProblemDetails(HttpContext context, Exception ex)
        {
            var logger = context.RequestServices.GetService<ILogger<ProblemDetails>>();
            logger?.LogError(ex, "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);

            var isDebug = ResolveIsQuickDebug(context);

            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                Title = "Internal Server Error",
                Status = 500,
                Detail = isDebug ? ex.ToString() : "An unexpected error occurred."
            };
            problem.Extensions["traceId"] = context.TraceIdentifier;

            if (isDebug)
            {
                problem.Extensions["exception"] = ex.ToString();
            }

            await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
        }

        private static async Task WriteStatusProblemDetails(HttpContext context)
        {
            var statusCode = context.Response.StatusCode;
            var title = statusCode switch
            {
                400 => "Bad Request",
                401 => "Unauthorized",
                403 => "Forbidden",
                404 => "Not Found",
                405 => "Method Not Allowed",
                409 => "Conflict",
                422 => "Unprocessable Entity",
                429 => "Too Many Requests",
                _ => "Error"
            };

            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Type = $"https://httpstatuses.io/{statusCode}",
                Title = title,
                Status = statusCode,
                Detail = $"{title}."
            };
            problem.Extensions["traceId"] = context.TraceIdentifier;

            await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
        }

        private static bool ResolveIsQuickDebug(HttpContext context)
        {
            try
            {
                var wtm = context.RequestServices.GetService<WTMContext>();
                if (wtm?.ConfigInfo != null)
                    return wtm.ConfigInfo.IsQuickDebug;
            }
            catch { }

            try
            {
                var configs = context.RequestServices.GetService<Configs>();
                if (configs != null)
                    return configs.IsQuickDebug;
            }
            catch { }

            return false;
        }
    }
}
