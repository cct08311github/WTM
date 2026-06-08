#nullable enable
namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Configuration for a single scheduled dashboard snapshot job.
/// Add one or more instances to <see cref="DashboardSnapshotOptions.Jobs"/>.
/// </summary>
public sealed class ScheduledDashboardJobConfig
{
    /// <summary>
    /// A short, unique identifier for this job within the host application.
    /// Used for logging and de-duplication.
    /// Required; must not be empty.
    /// </summary>
    public string JobId { get; set; } = "";

    /// <summary>
    /// ID of the dashboard to snapshot.
    /// </summary>
    public string DashboardId { get; set; } = "";

    /// <summary>
    /// Optional tenant context. Passed through to <see cref="IDashboardService.GetAsync"/>.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Output format. <see cref="DashboardExportFormat.Excel"/> is available out of the box.
    /// <see cref="DashboardExportFormat.Pdf"/> and <see cref="DashboardExportFormat.Png"/>
    /// require a host-registered <see cref="IDashboardRenderer"/>.
    /// </summary>
    public DashboardExportFormat Format { get; set; } = DashboardExportFormat.Excel;

    /// <summary>
    /// Cron-style schedule in standard 5-field UNIX cron notation (minute hour day month weekday).
    /// Example: <c>"0 8 * * 1"</c> = every Monday at 08:00 UTC.
    /// When null or empty, the job is disabled and never runs automatically.
    /// </summary>
    public string? CronExpression { get; set; }
}
