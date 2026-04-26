#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// 單一資料品質規則 — 套用於 Transform / ColumnMapping 之後、
/// BulkLoad 之前的記憶體 <c>DataTable</c>。違規列依
/// <see cref="EtlPipelineConfig.QualityRuleAction"/> 設定處理。
/// </summary>
public record EtlQualityRule
{
    /// <summary>要檢查的欄位名稱（必須存在於 transformed table 中）。</summary>
    public string Column { get; init; } = string.Empty;

    /// <summary>規則類型。</summary>
    public EtlQualityRuleType RuleType { get; init; }

    /// <summary>數值範圍下限（<see cref="EtlQualityRuleType.Range"/> 用，含端點）。</summary>
    public decimal? Min { get; init; }

    /// <summary>數值範圍上限（<see cref="EtlQualityRuleType.Range"/> 用，含端點）。</summary>
    public decimal? Max { get; init; }

    /// <summary>正規表達式（<see cref="EtlQualityRuleType.Regex"/> 用，必填）。</summary>
    public string? Pattern { get; init; }

    /// <summary>白名單值（<see cref="EtlQualityRuleType.In"/> 用，必填）。</summary>
    public IList<string>? Allowed { get; init; }

    /// <summary>違規時的可讀說明（記入 <see cref="EtlExecutionResult.QualityFailureSamples"/>）。</summary>
    public string? Description { get; init; }
}

/// <summary>規則類型。</summary>
public enum EtlQualityRuleType
{
    /// <summary>欄位值不可為 <c>DBNull</c> / <c>null</c>。</summary>
    NotNull,
    /// <summary>欄位值（轉為 decimal）必須在 [<see cref="EtlQualityRule.Min"/>, <see cref="EtlQualityRule.Max"/>] 之內，端點包含。</summary>
    Range,
    /// <summary>欄位字串值必須匹配 <see cref="EtlQualityRule.Pattern"/>（編譯快取）。</summary>
    Regex,
    /// <summary>欄位字串值必須在 <see cref="EtlQualityRule.Allowed"/> 列表內（大小寫敏感）。</summary>
    In,
}

/// <summary>
/// 違規列的處理策略。
/// </summary>
public enum EtlQualityRuleAction
{
    /// <summary>違規列從 batch 中剔除，其他列正常 BulkLoad（預設、最寬鬆）。</summary>
    Drop,
    /// <summary>違規列照常 BulkLoad，僅計數不阻擋。適合 audit-only 階段。</summary>
    Continue,
    /// <summary>任一列違規即拋例外、整個 job 失敗、watermark discard。</summary>
    Abort,
}
