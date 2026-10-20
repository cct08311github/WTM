using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Guards the workflow designer's activity list (<c>/v1/activities</c>, #208).
    /// The public form of the endpoint requires a signed-in user whatever WorkflowOptions:RequireAuthentication says.
    /// The internal form (<c>?inneruse=1</c>, the call WtmMiddleware makes to the engine) requires either a signed-in user or
    /// a per-process random secret that never leaves the process, so it cannot be used to reach the engine anonymously.
    /// </summary>
    public static class WorkflowActivitiesGuard
    {
        public const string ActivitiesPath = "/v1/activities";
        public const string InternalQuery = "?inneruse=1";

        /// <summary>Header that carries the per-process secret on the internal call.</summary>
        public const string InternalSecretHeader = "X-Wtm-Internal-Call";

        // generated once per process, memory only, never logged
        private static readonly string ProcessSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        /// <summary>Name of the HttpClient (registered by AddWtmWorkflow) used for the internal call; it does not follow redirects.</summary>
        public const string InternalClientName = "WtmWorkflowInternal";

        /// <summary>
        /// Registers the named HttpClient of the internal call with redirects disabled (a 3xx is returned as is and treated as a failure).
        /// Only this named client is affected; every other CallAPI call keeps its behavior.
        /// </summary>
        public static void AddInternalHttpClient(IServiceCollection services)
        {
            services.AddHttpClient(InternalClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false });
        }

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

        /// <summary>True for the public form (not the internal one) of the activities list request, any version segment.</summary>
        public static bool IsPublicActivitiesRequest(HttpRequest request)
        {
            return IsActivitiesListRequest(request) && request.QueryString.Value != InternalQuery;
        }

        /// <summary>True for the internal form of the activities list request.</summary>
        public static bool IsInternalActivitiesRequest(HttpRequest request)
        {
            return IsActivitiesListRequest(request) && request.QueryString.Value == InternalQuery;
        }

        /// <summary>
        /// Returns a copy of <paramref name="headers"/> (which may be null) with the internal-call secret added.
        /// </summary>
        public static Dictionary<string, string> WithInternalSecret(Dictionary<string, string> headers)
        {
            var rv = headers == null ? new Dictionary<string, string>() : new Dictionary<string, string>(headers);
            rv[InternalSecretHeader] = ProcessSecret;
            return rv;
        }

        /// <summary>Constant-time check of a presented secret against this process' secret.</summary>
        public static bool IsValidInternalSecret(string presented)
        {
            return IsValidInternalSecret(presented, ProcessSecret);
        }

        internal static bool IsValidInternalSecret(string presented, string expected)
        {
            if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
            {
                return false;
            }
            // FixedTimeEquals returns false immediately for different lengths; the secret length is not secret
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
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
        /// Decides whether the request may proceed: public form needs a signed-in user; internal form needs the secret or a signed-in user.
        /// </summary>
        public static async Task<bool> IsAllowedAsync(HttpContext context)
        {
            if (IsInternalActivitiesRequest(context.Request)
                && IsValidInternalSecret(context.Request.Headers[InternalSecretHeader].ToString()))
            {
                return true;
            }
            return await IsSignedInAsync(context);
        }
    }
}
