#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 純 in-memory 預測引擎 — 在實際資料 <see cref="AnalysisQueryResponse.Rows"/>
    /// 後追加 N 筆預測列。不查 DB、不重算 Insights / GrandTotal / TotalCount；
    /// 失敗條件（無時間維度、實際列不足、首列無法 parse 期別）下靜默 no-op
    /// 而非 throw，避免單一邊界情境炸掉整個 dashboard。
    /// </summary>
    public static class AnalysisForecastEngine
    {
        /// <summary>外推期數上限。超過此值在引擎驗證階段被拒絕。</summary>
        public const int MaxPeriods = 12;

        /// <summary>移動平均的窗口大小（最後 N 期）。</summary>
        public const int MovingAverageWindow = 3;

        /// <summary>預測列的旗標 key，前端用以區分實際 vs 預測。</summary>
        public const string ForecastFlagKey = "_IsForecast";

        /// <summary>
        /// 對 <paramref name="response"/> 套用預測，將外推列直接 append 到
        /// <see cref="AnalysisQueryResponse.Rows"/> 並設定
        /// <see cref="AnalysisQueryResponse.HasForecast"/>。
        /// </summary>
        /// <param name="response">已完成所有資料增益的查詢回應（會就地修改）。</param>
        /// <param name="spec">預測設定。</param>
        /// <param name="dimensionFields">原請求的維度欄位（依序）— 第一個必須是時間維度才會啟動預測。</param>
        /// <param name="measureColumns">measure-result 欄位 key（例如 <c>Amount_Sum</c>）。</param>
        /// <param name="hierarchies">維度的日期階層對照表（無此項則不啟動預測）。</param>
        public static void ApplyForecast(
            AnalysisQueryResponse response,
            ForecastSpec spec,
            List<string> dimensionFields,
            List<string> measureColumns,
            Dictionary<string, DateHierarchy>? hierarchies)
        {
            // Guards — 任何不滿足條件靜默 no-op
            if (response == null || spec == null) { return; }
            if (response.Rows.Count == 0) { return; }
            if (dimensionFields.Count == 0) { return; }
            if (measureColumns.Count == 0) { return; }
            if (hierarchies == null) { return; }

            var timeDim = dimensionFields[0];
            if (!hierarchies.TryGetValue(timeDim, out var hierarchy)) { return; }

            // 取最後一筆實際列的時間 label，作為預測起點
            var lastRow = response.Rows[^1];
            if (!lastRow.TryGetValue(timeDim, out var lastLabelObj) || lastLabelObj == null) { return; }
            var lastLabel = lastLabelObj.ToString();

            var periods = spec.Periods;
            if (periods <= 0) { return; }
            if (periods > MaxPeriods) { periods = MaxPeriods; }

            // 為每個 measure 產生預測值序列
            var predictionsByMeasure = new Dictionary<string, double?[]>();
            foreach (var col in measureColumns)
            {
                var series = ExtractNumericSeries(response.Rows, col);
                predictionsByMeasure[col] = spec.Method == ForecastMethod.MovingAverage
                    ? PredictMovingAverage(series, periods)
                    : PredictLinear(series, periods);
            }

            // 產生 N 筆預測列並 append
            var any = false;
            for (int i = 0; i < periods; i++)
            {
                var nextLabel = DateTruncator.NextLabel(lastLabel, hierarchy, i + 1);
                if (nextLabel == null) { break; }

                var row = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [ForecastFlagKey] = true,
                };
                // 時間維度填預測 label；其餘維度填 null（沒有意義的展開）
                foreach (var dim in dimensionFields)
                {
                    row[dim] = dim == timeDim ? (object?)nextLabel : null;
                }
                foreach (var col in measureColumns)
                {
                    var arr = predictionsByMeasure[col];
                    row[col] = arr[i].HasValue
                        ? (object?)System.Math.Round((decimal)arr[i]!.Value, 4)
                        : null;
                }
                response.Rows.Add(row);
                any = true;
            }

            if (any) { response.HasForecast = true; }
        }

        /// <summary>
        /// 從 rows 抽取一個 measure 欄位為 double 序列（null / 非數值 → null）。
        /// 順序保留 rows 的順序（已被 Sort 處理過，第一維是時間且升冪
        /// 是常見場景）。
        /// </summary>
        internal static double?[] ExtractNumericSeries(
            List<Dictionary<string, object?>> rows, string col)
        {
            var series = new double?[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].TryGetValue(col, out var v) || v == null)
                {
                    series[i] = null;
                    continue;
                }
                if (v is double d) { series[i] = d; continue; }
                if (v is decimal dec) { series[i] = (double)dec; continue; }
                if (v is int n) { series[i] = n; continue; }
                if (v is long l) { series[i] = l; continue; }
                if (v is float f) { series[i] = f; continue; }
                if (double.TryParse(v.ToString(), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var parsed))
                {
                    series[i] = parsed;
                }
                else { series[i] = null; }
            }
            return series;
        }

        /// <summary>
        /// 最小平方法線性回歸 — 在去 null 的 (index, value) 點集上擬合
        /// y = m·x + b，回傳長度 <paramref name="periods"/> 的預測陣列
        /// （每元素為 <c>null</c> 表示無法預測，例如有效點 &lt; 2）。
        /// </summary>
        internal static double?[] PredictLinear(double?[] series, int periods)
        {
            var output = new double?[periods];
            // Single-pass accumulation — no intermediate List<> or 4 LINQ Sum passes.
            // Collects (x, y) stats directly while iterating the series once.
            int n = 0;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            for (int i = 0; i < series.Length; i++)
            {
                if (!series[i].HasValue) continue;
                double x = i;
                double y = series[i]!.Value;
                n++;
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumXX += x * x;
            }
            if (n < 2) { return output; }

            double denom = n * sumXX - sumX * sumX;
            if (denom == 0)
            {
                // 所有 x 相同（理論上不會發生 — 但防 NaN）— 用平均值常數
                double avg = sumY / n;
                for (int i = 0; i < periods; i++) { output[i] = avg; }
                return output;
            }
            double m = (n * sumXY - sumX * sumY) / denom;
            double b = (sumY - m * sumX) / n;

            // 預測序列接續原 series 索引
            for (int i = 0; i < periods; i++)
            {
                double x = series.Length + i;
                output[i] = m * x + b;
            }
            return output;
        }

        /// <summary>
        /// 移動平均 — 取最後 N 期實際值的平均（N = min(<see cref="MovingAverageWindow"/>,
        /// 有效點數)）。所有外推期都填同一個常數值（適合無趨勢的平穩序列）。
        /// </summary>
        internal static double?[] PredictMovingAverage(double?[] series, int periods)
        {
            var output = new double?[periods];
            // 從尾端往前取最多 MovingAverageWindow 個有效點
            var window = new List<double>();
            for (int i = series.Length - 1; i >= 0 && window.Count < MovingAverageWindow; i--)
            {
                if (series[i].HasValue) { window.Add(series[i]!.Value); }
            }
            if (window.Count == 0) { return output; }
            double avg = window.Average();
            for (int i = 0; i < periods; i++) { output[i] = avg; }
            return output;
        }
    }
}
