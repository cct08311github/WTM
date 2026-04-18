#nullable enable
using System;
using System.Text.Json;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// Watermark 計算邏輯
///
/// 規則：
/// 1. FullLoad → WHERE 1=1（每次全量）
/// 2. Timestamp/Identity → WHERE {Column} > @watermark
/// 3. 首次執行：InitialWatermarkValue 有值用它，null 則全量
/// 4. Watermark 只在整個 Job 成功後更新
/// 5. 時區：存儲 UTC，查詢時轉回來源時區
/// </summary>
public class WatermarkStrategy
{
    public EtlWatermarkType Type { get; }
    public string? Column { get; }
    public string? CurrentValue { get; private set; }
    public string TimeZone { get; }

    private string? _pendingValue;

    public WatermarkStrategy(EtlWatermarkType type, string? column, string? currentValue, string timeZone = "UTC")
    {
        Type = type;
        Column = column;
        CurrentValue = currentValue;
        TimeZone = timeZone;
    }

    /// <summary>
    /// 產生 WHERE 子句（不含 WHERE 關鍵字）
    /// </summary>
    public string BuildWhereClause()
    {
        if (Type == EtlWatermarkType.FullLoad)
            return "1=1";

        if (string.IsNullOrEmpty(CurrentValue))
            return "1=1"; // 首次全量

        return $"{Column} > @watermark";
    }

    /// <summary>
    /// 取得 @watermark 參數值（已轉換時區）
    /// </summary>
    public object? GetParameterValue()
    {
        if (Type == EtlWatermarkType.FullLoad || string.IsNullOrEmpty(CurrentValue))
            return null;

        if (Type == EtlWatermarkType.Timestamp)
        {
            var utcValue = JsonSerializer.Deserialize<DateTime>(CurrentValue);
            if (TimeZone != "UTC")
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
                return TimeZoneInfo.ConvertTimeFromUtc(utcValue, tz);
            }
            return utcValue;
        }

        // Identity — 直接回傳數值
        return JsonSerializer.Deserialize<long>(CurrentValue);
    }

    /// <summary>
    /// 從一批資料中提取最大 watermark 值（暫存，不立即寫回 DB）
    /// </summary>
    public void UpdateFromBatchMax(object maxValue)
    {
        if (Type == EtlWatermarkType.FullLoad) return;

        if (Type == EtlWatermarkType.Timestamp && maxValue is DateTime dt)
        {
            if (TimeZone != "UTC")
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
                dt = TimeZoneInfo.ConvertTimeToUtc(dt, tz);
            }
            _pendingValue = JsonSerializer.Serialize(dt);
        }
        else if (Type == EtlWatermarkType.Identity)
        {
            // DB 的 INT 欄位從 DataReader 讀出為 int，BIGINT 為 long；統一轉換為 long
            long? idValue = maxValue switch
            {
                long l => l,
                int i => (long)i,
                _ => null
            };
            if (idValue.HasValue)
                _pendingValue = JsonSerializer.Serialize(idValue.Value);
        }
    }

    /// <summary>
    /// Job 成功後，確認更新 watermark（回傳新值寫回 DB）
    /// </summary>
    public string? CommitPendingValue()
    {
        if (_pendingValue != null)
        {
            CurrentValue = _pendingValue;
            _pendingValue = null;
        }
        return CurrentValue;
    }

    /// <summary>
    /// Job 失敗時，丟棄暫存值（watermark 不更新）
    /// </summary>
    public void DiscardPendingValue()
    {
        _pendingValue = null;
    }

    /// <summary>
    /// 偷看 pending value（不 commit，不清除）— 乾跑模式用來報告「如果真跑 watermark 會變成什麼」(#834)
    /// </summary>
    public string? PeekPendingValue() => _pendingValue ?? CurrentValue;
}
