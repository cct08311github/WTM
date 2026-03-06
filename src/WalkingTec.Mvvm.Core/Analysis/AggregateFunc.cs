#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 分析模式中可用的聚合函式，支援 Flag 組合
    /// </summary>
    [Flags]
    public enum AggregateFunc
    {
        /// <summary>記錄筆數</summary>
        Count = 1,
        /// <summary>加總</summary>
        Sum = 2,
        /// <summary>平均值</summary>
        Avg = 4,
        /// <summary>最大值</summary>
        Max = 8,
        /// <summary>最小值</summary>
        Min = 16
    }
}
