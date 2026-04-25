#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 分析查詢結果，包含欄位清單、資料列及截斷狀態。
    /// </summary>
    public class AnalysisQueryResponse
    {
        /// <summary>欄位名稱清單（有序，對應 Rows 中的 key）。</summary>
        public List<string> Columns { get; set; } = [];

        /// <summary>資料列（每列為 欄位名→值 的字典）。</summary>
        public List<Dictionary<string, object?>> Rows { get; set; } = [];

        /// <summary>GroupBy 後總筆數。</summary>
        public int TotalCount { get; set; }

        /// <summary>是否因超過 10,000 筆而截斷。</summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// 原始資料列數是否超過 50,000 而被截斷。
        /// 當為 true 時，Sum/Avg 等聚合結果僅基於部分資料，可能不準確。
        /// </summary>
        public bool DataTruncated { get; set; }

        /// <summary>
        /// 當 DataTruncated 為 true 時，提供給終端使用者的說明訊息（含建議操作）。
        /// DataTruncated 為 false 時為 null。
        /// </summary>
        public string? DataTruncatedMessage { get; set; }

        /// <summary>Phase 2 快取識別用，目前留空。</summary>
        public string QueryHash { get; set; } = string.Empty;

        /// <summary>
        /// 量值欄位 key（如 "Amount_Sum"）→ MeasureFormat 的對照表。
        /// 匯出時依此套用數字格式；不在此表中的欄位以 Auto 處理。
        /// </summary>
        public Dictionary<string, MeasureFormat> ColumnFormats { get; set; } = new Dictionary<string, MeasureFormat>();

        /// <summary>
        /// 欄位 key（Columns 中的值）到使用者友善顯示名稱的對照表。
        /// 維度：key = 欄位名稱，value = DisplayName（如 "地區"）。
        /// 量值：key = "Field_Func"（如 "Amount_Sum"），value = "DisplayName 合計"（如 "金額 合計"）。
        /// 匯出標頭應優先使用此表；key 不存在時 fallback 回 column key 本身。
        /// </summary>
        public Dictionary<string, string> ColumnDisplayNames { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 「總計」彙總列。當 <see cref="AnalysisQueryRequest.IncludeGrandTotal"/>
        /// 為 <c>true</c> 時非 null，每個 measure-result 欄位填入合適的彙
        /// 總值（Sum/Count → 群組值總和；Max → 全體最大；Min → 全體最
        /// 小；Avg/DistinctCount → 無法合理彙總，輸出 <c>null</c>）。
        /// 維度欄位輸出 <c>null</c> 讓前端自由附加 "總計 / Total" 字樣。
        /// 總計範圍與 <see cref="TotalCount"/> 同步：包含所有 HAVING-
        /// 篩選後的群組，<see cref="AnalysisQueryRequest.TopN"/> 不影響範圍。
        /// </summary>
        public Dictionary<string, object?>? GrandTotalRow { get; set; }
    }
}
