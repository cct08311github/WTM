#nullable enable
using System;

namespace WalkingTec.Mvvm.Etl.Models;

/// <summary>
/// ETL 執行進度（用於即時監控）
/// </summary>
public record EtlProgress
{
    public Guid JobId { get; init; }
    public string JobName { get; init; } = string.Empty;
    public int ProcessedRows { get; init; }
    public int? TotalRows { get; init; }
    /// <summary>Extracting / Loading / Merging / Done</summary>
    public string Phase { get; init; } = "Idle";
    public double? RowsPerSecond { get; init; }
    public DateTime StartedAt { get; init; }

    /// <summary>
    /// #883: the owning job's tenant code (null for a null-tenant/host job). Populated by
    /// <see cref="WalkingTec.Mvvm.Etl.Pipeline.EtlPipelineExecutor.ReportProgress"/> from
    /// <c>EtlPipelineConfig.DeadLetterTenantCode</c> (which, despite the dead-letter-specific
    /// name, already carries the executing job's tenant code generally -- see that field's own
    /// doc comment). <see cref="WalkingTec.Mvvm.Etl.Scheduling.EtlProgressTracker"/> is a
    /// single process-wide, un-scoped dictionary keyed only by <see cref="JobId"/> -- before
    /// this field existed, <c>_EtlMonitorController.Running()</c> returned every tenant's
    /// currently-running jobs (including their <see cref="JobId"/>) to any caller who passed
    /// the role gate, regardless of which tenant they belonged to.
    /// </summary>
    public string? TenantCode { get; init; }
}
