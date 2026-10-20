using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Requirement: the current WTM user holds the workflow admin role.
    /// </summary>
    public class WorkflowAdminRequirement : IAuthorizationRequirement
    {
    }

    /// <summary>
    /// Evaluates <see cref="WorkflowAdminRequirement"/> against the WTM login user's roles.
    /// </summary>
    public class WorkflowAdminRequirementHandler : AuthorizationHandler<WorkflowAdminRequirement>
    {
        private readonly IHttpContextAccessor _accessor;

        public WorkflowAdminRequirementHandler(IHttpContextAccessor accessor)
        {
            _accessor = accessor;
        }

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, WorkflowAdminRequirement requirement)
        {
            try
            {
                var wtm = _accessor?.HttpContext?.RequestServices?.GetService<WTMContext>();
                if (WorkflowApiSecurity.IsWorkflowAdmin(wtm?.LoginUserInfo?.Roles))
                {
                    context.Succeed(requirement);
                }
            }
            catch
            {
                // fail closed: the requirement is simply not satisfied
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Adds an authorization filter to the workflow engine API controllers (type name starts with "Elsa.Server.Api").
    /// </summary>
    public class WorkflowApiAuthorizationConvention : IControllerModelConvention
    {
        private readonly bool _requireAdminRole;

        public WorkflowApiAuthorizationConvention(bool requireAdminRole)
        {
            _requireAdminRole = requireAdminRole;
        }

        public void Apply(ControllerModel controller)
        {
            if (controller?.ControllerType?.FullName == null
                || controller.ControllerType.FullName.StartsWith(WorkflowApiSecurity.ApiNamespacePrefix, StringComparison.Ordinal) == false)
            {
                return;
            }
            controller.Filters.Add(new AuthorizeFilter(WorkflowApiSecurity.BuildPolicy(_requireAdminRole)));
        }
    }

    /// <summary>
    /// Removes <see cref="WorkflowApiController"/> (route _WorkflowApi) from the application when workflow is disabled, so none of its
    /// actions is routable and nothing can be queried through it (#13). Registered by AddWtmContext when EnableWorkflow is false.
    /// </summary>
    public class WorkflowApiDisabledConvention : IApplicationModelConvention
    {
        public void Apply(ApplicationModel application)
        {
            if (application == null)
            {
                return;
            }
            foreach (var controller in application.Controllers.Where(x => x.ControllerType.AsType() == typeof(WorkflowApiController)).ToList())
            {
                application.Controllers.Remove(controller);
            }
        }
    }

    public static class WorkflowApiSecurity
    {
        /// <summary>
        /// Registers <see cref="WorkflowApiDisabledConvention"/> when workflow is disabled, so _WorkflowApi is only part of the
        /// application when EnableWorkflow is true (#13). Read at startup: changing EnableWorkflow needs a restart.
        /// </summary>
        public static void AddWorkflowApiConvention(Microsoft.Extensions.DependencyInjection.IServiceCollection services, Configs configs)
        {
            if (configs == null || configs.EnableWorkflow == false)
            {
                services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(mvc => mvc.Conventions.Add(new WorkflowApiDisabledConvention()));
            }
        }

        public const string ApiNamespacePrefix = "Elsa.Server.Api.";

        // same admin definition as the workflow management page
        public const string AdminRoleCode = "001";
        public const string AdminRoleName = "流程管理员";

        /// <summary>
        /// Authenticated user via cookie or JWT bearer (the same schemes as AuthorizeJwtWithCookie), optionally with the admin role.
        /// </summary>
        public static AuthorizationPolicy BuildPolicy(bool requireAdminRole)
        {
            var builder = new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser();
            if (requireAdminRole)
            {
                builder.AddRequirements(new WorkflowAdminRequirement());
            }
            return builder.Build();
        }

        public static bool IsWorkflowAdmin(IEnumerable<SimpleRole> roles)
        {
            return roles != null && roles.Any(x => x != null && (x.RoleCode == AdminRoleCode || x.RoleName == AdminRoleName));
        }

        /// <summary>
        /// True when workflow is enabled but its API endpoints are not protected.
        /// </summary>
        public static bool ShouldWarnUnprotected(Configs configs)
        {
            return configs != null && configs.EnableWorkflow && configs.WorkflowOptions?.RequireAuthentication != true;
        }

        /// <summary>
        /// Logs a warning when <see cref="ShouldWarnUnprotected"/> is true. Never throws.
        /// </summary>
        public static void LogUnprotectedWarning(Configs configs, ILogger logger)
        {
            try
            {
                if (ShouldWarnUnprotected(configs))
                {
                    logger?.LogWarning("Workflow is enabled and the workflow management API controllers (Elsa.Server.Api.*) are reachable without WTM authentication. Set WorkflowOptions:RequireAuthentication to true (and optionally WorkflowOptions:RequireAdminRole) to require a signed-in user for them; a restart is required. Other workflow endpoints (HTTP activities, signal endpoints, Swagger) are not covered by this setting.");
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Name of the WTM authentication cookie (same formula as the cookie authentication setup).
        /// </summary>
        public static string GetAuthCookieName(Configs configs)
        {
            return CookieAuthenticationDefaults.CookiePrefix + configs?.CookiePre + "." + AuthConstants.CookieAuthName;
        }

        /// <summary>
        /// Result of <see cref="ResolveSelfCall"/>.
        /// </summary>
        public class SelfCallTarget
        {
            /// <summary>Base address (scheme://host) used for the internal call.</summary>
            public string BaseAddress { get; set; }

            /// <summary>Headers to send, or null when no credentials are forwarded.</summary>
            public Dictionary<string, string> Headers { get; set; }

            /// <summary>True when RequireAuthentication is on but credentials could not be forwarded to a trusted target.</summary>
            public bool CredentialsWithheld { get; set; }

            /// <summary>True when no InternalCallBaseAddress is configured, RequireAuthentication is off, AllowedHosts is an explicit
            /// (non-wildcard) list and the request host is not on it: the internal call must not be made.</summary>
            public bool Rejected { get; set; }

            /// <summary>True when WorkflowOptions:InternalCallBaseAddress is set but is not an absolute http/https URI (it is then ignored).</summary>
            public bool InvalidInternalCallBaseAddress { get; set; }
        }

        /// <summary>
        /// Normalizes WorkflowOptions:InternalCallBaseAddress (trim, drop trailing '/'). Returns null when not set;
        /// <paramref name="invalid"/> is true when it is set but not an absolute http/https URI (treated as not set).
        /// </summary>
        public static string NormalizeInternalCallBaseAddress(string value, out bool invalid)
        {
            invalid = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && string.IsNullOrEmpty(uri.Host) == false
                && string.IsNullOrEmpty(uri.UserInfo)
                && string.IsNullOrEmpty(uri.Query)
                && string.IsNullOrEmpty(uri.Fragment))
            {
                return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            }
            invalid = true;
            return null;
        }

        /// <summary>
        /// True when AllowedHosts is non-blank and none of its entries contains a wildcard.
        /// </summary>
        public static bool IsExplicitHostList(string allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(allowedHosts))
            {
                return false;
            }
            var entries = allowedHosts.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            return entries.Count > 0 && entries.Any(x => x.Contains("*")) == false;
        }

        /// <summary>
        /// Decides where the internal call for the workflow activities goes and which credentials it carries.
        /// WorkflowOptions:InternalCallBaseAddress set (valid absolute http/https URI): it is always the target, regardless of the Host header;
        /// with RequireAuthentication off no headers are sent, with it on it takes precedence over Domains:server and AllowedHosts as the trusted target.
        /// Not set, RequireAuthentication off: the request's own address and no extra headers (unchanged behavior), except that when AllowedHosts
        /// is an explicit non-wildcard list and the host is not on it, the result is <see cref="SelfCallTarget.Rejected"/>.
        /// Not set, RequireAuthentication on: credentials (Authorization header and the WTM auth cookie only) are forwarded only to
        /// the configured "server" domain, or, when none is configured, to the request host if it is listed (non-wildcard)
        /// in AllowedHosts. Credentials are never sent over plain http to a non-loopback host.
        /// </summary>
        public static SelfCallTarget ResolveSelfCall(Configs configs, string allowedHosts, string scheme, string host, string authorization, string cookieHeader)
        {
            var requestBase = $"{scheme}://{host}";
            var internalBase = NormalizeInternalCallBaseAddress(configs?.WorkflowOptions?.InternalCallBaseAddress, out var invalidInternal);
            if (configs?.WorkflowOptions?.RequireAuthentication != true)
            {
                if (internalBase != null)
                {
                    return new SelfCallTarget { BaseAddress = internalBase };
                }
                if (IsExplicitHostList(allowedHosts) && IsAllowedHost(allowedHosts, host) == false)
                {
                    return new SelfCallTarget { Rejected = true, InvalidInternalCallBaseAddress = invalidInternal };
                }
                return new SelfCallTarget { BaseAddress = requestBase, InvalidInternalCallBaseAddress = invalidInternal };
            }

            string target = null;
            if (internalBase != null)
            {
                target = internalBase;
            }
            else if (configs.Domains != null && configs.Domains.TryGetValue("server", out var server) && string.IsNullOrWhiteSpace(server?.Address) == false)
            {
                target = server.Address.Trim().TrimEnd('/');
            }
            else if (IsAllowedHost(allowedHosts, host))
            {
                target = requestBase;
            }

            var headers = new Dictionary<string, string>();
            if (target != null && IsSafeTarget(target))
            {
                if (string.IsNullOrEmpty(authorization) == false)
                {
                    headers["Authorization"] = authorization;
                }
                var cookie = FilterAuthCookie(cookieHeader, GetAuthCookieName(configs));
                if (string.IsNullOrEmpty(cookie) == false)
                {
                    headers["Cookie"] = cookie;
                }
                return new SelfCallTarget { BaseAddress = target, Headers = headers.Count == 0 ? null : headers, InvalidInternalCallBaseAddress = invalidInternal };
            }
            return new SelfCallTarget { BaseAddress = target ?? requestBase, Headers = null, CredentialsWithheld = true, InvalidInternalCallBaseAddress = invalidInternal };
        }

        /// <summary>
        /// True when the host (with or without port) equals a non-wildcard entry of the semicolon separated AllowedHosts value.
        /// </summary>
        public static bool IsAllowedHost(string allowedHosts, string host)
        {
            if (string.IsNullOrWhiteSpace(allowedHosts) || string.IsNullOrWhiteSpace(host))
            {
                return false;
            }
            var hostOnly = StripPort(host);
            foreach (var entry in allowedHosts.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var e = entry.Trim();
                if (e.Length == 0 || e.Contains("*"))
                {
                    continue;
                }
                if (string.Equals(e, host, StringComparison.OrdinalIgnoreCase)
                    || (EntryHasPort(e) == false && string.Equals(e, hostOnly, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool EntryHasPort(string entry)
        {
            return entry.StartsWith("[") ? entry.EndsWith("]") == false : entry.Contains(":");
        }

        private static string StripPort(string host)
        {
            if (host.StartsWith("["))
            {
                var end = host.IndexOf(']');
                return end > 0 ? host.Substring(0, end + 1) : host;
            }
            var i = host.LastIndexOf(':');
            return i > 0 ? host.Substring(0, i) : host;
        }

        /// <summary>
        /// https is always acceptable; plain http only for loopback hosts.
        /// </summary>
        public static bool IsSafeTarget(string address)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) == false)
            {
                return false;
            }
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                return true;
            }
            if (uri.Scheme != Uri.UriSchemeHttp)
            {
                return false;
            }
            return uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Keeps only the WTM auth cookie (including its chunks, named {name}C1, {name}C2, ...) from a raw Cookie header.
        /// </summary>
        public static string FilterAuthCookie(string cookieHeader, string authCookieName)
        {
            if (string.IsNullOrEmpty(cookieHeader) || string.IsNullOrEmpty(authCookieName))
            {
                return null;
            }
            var chunk = new Regex("^" + Regex.Escape(authCookieName) + @"C\d+$", RegexOptions.CultureInvariant);
            var kept = new List<string>();
            foreach (var part in cookieHeader.Split(';'))
            {
                var pair = part.Trim();
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                var name = pair.Substring(0, eq).Trim();
                if (name == authCookieName || chunk.IsMatch(name))
                {
                    kept.Add(pair);
                }
            }
            return kept.Count == 0 ? null : string.Join("; ", kept);
        }
    }
}
