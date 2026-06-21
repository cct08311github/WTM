#nullable enable
using System;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger? _logger;

    public WatermarkStrategy(EtlWatermarkType type, string? column, string? currentValue, string timeZone = "UTC", ILogger? logger = null)
    {
        Type = type;
        Column = column;
        CurrentValue = currentValue;
        TimeZone = timeZone;
        _logger = logger;
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
            // DB 欄位型別因資料庫而異：SQL Server INT→int / BIGINT→long、
            // Oracle NUMBER→decimal、SMALLINT→short、TINYINT→byte 等。
            // null 或 DBNull 表示本批次沒有資料（空批次或 watermark 欄位 max 為 null）——
            // 這是正常情況，不推進 watermark 也不記錄警告。
            // 對非 null 的真實值統一用 Convert.ToInt64 做安全型別擴展；
            // overflow（ulong 超過 long.MaxValue 或大 decimal）和真正無法轉換的型別
            // （string、Guid 等配置錯誤）則記錄警告而非靜默丟失 watermark。
            if (maxValue is null || maxValue is DBNull)
            {
                // No data in this batch (or null max) — leave the watermark unchanged; not a misconfiguration.
                return;
            }

            long? idValue = null;
            try
            {
                idValue = Convert.ToInt64(maxValue, CultureInfo.InvariantCulture);
            }
            catch (OverflowException ex)
            {
                _logger?.LogWarning(ex,
                    "Identity watermark coercion overflow for column '{Column}': value {Value} (type {Type}) " +
                    "exceeds long range. Watermark will not advance; the next run will re-process this window. " +
                    "Consider switching to a Timestamp watermark or verifying the Identity column type.",
                    Column, maxValue, maxValue?.GetType().FullName);
            }
            catch (InvalidCastException ex)
            {
                _logger?.LogWarning(ex,
                    "Identity watermark coercion failed for column '{Column}': value {Value} (type {Type}) " +
                    "cannot be converted to long. Watermark will not advance; verify EtlPipelineConfig.WatermarkColumn " +
                    "is an integral or decimal numeric column.",
                    Column, maxValue, maxValue?.GetType().FullName);
            }
            catch (FormatException ex)
            {
                _logger?.LogWarning(ex,
                    "Identity watermark coercion failed for column '{Column}': value {Value} (type {Type}) " +
                    "cannot be converted to long. Watermark will not advance; verify EtlPipelineConfig.WatermarkColumn " +
                    "is an integral or decimal numeric column.",
                    Column, maxValue, maxValue?.GetType().FullName);
            }
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
