namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Optional protection for the workflow engine API endpoints. Only relevant when
    /// <see cref="Configs.EnableWorkflow"/> is true. Both settings default to false (behavior unchanged).
    /// The settings cover the workflow engine's API controllers (types in the Elsa.Server.Api.* namespaces) only;
    /// they do not cover the HTTP activity endpoints, signal endpoints or Swagger. Changes require an application restart.
    /// When enabled, set InternalCallBaseAddress (or Domains:server, or list the host in AllowedHosts) so the internal activities call can carry the caller's credentials.
    /// The internal /v1/activities call is made to the request's own scheme://host by default; set <see cref="InternalCallBaseAddress"/> in production
    /// so the target no longer depends on the Host header.
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

        /// <summary>
        /// Optional (default null: unchanged behavior). Base address (for example "https://localhost:5001" or an internal address such as
        /// "http://wtm-app:8080") that the workflow internal call (/v1/activities) always goes to, instead of the scheme://host taken from the incoming request.
        /// Recommended in production, especially behind a reverse proxy or in containers. Must be an absolute http/https URI; an invalid value is ignored (a warning is logged).
        /// When <see cref="RequireAuthentication"/> is true it also takes precedence over Domains:server and AllowedHosts as the trusted target for forwarded credentials
        /// (plain http is still only accepted for loopback hosts). When not set and AllowedHosts is an explicit list without wildcards, only requests whose Host is on
        /// that list are forwarded, others get 400. Changes require an application restart.
        /// </summary>
        public string InternalCallBaseAddress { get; set; }
    }
}
