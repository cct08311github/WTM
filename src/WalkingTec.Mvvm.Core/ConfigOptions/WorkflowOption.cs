namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Optional protection for the workflow engine API endpoints. Only relevant when
    /// <see cref="Configs.EnableWorkflow"/> is true. Both settings default to false (behavior unchanged).
    /// The settings cover the workflow engine's API controllers (types in the Elsa.Server.Api.* namespaces) only;
    /// they do not cover the HTTP activity endpoints, signal endpoints or Swagger. Changes require an application restart.
    /// Since 10.0.0.7 /v1/activities is answered in-process (#216): there is no internal HTTP call any more, so <see cref="InternalCallBaseAddress"/>
    /// is no longer needed for it.
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
        /// Obsolete: since 10.0.0.7 /v1/activities is answered in-process (#216) and this setting is no longer used by it (nor by anything else in the framework).
        /// It is kept so existing configuration still binds, and will be removed in a future major version.
        /// Formerly: optional (default null: unchanged behavior). Base address (for example "https://localhost:5001" or an internal address such as
        /// "http://wtm-app:8080") that the workflow internal call (/v1/activities) always goes to, instead of the scheme://host taken from the incoming request.
        /// Recommended in production, especially behind a reverse proxy or in containers. Must be an absolute http/https URI; an invalid value is ignored (a warning is logged).
        /// When <see cref="RequireAuthentication"/> is true it also takes precedence over Domains:server and AllowedHosts as the trusted target for forwarded credentials
        /// (plain http is still only accepted for loopback hosts). When not set and AllowedHosts is an explicit list without wildcards, only requests whose Host is on
        /// that list are forwarded, others get 400. Changes require an application restart.
        /// </summary>
        [System.Obsolete("Since 10.0.0.7 /v1/activities is answered in-process and no longer uses this setting; it will be removed in a future major version.", false)]
        public string InternalCallBaseAddress { get; set; }
    }
}
