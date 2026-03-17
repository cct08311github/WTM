#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 記憶體內 GroupBy 聚合策略。
    /// 先 Take(MaxMaterializeRows) 載入記憶體，再以 LINQ-to-Objects 執行 GroupBy。
    /// </summary>
    public class InProcessGroupByStrategy : IGroupByStrategy
    {
        internal const int MaxMaterializeRows = 50_000;
        private const int MaxRows = 10_000;

        public List<Dictionary<string, object?>> Execute<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist,
            CancellationToken cancellationToken = default)
        {
            var queryToRun = query.Take(MaxMaterializeRows);
            var items = new List<TModel>();
            foreach (var item in queryToRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(item);
            }

            return items
                .GroupBy(row => BuildGroupKey(row, req.Dimensions, req.DimensionHierarchies, whitelist))
                .Take(MaxRows + 1)
                .Select(g =>
                {
                    var dict = new Dictionary<string, object?>();
                    var keyParts = g.Key.Split('\0');
                    for (int i = 0; i < req.Dimensions.Count; i++)
                    {
                        var raw = i < keyParts.Length ? keyParts[i] : string.Empty;
                        dict[req.Dimensions[i]] = DecodeKeyPart(raw);
                    }

                    foreach (var m in req.Measures)
                    {
                        var propInfo = typeof(TModel).GetProperty(m.Field);
                        if (propInfo is null)
                            throw new InvalidOperationException($"Property '{m.Field}' not found on {typeof(TModel).Name}.");

                        var rawValues = g.Select(row => propInfo.GetValue(row)).ToList();
                        var numericValues = rawValues
                            .Where(v => v != null)
                            .Select(v => Convert.ToDecimal(v))
                            .ToList();

                        decimal? aggValue;
                        switch (m.Func)
                        {
                            case AggregateFunc.Sum:   aggValue = numericValues.Count == 0 ? 0m : numericValues.Sum(); break;
                            case AggregateFunc.Count: aggValue = numericValues.Count; break;
                            case AggregateFunc.Avg:   aggValue = numericValues.Count == 0 ? (decimal?)null : numericValues.Average(); break;
                            case AggregateFunc.Max:   aggValue = numericValues.Count == 0 ? (decimal?)null : numericValues.Max(); break;
                            case AggregateFunc.Min:   aggValue = numericValues.Count == 0 ? (decimal?)null : numericValues.Min(); break;
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

                   return EncodeKeyPart(val.ToString() ?? string.Empty);
               }));

        /// <summary>
        /// Escapes <c>%</c> and <c>\0</c> in a dimension value so it can be safely
        /// joined with the <c>\0</c> separator without ambiguity.
        /// Encode order: % → %25 first, then \0 → %00.
        /// </summary>
        internal static string EncodeKeyPart(string s)
        {
            if (s.IndexOf('%') < 0 && s.IndexOf('\0') < 0) return s;
            return s.Replace("%", "%25").Replace("\0", "%00");
        }

        /// <summary>
        /// Reverses <see cref="EncodeKeyPart"/>.
        /// Decode order: %00 → \0 first, then %25 → % (order is critical).
        /// </summary>
        internal static string DecodeKeyPart(string s)
        {
            if (s.IndexOf('%') < 0) return s;
            return s.Replace("%00", "\0").Replace("%25", "%");
        }
    }
}
