#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 伺服器端 GroupBy 聚合策略。
    /// 透過 Expression Tree 動態建構 IQueryable.GroupBy().Select()，
    /// 將 GROUP BY + 聚合推送至資料庫引擎執行，避免載入大量原始列到記憶體。
    /// 聚合使用 double（SQLite 相容），結果轉回 decimal 以保持與 InProcess 策略一致。
    /// </summary>
    public class ServerSideGroupByStrategy : IGroupByStrategy
    {
        // Result-row cap is centralised in AnalysisLimits; preserve the
        // local read-site shape so per-call sites don't change.
        private static int MaxRows => AnalysisLimits.MaxResultRows;
        // Non-printable 3-byte separator used to join multiple dimension values into a
        // single GROUP BY key. These characters never appear in real data, so the key can
        // be split unambiguously after materialization.
        internal const string KeySeparator = "\x01\x02\x03";

        // ── Enumerable aggregate method caches ─────────────────────────────────
        // Resolved once via typed GetMethod overloads instead of GetMethods().First(predicate).
        // Closed specialisations (keyed by TModel) are cached in per-method dictionaries.
        // MethodInfo is immutable — safe for concurrent reads without locking.
        private static readonly MethodInfo _countWithPredicateOpen =
            typeof(Enumerable).GetMethods()
                .First(m => m.Name == nameof(Enumerable.Count)
                            && m.IsGenericMethod
                            && m.GetParameters().Length == 2);

        private static readonly MethodInfo _countWithoutPredicateOpen =
            typeof(Enumerable).GetMethods()
                .First(m => m.Name == nameof(Enumerable.Count)
                            && m.IsGenericMethod
                            && m.GetParameters().Length == 1);

        private static readonly MethodInfo _selectOpen =
            typeof(Enumerable).GetMethods()
                .First(m => m.Name == nameof(Enumerable.Select)
                            && m.IsGenericMethod
                            && m.GetParameters().Length == 2
                            && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);

        private static readonly MethodInfo _distinctOpen =
            typeof(Enumerable).GetMethods()
                .First(m => m.Name == nameof(Enumerable.Distinct)
                            && m.IsGenericMethod
                            && m.GetParameters().Length == 1);

        // Aggregate methods for Sum/Avg/Max/Min with Func<TModel, double?> selector
        // key: (methodName, TModel) → closed MethodInfo
        private static readonly ConcurrentDictionary<(string, Type), MethodInfo> _aggMethodCache = new();
        // Count/Distinct caches keyed by TModel or keyType
        private static readonly ConcurrentDictionary<Type, MethodInfo> _countWithPredicateCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> _countWithoutPredicateCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> _distinctCache = new();
        // Select<TModel,TKey>: keyed by (TModel, keyType)
        private static readonly ConcurrentDictionary<(Type, Type), MethodInfo> _selectCache = new();
        // Count<keyType>(no predicate): keyed by keyType
        private static readonly ConcurrentDictionary<Type, MethodInfo> _countForDistinctCache = new();

        public virtual List<Dictionary<string, object?>> Execute<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist,
            CancellationToken cancellationToken = default)
        {
            if (req.Dimensions.Count == 0)
                return [];

            // M5: the fixed Tuple<string, double?, double?, double?> projection supports
            // exactly 3 measure slots. A 4th measure would be silently dropped (null).
            // Throw early with a clear diagnostic so the caller knows to switch strategy.
            if (req.Measures != null && req.Measures.Count > 3)
                throw new InvalidOperationException("ServerSideGroupByStrategy supports at most 3 measures.");

            if (req.DimensionHierarchies != null && req.Dimensions.Any(d => req.DimensionHierarchies.TryGetValue(d, out var h) && h != DateHierarchy.None))
            {
                throw new InvalidOperationException("ServerSideGroupByStrategy does not support DateHierarchy. Falling back to InProcess.");
            }

            var projected = BuildProjected<TModel>(query, req, whitelist);
            List<Tuple<string, double?, double?, double?>> materialized = [];
            foreach (var item in projected.Take(MaxRows + 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                materialized.Add(item);
            }

            return BuildResults(materialized, req);
        }

        public virtual async Task<List<Dictionary<string, object?>>> ExecuteAsync<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist,
            CancellationToken cancellationToken = default)
        {
            if (req.Dimensions.Count == 0)
                return [];

            // M5: same guard as Execute; keeps sync/async behaviour identical.
            if (req.Measures != null && req.Measures.Count > 3)
                throw new InvalidOperationException("ServerSideGroupByStrategy supports at most 3 measures.");

            if (req.DimensionHierarchies != null && req.Dimensions.Any(d => req.DimensionHierarchies.TryGetValue(d, out var h) && h != DateHierarchy.None))
            {
                throw new InvalidOperationException("ServerSideGroupByStrategy does not support DateHierarchy. Falling back to InProcess.");
            }

            var projected = BuildProjected<TModel>(query, req, whitelist);
            var materialized = await AsyncQueryHelper.SafeToListAsync(
                projected.Take(MaxRows + 1), cancellationToken);

            return BuildResults(materialized, req);
        }

        private static IQueryable<Tuple<string, double?, double?, double?>> BuildProjected<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist)
        {
            var param = Expression.Parameter(typeof(TModel), "x");

            Expression keyExpr = BuildDimensionToString(param, req.Dimensions[0], whitelist);
            for (int i = 1; i < req.Dimensions.Count; i++)
            {
                var separator = Expression.Constant(KeySeparator);
                var nextDim = BuildDimensionToString(param, req.Dimensions[i], whitelist);
                keyExpr = Expression.Call(
                    typeof(string).GetMethod(nameof(string.Concat),
                        new[] { typeof(string), typeof(string), typeof(string) })!,
                    keyExpr, separator, nextDim);
            }
            var keySelector = Expression.Lambda<Func<TModel, string>>(keyExpr, param);

            var grouped = query.GroupBy(keySelector);

            var gParam = Expression.Parameter(typeof(IGrouping<string, TModel>), "g");
            var keyAccess = Expression.Property(gParam, nameof(IGrouping<string, TModel>.Key));

            // Fixed to Tuple<string, double?, double?, double?> — max 3 measures.
            // EF Core's GroupBy translator requires a compile-time-known projection shape;
            // a dynamic anonymous type cannot be expressed as a translatable expression tree.
            // Unused slots are filled with null. double (not decimal) for SQLite compatibility.
            var measureExprs = new Expression[3];
            for (int i = 0; i < 3; i++)
            {
                if (i < req.Measures.Count)
                {
                    measureExprs[i] = BuildAggregateExpression<TModel>(
                        gParam, req.Measures[i], whitelist);
                }
                else
                {
                    measureExprs[i] = Expression.Constant(null, typeof(double?));
                }
            }

            var tupleType = typeof(Tuple<string, double?, double?, double?>);
            var tupleCtor = tupleType.GetConstructor(
                new[] { typeof(string), typeof(double?), typeof(double?), typeof(double?) })!;
            var tupleNew = Expression.New(tupleCtor, keyAccess,
                measureExprs[0], measureExprs[1], measureExprs[2]);
            var selectLambda = Expression.Lambda<
                Func<IGrouping<string, TModel>, Tuple<string, double?, double?, double?>>>(
                tupleNew, gParam);

            return grouped.Select(selectLambda);
        }

        private static List<Dictionary<string, object?>> BuildResults(
            List<Tuple<string, double?, double?, double?>> materialized,
            AnalysisQueryRequest req)
        {
            List<Dictionary<string, object?>> results = [];
            foreach (var row in materialized)
            {
                var dict = new Dictionary<string, object?>();

                var keyParts = row.Item1.Split(KeySeparator);
                for (int i = 0; i < req.Dimensions.Count; i++)
                {
                    dict[req.Dimensions[i]] = i < keyParts.Length ? keyParts[i] : string.Empty;
                }

                for (int i = 0; i < req.Measures.Count; i++)
                {
                    var m = req.Measures[i];
                    double? raw = i switch
                    {
                        0 => row.Item2,
                        1 => row.Item3,
                        2 => row.Item4,
                        _ => null
                    };
                    dict[$"{m.Field}_{m.Func}"] = (decimal?)raw;
                }

                results.Add(dict);
            }
            return results;
        }

        private static Expression BuildDimensionToString(
            ParameterExpression param, string dimName,
            Dictionary<string, AnalysisFieldMeta> whitelist)
        {
            var meta = whitelist[dimName];
            var prop = Expression.Property(param, dimName);

            if (meta.ClrType == typeof(string))
            {
                return Expression.Coalesce(prop, Expression.Constant(string.Empty));
            }

            var underlying = Nullable.GetUnderlyingType(meta.ClrType);
            if (underlying != null)
            {
                var hasValue = Expression.Property(prop, "HasValue");
                var getValue = Expression.Property(prop, "Value");
                var toString = Expression.Call(
                    Expression.Convert(getValue, typeof(object)),
                    typeof(object).GetMethod(nameof(object.ToString))!);
                return Expression.Condition(hasValue, toString, Expression.Constant(string.Empty));
            }

            var boxed = Expression.Convert(prop, typeof(object));
            return Expression.Call(boxed, typeof(object).GetMethod(nameof(object.ToString))!);
        }

        private static Expression BuildAggregateExpression<TModel>(
            ParameterExpression gParam,
            MeasureRequest measure,
            Dictionary<string, AnalysisFieldMeta> whitelist)
        {
            var innerParam = Expression.Parameter(typeof(TModel), "e");
            var meta = whitelist[measure.Field];
            var propType = meta.ClrType;

            if (measure.Func == AggregateFunc.Count)
            {
                bool isNullable = Nullable.GetUnderlyingType(propType) != null || !propType.IsValueType;

                if (isNullable)
                {
                    Expression propAccess = Expression.Property(innerParam, measure.Field);
                    var predicateBody = Expression.NotEqual(propAccess, Expression.Constant(null, propType));
                    var predicate = Expression.Lambda<Func<TModel, bool>>(predicateBody, innerParam);

                    var countWithPredicateMethod = _countWithPredicateCache.GetOrAdd(
                        typeof(TModel),
                        t => _countWithPredicateOpen.MakeGenericMethod(t));

                    return Expression.Convert(
                        Expression.Call(countWithPredicateMethod, gParam, predicate),
                        typeof(double?));
                }
                else
                {
                    var countMethod = _countWithoutPredicateCache.GetOrAdd(
                        typeof(TModel),
                        t => _countWithoutPredicateOpen.MakeGenericMethod(t));

                    return Expression.Convert(
                        Expression.Call(countMethod, gParam),
                        typeof(double?));
                }
            }

            if (measure.Func == AggregateFunc.DistinctCount)
            {
                // Canonical EF Core 8+ pattern that translates to
                // SQL COUNT(DISTINCT col): g.Select(e => e.Prop).Distinct().Count().
                // For a nullable property, EF emits COUNT(DISTINCT col) which
                // already excludes NULLs per ANSI SQL semantics, so no
                // pre-filter is needed on the server-side path.
                Expression propAccess = Expression.Property(innerParam, measure.Field);
                var keyType = propAccess.Type;
                var keySelector = Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(typeof(TModel), keyType),
                    propAccess, innerParam);

                var selectMethod = _selectCache.GetOrAdd(
                    (typeof(TModel), keyType),
                    k => _selectOpen.MakeGenericMethod(k.Item1, k.Item2));
                Expression projected = Expression.Call(selectMethod, gParam, keySelector);

                var distinctMethod = _distinctCache.GetOrAdd(
                    keyType,
                    t => _distinctOpen.MakeGenericMethod(t));
                Expression distinct = Expression.Call(distinctMethod, projected);

                var countMethod = _countForDistinctCache.GetOrAdd(
                    keyType,
                    t => _countWithoutPredicateOpen.MakeGenericMethod(t));
                return Expression.Convert(
                    Expression.Call(countMethod, distinct),
                    typeof(double?));
            }

            Expression pAccess = Expression.Property(innerParam, measure.Field);
            pAccess = Expression.Convert(pAccess, typeof(double?));

            var valueSelector = Expression.Lambda<Func<TModel, double?>>(pAccess, innerParam);

            // Enumerable.Sum/Average/Max/Min are used here (not Queryable) because the grouping
            // element type is IGrouping<K,TModel> which implements IEnumerable<TModel>.
            // EF Core 8 GroupBy translator recognises Enumerable aggregate calls within a
            // GroupBy.Select() expression tree and emits the corresponding SQL aggregate function.
            string methodName = measure.Func switch
            {
                AggregateFunc.Sum => nameof(Enumerable.Sum),
                AggregateFunc.Avg => nameof(Enumerable.Average),
                AggregateFunc.Max => nameof(Enumerable.Max),
                AggregateFunc.Min => nameof(Enumerable.Min),
                _ => throw new NotSupportedException($"Unsupported aggregate function: {measure.Func}")
            };

            var aggMethod = _aggMethodCache.GetOrAdd(
                (methodName, typeof(TModel)),
                k => typeof(Enumerable)
                    .GetMethods()
                    .Where(m => m.Name == k.Item1 && m.GetParameters().Length == 2)
                    .First(m =>
                    {
                        if (!m.IsGenericMethod) return false;
                        var gm = m.MakeGenericMethod(typeof(TModel));
                        var selectorParam = gm.GetParameters()[1];
                        return selectorParam.ParameterType == typeof(Func<TModel, double?>);
                    })
                    .MakeGenericMethod(typeof(TModel)));

            return Expression.Call(aggMethod, gParam, valueSelector);
        }
    }
}
