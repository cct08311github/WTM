#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 標記 ListVM 類別可加入分析模式白名單（AnalysisVmRegistry）。
    /// 只有標記此 Attribute 的 BasePagedListVM 子類別才能透過 _AnalysisController 存取。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class EnableAnalysisAttribute : Attribute
    {
        /// <summary>允許存取此分析 VM 的角色列表（逗號分隔）。若為空則不限角色。</summary>
        public string? AllowedRoles { get; set; }
    }
}
