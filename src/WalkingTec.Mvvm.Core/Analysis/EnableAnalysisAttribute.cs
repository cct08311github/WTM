#nullable disable
using System;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 標記 ListVM 類別可加入分析模式白名單（AnalysisVmRegistry）。
    /// 只有標記此 Attribute 的 BasePagedListVM 子類別才能透過 _AnalysisController 存取。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class EnableAnalysisAttribute : Attribute { }
}
