#nullable enable
using System;
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>分析欄位的種類</summary>
    public enum AnalysisFieldKind
    {
        /// <summary>維度（GROUP BY 候選）</summary>
        Dimension,
        /// <summary>度量（聚合計算目標）</summary>
        Measure
    }

    /// <summary>描述一個可分析欄位的 Metadata</summary>
    public class AnalysisFieldMeta
    {
        /// <summary>C# 屬性名稱</summary>
        public string FieldName { get; set; } = string.Empty;

        /// <summary>前端顯示名稱</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>欄位種類（Dimension 或 Measure）</summary>
        public AnalysisFieldKind Kind { get; set; }

        /// <summary>允許的聚合函式（僅 Measure 有效）</summary>
        public AggregateFunc AllowedFuncs { get; set; }

        /// <summary>欄位的 CLR 型別</summary>
        public Type ClrType { get; set; } = typeof(object);

        /// <summary>是否為日期型別的維度欄位</summary>
        public bool IsDate { get; set; }

        /// <summary>日期維度的時間層級（僅 IsDate=true 時有意義）</summary>
        public DateHierarchy Hierarchy { get; set; } = DateHierarchy.None;

        /// <summary>允許存取此欄位的角色列表（逗號分隔）。若為空則不限角色。</summary>
        public string? AllowedRoles { get; set; }

        /// <summary>
        /// 枚舉欄位的所有允許值（顯示名稱）。非枚舉欄位為 null。
        /// 前端用於渲染 &lt;select&gt; 控件，避免使用者猜測枚舉值。
        /// </summary>
        public IReadOnlyList<string>? AllowedValues { get; init; }
    }
}
