#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // 部分型別載入失敗（常見於 plugin 或依賴版本衝突），取回已成功載入的型別繼續掃描
                    types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
                }

                foreach (var type in types)
                {
                    if (!type.IsAbstract
                        && IsBasePagedListVm(type)
                        && type.GetCustomAttribute<EnableAnalysisAttribute>() != null)
                    {
                        if (!string.IsNullOrEmpty(type.FullName))
                        {
                            _whitelist[type.FullName] = type;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 依 FullName 查詢白名單，找不到時拋出 <see cref="AnalysisVmNotFoundException"/>。
        /// </summary>
        public Type Resolve(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                throw new AnalysisVmNotFoundException(fullName ?? "");
            if (_whitelist.TryGetValue(fullName, out var t))
                return t;
            throw new AnalysisVmNotFoundException(fullName);
        }

        /// <summary>
        /// 回傳所有已註冊的 VM 型別（FullName → Type）。
        /// </summary>
        public IReadOnlyDictionary<string, Type> GetRegisteredTypes()
            => _whitelist;

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
