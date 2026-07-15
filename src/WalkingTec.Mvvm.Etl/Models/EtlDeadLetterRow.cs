#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Etl.Models;

/// <summary>
/// ETL-004 — Dead-letter / quarantine store.
/// Records a single row that failed quality rules (Drop path) or failed to load,
/// allowing operators to inspect and optionally replay failures.
/// Only persisted when <see cref="Pipeline.EtlPipelineConfig.EnableDeadLetter"/> is true.
/// </summary>
public class EtlDeadLetterRow : BasePoco
{
    [Required]
    public Guid JobId { get; set; }

    [Required]
    public Guid RunId { get; set; }

    /// <summary>
    /// JSON-serialized row data (column-name → value).
    /// Sanitized before persistence: connection-string credentials are redacted.
    /// </summary>
    public string RowJson { get; set; } = string.Empty;

    /// <summary>Human-readable failure reason (quality-rule violation message or load error).</summary>
    [StringLength(2000)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this row was quarantined.</summary>
    public DateTime QuarantinedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional tenant code when multi-tenancy is enabled.
    /// Populated by the pipeline from the executing job's TenantCode context.
    /// Null in single-tenant deployments.
    /// </summary>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>
    /// Indicates the quarantine source — see <see cref="EtlDeadLetterSource"/> for the
    /// full set of well-known values.
    /// </summary>
    [StringLength(50)]
    public string Source { get; set; } = EtlDeadLetterSource.QualityRule;

    /// <summary>
    /// #673(d) — run-scoped dedupe tri-state:
    /// <list type="bullet">
    /// <item><c>null</c> (default) — legacy rows written before this column existed, or
    /// written by an <see cref="Governance.IEtlGovernanceStore"/> implementation that
    /// doesn't track it. <b>Never</b> touched by the failed-run cleanup below — this is
    /// what makes the column additive/migration-safe: upgrading never deletes existing
    /// dead-letter history.</item>
    /// <item><c>false</c> — written by the run identified by <see cref="RunId"/>, which
    /// has not (yet, or ever) completed successfully. At the START of the job's NEXT
    /// run, <see cref="Governance.IEtlGovernanceStore.ClearDeadLetterFromFailedRunsAsync"/>
    /// deletes rows in this state for the same <see cref="JobId"/> — a retry re-extracts
    /// the same (watermark-unchanged) window and will produce its own up-to-date
    /// diagnostics, so the previous failed attempt's rows are superseded, not
    /// permanent history.</item>
    /// <item><c>true</c> — the owning run completed successfully;
    /// <see cref="Governance.IEtlGovernanceStore.MarkDeadLetterRunSucceededAsync"/> flips
    /// this after a successful flush. These rows represent a genuine, permanent
    /// Quality-rule Drop-path record (the row really was excluded from an otherwise
    /// successful load) and are never auto-deleted by the failed-run cleanup — only by
    /// <see cref="EtlOptions.DeadLetterRetentionDays"/> pruning, if enabled.</item>
    /// </list>
    /// </summary>
    public bool? RunSucceeded { get; set; }
}

/// <summary>
/// Well-known values for <see cref="EtlDeadLetterRow.Source"/>.
/// <para>
/// <b>Migration-safe by design (#673):</b> <see cref="EtlDeadLetterRow.Source"/> is a
/// plain <c>string</c> column (not a persisted enum/int), so adding new well-known
/// values here never requires a schema change or an EF migration in downstream apps —
/// existing rows and existing string comparisons against <see cref="QualityRule"/> /
/// <see cref="LoadError"/> keep working unchanged.
/// </para>
/// </summary>
public static class EtlDeadLetterSource
{
    /// <summary>Quality-rule Drop path (10.5.1+) — a row was excluded from load.</summary>
    public const string QualityRule = "QualityRule";

    /// <summary>Bulk-load failure (#673) — batch could not be written to staging.</summary>
    public const string LoadError = "LoadError";

    /// <summary>
    /// Quality-rule Abort path (#673) — the offending row that triggered
    /// <see cref="Pipeline.EtlQualityRuleViolationException"/> before the run aborted.
    /// Diagnostic only: capturing this row does not change Abort's throw-and-fail semantics.
    /// </summary>
    public const string QualityRuleAbort = "QualityRuleAbort";

    /// <summary>
    /// <see cref="Pipeline.EtlPipelineConfig.TransformFunc"/> threw (#673). Per-row
    /// attribution is not possible for transform failures (the function operates on the
    /// whole batch), so the captured entry is a batch-level marker — see
    /// <see cref="Pipeline.EtlPipelineExecutor"/>'s batch-level capture helper.
    /// </summary>
    public const string TransformError = "TransformError";
}
