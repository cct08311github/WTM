#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Etl.Models;

/// <summary>
/// ETL-005 — Data lineage record.
/// Captures per-run provenance: source kind, target table, column mappings,
/// and row counts, so operators can answer "where did this column come from".
/// Written at the end of every successful run when
/// <see cref="Pipeline.EtlPipelineConfig.EnableLineage"/> is true.
/// </summary>
public class EtlLineageRecord : BasePoco
{
    [Required]
    public Guid JobId { get; set; }

    [Required]
    public Guid RunId { get; set; }

    /// <summary>Source kind identifier (e.g. "SqlServer", "Oracle", "CSV", "rest").</summary>
    [StringLength(50)]
    public string SourceKind { get; set; } = string.Empty;

    /// <summary>Target table name written to.</summary>
    [StringLength(200)]
    public string TargetTable { get; set; } = string.Empty;

    /// <summary>
    /// JSON dictionary of column mappings applied during this run
    /// (source-column → target-column). Null / empty when no explicit mapping
    /// was configured (1:1 same-name passthrough).
    /// </summary>
    public string? ColumnMappingsJson { get; set; }

    /// <summary>Number of rows extracted from the source.</summary>
    public int ExtractedRows { get; set; }

    /// <summary>Number of rows loaded to the target table.</summary>
    public int LoadedRows { get; set; }

    /// <summary>
    /// Number of rows rejected by quality rules during this run.
    /// 0 when no quality rules are configured or no violations occurred.
    /// </summary>
    public int QualityFailedRows { get; set; }

    /// <summary>UTC timestamp when the run completed.</summary>
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
