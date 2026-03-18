#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;

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
        private const int MaxRows = 10_000;
        internal const string KeySeparator = "\x01\x02\x03";

        public virtual List<Dictionary<string, object?>> Execute<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist,
            CancellationToken cancellationToken = default)
        {
            if (req.Dimensions.Count == 0)
                return new List<Dictionary<string, object?>>();

            if (req.DimensionHierarchies != null && req.Dimensions.Any(d => req.DimensionHierarchies.TryGetValue(d, out var h) && h != DateHierarchy.None))
            {
                throw new InvalidOperationException("ServerSideGroupByStrategy does not support DateHierarchy. Falling back to InProcess.");
            }

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

            var projected = grouped.Select(selectLambda);
            var queryToRun = projected.Take(MaxRows + 1);
            var materialized = new List<Tuple<string, double?, double?, double?>>();
            foreach (var item in queryToRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                materialized.Add(item);
            }

            var results = new List<Dictionary<string, object?>>(materialized.Count);
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
                    // Round to 10 decimal places when converting double→decimal to eliminate
                    // floating-point noise inherent in SQL aggregation results (#558).
                    dict[$"{m.Field}_{m.Func}"] = raw.HasValue
                        ? (decimal?)Math.Round((decimal)raw.Value, 10, MidpointRounding.AwayFromZero)
                        : null;
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
                // Use Enumerable.Count<TSource>(IEnumerable<TSource>) for non-nullable value types,
                // or Enumerable.Count<TSource>(IEnumerable<TSource>, Func<TSource,bool>) for nullable.
                // The predicate overload requires a strongly-typed Func<TModel,bool> lambda so that
                // EF Core's GroupBy translator can match and emit "COUNT(*)" or "COUNT(col)".
                bool isNullable = Nullable.GetUnderlyingType(propType) != null || !propType.IsValueType;

                if (isNullable)
                {
                    Expression propAccess = Expression.Property(innerParam, measure.Field);
                    var predicateBody = Expression.NotEqual(propAccess, Expression.Constant(null, propType));
                    var predicate = Expression.Lambda<Func<TModel, bool>>(predicateBody, innerParam);

                    var countWithPredicateMethod = typeof(Enumerable)
                        .GetMethods()
                        .First(m => m.Name == nameof(Enumerable.Count)
                                    && m.IsGenericMethod
                                    && m.GetParameters().Length == 2)
                        .MakeGenericMethod(typeof(TModel));

                    return Expression.Convert(
                        Expression.Call(countWithPredicateMethod, gParam, predicate),
                        typeof(double?));
                }
                else
                {
                    var countMethod = typeof(Enumerable)
                        .GetMethods()
                        .First(m => m.Name == nameof(Enumerable.Count)
                                    && m.IsGenericMethod
                                    && m.GetParameters().Length == 1)
                        .MakeGenericMethod(typeof(TModel));

                    return Expression.Convert(
                        Expression.Call(countMethod, gParam),
                        typeof(double?));
                }
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

            var aggMethod = typeof(Enumerable)
                .GetMethods()
                .Where(m => m.Name == methodName && m.GetParameters().Length == 2)
                .First(m =>
                {
                    if (!m.IsGenericMethod) return false;
                    var gm = m.MakeGenericMethod(typeof(TModel));
                    var selectorParam = gm.GetParameters()[1];
                    return selectorParam.ParameterType == typeof(Func<TModel, double?>);
                })
                .MakeGenericMethod(typeof(TModel));

            return Expression.Call(aggMethod, gParam, valueSelector);
        }
    }
}
