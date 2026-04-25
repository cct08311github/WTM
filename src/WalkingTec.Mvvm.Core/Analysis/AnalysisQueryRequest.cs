#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 分析查詢請求，包含 ListVM 型別、Searcher 狀態、維度、度量及過濾條件。
    /// </summary>
    public class AnalysisQueryRequest
    {
        /// <summary>ListVM 的 FullName，在 AnalysisVmRegistry 白名單中查找。</summary>
        public string ListVmType { get; set; } = string.Empty;

        /// <summary>SearchPanel 的 form data（JSON 字串），傳入後 bind 到 Searcher 實例。</summary>
        public string? SearcherFormData { get; set; }

        /// <summary>選取的維度欄位名稱（最多 3 個）。</summary>
        public List<string> Dimensions { get; set; } = [];

        /// <summary>選取的度量及聚合函式。</summary>
        public List<MeasureRequest> Measures { get; set; } = [];

        /// <summary>額外過濾條件（白名單驗證後進 Expression Tree）。</summary>
        public List<FilterCondition> Filters { get; set; } = [];

        /// <summary>維度對應的日期階層（僅日期維度需要，key=fieldName, value=hierarchy）</summary>
        public Dictionary<string, DateHierarchy>? DimensionHierarchies { get; set; }

        /// <summary>
        /// 結果排序規格。每筆 <see cref="SortSpec"/> 指定一個欄位與升降冪。
        /// 多筆 = 多級排序（依序套用，前一筆優先）。<c>Field</c> 必須是
        /// <see cref="Dimensions"/> 中的維度，或 <c>{measure.Field}_{measure.Func}</c>
        /// 形式的度量結果欄位（例如 <c>Amount_Sum</c>）— 與
        /// <see cref="AnalysisQueryResponse.Columns"/> 命名一致。任何不在
        /// 維度或度量結果欄位中的 <c>Field</c> 會在驗證階段被拒絕，避免
        /// 排序語意不明確或洩漏白名單外的欄位。
        /// </summary>
        public List<SortSpec>? Sort { get; set; }

        /// <summary>
        /// 取結果前 N 筆（聚合 + 排序之後）。<c>null</c> = 不限制（仍受
        /// 10,000 列硬上限）。範圍 1‒10,000；超出範圍會在驗證階段被拒絕。
        /// 建議與 <see cref="Sort"/> 同時使用 — 沒有排序的「Top N」幾乎
        /// 一定不是調用者想要的；引擎會給予提示但不阻擋（保持向後相容）。
        /// </summary>
        public int? TopN { get; set; }
    }

    /// <summary>
    /// 排序規格 — 維度或度量結果欄位 + 升/降冪。
    /// </summary>
    public class SortSpec
    {
        /// <summary>排序欄位名稱（必須是維度名或 <c>{measure}_{Func}</c>）。</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>True = 降冪（DESC），預設 false = 升冪（ASC）。</summary>
        public bool Descending { get; set; }
    }

    /// <summary>
    /// 單一度量請求，指定欄位名稱及聚合函式。
    /// </summary>
    public class MeasureRequest
    {
        /// <summary>度量欄位名稱。</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>聚合函式（必須在欄位的 AllowedFuncs 範圍內）。</summary>
        public AggregateFunc Func { get; set; }
    }

    /// <summary>
    /// 欄位過濾條件，Value 統一為 string，server-side 依欄位型別安全轉換。
    /// </summary>
    public class FilterCondition
    {
        /// <summary>欄位名稱（白名單驗證）。</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>比較運算子。</summary>
        public FilterOperator Operator { get; set; }

        /// <summary>過濾值（統一 string，server-side 做 type conversion）。</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>In 運算子的多個值（以逗號分隔或 JSON array）</summary>
        public List<string>? Values { get; set; }
    }

    /// <summary>
    /// 過濾運算子列舉。
    /// </summary>
    public enum FilterOperator
    {
        Eq, Gt, Gte, Lt, Lte, Contains, In,
        NotEq, NotContains, NotIn
    }
}
