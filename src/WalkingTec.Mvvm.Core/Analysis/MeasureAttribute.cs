#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 標記此屬性可作為分析模式的度量（聚合計算目標）
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class MeasureAttribute : Attribute
    {
        /// <summary>允許的聚合函式（Flag 組合）</summary>
        public AggregateFunc AllowedFuncs { get; set; }

        /// <summary>前端顯示名稱（可為 null，預設使用屬性名）</summary>
        public string? DisplayName { get; set; }

        /// <summary>允許存取此度量的角色列表（逗號分隔）</summary>
        public string? AllowedRoles { get; set; }

        /// <summary>Excel 匯出時的數字格式（預設 Auto）</summary>
        public MeasureFormat Format { get; set; } = MeasureFormat.Auto;
    }
}
