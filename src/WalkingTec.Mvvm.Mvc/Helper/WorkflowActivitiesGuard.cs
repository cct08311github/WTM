using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Guards the Elsa workflow API (<c>/v1/*</c>, #208, #216, #225, #256), including the designer's activity list (<c>/v1/activities</c>).
    /// Every form of an Elsa endpoint (any API version segment, trailing slash, sub path, and the former <c>?inneruse=1</c> form) requires a
    /// signed-in user whatever WorkflowOptions:RequireAuthentication says. Since 10.0.0.7 WtmMiddleware answers the list in-process
    /// (<see cref="WorkflowActivitiesDescriber"/>), so there is no internal call any more and no internal form that could be let through.
    /// </summary>
    public static class WorkflowActivitiesGuard
    {
        // Elsa routes v{version:apiVersion}/activities, so the version segment may be v1, v1.0, v01 ... (any case)
        private static readonly Regex ActivitiesPathRegex = new Regex(@"^/+v[^/]*/activities(/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        // Only the first path segment after the version that Elsa.Server.Api 2.17.0 actually routes, so other applications' own /v1/... routes
        // (and any unknown path) are left alone. Keep in sync when Elsa.Server.Api is upgraded.
        public static readonly System.Collections.Generic.IReadOnlyList<string> ElsaApiSegments = new[]
        {
            "activities", "designer", "features", "scripting", "signals", "version", "workflow-channels", "workflow-definitions",
            "workflow-instances", "workflow-providers", "workflow-registry", "workflow-storage-providers", "workflows"
        };
        private static readonly Regex ElsaApiPathRegex = new Regex(
            @"^/+v[^/]*/+(" + string.Join("|", System.Linq.Enumerable.Select(ElsaApiSegments, Regex.Escape)) + @")(/|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex ActivitiesListPathRegex = new Regex(@"^/+v[^/]*/activities/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// True for /v{any}/activities and anything below it (case-insensitive, trailing slash included), so another API version
        /// segment, a trailing slash or a sub path cannot be used to reach the engine without passing the guard.
        /// </summary>
        public static bool IsActivitiesRequest(HttpRequest request)
        {
            var path = request?.Path.Value;
            return path != null && ActivitiesPathRegex.IsMatch(path);
        }

        /// <summary>
        /// True for an Elsa API endpoint (#256): /v{any}/{segment} and anything below it, where segment is one of the routes of Elsa.Server.Api
        /// (<see cref="ElsaApiSegments"/>, which includes activities and designer). Case-insensitive; any version segment, trailing slash, sub path
        /// and leading extra slashes count. Other /v1/... paths are not Elsa's and do not match.
        /// </summary>
        public static bool IsElsaApiRequest(HttpRequest request)
        {
            var path = request?.Path.Value;
            return path != null && ElsaApiPathRegex.IsMatch(path);
        }

        /// <summary>
        /// True for the activities list request that WtmMiddleware answers itself: any version segment, trailing slash and leading
        /// extra slashes allowed, with any query string (the former <c>?inneruse=1</c> form is no longer special).
        /// </summary>
        public static bool IsActivitiesListRequest(HttpRequest request)
        {
            var path = request?.Path.Value;
            return path != null && ActivitiesListPathRegex.IsMatch(path);
        }

        /// <summary>The list is only answered for GET and HEAD; other methods go on to the engine.</summary>
        public static bool IsReadMethod(HttpRequest request)
        {
            return HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
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
                        // JWT bearer is not the default scheme, so HttpContext.User is still anonymous here; WTMContext.LoginUserInfo
                        // (and the tenant) are read from HttpContext.User, so hand the validated principal over
                        context.User = result.Principal;
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
        /// <remarks>Does not apply WorkflowOptions:RequireAuthentication / RequireAdminRole; see <see cref="AuthorizeAsync"/>.</remarks>
        public static Task<bool> IsAllowedAsync(HttpContext context)
        {
            return IsSignedInAsync(context);
        }

        /// <summary>
        /// When WorkflowOptions:RequireAuthentication is on, applies the same policy the Elsa API controllers get from
        /// <see cref="WorkflowApiAuthorizationConvention"/> (cookie or JWT, plus the workflow admin role when RequireAdminRole is on), because the
        /// in-process answer no longer passes through the controller's AuthorizeFilter (#216).
        /// Returns 200 to proceed, 401 when not authenticated, 403 when authenticated but not permitted. Fails closed (throws) when the
        /// authorization services are missing; WtmMiddleware turns that into a 500.
        /// </summary>
        public static async Task<int> AuthorizeAsync(HttpContext context, Core.Configs configs)
        {
            if (configs?.WorkflowOptions?.RequireAuthentication != true)
            {
                return StatusCodes.Status200OK;
            }
            var evaluator = context.RequestServices.GetRequiredService<IPolicyEvaluator>();
            var policy = WorkflowApiSecurity.BuildPolicy(configs.WorkflowOptions.RequireAdminRole);
            var authn = await evaluator.AuthenticateAsync(policy, context);
            var authz = await evaluator.AuthorizeAsync(policy, authn, context, null);
            if (authz.Succeeded)
            {
                return StatusCodes.Status200OK;
            }
            return authz.Challenged ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
        }
    }
}
