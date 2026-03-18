#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
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

        /// <summary>
        /// 跨請求快取：(modelType, propertyName) → 已編譯的 accessor delegate。
        /// 避免每次請求重新呼叫 Expression.Lambda(...).Compile()。
        /// </summary>
        private static readonly ConcurrentDictionary<(Type, string), Func<object, object?>> _accessorCache
            = new ConcurrentDictionary<(Type, string), Func<object, object?>>();

        public List<Dictionary<string, object?>> Execute<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist,
            CancellationToken cancellationToken = default)
        {
            // 一次性建立所有需要的 accessor — 迴圈內不再做任何反射
            var allFields = req.Dimensions
                .Concat(req.Measures.Select(m => m.Field))
                .Distinct();

            var accessors = new Dictionary<string, Func<TModel, object?>>();
            foreach (var field in allFields)
            {
                accessors[field] = GetOrCreateAccessor<TModel>(field);
            }

            var queryToRun = query.Take(MaxMaterializeRows);
            var items = new List<TModel>();
            foreach (var item in queryToRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(item);
            }

            return items
                .GroupBy(row => BuildGroupKey(row, req.Dimensions, req.DimensionHierarchies, accessors))
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
                        var accessor = accessors[m.Field];
                        var rawValues = g.Select(row => accessor(row)).ToList();
                        var numericValues = rawValues
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
                            })
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

        /// <summary>
        /// 從跨請求快取取得（或建立）強型別 accessor，
        /// 再包裝成 Func&lt;TModel, object?&gt; 供本次請求使用。
        /// </summary>
        private static Func<TModel, object?> GetOrCreateAccessor<TModel>(string fieldName)
        {
            // 快取的是 Func<object, object?> 以支援非泛型索引
            var boxed = _accessorCache.GetOrAdd((typeof(TModel), fieldName), key =>
            {
                var (type, name) = key;
                var prop = type.GetProperty(name)
                    ?? throw new InvalidOperationException($"Property '{name}' not found on {type.Name}.");
                var param = Expression.Parameter(typeof(object), "x");
                var cast = Expression.Convert(param, type);
                var propAccess = Expression.Property(cast, prop);
                var body = Expression.Convert(propAccess, typeof(object));
                return Expression.Lambda<Func<object, object?>>(body, param).Compile();
            });

            // 將 Func<object, object?> 包裝為 Func<TModel, object?> — 無額外反射
            return row => boxed(row!);
        }

        private static string BuildGroupKey<TModel>(
            TModel row,
            List<string> dimensions,
            Dictionary<string, DateHierarchy>? hierarchies,
            Dictionary<string, Func<TModel, object?>> accessors)
            => string.Join('\0', dimensions.Select(d =>
               {
                   var val = accessors[d](row);
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
