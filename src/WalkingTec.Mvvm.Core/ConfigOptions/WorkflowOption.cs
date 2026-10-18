namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Optional protection for the workflow engine API endpoints. Only relevant when
    /// <see cref="Configs.EnableWorkflow"/> is true. Both settings default to false (behavior unchanged).
    /// The settings cover the workflow engine's API controllers (types in the Elsa.Server.Api.* namespaces) only;
    /// they do not cover the HTTP activity endpoints, signal endpoints or Swagger. Changes require an application restart.
    /// When enabled, set Domains:server (or list the host in AllowedHosts) so the internal activities call can carry the caller's credentials.
    /// </summary>
    public class WorkflowOption
    {
        /// <summary>
        /// false (default, unchanged behavior): the workflow engine API controllers apply no WTM authentication.
        /// true: they require an authenticated WTM user (cookie or JWT bearer).
        /// </summary>
        public bool RequireAuthentication { get; set; }

        /// <summary>
        /// Only applies when <see cref="RequireAuthentication"/> is true.
        /// false (default): any authenticated user is accepted.
        /// true: additionally require the workflow admin role (role code "001" or the role named as in the workflow management page).
        /// </summary>
        public bool RequireAdminRole { get; set; }
    }
}
