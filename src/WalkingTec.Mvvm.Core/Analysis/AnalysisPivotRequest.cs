#nullable enable

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// Pivot 查詢請求，繼承標準查詢請求並指定要展開為欄位的維度。
    /// </summary>
    public class AnalysisPivotRequest : AnalysisQueryRequest
    {
        /// <summary>作為 Pivot 欄位的維度（必須是 Dimensions 中的一個）。</summary>
        public string PivotDimension { get; set; } = string.Empty;
    }
}
