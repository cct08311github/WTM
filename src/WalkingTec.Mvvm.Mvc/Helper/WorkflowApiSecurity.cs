using System;
using System.Collections.Generic;
using System.Linq;
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

    public static class WorkflowApiSecurity
    {
        public const string ApiNamespacePrefix = "Elsa.Server.Api";

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
                    logger?.LogWarning("Workflow is enabled and the workflow API endpoints are reachable without WTM authentication. Set WorkflowOptions:RequireAuthentication to true (and optionally WorkflowOptions:RequireAdminRole) to require a signed-in user.");
                }
            }
            catch
            {
            }
        }
    }
}
