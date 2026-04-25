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

    /// <summary>
    /// 單一 batch 在 BulkLoad 失敗時的重試次數上限（不含第一次嘗試）。
    /// 預設 <c>0</c> = 不重試（保持與 10.4.x 之前完全一致的行為）。設 ≥ 1
    /// 啟用 per-batch 重試：當 <c>BulkLoadAsync</c> 拋例外時，等待
    /// <see cref="BatchRetryBaseDelayMs"/> · 2^attempt + jitter 後再試，
    /// 最多重試 <c>MaxBatchRetries</c> 次仍失敗才中止整個 job。
    /// 適合 DB lock timeout / 網路抖動等轉瞬故障，避免 50K 列已抓回卻
    /// 因為一個 batch 失敗就得整個 job 重抓。範圍 0..50；超出範圍由
    /// 執行時夾到合理區間。
    /// </summary>
    public int MaxBatchRetries { get; init; }

    /// <summary>
    /// 重試延遲基數（毫秒）。實際延遲 = <c>BaseDelay × 2^attempt</c> +
    /// 0..BaseDelay 的隨機抖動（防 thundering herd）。預設 200ms。
    /// 第 N 次重試延遲：N=1 時 200‒400ms、N=2 時 400‒800ms、
    /// N=3 時 800‒1600ms…等比加倍直到 <see cref="BatchRetryMaxDelayMs"/>。
    /// </summary>
    public int BatchRetryBaseDelayMs { get; init; } = 200;

    /// <summary>
    /// 單次重試的最大延遲（毫秒），預設 30,000 (30s)。對抗極端
    /// 重試風暴 — 第 N 次延遲計算結果若超過此上限，會夾到此值。
    /// </summary>
    public int BatchRetryMaxDelayMs { get; init; } = 30_000;
}
