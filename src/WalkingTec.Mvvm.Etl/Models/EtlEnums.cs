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

/// <summary>
/// 目標資料表的載入模式（10.5+）。每個 Job 二選一：
/// </summary>
public enum EtlLoadMode
{
    /// <summary>
    /// 預設模式（與 10.4.x 之前完全一致）：staging → target 走
    /// <c>MERGE INTO target ON mergeKey WHEN MATCHED THEN UPDATE
    /// WHEN NOT MATCHED THEN INSERT</c>。「存在更新、不存在新增」。
    /// </summary>
    Merge = 0,

    /// <summary>
    /// 刪除重建模式：先 <c>DELETE FROM target [WHERE clause]</c>，
    /// 再 <c>INSERT INTO target SELECT * FROM staging</c>，全程在
    /// 單一 transaction 內。<c>EtlPipelineConfig.ReplaceWhereClause</c>
    /// 提供刪除條件；為 null/空白時刪除整個 target table 內容。
    /// 適用「每次都重抓某個區段全資料」的場景，例如「每天重新匯入
    /// 昨天到今天的訂單，無視之前的記錄」。
    /// </summary>
    Replace = 1,
}
