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

        /// <summary>
        /// 對聚合結果欄位的過濾條件（等同 SQL HAVING 子句）。
        /// 例如 <c>HavingFilters = [ { Field = "Amount_Sum", Op = Gte, Value = "1000000" } ]</c>
        /// 過濾出「銷售額 ≥ 1,000,000」的群組。<c>Field</c> 必須是
        /// <c>{measure.Field}_{measure.Func}</c> 形式（與 <see cref="Sort"/>
        /// 規則一致），不在已選度量結果欄位內的條件會在驗證階段被拒絕。
        /// 套用順序為 GroupBy → HavingFilters → Sort → TopN，符合 SQL
        /// 標準語意。空 list 或 null = 不過濾。
        /// </summary>
        public List<HavingFilter>? HavingFilters { get; set; }

        /// <summary>
        /// 是否在回應中附加一筆「總計」資料於
        /// <see cref="AnalysisQueryResponse.GrandTotalRow"/>。預設 <c>false</c>
        /// 完全不影響舊呼叫者。為 <c>true</c> 時，引擎在套用
        /// <see cref="HavingFilters"/> 之後（但在 <see cref="Sort"/> /
        /// <see cref="TopN"/> 之前）依各度量函式產生彙總值：
        /// Sum / Count → 各群組值相加；Max → 全體最大；Min → 全體最小；
        /// Avg / DistinctCount → 不適用（合理彙總需要原始資料；客戶端應
        /// 自行取捨），輸出 <c>null</c>。維度欄位輸出 <c>null</c>，前端
        /// 自由顯示「總計 / Total」字樣。總計是相對於 HAVING-篩選後的
        /// 群組集合，與 <see cref="AnalysisQueryResponse.TotalCount"/> 同
        /// 一基準，<see cref="TopN"/> 只截短可見列、不影響總計範圍。
        /// </summary>
        public bool IncludeGrandTotal { get; set; }
    }

    /// <summary>
    /// HAVING 子句條件 — 對聚合結果欄位（measure 的 <c>{Field}_{Func}</c>）
    /// 套用數值比較。值統一以字串傳入，server-side 嘗試解析為 decimal；
    /// 不可解析則該條件被視為不成立（保守拒絕，避免污染結果）。
    /// </summary>
    public class HavingFilter
    {
        /// <summary>聚合結果欄位名（例如 <c>"Amount_Sum"</c>）。</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>數值比較運算子。僅支援 Eq / NotEq / Gt / Gte / Lt / Lte。</summary>
        public FilterOperator Operator { get; set; } = FilterOperator.Gte;

        /// <summary>比較值（會解析為 decimal）。</summary>
        public string Value { get; set; } = string.Empty;
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
