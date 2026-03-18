#nullable enable
namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// Analysis Mode Excel 匯出時，量值欄位的數字顯示格式。
    /// </summary>
    public enum MeasureFormat
    {
        /// <summary>自動：千分位 + 兩位小數（#,##0.00），與舊版行為相同。</summary>
        Auto = 0,

        /// <summary>整數：千分位，無小數（#,##0）。適合數量、筆數等整數量值。</summary>
        Integer = 1,

        /// <summary>貨幣：人民幣符號 + 千分位 + 兩位小數（¥#,##0.00）。</summary>
        Currency = 2,

        /// <summary>百分比：兩位小數百分比（0.00%）。值應以小數形式儲存（如 0.75 = 75%）。</summary>
        Percent = 3,
    }
}
