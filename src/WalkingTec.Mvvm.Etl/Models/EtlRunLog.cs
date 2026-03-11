#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Etl.Models;

/// <summary>
/// ETL 執行記錄 — 每次 Job 執行產生一筆
/// </summary>
public class EtlRunLog : BasePoco
{
    [Required]
    public Guid JobId { get; set; }

    /// <summary>關聯的 Job 定義</summary>
    public EtlJobDefinition? Job { get; set; }

    public EtlRunTrigger Trigger { get; set; }
    public EtlRunResult Result { get; set; }

    public int ExtractedRows { get; set; }
    public int LoadedRows { get; set; }
    public int ErrorRows { get; set; }
    public long ElapsedMs { get; set; }

    [StringLength(4000)]
    public string? ErrorMessage { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>執行時的 watermark 起始值快照（供重跑用）</summary>
    public string? WatermarkSnapshot { get; set; }
}
