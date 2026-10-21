using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Mvc
{
    public class WtmMiddleware
    {
        private readonly RequestDelegate _next;

        public WtmMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, WTMContext wtm)
        {
            var max = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (max.IsReadOnly == false)
            {
                max.MaxRequestBodySize = wtm.ConfigInfo.FileUploadOptions.UploadLimit;
            }
            if (WorkflowActivitiesGuard.IsElsaApiRequest(context.Request)
                && await WorkflowActivitiesGuard.IsAllowedAsync(context) == false)
            {
                // #208, #225, #256: anonymous callers never reach the engine, whatever WorkflowOptions:RequireAuthentication says
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (WorkflowActivitiesGuard.IsActivitiesListRequest(context.Request) && WorkflowActivitiesGuard.IsReadMethod(context.Request))
            {
                // #216: describe the activities in-process (no HTTP self-call, no secret header, no call target that follows the Host header)
                string txt;
                try
                {
                    // the Elsa controller's AuthorizeFilter is no longer in the way, so apply the same policy here
                    var status = await WorkflowActivitiesGuard.AuthorizeAsync(context, wtm.ConfigInfo);
                    if (status != StatusCodes.Status200OK)
                    {
                        context.Response.StatusCode = status;
                        return;
                    }
                    txt = await WorkflowActivitiesDescriber.DescribeAsync(context.RequestServices, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // nothing has been written yet; log the type and message only (no request content)
                    context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("WalkingTec.Mvvm.Workflow")
                        .LogError("Describing the workflow activities failed: {ExceptionType}: {ExceptionMessage}", ex.GetType().FullName, ex.Message);
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                }
                if (txt == null)
                {
                    // workflow engine not registered: behave as if this middleware did not handle the path
                    await _next(context);
                    return;
                }
                var cul = Thread.CurrentThread.CurrentCulture;
                if (cul.Name?.ToLower() == "zh" || cul.Name?.ToLower() == "zh-cn")
                {
                    txt = this.TranslateWorkflow(txt);
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Cache-Control"] = "no-store";
                if (HttpMethods.IsHead(context.Request.Method) == false)
                {
                    await context.Response.WriteAsync(txt);
                }
                return;
            }
            if (context.Request.Path == "/")
            {
                context.Response.Cookies.Append("pagemode", wtm.ConfigInfo.PageMode.ToString(), new Microsoft.AspNetCore.Http.CookieOptions { Secure = context.Request.IsHttps });
                context.Response.Cookies.Append("tabmode", wtm.ConfigInfo.TabMode.ToString(), new Microsoft.AspNetCore.Http.CookieOptions { Secure = context.Request.IsHttps });
            }
            await BufferRequestBodyAsync(context, wtm.ConfigInfo.BufferJsonBodyOnly);
            await _next(context);
            if (context.Response.StatusCode == 404)
            {
                await context.Response.WriteAsync(string.Empty);
            }
        }

        /// <summary>
        /// The key of <see cref="HttpContext.Items"/> that holds the buffered request body.
        /// </summary>
        public const string RequestBodyItemKey = "DONOTUSE_REQUESTBODY";

        /// <summary>
        /// Buffers the request body (non-form requests with a body) into <see cref="HttpContext.Items"/>
        /// and rewinds the body stream to position 0.
        /// </summary>
        /// <param name="context">current http context</param>
        /// <param name="jsonOnly">when true only application/json and *+json bodies are buffered; default behavior (false) buffers every non-form body</param>
        public static async Task BufferRequestBodyAsync(HttpContext context, bool jsonOnly = false)
        {
            if (context.Request.ContentLength > 0 && context.Request.HasFormContentType == false)
            {
                if (jsonOnly && IsJsonContentType(context.Request.ContentType) == false)
                {
                    return;
                }
                try
                {
                    context.Request.EnableBuffering();
                    context.Request.Body.Position = 0;
                    string body;
                    using (var tr = new StreamReader(context.Request.Body, Encoding.UTF8, true, 1024, leaveOpen: true))
                    {
                        body = await tr.ReadToEndAsync();
                    }
                    context.Request.Body.Position = 0;
                    if (context.Items.ContainsKey(RequestBodyItemKey) == false)
                    {
                        context.Items.Add(RequestBodyItemKey, body);
                    }
                    else
                    {
                        context.Items[RequestBodyItemKey] = body;
                    }
                }
                catch
                {
                    context.Request.Body.Position = 0;
                }
            }
        }

        private static bool IsJsonContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType) || MediaTypeHeaderValue.TryParse(contentType, out var mt) == false)
            {
                return false;
            }
            var mediaType = mt.MediaType.Value;
            return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || (mediaType != null && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
        }

        private string TranslateWorkflow(string txt)
        {
            if(txt == null)
            {
                return "";
            }
            string rv = txt
                .Replace("\"displayName\":\"Read Line\"", "\"displayName\":\"Read\"")
                .Replace("\"description\":\"Read text from standard in.\"", "\"description\":\"Read text from standard in.\"")
                .Replace("\"category\":\"Console\"", "\"category\":\"Console\"")
                .Replace("\"displayName\":\"Write Line\"", "\"displayName\":\"Write\"")
                .Replace("\"description\":\"Write text to standard out.\"", "\"description\":\"Write text to standard out.\"")
                .Replace("\"label\":\"Text\"", "\"label\":\"Text\"")
                .Replace("\"hint\":\"The text to write.\"", "\"hint\":\"The text to write.\"")
                .Replace("\"label\":\"Script\"", "\"label\":\"Script\"")
                .Replace("\"hint\":\"The JavaScript to run.\"", "\"hint\":\"The JavaScript to run.\"")
                .Replace("\"label\":\"Possible Outcomes\"", "\"label\":\"Possible Outcomes\"")
                .Replace("\"hint\":\"The possible outcomes that can be set by the script.\"", "\"hint\":\"The possible outcomes that can be set by the script.\"")
                .Replace("\"displayName\":\"HTTP Endpoint\"", "\"displayName\":\"HTTP Endpoint\"")
                .Replace("\"description\":\"Handle an incoming HTTP request.\"", "\"description\":\"Handle an incoming HTTP request.\"");

            return rv;
        }
    }

    public static class WtmMiddlewareExtensions
    {
        public static IApplicationBuilder UseWtm(
            this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<WtmMiddleware>();
        }
    }
}
