#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
            List<TModel> items = [];
            foreach (var item in queryToRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(item);
            }
            return GroupAndAggregate(items, req);
        }

        /// <inheritdoc/>
        public async Task<List<Dictionary<string, object?>>> ExecuteAsync<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist,
            CancellationToken cancellationToken = default)
        {
            var items = await AsyncQueryHelper.SafeToListAsync(
                query.Take(MaxMaterializeRows), cancellationToken);
            return GroupAndAggregate(items, req);
        }

        private static List<Dictionary<string, object?>> GroupAndAggregate<TModel>(
            List<TModel> items,
            AnalysisQueryRequest req)
        {
            List<Dictionary<string, object?>> result = [.. items
                .GroupBy(row => BuildGroupKey(row, req.Dimensions, req.DimensionHierarchies))
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

                        // DistinctCount works on the raw value set — no
                        // numeric conversion required, since "how many
                        // unique values" is meaningful regardless of CLR
                        // type. Computing it before the decimal pipeline
                        // also lets it tolerate non-numeric values that
                        // would otherwise throw at conversion.
                        if (m.Func == AggregateFunc.DistinctCount)
                        {
                            int distinct = g
                                .Select(row => propInfo.GetValue(row))
                                .Where(v => v != null)
                                .Distinct()
                                .Count();
                            dict[$"{m.Field}_{m.Func}"] = (decimal?)distinct;
                            continue;
                        }

                        List<decimal?> numericValues = [.. g
                            .Select(row => propInfo.GetValue(row))
                            .Where(v => v != null)
                            .Select(v =>
                            {
                                try
                                {
                                    return Convert.ToDecimal(v);
                                }
                                catch (Exception ex) when (ex is FormatException
                                                         || ex is InvalidCastException
                                                         || ex is OverflowException)
                                {
                                    throw new InvalidOperationException(
                                        $"欄位 '{m.Field}' 包含無法轉換為數值的值" +
                                        $"（型別 {v!.GetType().Name}，值 '{v}'）。" +
                                        "請確認 [Measure] 僅標記數值型別屬性。", ex);
                                }
                            })];

                        decimal? aggValue = m.Func switch
                        {
                            AggregateFunc.Sum   => numericValues.Count == 0 ? 0m : numericValues.Sum(),
                            AggregateFunc.Count => numericValues.Count,
                            AggregateFunc.Avg   => numericValues.Count == 0 ? (decimal?)null : numericValues.Average(),
                            AggregateFunc.Max   => numericValues.Count == 0 ? (decimal?)null : numericValues.Max(),
                            AggregateFunc.Min   => numericValues.Count == 0 ? (decimal?)null : numericValues.Min(),
                            _ => throw new NotSupportedException($"Unsupported func {m.Func}")
                        };
                        dict[$"{m.Field}_{m.Func}"] = aggValue;
                    }
                    return dict;
                })];
            return result;
        }

        private static string BuildGroupKey<TModel>(
            TModel row,
            List<string> dimensions,
            Dictionary<string, DateHierarchy>? hierarchies)
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
                       // Encode date hierarchy as a compact integer for grouping:
                       //   Year: 2026, Quarter: 20261, Month: 202603, Day: 20260309
                       // Formatted to a human-readable label by DateTruncator.FormatKey.
                       int key = h switch
                       {
                           DateHierarchy.Year    => dt.Year,
                           DateHierarchy.Quarter => dt.Year * 10 + ((dt.Month - 1) / 3 + 1),
                           DateHierarchy.Month   => dt.Year * 100 + dt.Month,
                           DateHierarchy.Day     => dt.Year * 10000 + dt.Month * 100 + dt.Day,
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
