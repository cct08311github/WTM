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
}
