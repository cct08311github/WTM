#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 透過反射掃描 Model 型別上的 [Dimension] / [Measure] attribute，
    /// 回傳可分析欄位的 Metadata 清單。
    /// </summary>
    public static class AnalysisFieldScanner
    {
        /// <summary>
        /// 掃描指定型別的所有 public instance 屬性，
        /// 回傳有 [Dimension] 或 [Measure] 標記的欄位 Metadata。
        /// </summary>
        /// <exception cref="ArgumentNullException">當 modelType 為 null 時立即擲回。</exception>
        public static IEnumerable<AnalysisFieldMeta> ScanModel(Type modelType)
        {
            ArgumentNullException.ThrowIfNull(modelType);
            return BuildFromTemplates(modelType);
        }

        private static AnalysisFieldMeta[] BuildFromTemplates(Type modelType)
        {
            // Fetch or build the structural template (cached, no localizer-dependent fields).
            var templates = ReflectionCache.AnalysisFieldTemplates.GetOrAdd(
                modelType,
                static t => BuildTemplates(t));

            // Materialize into AnalysisFieldMeta; regenerate AllowedValues per-call so
            // localisation changes are respected at runtime.
            var result = new AnalysisFieldMeta[templates.Length];
            for (int i = 0; i < templates.Length; i++)
            {
                var tmpl = templates[i];
                result[i] = new AnalysisFieldMeta
                {
                    FieldName = tmpl.FieldName,
                    DisplayName = tmpl.DisplayName,
                    Kind = tmpl.Kind,
                    AllowedFuncs = tmpl.AllowedFuncs,
                    ClrType = tmpl.ClrType,
                    IsDate = tmpl.IsDate,
                    Hierarchy = tmpl.Hierarchy,
                    AllowedRoles = tmpl.AllowedRoles,
                    Format = tmpl.Format,
                    AllowedValues = tmpl.UnderlyingEnumType != null
                        ? BuildAllowedValues(tmpl.UnderlyingEnumType)
                        : null
                };
            }
            return result;
        }

        private static AnalysisFieldScanTemplate[] BuildTemplates(Type modelType)
        {
            var list = new List<AnalysisFieldScanTemplate>();
            foreach (var prop in modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var dim = prop.GetCustomAttribute<DimensionAttribute>();
                if (dim != null)
                {
                    var clrType = prop.PropertyType;
                    var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;
                    var isDate = underlying == typeof(DateTime);

                    list.Add(new AnalysisFieldScanTemplate
                    {
                        FieldName = prop.Name,
                        DisplayName = dim.DisplayName ?? prop.Name,
                        Kind = AnalysisFieldKind.Dimension,
                        ClrType = clrType,
                        IsDate = isDate,
                        Hierarchy = isDate ? dim.Hierarchy : DateHierarchy.None,
                        AllowedRoles = dim.AllowedRoles,
                        // Store the underlying enum type for per-call AllowedValues generation.
                        UnderlyingEnumType = underlying.IsEnum ? underlying : null
                    });
                    continue;
                }

                var msr = prop.GetCustomAttribute<MeasureAttribute>();
                if (msr != null)
                {
                    list.Add(new AnalysisFieldScanTemplate
                    {
                        FieldName = prop.Name,
                        DisplayName = msr.DisplayName ?? prop.Name,
                        Kind = AnalysisFieldKind.Measure,
                        AllowedFuncs = msr.AllowedFuncs,
                        ClrType = prop.PropertyType,
                        AllowedRoles = msr.AllowedRoles,
                        Format = msr.Format,
                        UnderlyingEnumType = null
                    });
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// 若 type 為枚舉，回傳所有成員的顯示名稱清單；否則回傳 null。
        /// Localizer is invoked per-call through GetEnumDisplayName.
        /// </summary>
        private static IReadOnlyList<string>? BuildAllowedValues(Type type)
        {
            if (!type.IsEnum) return null;
            IReadOnlyList<string> result = [.. Enum.GetValues(type)
                .Cast<Enum>()
                .Select(e => e.GetEnumDisplayName() ?? e.ToString())];
            return result;
        }
    }
}
