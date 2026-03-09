#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 標記此屬性可作為分析模式的維度（GROUP BY 候選欄位）
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class DimensionAttribute : Attribute
    {
        /// <summary>前端顯示名稱（可為 null，預設使用屬性名）</summary>
        public string? DisplayName { get; set; }

        /// <summary>日期維度的時間層級（僅對 DateTime/DateTime? 屬性有效）</summary>
        public DateHierarchy Hierarchy { get; set; } = DateHierarchy.None;
    }
}
