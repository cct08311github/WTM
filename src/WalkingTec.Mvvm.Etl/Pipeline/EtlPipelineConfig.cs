#nullable enable
using System;
using System.Data;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// ETL Pipeline 執行配置
/// </summary>
public record EtlPipelineConfig
{
    public Guid JobId { get; init; }
    public string JobName { get; init; } = string.Empty;
    public string SourceConnectionString { get; init; } = string.Empty;
    public string TargetConnectionString { get; init; } = string.Empty;
    public string QueryTemplate { get; init; } = string.Empty;
    public string TargetTableName { get; init; } = string.Empty;
    public string MergeKeyColumn { get; init; } = string.Empty;
    public int BatchSize { get; init; } = 50_000;
    public StagingTableSpec StagingTable { get; init; } = null!;

    /// <summary>可選的轉換函式（null = 零轉換直搬）</summary>
    public Func<DataTable, DataTable>? TransformFunc { get; init; }

    /// <summary>
    /// 乾跑模式（#834）。啟用時執行器會：
    /// <list type="bullet">
    /// <item>執行 Extract（可驗證 SQL 語法 / 權限 / 欄位存在）</item>
    /// <item>只抓第一個 batch 就停（避免大查詢負擔）</item>
    /// <item>執行 Transform（可驗證 mapper 行為）</item>
    /// <item><b>跳過</b> EnsureStagingTable / TruncateStaging / BulkLoad / Merge</item>
    /// <item>驗證 <see cref="MergeKeyColumn"/> 是否存在於 source columns</item>
    /// <item>回傳第一批前 N 筆轉換後資料作為預覽（<see cref="DryRunPreviewSampleSize"/>）</item>
    /// <item>Watermark pending value 計算但不 commit</item>
    /// </list>
    /// 預設 <c>false</c>（行為與 10.4.0 之前完全一致）。
    /// </summary>
    public bool IsDryRun { get; init; }

    /// <summary>
    /// 乾跑模式下要回傳的預覽列數上限（#834）。預設 10。必須 ≥ 0；設 0 表示只驗證不預覽。
    /// </summary>
    public int DryRunPreviewSampleSize { get; init; } = 10;
}
