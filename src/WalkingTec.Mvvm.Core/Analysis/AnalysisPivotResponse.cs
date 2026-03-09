#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// Pivot 查詢結果，包含行維度、展開值、度量名稱及轉置後的資料列。
    /// </summary>
    public class AnalysisPivotResponse
    {
        /// <summary>行維度欄位名稱（不含 PivotDimension）。</summary>
        public List<string> RowDimensions { get; set; } = new();

        /// <summary>PivotDimension 的唯一值清單（展開為欄位標頭）。</summary>
        public List<string> PivotValues { get; set; } = new();

        /// <summary>度量名稱（Field_Func 格式）。</summary>
        public List<string> MeasureNames { get; set; } = new();

        /// <summary>轉置後的資料列。Key 格式為 RowDim 值 + PivotValue_MeasureName。</summary>
        public List<Dictionary<string, object?>> Rows { get; set; } = new();

        /// <summary>動態欄位名稱清單（RowDimensions + 交叉欄位名）。</summary>
        public List<string> Columns { get; set; } = new();
    }
}
