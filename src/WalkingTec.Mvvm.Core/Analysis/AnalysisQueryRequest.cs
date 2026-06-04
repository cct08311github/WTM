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

        /// <summary>
        /// 是否在回應中附加自動產生的 BI 洞察文字
        /// (<see cref="AnalysisQueryResponse.Insights"/>)。預設 <c>false</c>
        /// 完全不影響舊呼叫者。為 <c>true</c> 時，引擎在所有資料增益
        /// (HAVING、CompareWith、GrandTotal、Sort、TopN) 完成後，依當前
        /// 第一個度量產生 2‒5 條人類可讀的洞察句子，每條獨立 try/catch
        /// 避免單條 heuristic 失敗影響其他。涵蓋的 heuristic：
        /// (1) Top performer + 平均倍率；(2) Bottom performer；
        /// (3) 比較期最大漲幅 / 跌幅 (僅當 <see cref="CompareWith"/> 啟用)；
        /// (4) Pareto 集中度（Top 20% 占總計）；(5) z-score 離群值。
        /// </summary>
        public bool IncludeInsights { get; set; }

        /// <summary>
        /// 期間對比設定 — 啟用「本期 vs 對比期」雙查詢模式。預設
        /// <c>null</c> 不影響舊呼叫者。
        /// 設定後，引擎會以 <see cref="ComparisonRequest.Filters"/>
        /// 取代 <see cref="Filters"/>（其餘維度/度量沿用本請求）跑第二
        /// 次查詢，再以維度為 join key 在每個結果列附加三個衍生欄位：
        /// <c>{Field}_{Func}_Compare</c>（對比期值）、
        /// <c>{Field}_{Func}_Delta</c>（差值 = 本期 − 對比期）、
        /// <c>{Field}_{Func}_ChangePct</c>（變化百分比；對比期為 0 時
        /// 輸出 <c>null</c> 避免 Infinity）。本期沒有但對比期有的群組
        /// 也會出現在結果中，本期欄位 <c>null</c>。
        /// 與 <see cref="Sort"/> / <see cref="TopN"/> / <see cref="HavingFilters"/>
        /// 完全相容；Sort 可指向衍生欄位（例如
        /// <c>Amount_Sum_ChangePct</c> 做「漲幅前 N 名」）。
        /// </summary>
        public ComparisonRequest? CompareWith { get; set; }

        /// <summary>
        /// 預測（forecast）設定 — 啟用後引擎會在所有資料增益（HAVING、
        /// CompareWith、GrandTotal、Sort、TopN、Insights）完成後，依時間
        /// 序的第一個維度（必須是 <see cref="DimensionHierarchies"/>
        /// 中宣告為日期階層的維度）外推 <see cref="ForecastSpec.Periods"/>
        /// 期。每筆預測列以下一期的人類可讀標籤（例如
        /// <c>"2026-05"</c>）作為維度值，並在每個 measure-result 欄位
        /// 填入估計值；除原本的欄位外，預測列額外帶一個
        /// <c>"_IsForecast" = true</c> 的旗標 key，前端用以區分實際 vs
        /// 預測（例如以虛線繪製預測段）。預測**不**重新計算 Insights /
        /// GrandTotalRow / TotalCount — 那些反映實際資料；預測純為
        /// 視覺輔助。<see cref="ForecastMethod.Linear"/> 走最小平方法
        /// 線性回歸，<see cref="ForecastMethod.MovingAverage"/> 走最後
        /// 3 期均值（小於 3 期則用全部）。預設 <c>null</c> 完全不影響
        /// 舊呼叫者。
        /// </summary>
        public ForecastSpec? Forecast { get; set; }
    }

    /// <summary>
    /// 對比查詢設定 — 與主查詢共用維度和度量，但用獨立的 filter set
    /// 取得對比期資料。常見用法：本月 vs 上月、本年 vs 去年、本週 vs
    /// 對應週、A 通路 vs B 通路（不限於日期維度）。
    /// </summary>
    public class ComparisonRequest
    {
        /// <summary>
        /// 對比期的篩選條件（取代主查詢的 <see cref="AnalysisQueryRequest.Filters"/>）。
        /// 與主查詢的 Filters 同樣支援相對日期 token（@today、@lastMonth…）。
        /// 必要欄位 — 沒設等於沒做對比。
        /// </summary>
        public List<FilterCondition> Filters { get; set; } = [];

        /// <summary>
        /// 對比期的人類可讀標籤（顯示於回應的
        /// <see cref="AnalysisQueryResponse.ColumnDisplayNames"/> 中，
        /// 例如「上月」「去年同期」「對照組」）。為 null 時 fallback
        /// 為 "Compare"。不影響資料行為，純顯示用。
        /// </summary>
        public string? Label { get; set; }
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

        /// <summary>
        /// 過濾值（統一 string，server-side 做 type conversion）。
        /// 對 <see cref="FilterOperator.In"/> / <see cref="FilterOperator.NotIn"/> 運算子，
        /// 此欄位為逗號分隔字串形式（向後相容）。
        /// 若同時提供 <see cref="Values"/>（且不為空），引擎會優先使用 <see cref="Values"/>
        /// 並忽略本欄位（L1 修正：<see cref="Values"/> 優先於逗號分割的 <see cref="Value"/>）。
        /// </summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// In / NotIn 運算子的多個值（List&lt;string&gt; 形式，優先於 <see cref="Value"/> 的逗號分割）。
        /// 當此屬性為非空 list 時，引擎直接使用此 list 作為值來源，忽略 <see cref="Value"/>。
        /// 當此屬性為 null 或空 list 時，引擎 fallback 到對 <see cref="Value"/> 做逗號分割。
        /// 非 In/NotIn 運算子不使用此欄位。
        /// </summary>
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

    /// <summary>
    /// 預測規格 — 設定外推期數與方法。
    /// </summary>
    public class ForecastSpec
    {
        /// <summary>
        /// 要外推的期數。範圍 1..12，超出範圍由引擎驗證階段拒絕。
        /// 預測長度受實際序列長度限制 — 線性回歸至少需 2 期實際資料，
        /// 移動平均至少需 1 期；不足則該欄輸出 <c>null</c>（前端可隱藏
        /// 該段預測線而非 throw）。
        /// </summary>
        public int Periods { get; set; } = 1;

        /// <summary>預測方法，預設 <see cref="ForecastMethod.Linear"/>。</summary>
        public ForecastMethod Method { get; set; } = ForecastMethod.Linear;
    }

    /// <summary>預測方法列舉。</summary>
    public enum ForecastMethod
    {
        /// <summary>最小平方法線性回歸 — 適合明顯有趨勢的時間序列。</summary>
        Linear,
        /// <summary>移動平均 — 適合平穩、無趨勢的時間序列。常數預測。</summary>
        MovingAverage,
    }
}
