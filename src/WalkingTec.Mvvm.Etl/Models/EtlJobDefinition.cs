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

    // ─── 目標設定 ───

    /// <summary>引用 Configs.Connections 的 Key</summary>
    [Display(Name = "目標連線 Key")]
    [Required]
    [StringLength(100)]
    public string TargetCsKey { get; set; } = string.Empty;

    /// <summary>目標資料庫類型</summary>
    [Display(Name = "目標 DB 類型")]
    public DBTypeEnum TargetDbType { get; set; }

    /// <summary>目標資料表名稱</summary>
    [Display(Name = "目標資料表")]
    [Required]
    [StringLength(100)]
    public string TargetTableName { get; set; } = string.Empty;

    /// <summary>合併主鍵欄位（Merge 模式必填；Replace 模式忽略）</summary>
    [Display(Name = "合併主鍵 (Merge Key)")]
    [StringLength(100)]
    public string MergeKeyColumn { get; set; } = string.Empty;

    /// <summary>SQL 查詢模板</summary>
    [Display(Name = "查詢模板 (SQL)")]
    [Required]
    public string QueryTemplate { get; set; } = string.Empty;

    /// <summary>
    /// 載入模式（10.5+）。預設 <see cref="EtlLoadMode.Merge"/>；
    /// 設為 <see cref="EtlLoadMode.Replace"/> 啟用「先刪後插」，
    /// 配合 <see cref="ReplaceWhereClause"/> 使用。
    /// </summary>
    [Display(Name = "載入模式")]
    public EtlLoadMode LoadMode { get; set; } = EtlLoadMode.Merge;

    /// <summary>
    /// Replace 模式下的 DELETE WHERE 子句（不含 "WHERE" 關鍵字本身）。
    /// 例：<c>OrderDate &gt;= '2026-01-01'</c>。為 null/空白則刪整張
    /// target table。Merge 模式忽略。
    /// </summary>
    [Display(Name = "Replace 模式條件 (WHERE)")]
    [StringLength(2000)]
    public string? ReplaceWhereClause { get; set; }

    /// <summary>
    /// 來源欄位 → 目標欄位 的 JSON 對應字典（10.5+）。
    /// 例：<c>{"cust_id":"CustomerID","order_no":"OrderNumber"}</c>
    /// 為 null/空白時走「同名 1:1」（與 10.4.x 完全一致）。
    /// </summary>
    [Display(Name = "欄位對應 (JSON)")]
    public string? ColumnMappingJson { get; set; }

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

    // ─── 告警設定 ───

    /// <summary>失敗時寄送告警的 Email（多個地址以逗號分隔）</summary>
    [Display(Name = "告警 Email")]
    [StringLength(500)]
    public string? AlertEmail { get; set; }

    /// <summary>失敗時 POST 的 Webhook URL（JSON 格式，相容 Slack/Teams/Feishu incoming webhook）</summary>
    [Display(Name = "告警 Webhook URL")]
    [StringLength(2000)]
    public string? AlertWebhookUrl { get; set; }

    /// <summary>連續失敗幾次後才觸發告警（預設 1 = 每次失敗都告警）</summary>
    [Display(Name = "連續失敗告警門檻")]
    public int AlertAfterConsecutiveFailures { get; set; } = 1;

    /// <summary>目前連續失敗次數（成功後歸零）。由 EtlQuartzJob 自動維護，請勿手動修改。</summary>
    public int ConsecutiveFailureCount { get; set; }
}
