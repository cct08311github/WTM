using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Guards the workflow designer's activity list (<c>/v1/activities</c>, #208, #216).
    /// Every form of the endpoint (any API version segment, trailing slash, sub path, and the former <c>?inneruse=1</c> form) requires a
    /// signed-in user whatever WorkflowOptions:RequireAuthentication says. Since 10.0.0.7 WtmMiddleware answers the list in-process
    /// (<see cref="WorkflowActivitiesDescriber"/>), so there is no internal call any more and no internal form that could be let through.
    /// </summary>
    public static class WorkflowActivitiesGuard
    {
        public const string ActivitiesPath = "/v1/activities";
        /// <summary>The query string of the former internal call; it is no longer treated specially (an anonymous caller gets 401 like on the public form).</summary>
        public const string InternalQuery = "?inneruse=1";

        // Elsa routes v{version:apiVersion}/activities, so the version segment may be v1, v1.0, v01 ... (any case)
        private static readonly Regex ActivitiesPathRegex = new Regex(@"^/v[^/]*/activities(/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex ActivitiesListPathRegex = new Regex(@"^/v[^/]*/activities/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// True for /v{any}/activities and anything below it (case-insensitive, trailing slash included), so another API version
        /// segment, a trailing slash or a sub path cannot be used to reach the engine without passing the guard.
        /// </summary>
        public static bool IsActivitiesRequest(HttpRequest request)
        {
            var path = request?.Path.Value;
            return path != null && ActivitiesPathRegex.IsMatch(path);
        }

        private static bool IsActivitiesListRequest(HttpRequest request)
        {
            var path = request?.Path.Value;
            return path != null && ActivitiesListPathRegex.IsMatch(path);
        }

        /// <summary>True for the activities list request that WtmMiddleware answers itself: any version segment, trailing slash allowed, without the former <c>?inneruse=1</c> query.</summary>
        public static bool IsPublicActivitiesRequest(HttpRequest request)
        {
            return IsActivitiesListRequest(request) && request.QueryString.Value != InternalQuery;
        }

        /// <summary>
        /// True when the caller is signed in: an authenticated principal (WTM cookie, the default scheme, which UseAuthentication has already put in
        /// HttpContext.User) or, failing that, a valid cookie or JWT bearer token validated here (the JWT scheme is not the default one,
        /// so UseAuthentication does not evaluate it). Fails closed.
        /// </summary>
        public static async Task<bool> IsSignedInAsync(HttpContext context)
        {
            try
            {
                if (context.User?.Identity?.IsAuthenticated == true)
                {
                    return true;
                }
                foreach (var scheme in new[] { CookieAuthenticationDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme })
                {
                    var result = await context.AuthenticateAsync(scheme);
                    if (result.Succeeded && result.Principal?.Identity?.IsAuthenticated == true)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Decides whether the request may proceed: every form needs a signed-in user.
        /// </summary>
        public static Task<bool> IsAllowedAsync(HttpContext context)
        {
            return IsSignedInAsync(context);
        }
    }
}
