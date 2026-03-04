#nullable disable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 分析查詢請求，包含 ListVM 型別、Searcher 狀態、維度、度量及過濾條件。
    /// </summary>
    public class AnalysisQueryRequest
    {
        /// <summary>ListVM 的 FullName，在 AnalysisVmRegistry 白名單中查找。</summary>
        public string ListVmType { get; set; }

        /// <summary>SearchPanel 的 form data（JSON 字串），傳入後 bind 到 Searcher 實例。</summary>
        public string SearcherFormData { get; set; }

        /// <summary>選取的維度欄位名稱（最多 3 個）。</summary>
        public List<string> Dimensions { get; set; } = new List<string>();

        /// <summary>選取的度量及聚合函式。</summary>
        public List<MeasureRequest> Measures { get; set; } = new List<MeasureRequest>();

        /// <summary>額外過濾條件（白名單驗證後進 Expression Tree）。</summary>
        public List<FilterCondition> Filters { get; set; } = new List<FilterCondition>();
    }

    /// <summary>
    /// 單一度量請求，指定欄位名稱及聚合函式。
    /// </summary>
    public class MeasureRequest
    {
        /// <summary>度量欄位名稱。</summary>
        public string Field { get; set; }

        /// <summary>聚合函式（必須在欄位的 AllowedFuncs 範圍內）。</summary>
        public AggregateFunc Func { get; set; }
    }

    /// <summary>
    /// 欄位過濾條件，Value 統一為 string，server-side 依欄位型別安全轉換。
    /// </summary>
    public class FilterCondition
    {
        /// <summary>欄位名稱（白名單驗證）。</summary>
        public string Field { get; set; }

        /// <summary>比較運算子。</summary>
        public FilterOperator Operator { get; set; }

        /// <summary>過濾值（統一 string，server-side 做 type conversion）。</summary>
        public string Value { get; set; }
    }

    /// <summary>
    /// 過濾運算子列舉。
    /// </summary>
    public enum FilterOperator
    {
        Eq, Gt, Gte, Lt, Lte, Contains, In
    }
}
