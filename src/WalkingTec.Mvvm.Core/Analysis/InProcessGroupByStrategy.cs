#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 記憶體內 GroupBy 聚合策略。
    /// 先 Take(MaxMaterializeRows) 載入記憶體，再以 LINQ-to-Objects 執行 GroupBy。
    /// </summary>
    public class InProcessGroupByStrategy : IGroupByStrategy
    {
        // Tunables live in AnalysisLimits; these wrappers preserve the
        // existing read-site references inside this file and from
        // AnalysisQueryEngine without forcing every call site to know
        // about the limits class.
        internal static int MaxMaterializeRows => AnalysisLimits.MaxMaterializeRows;
        private static int MaxRows => AnalysisLimits.MaxResultRows;

        // ── Truncation probe elimination (Bucket B opt-in) ────────────────────
        // Records how many rows were actually pulled from the DB for the most
        // recent Execute/ExecuteAsync call. Set to MaxMaterializeRows + 1 when
        // the source contained MORE rows than the limit so the engine can
        // compute dataTruncated without issuing a second COUNT(*) query.
        // The field is internal so AnalysisQueryEngine can access it via a
        // concrete-type check; the IGroupByStrategy interface is unchanged.
        //
        // THREADING: this is per-call mutable state. Instances MUST NOT be
        // shared across concurrent queries. GroupByStrategyResolver.Resolve()
        // returns a fresh InProcessGroupByStrategy per call to enforce this.
        internal int LastMaterializeCount { get; private set; }

        // ── Property accessor cache ────────────────────────────────────────────
        // Keyed by (clrType, propertyName) → PropertyInfo resolved once per type.
        // PropertyInfo is immutable — safe for concurrent reads without locking.
        private static readonly ConcurrentDictionary<(Type, string), PropertyInfo> _propCache = new();

        private static PropertyInfo ResolveProperty(Type type, string name)
        {
            return _propCache.GetOrAdd((type, name), k =>
            {
                var pi = k.Item1.GetProperty(k.Item2);
                if (pi is null)
                    throw new InvalidOperationException(
                        $"Property '{k.Item2}' not found on {k.Item1.Name}.");
                return pi;
            });
        }

        // #705: GroupAndAggregate previously called PropertyInfo.GetValue(row) once per
        // dimension/measure per row (reflection invoke on every row of every group). Reuse
        // the PropertyHelper compiled-getter factory (WalkingTec.Mvvm.Core.PropertyHelper.
        // GetPropertyExpression, already cached via ReflectionCache.PropertyAccessors) for a
        // compiled delegate instead. ResolveProperty above is kept and called first so the
        // existing descriptive InvalidOperationException on an unknown field name is
        // preserved unchanged — PropertyHelper.GetPropertyExpression itself throws a raw
        // NullReferenceException for a missing property, which would be a behaviour change.
        private static readonly ConcurrentDictionary<(Type, string), Func<object, object?>> _getterCache = new();

        private static Func<object, object?> ResolveGetter(Type type, string name)
        {
            return _getterCache.GetOrAdd((type, name), k =>
            {
                ResolveProperty(k.Item1, k.Item2);
                return PropertyHelper.GetPropertyExpression(k.Item1, k.Item2);
            });
        }

        public List<Dictionary<string, object?>> Execute<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist,
            CancellationToken cancellationToken = default)
        {
            // Materialise N+1 rows so we can detect truncation in a single
            // DB round-trip — no second COUNT(*) probe needed.
            var queryToRun = query.Take(MaxMaterializeRows + 1);
            List<TModel> items = [];
            foreach (var item in queryToRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(item);
            }
            LastMaterializeCount = items.Count;
            // Trim to MaxMaterializeRows before aggregating so behaviour
            // is identical to the old Take(MaxMaterializeRows) path.
            if (items.Count > MaxMaterializeRows)
                items = items.Take(MaxMaterializeRows).ToList();
            return GroupAndAggregate(items, req);
        }

        /// <inheritdoc/>
        public async Task<List<Dictionary<string, object?>>> ExecuteAsync<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist,
            CancellationToken cancellationToken = default)
        {
            // Materialise N+1 rows to enable single-round-trip truncation detection.
            var raw = await AsyncQueryHelper.SafeToListAsync(
                query.Take(MaxMaterializeRows + 1), cancellationToken);
            LastMaterializeCount = raw.Count;
            // Trim to MaxMaterializeRows before aggregating.
            var items = raw.Count > MaxMaterializeRows
                ? raw.Take(MaxMaterializeRows).ToList()
                : raw;
            return GroupAndAggregate(items, req);
        }

        // ── MeasureAccumulator: single-pass struct to collect sum/count/max/min ───
        private struct MeasureAccumulator
        {
            public decimal Sum;
            public int Count;      // non-null numeric rows
            public decimal Max;
            public decimal Min;
            public bool HasValue;  // true once at least one non-null row is seen
            // DistinctCount uses a HashSet; null-safe via Where in caller
            public HashSet<object?>? DistinctSet;
        }

        private static List<Dictionary<string, object?>> GroupAndAggregate<TModel>(
            List<TModel> items,
            AnalysisQueryRequest req)
        {
            // Pre-resolve dimension compiled getters once (avoids per-row GetProperty +
            // PropertyInfo.GetValue reflection calls inside BuildGroupKey and the
            // key-building lambda).
            var dimGetters = req.Dimensions
                .Select(d => ResolveGetter(typeof(TModel), d))
                .ToArray();

            // Pre-resolve measure compiled getters once.
            var measureGetters = req.Measures
                .Select(m => ResolveGetter(typeof(TModel), m.Field))
                .ToArray();

            List<Dictionary<string, object?>> result = [.. items
                .GroupBy(row => BuildGroupKey(row, req.Dimensions, req.DimensionHierarchies, dimGetters))
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

                    // Single-pass accumulation over the group rows.
                    int measureCount = req.Measures.Count;
                    var accumulators = new MeasureAccumulator[measureCount];
                    // Initialise DistinctSet only for DistinctCount measures.
                    for (int mi = 0; mi < measureCount; mi++)
                    {
                        if (req.Measures[mi].Func == AggregateFunc.DistinctCount)
                            accumulators[mi].DistinctSet = new HashSet<object?>();
                    }

                    foreach (var row in g)
                    {
                        for (int mi = 0; mi < measureCount; mi++)
                        {
                            var m = req.Measures[mi];
                            var rawVal = measureGetters[mi](row!);

                            if (m.Func == AggregateFunc.DistinctCount)
                            {
                                if (rawVal != null)
                                    accumulators[mi].DistinctSet!.Add(rawVal);
                                continue;
                            }

                            if (rawVal == null) continue;

                            decimal dec;
                            try
                            {
                                dec = Convert.ToDecimal(rawVal);
                            }
                            catch (Exception ex) when (ex is FormatException
                                                     || ex is InvalidCastException
                                                     || ex is OverflowException)
                            {
                                throw new InvalidOperationException(
                                    $"欄位 '{m.Field}' 包含無法轉換為數值的值" +
                                    $"（型別 {rawVal.GetType().Name}，值 '{rawVal}'）。" +
                                    "請確認 [Measure] 僅標記數值型別屬性。", ex);
                            }

                            ref var acc = ref accumulators[mi];
                            acc.Sum += dec;
                            acc.Count++;
                            if (!acc.HasValue)
                            {
                                acc.Max = dec;
                                acc.Min = dec;
                                acc.HasValue = true;
                            }
                            else
                            {
                                if (dec > acc.Max) acc.Max = dec;
                                if (dec < acc.Min) acc.Min = dec;
                            }
                        }
                    }

                    // Convert accumulators to final aggregate values.
                    for (int mi = 0; mi < measureCount; mi++)
                    {
                        var m = req.Measures[mi];
                        ref var acc = ref accumulators[mi];

                        decimal? aggValue;
                        if (m.Func == AggregateFunc.DistinctCount)
                        {
                            aggValue = (decimal?)acc.DistinctSet!.Count;
                        }
                        else
                        {
                            aggValue = m.Func switch
                            {
                                AggregateFunc.Sum   => acc.Count == 0 ? 0m : acc.Sum,
                                AggregateFunc.Count => (decimal?)acc.Count,
                                AggregateFunc.Avg   => acc.Count == 0 ? (decimal?)null : acc.Sum / acc.Count,
                                AggregateFunc.Max   => acc.HasValue ? (decimal?)acc.Max : (decimal?)null,
                                AggregateFunc.Min   => acc.HasValue ? (decimal?)acc.Min : (decimal?)null,
                                _ => throw new NotSupportedException($"Unsupported func {m.Func}")
                            };
                        }
                        dict[$"{m.Field}_{m.Func}"] = aggValue;
                    }

                    return dict;
                })];
            return result;
        }

        private static string BuildGroupKey<TModel>(
            TModel row,
            List<string> dimensions,
            Dictionary<string, DateHierarchy>? hierarchies,
            Func<object, object?>[] dimGetters)
            => string.Join('\0', dimensions.Select((d, idx) =>
               {
                   var getter = dimGetters[idx];
                   var val = getter(row!);
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
