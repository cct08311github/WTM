#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Etl.Models;

/// <summary>
/// ETL Job 定義 — 描述一個定時資料導入任務的配置
/// </summary>
public class EtlJobDefinition : BasePoco
{
    [Display(Name = "Job 名稱")]
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "描述")]
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>Cron 表達式（Quartz 格式，如 "0 0 2 * * ?"）</summary>
    [Display(Name = "排程 (Cron)")]
    [Required]
    [StringLength(100)]
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>Job 實作類別的完整型別名</summary>
    [Required]
    [StringLength(500)]
    public string JobClassName { get; set; } = string.Empty;

    [Display(Name = "狀態")]
    public EtlJobStatus Status { get; set; } = EtlJobStatus.Disabled;

    /// <summary>引用 Configs.Connections 的 Key（appsettings.json 中定義）</summary>
    [Display(Name = "來源連線 Key")]
    [Required]
    [StringLength(100)]
    public string SourceCsKey { get; set; } = string.Empty;

    /// <summary>來源資料庫類型</summary>
    [Display(Name = "來源 DB 類型")]
    public DBTypeEnum SourceDbType { get; set; }

    // ─── Watermark 設定 ───

    [Display(Name = "Watermark 模式")]
    public EtlWatermarkType WatermarkType { get; set; } = EtlWatermarkType.FullLoad;

    /// <summary>Watermark 欄位名（如 "UpdatedAt" 或 "OrderId"）</summary>
    [StringLength(100)]
    public string? WatermarkColumn { get; set; }

    /// <summary>上次成功完成後的 watermark 值（JSON 序列化）</summary>
    public string? LastWatermarkValue { get; set; }

    /// <summary>首次執行的起始值（null = 全量首跑）</summary>
    public string? InitialWatermarkValue { get; set; }

    /// <summary>Watermark 時區（IANA 格式，如 "Asia/Taipei"），存儲統一用 UTC</summary>
    [StringLength(50)]
    public string WatermarkTimeZone { get; set; } = "UTC";

    // ─── 執行控制 ───

    /// <summary>跳過接下來 N 次排程觸發</summary>
    public int SkipCount { get; set; }

    /// <summary>失敗後最大重試次數</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>單次執行超時（分鐘）</summary>
    public int TimeoutMinutes { get; set; } = 60;

    // ─── 執行狀態（唯讀顯示） ───

    public DateTime? LastRunAt { get; set; }
    public DateTime? NextFireAt { get; set; }

    [StringLength(2000)]
    public string? LastError { get; set; }
}
