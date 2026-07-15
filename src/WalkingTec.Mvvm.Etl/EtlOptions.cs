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

    /// <summary>
    /// Number of days to retain <see cref="Models.EtlDeadLetterRow"/> records (#673).
    /// <para>
    /// <b>Default 0 = keep forever</b> (preserves pre-10.14 behaviour exactly — dead-letter
    /// rows are never automatically deleted unless an operator opts in). Set to a
    /// positive value (e.g. 90) to enable automatic pruning: the
    /// <see cref="Scheduling.EtlSchedulerService.PruneDeadLetterAsync"/> method deletes
    /// rows whose <see cref="Models.EtlDeadLetterRow.QuarantinedAt"/> is older than
    /// <c>UtcNow − DeadLetterRetentionDays</c> via a single <c>ExecuteDeleteAsync</c> call.
    /// </para>
    /// <para>
    /// Mirrors <see cref="RunLogRetentionDays"/>: pruning is triggered once per
    /// application startup by <see cref="Scheduling.EtlHostedService"/> after jobs are
    /// loaded, fire-and-forget — failures are logged but never surface to the scheduler,
    /// and (like <see cref="RunLogRetentionDays"/>) the knob defaults to disabled so
    /// upgrading to a version that has it never silently deletes existing data.
    /// </para>
    /// </summary>
    public int DeadLetterRetentionDays { get; set; }

    /// <summary>
    /// Maximum dead-letter rows buffered and persisted per run on the
    /// <c>EnableDeadLetter</c> path; excess rows are dropped with a truncation marker.
    /// Default 10000.
    /// <para>
    /// #700 follow-up to #673: dead-letter capture moved from a per-batch flush to a
    /// single buffered flush-once-per-run (enabling run-scoped de-duplication). The
    /// buffer is bounded in memory by this cap — a run producing more violations than
    /// this is almost certainly misconfigured (e.g. a quality rule that rejects nearly
    /// every row), and capturing unbounded rows in memory risks OOM long before the DB
    /// write. Once the cap is reached, a single truncation-marker entry is appended
    /// (never silently — the marker documents that truncation happened) and further
    /// entries for that run are dropped. Read by <see cref="Pipeline.EtlPipelineExecutor"/>
    /// at construction time — see <c>EtlQuartzJob.Execute</c> /
    /// <see cref="Scheduling.EtlSchedulerService.DryRunAsync"/> for the wiring.
    /// </para>
    /// </summary>
    public int MaxDeadLetterRowsPerRun { get; set; } = 10_000;
}
