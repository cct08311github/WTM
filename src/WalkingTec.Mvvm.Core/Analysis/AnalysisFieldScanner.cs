#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;

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
        public static IEnumerable<AnalysisFieldMeta> ScanModel(Type modelType)
        {
            foreach (var prop in modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var dim = prop.GetCustomAttribute<DimensionAttribute>();
                if (dim != null)
                {
                    yield return new AnalysisFieldMeta
                    {
                        FieldName = prop.Name,
                        DisplayName = dim.DisplayName ?? prop.Name,
                        Kind = AnalysisFieldKind.Dimension,
                        ClrType = prop.PropertyType
                    };
                    continue;
                }

                var msr = prop.GetCustomAttribute<MeasureAttribute>();
                if (msr != null)
                {
                    yield return new AnalysisFieldMeta
                    {
                        FieldName = prop.Name,
                        DisplayName = msr.DisplayName ?? prop.Name,
                        Kind = AnalysisFieldKind.Measure,
                        AllowedFuncs = msr.AllowedFuncs,
                        ClrType = prop.PropertyType
                    };
                }
            }
        }
    }
}
