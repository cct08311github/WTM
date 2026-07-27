using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using NPOI.HPSF;
using NPOI.SS.Formula;
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
            if (max != null && !max.IsReadOnly)
            {
                max.MaxRequestBodySize = wtm.ConfigInfo.FileUploadOptions.UploadLimit;
            }
            if (context.Request.Path == "/")
            {
                // UI preference cookies — NOT HttpOnly because LayUI JS reads them via $.cookie() (#779)
                var uiCookieOptions = new CookieOptions
                {
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                    Path = "/",
                };
                context.Response.Cookies.Append("pagemode", wtm.ConfigInfo.PageMode.ToString(), uiCookieOptions);
                context.Response.Cookies.Append("tabmode", wtm.ConfigInfo.TabMode.ToString(), uiCookieOptions);
            }
            if (context.Request.ContentLength > 0 && context.Request.HasFormContentType == false)
            {
                try
                {
                    context.Request.EnableBuffering();
                    context.Request.Body.Position = 0;
                    StreamReader tr = new StreamReader(context.Request.Body);
                    string body = await tr.ReadToEndAsync();
                    context.Request.Body.Position = 0;
                    if (context.Items.ContainsKey("DONOTUSE_REQUESTBODY") == false)
                    {
                        context.Items.Add("DONOTUSE_REQUESTBODY", body);
                    }
                    else
                    {
                        context.Items["DONOTUSE_REQUESTBODY"] = body;
                    }
                }
                catch {
                    context.Request.Body.Position = 0;
                }
            }
            // Pre-resolve LoginUserInfo asynchronously so the sync getter
            // on the PrivilegeFilter hot path finds _loginUserInfo already set
            // and does not block a ThreadPool thread via GetAwaiter().GetResult().
            // EnsureLoginUserInfoAsync is a no-op when the user is not authenticated
            // or when _loginUserInfo is already populated.
            await wtm.EnsureLoginUserInfoAsync().ConfigureAwait(false);

            await _next(context);
            if (context.Response.StatusCode == 404)
            {
                await context.Response.WriteAsync(string.Empty);
            }
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
