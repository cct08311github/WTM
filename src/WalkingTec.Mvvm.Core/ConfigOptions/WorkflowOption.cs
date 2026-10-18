namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Optional protection for the workflow engine API endpoints. Only relevant when
    /// <see cref="Configs.EnableWorkflow"/> is true. Both settings default to false (behavior unchanged).
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
