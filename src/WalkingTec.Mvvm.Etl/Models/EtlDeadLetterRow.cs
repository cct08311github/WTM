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
    /// Indicates the quarantine source — "QualityRule" (Drop path) or "LoadError".
    /// </summary>
    [StringLength(50)]
    public string Source { get; set; } = EtlDeadLetterSource.QualityRule;
}

/// <summary>Well-known values for <see cref="EtlDeadLetterRow.Source"/>.</summary>
public static class EtlDeadLetterSource
{
    public const string QualityRule = "QualityRule";
    public const string LoadError   = "LoadError";
}
