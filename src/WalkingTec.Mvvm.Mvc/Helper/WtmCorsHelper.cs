using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using ConfiguredCorsPolicy = WalkingTec.Mvvm.Core.CorsPolicy;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Builds the CORS policies used by AddWtmCrossDomain / UseWtmCrossDomain and reports risky settings at startup.
    /// </summary>
    public static class WtmCorsHelper
    {
        /// <summary>Name of the policy registered when no Policy is configured.</summary>
        public const string DefaultPolicyName = "_donotusedefault";

        /// <summary>
        /// Split a comma separated domain list. Entries are trimmed and empty entries are ignored;
        /// a null or empty value yields an empty array instead of throwing.
        /// </summary>
        public static string[] ParseDomains(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                return Array.Empty<string>();
            }
            return domain.Split(',')
                         .Select(x => x.Trim())
                         .Where(x => x.Length > 0)
                         .ToArray();
        }

        /// <summary>Configure a policy for a configured Domain list (credentials are allowed, as before).</summary>
        public static void ConfigureConfiguredPolicy(CorsPolicyBuilder builder, string domain)
        {
            var domains = ParseDomains(domain);
            // with no usable origin the policy simply matches nothing
            builder.WithOrigins(domains)
                   .AllowAnyHeader()
                   .AllowAnyMethod()
                   .AllowCredentials()
                   .WithExposedHeaders("Content-Disposition");
        }

        /// <summary>Configure the built-in policy used when no Policy is configured.</summary>
        public static void ConfigureDefaultPolicy(CorsPolicyBuilder builder, Cors options)
        {
            if (options == null || options.AllowCredentialsWithAnyOrigin)
            {
                // unchanged default: any origin is reflected back, with credentials
                builder.SetIsOriginAllowed((a) => true)
                       .AllowAnyHeader()
                       .AllowAnyMethod()
                       .AllowCredentials()
                       .WithExposedHeaders("Content-Disposition");
            }
            else
            {
                // any origin, but no credentials: the browser gets "Access-Control-Allow-Origin: *"
                builder.AllowAnyOrigin()
                       .AllowAnyHeader()
                       .AllowAnyMethod()
                       .WithExposedHeaders("Content-Disposition");
            }
        }

        /// <summary>
        /// Log CORS configuration risks. Never throws.
        /// </summary>
        public static void LogStartupDiagnostics(Cors options, bool isDevelopment, ILogger logger)
        {
            if (options == null || logger == null)
            {
                return;
            }
            try
            {
                var policies = options.Policy ?? new List<ConfiguredCorsPolicy>();
                foreach (var item in policies)
                {
                    if (item != null && ParseDomains(item.Domain).Length == 0)
                    {
                        logger.LogWarning("CorsOptions policy '{PolicyName}' has no usable Domain entries, so it allows no origin. Set Domain to a comma separated list of allowed origins.", item.Name);
                    }
                }
                if (options.EnableAll == true && policies.Count == 0)
                {
                    if (options.AllowCredentialsWithAnyOrigin)
                    {
                        const string msg = "CorsOptions:EnableAll is true without an explicit Policy, so the default CORS policy allows ANY origin together with credentials (cookies and Authorization headers). Any website can then make authenticated cross-origin calls on behalf of a signed-in user. Configure CorsOptions:Policy with the origins you trust" +
                                           " (or set CorsOptions:AllowCredentialsWithAnyOrigin=false to drop credentials).";
                        if (isDevelopment)
                        {
                            logger.LogWarning(msg);
                        }
                        else
                        {
                            logger.LogCritical(msg);
                        }
                    }
                    else
                    {
                        logger.LogInformation("CorsOptions:EnableAll is true without an explicit Policy: any origin is allowed, without credentials. Configure CorsOptions:Policy to restrict origins.");
                    }
                }
            }
            catch (Exception)
            {
                // diagnostics must never break startup
            }
        }
    }
}
