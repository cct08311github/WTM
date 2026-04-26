#nullable enable
using System;
using System.Data;
using WalkingTec.Mvvm.Etl.Models;

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

    /// <summary>
    /// 目標載入模式（10.5+）。
    /// 預設 <see cref="EtlLoadMode.Merge"/>（與 10.4.x 之前完全一致）。
    /// 設為 <see cref="EtlLoadMode.Replace"/> 啟用「先刪後插」模式：
    /// staging 載完後在單一 transaction 內 <c>DELETE FROM target</c>
    /// （可選 <see cref="ReplaceWhereClause"/>）然後
    /// <c>INSERT INTO target SELECT * FROM staging</c>。
    /// </summary>
    public EtlLoadMode LoadMode { get; init; } = EtlLoadMode.Merge;

    /// <summary>
    /// Replace 模式下的 DELETE WHERE 子句（不含 "WHERE" 關鍵字本身）。
    /// 例：<c>"OrderDate &gt;= '2026-01-01' AND OrderDate &lt; '2026-02-01'"</c>
    /// 為 null / 空白時會刪除 target 整張表所有列（謹慎使用）。
    /// 操作員必須自行確保此字串無 SQL injection 風險 — 框架做基本
    /// guard（拒絕 <c>;</c> / <c>--</c> / <c>/*</c> / 已知 system-procedure
    /// prefix）但不做完整 parser。Merge 模式下被忽略。
    /// </summary>
    public string? ReplaceWhereClause { get; init; }

    /// <summary>
    /// 來源欄位 → 目標欄位的對應表（10.5+，opt-in）。
    /// 例：<c>{ ["cust_id"] = "CustomerID", ["order_no"] = "OrderNumber" }</c>
    /// — 來源叫 <c>cust_id</c> 寫到 staging 時改名為 <c>CustomerID</c>。
    /// <para>
    /// 為 null（預設）時走原本的「同名 1:1」行為，與 10.4.x 完全相同。
    /// 為非空時改成「白名單 + 改名」：
    /// </para>
    /// <list type="bullet">
    /// <item>來源中存在於 mapping key 的欄位 → 改名為對應 value 後寫入</item>
    /// <item>來源中不在 mapping key 的欄位 → <b>不寫入</b>（drop 掉）</item>
    /// </list>
    /// staging table 的 schema 必須對應 mapping value（已改名後的目標欄位）。
    /// </summary>
    public System.Collections.Generic.IDictionary<string, string>? ColumnMappings { get; init; }
}
