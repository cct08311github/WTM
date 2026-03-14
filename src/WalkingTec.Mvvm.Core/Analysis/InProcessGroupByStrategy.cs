#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 記憶體內 GroupBy 聚合策略。
    /// 先 Take(MaxMaterializeRows) 載入記憶體，再以 LINQ-to-Objects 執行 GroupBy。
    /// 從 AnalysisQueryEngine.ExecuteGroupBy 原封不動提取。
    /// </summary>
    public class InProcessGroupByStrategy : IGroupByStrategy
    {
        /// <summary>防止全表載入造成記憶體耗盡（C-1）</summary>
        internal const int MaxMaterializeRows = 50_000;

        // 與 AnalysisQueryEngine.MaxRows 保持一致，用於提前截斷 GroupBy 結果
        private const int MaxRows = 10_000;

        public List<Dictionary<string, object?>> Execute<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist)
        {
            // Phase 1: materialise then group in-process (SQLite + InMemory safe)
            // 限制載入筆數防止 OOM（C-1）；超出上限時查詢結果可能不完整，由呼叫端決策
            var items = query.Take(MaxMaterializeRows).ToList();

            return items
                .GroupBy(row => BuildGroupKey(row, req.Dimensions, req.DimensionHierarchies, whitelist))
                .Take(MaxRows + 1)
                .Select(g =>
                {
                    var dict = new Dictionary<string, object?>();
                    var keyParts = g.Key.Split('\0');
                    for (int i = 0; i < req.Dimensions.Count; i++)
                        dict[req.Dimensions[i]] = keyParts[i];

                    foreach (var m in req.Measures)
                    {
                        var propInfo = typeof(TModel).GetProperty(m.Field);
                        if (propInfo is null)
                            throw new InvalidOperationException($"Property '{m.Field}' not found on {typeof(TModel).Name}.");
                        // 過濾 null 值，避免 nullable 型別的 Convert.ToDecimal 例外（I-10）
                        var values = g
                            .Select(row => propInfo.GetValue(row))
                            .Where(v => v != null)
                            .Select(v => Convert.ToDecimal(v))
                            .ToList();
                        decimal aggValue;
                        switch (m.Func)
                        {
                            case AggregateFunc.Sum:   aggValue = values.Count == 0 ? 0m : values.Sum(); break;
                            case AggregateFunc.Count: aggValue = values.Count; break;
                            case AggregateFunc.Avg:   aggValue = values.Count == 0 ? 0m : values.Average(); break;
                            case AggregateFunc.Max:   aggValue = values.Count == 0 ? 0m : values.Max(); break;
                            case AggregateFunc.Min:   aggValue = values.Count == 0 ? 0m : values.Min(); break;
                            default: throw new NotSupportedException($"Unsupported func {m.Func}");
                        }
                        dict[$"{m.Field}_{m.Func}"] = aggValue;
                    }
                    return dict;
                })
                .ToList();
        }

        private static string BuildGroupKey<TModel>(
            TModel row,
            List<string> dimensions,
            Dictionary<string, DateHierarchy>? hierarchies,
            Dictionary<string, AnalysisFieldMeta> whitelist)
            => string.Join('\0', dimensions.Select(d =>
               {
                   var propInfo = typeof(TModel).GetProperty(d);
                   if (propInfo is null)
                       throw new InvalidOperationException($"Property '{d}' not found on {typeof(TModel).Name}.");
                   var val = propInfo.GetValue(row);
                   if (val == null) return string.Empty;

                   // 日期維度按 hierarchy 截斷
                   if (hierarchies != null
                       && hierarchies.TryGetValue(d, out var h)
                       && h != DateHierarchy.None
                       && val is DateTime dt)
                   {
                       int key = h switch
                       {
                           DateHierarchy.Year => dt.Year,
                           DateHierarchy.Quarter => dt.Year * 10 + ((dt.Month - 1) / 3 + 1),
                           DateHierarchy.Month => dt.Year * 100 + dt.Month,
                           DateHierarchy.Day => dt.Year * 10000 + dt.Month * 100 + dt.Day,
                           _ => throw new ArgumentException($"Unsupported hierarchy: {h}")
                       };
                       return DateTruncator.FormatKey(key, h);
                   }

                   return val.ToString() ?? string.Empty;
               }));
    }
}
