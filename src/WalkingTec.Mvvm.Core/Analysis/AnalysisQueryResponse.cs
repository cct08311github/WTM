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
        public List<string> Columns { get; set; } = new List<string>();

        /// <summary>資料列（每列為 欄位名→值 的字典）。</summary>
        public List<Dictionary<string, object?>> Rows { get; set; } = new List<Dictionary<string, object?>>();

        /// <summary>GroupBy 後總筆數。</summary>
        public int TotalCount { get; set; }

        /// <summary>是否因超過 10,000 筆而截斷。</summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// 原始資料列數是否超過 50,000 而被截斷。
        /// 當為 true 時，Sum/Avg 等聚合結果僅基於部分資料，可能不準確。
        /// </summary>
        public bool DataTruncated { get; set; }

        /// <summary>Phase 2 快取識別用，目前留空。</summary>
        public string QueryHash { get; set; } = string.Empty;
    }
}
