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

    /// <summary>
    /// Dead-letter durability/dedup tradeoff (#700, follow-up to #673). Default
    /// <see cref="EtlDeadLetterFlushMode.OncePerRun"/> preserves #673's behaviour
    /// exactly: the whole run's buffer is written in a single flush after the run's
    /// outcome is known, which enables the run-scoped dedupe in
    /// <see cref="Governance.IEtlGovernanceStore.ClearDeadLetterFromFailedRunsAsync"/>
    /// but means a hard process crash mid-run (kill -9, host reboot, OOM) loses ALL
    /// dead-letter diagnostics buffered for that run — nothing was ever written.
    /// <para>
    /// Set to <see cref="EtlDeadLetterFlushMode.Periodic"/> to opt into
    /// crash-durability: <see cref="Pipeline.EtlPipelineExecutor"/> writes the buffer
    /// to the store every <see cref="DeadLetterFlushThreshold"/> entries instead of
    /// only at the end. This does NOT reintroduce #673's duplicate-on-rerun bug —
    /// partial flushes are written with the SAME <c>RunId</c> and
    /// <c>RunSucceeded = false</c> as the final flush, so the existing
    /// <see cref="Governance.IEtlGovernanceStore.ClearDeadLetterFromFailedRunsAsync"/>
    /// start-of-run cleanup (which deletes by <c>JobId</c> + <c>RunSucceeded == false</c>,
    /// not by flush-batch) removes every partially-flushed row from a crashed/failed
    /// run exactly as it already removes a fully-buffered failed run's rows. On
    /// success, <see cref="Governance.IEtlGovernanceStore.MarkDeadLetterRunSucceededAsync"/>
    /// flips ALL rows for that <c>RunId</c> — not just the final batch — to
    /// <c>RunSucceeded = true</c>, because it is keyed by <c>(JobId, RunId)</c>, not by
    /// which flush call wrote them.
    /// </para>
    /// </summary>
    public EtlDeadLetterFlushMode DeadLetterFlushMode { get; set; } = EtlDeadLetterFlushMode.OncePerRun;

    /// <summary>
    /// Number of buffered dead-letter entries that triggers a partial flush when
    /// <see cref="DeadLetterFlushMode"/> = <see cref="EtlDeadLetterFlushMode.Periodic"/>.
    /// Ignored when <see cref="DeadLetterFlushMode"/> = <see cref="EtlDeadLetterFlushMode.OncePerRun"/>
    /// (the default). Default 500 — bounds the worst-case diagnostics loss on a crash
    /// to at most this many not-yet-flushed entries, while keeping the extra DB
    /// round-trips infrequent relative to <see cref="MaxDeadLetterRowsPerRun"/>'s
    /// default of 10000. Values &lt;= 0 are treated as 1 by
    /// <see cref="Pipeline.EtlPipelineExecutor"/> (flush after every entry) rather than
    /// disabling periodic flushing outright.
    /// </summary>
    public int DeadLetterFlushThreshold { get; set; } = 500;
}

/// <summary>
/// #700: dead-letter buffer flush strategy — see <see cref="EtlOptions.DeadLetterFlushMode"/>.
/// </summary>
public enum EtlDeadLetterFlushMode
{
    /// <summary>
    /// Default (#673 behaviour, unchanged). The whole run's dead-letter buffer is
    /// written in a single flush after the run's outcome is known. Maximizes the
    /// run-scoped dedupe guarantee's simplicity; a hard crash mid-run loses all of
    /// that run's buffered diagnostics (nothing was written yet).
    /// </summary>
    OncePerRun = 0,

    /// <summary>
    /// Opt-in crash-durability (#700). The buffer is flushed every
    /// <see cref="EtlOptions.DeadLetterFlushThreshold"/> entries in addition to the
    /// final flush. Rerun-dedupe is preserved — see
    /// <see cref="EtlOptions.DeadLetterFlushMode"/> for why partial flushes are safe
    /// under the existing <c>RunId</c>/<c>RunSucceeded</c> cleanup.
    /// </summary>
    Periodic = 1,
}
