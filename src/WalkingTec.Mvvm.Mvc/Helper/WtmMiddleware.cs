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
using Microsoft.Extensions.Configuration;
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
        private static int _withheldWarned;
        private static int _rejectedWarned;
        private static int _invalidBaseWarned;

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
            if (WorkflowActivitiesGuard.IsActivitiesRequest(context.Request)
                && await WorkflowActivitiesGuard.IsAllowedAsync(context) == false)
            {
                // #208: anonymous callers never reach the engine, whatever WorkflowOptions:RequireAuthentication says
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (WorkflowActivitiesGuard.IsPublicActivitiesRequest(context.Request))
            {
                var selfCall = WorkflowApiSecurity.ResolveSelfCall(
                    wtm.ConfigInfo,
                    context.RequestServices.GetService<IConfiguration>()?["AllowedHosts"],
                    context.Request.Scheme,
                    context.Request.Host.Value,
                    context.Request.Headers["Authorization"].ToString(),
                    context.Request.Headers["Cookie"].ToString());
                if (selfCall.InvalidInternalCallBaseAddress && Interlocked.Exchange(ref _invalidBaseWarned, 1) == 0)
                {
                    context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("WalkingTec.Mvvm.Security")
                        .LogWarning("WorkflowOptions:InternalCallBaseAddress is not an absolute http/https URI and is ignored.");
                }
                if (selfCall.Rejected)
                {
                    if (Interlocked.Exchange(ref _rejectedWarned, 1) == 0)
                    {
                        context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("WalkingTec.Mvvm.Security")
                            .LogWarning("The workflow internal call was rejected because the request host is not listed in AllowedHosts. Set WorkflowOptions:InternalCallBaseAddress (recommended) or add the host to AllowedHosts.");
                    }
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                if (selfCall.CredentialsWithheld && Interlocked.Exchange(ref _withheldWarned, 1) == 0)
                {
                    context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("WalkingTec.Mvvm.Security")
                        .LogWarning("WorkflowOptions:RequireAuthentication is enabled but no trusted address is available for the internal workflow call, so the caller's credentials are not forwarded and the call may be rejected. Configure WorkflowOptions:InternalCallBaseAddress, Domains:server (or list the host in AllowedHosts).");
                }
                // the internal client never follows redirects, so the secret and the forwarded credentials cannot be sent on to another host
                var inner = await wtm.CallAPI(WorkflowActivitiesGuard.InternalClientName, $"{selfCall.BaseAddress}/v1/activities?inneruse=1", headers: WorkflowActivitiesGuard.WithInternalSecret(selfCall.Headers));
                if (inner.StatusCode == null || (int)inner.StatusCode.Value < 200 || (int)inner.StatusCode.Value > 299)
                {
                    var status = inner.StatusCode == null ? 0 : (int)inner.StatusCode.Value;
                    // a redirect is a failure of the internal call, not something to hand to the caller
                    context.Response.StatusCode = status == 0 || (status >= 300 && status <= 399) ? StatusCodes.Status502BadGateway : status;
                    return;
                }
                var txt = inner.Data;
                var cul = Thread.CurrentThread.CurrentCulture;
                if (cul.Name?.ToLower() == "zh" || cul.Name?.ToLower() == "zh-cn")
                {
                    await context.Response.WriteAsync(this.TranslateWorkflow(txt));
                }
                else
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
