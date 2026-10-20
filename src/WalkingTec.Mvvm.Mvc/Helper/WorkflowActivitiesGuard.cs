using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;

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

        /// <summary>
        /// True for /v1/activities and anything below it (case-insensitive, trailing slash included), so a trailing slash or a
        /// sub path cannot be used to reach the engine without passing the guard.
        /// </summary>
        public static bool IsActivitiesRequest(HttpRequest request)
        {
            var path = request?.Path.Value;
            return path != null
                && (path.Equals(ActivitiesPath, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(ActivitiesPath + "/", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>True for the public form (not the internal one) of the activities request.</summary>
        public static bool IsPublicActivitiesRequest(HttpRequest request)
        {
            return request != null && request.Path == ActivitiesPath && request.QueryString.Value != InternalQuery;
        }

        /// <summary>True for the internal form of the activities request.</summary>
        public static bool IsInternalActivitiesRequest(HttpRequest request)
        {
            return request != null && request.Path == ActivitiesPath && request.QueryString.Value == InternalQuery;
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
