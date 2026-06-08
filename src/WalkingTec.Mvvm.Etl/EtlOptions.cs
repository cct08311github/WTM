#nullable enable
namespace WalkingTec.Mvvm.Etl;

/// <summary>
/// Global ETL module options — registered via <c>services.Configure&lt;EtlOptions&gt;(…)</c>
/// or bound from <c>appsettings.json</c> section <c>"WtmEtl"</c>.
/// </summary>
public sealed class EtlOptions
{
    /// <summary>
    /// Number of days to retain <see cref="Models.EtlRunLog"/> records.
    /// <para>
    /// <b>Default 0 = keep forever</b> (preserves pre-10.6 behaviour exactly).
    /// Set to a positive value (e.g. 90) to enable automatic pruning: the
    /// <see cref="Scheduling.EtlSchedulerService.PruneRunLogsAsync"/> method
    /// deletes logs whose <c>StartedAt</c> is older than <c>UtcNow − RetentionDays</c>
    /// via a single <c>ExecuteDeleteAsync</c> call.
    /// </para>
    /// <para>
    /// Pruning is triggered once per application startup by
    /// <see cref="Scheduling.EtlHostedService"/> after jobs are loaded, and is
    /// designed to be a fire-and-forget background sweep — failures are logged
    /// but never surface to the scheduler.
    /// </para>
    /// </summary>
    public int RunLogRetentionDays { get; set; }
}
