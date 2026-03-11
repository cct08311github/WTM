#nullable enable

namespace WalkingTec.Mvvm.Etl.Models;

/// <summary>
/// ETL Job 狀態
/// </summary>
public enum EtlJobStatus
{
    Enabled,
    Disabled,
    Running,
    Paused,
    Failed
}

/// <summary>
/// ETL 執行觸發方式
/// </summary>
public enum EtlRunTrigger
{
    Scheduled,
    Manual,
    Retry
}

/// <summary>
/// ETL 執行結果
/// </summary>
public enum EtlRunResult
{
    Success,
    Failed,
    Aborted,
    Skipped
}

/// <summary>
/// Watermark 模式
/// </summary>
public enum EtlWatermarkType
{
    /// <summary>每次全量載入，無 watermark</summary>
    FullLoad,
    /// <summary>用時間欄位做增量（如 UpdatedAt）</summary>
    Timestamp,
    /// <summary>用自增 ID 做增量（如 auto-increment PK）</summary>
    Identity
}
