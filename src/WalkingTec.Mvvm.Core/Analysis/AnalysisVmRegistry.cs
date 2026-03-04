#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 維護允許進行分析查詢的 ListVM 型別白名單。
    /// 取代不安全的 Type.GetType() 直接反射，確保只有明確標記的 VM 才能被存取。
    /// </summary>
    public class AnalysisVmRegistry
    {
        private readonly Dictionary<string, Type> _whitelist = new Dictionary<string, Type>();

        /// <summary>
        /// 掃描指定 Assembly 集合，將所有標記 [EnableAnalysis] 的 BasePagedListVM 子類別加入白名單。
        /// </summary>
        public void Build(IEnumerable<Assembly> assemblies)
        {
            _whitelist.Clear();
            foreach (var asm in assemblies)
            {
                foreach (var type in asm.GetTypes())
                {
                    if (!type.IsAbstract
                        && IsBasePagedListVm(type)
                        && type.GetCustomAttribute<EnableAnalysisAttribute>() != null)
                    {
                        _whitelist[type.FullName] = type;
                    }
                }
            }
        }

        /// <summary>
        /// 依 FullName 查詢白名單，找不到時拋出 InvalidOperationException。
        /// </summary>
        public Type Resolve(string fullName)
        {
            if (_whitelist.TryGetValue(fullName, out var t))
                return t;
            throw new InvalidOperationException($"VM type not registered for analysis: {fullName}");
        }

        private static bool IsBasePagedListVm(Type t)
        {
            var bt = t.BaseType;
            while (bt != null)
            {
                if (bt.IsGenericType && bt.GetGenericTypeDefinition() == typeof(BasePagedListVM<,>))
                    return true;
                bt = bt.BaseType;
            }
            return false;
        }
    }
}
