#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 動態 GroupBy 聚合引擎。
    /// 驗證白名單 → 套用 Filter → 執行 GroupBy + 聚合 → 強制截斷。
    /// </summary>
    public class AnalysisQueryEngine
    {
        private const int MaxRows = 10_000;

        /// <summary>
        /// 執行分析查詢，回傳聚合結果。
        /// </summary>
        public AnalysisQueryResponse Execute<TModel>(
            IQueryable<TModel> baseQuery,
            AnalysisQueryRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist)
        {
            var wl = whitelist.ToDictionary(f => f.FieldName);
            ValidateFields(req, wl);

            var filtered = ApplyFilters(baseQuery, req.Filters, wl);
            var rows = ExecuteGroupBy(filtered, req, wl);
            bool truncated = rows.Count > MaxRows;
            if (truncated) rows = rows.Take(MaxRows).ToList();

            var columns = req.Dimensions
                .Concat(req.Measures.Select(m => $"{m.Field}_{m.Func}"))
                .ToList();

            return new AnalysisQueryResponse
            {
                Columns = columns,
                Rows = rows,
                TotalCount = rows.Count,
                Truncated = truncated,
                QueryHash = ComputeHash(req)
            };
        }

        /// <summary>
        /// 非泛型入口，供 Controller 使用（IQueryable 無型別參數時）。
        /// </summary>
        public AnalysisQueryResponse ExecuteDynamic(
            IQueryable baseQuery,
            AnalysisQueryRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist)
        {
            var elementType = baseQuery.ElementType;
            var method = typeof(AnalysisQueryEngine)
                .GetMethod(nameof(Execute))
                .MakeGenericMethod(elementType);
            return (AnalysisQueryResponse)method.Invoke(this, new object[] { baseQuery, req, whitelist });
        }

        private static void ValidateFields(AnalysisQueryRequest req, Dictionary<string, AnalysisFieldMeta> wl)
        {
            foreach (var dim in req.Dimensions)
            {
                if (!wl.TryGetValue(dim, out var m) || m.Kind != AnalysisFieldKind.Dimension)
                    throw new InvalidOperationException($"Field '{dim}' is not a valid Dimension.");
            }

            foreach (var mr in req.Measures)
            {
                if (!wl.TryGetValue(mr.Field, out var m) || m.Kind != AnalysisFieldKind.Measure)
                    throw new InvalidOperationException($"Field '{mr.Field}' is not a valid Measure.");
                if (!m.AllowedFuncs.HasFlag(mr.Func))
                    throw new InvalidOperationException(
                        $"AggregateFunc '{mr.Func}' is not allowed for '{mr.Field}'.");
            }

            foreach (var f in req.Filters)
            {
                if (!wl.ContainsKey(f.Field))
                    throw new InvalidOperationException($"Filter field '{f.Field}' is not in whitelist.");
            }
        }

        private static IQueryable<TModel> ApplyFilters<TModel>(
            IQueryable<TModel> query,
            List<FilterCondition> filters,
            Dictionary<string, AnalysisFieldMeta> wl)
        {
            foreach (var filter in filters)
            {
                var param = Expression.Parameter(typeof(TModel), "x");
                var prop = Expression.Property(param, filter.Field);
                var meta = wl[filter.Field];
                var converted = ConvertValue(filter.Value, meta.ClrType);
                var constant = Expression.Constant(converted, meta.ClrType);

                Expression body;
                switch (filter.Operator)
                {
                    case FilterOperator.Eq:
                        body = Expression.Equal(prop, constant);
                        break;
                    case FilterOperator.Gt:
                        body = Expression.GreaterThan(prop, constant);
                        break;
                    case FilterOperator.Gte:
                        body = Expression.GreaterThanOrEqual(prop, constant);
                        break;
                    case FilterOperator.Lt:
                        body = Expression.LessThan(prop, constant);
                        break;
                    case FilterOperator.Lte:
                        body = Expression.LessThanOrEqual(prop, constant);
                        break;
                    case FilterOperator.Contains:
                        body = Expression.Call(prop,
                            typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) }),
                            constant);
                        break;
                    default:
                        throw new NotSupportedException($"Operator {filter.Operator} not supported.");
                }

                query = query.Where(Expression.Lambda<Func<TModel, bool>>(body, param));
            }
            return query;
        }

        private static object ConvertValue(string value, Type targetType)
        {
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            try
            {
                return Convert.ChangeType(value, underlying);
            }
            catch
            {
                throw new InvalidOperationException($"Cannot convert '{value}' to {underlying.Name}.");
            }
        }

        private static List<Dictionary<string, object>> ExecuteGroupBy<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> wl)
        {
            // Phase 1: materialise then group in-process (SQLite + InMemory safe)
            // Production note: verify actual SQL with ToQueryString() to ensure server-side execution
            var items = query.ToList();

            return items
                .GroupBy(row => BuildGroupKey(row, req.Dimensions))
                .Take(MaxRows + 1)
                .Select(g =>
                {
                    var dict = new Dictionary<string, object>();
                    var keyParts = g.Key.Split('\0');
                    for (int i = 0; i < req.Dimensions.Count; i++)
                        dict[req.Dimensions[i]] = keyParts[i];

                    foreach (var m in req.Measures)
                    {
                        var propInfo = typeof(TModel).GetProperty(m.Field);
                        var values = g.Select(row => Convert.ToDecimal(propInfo.GetValue(row))).ToList();
                        decimal aggValue;
                        switch (m.Func)
                        {
                            case AggregateFunc.Sum:   aggValue = values.Sum(); break;
                            case AggregateFunc.Count: aggValue = values.Count; break;
                            case AggregateFunc.Avg:   aggValue = values.Average(); break;
                            case AggregateFunc.Max:   aggValue = values.Max(); break;
                            case AggregateFunc.Min:   aggValue = values.Min(); break;
                            default: throw new NotSupportedException($"Unsupported func {m.Func}");
                        }
                        dict[$"{m.Field}_{m.Func}"] = aggValue;
                    }
                    return dict;
                })
                .ToList();
        }

        private static string BuildGroupKey<TModel>(TModel row, List<string> dimensions)
            => string.Join('\0', dimensions.Select(d =>
                   typeof(TModel).GetProperty(d).GetValue(row)?.ToString() ?? ""));

        private static string ComputeHash(AnalysisQueryRequest req)
        {
            var raw = System.Text.Json.JsonSerializer.Serialize(req);
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes).Substring(0, 16);
        }
    }
}
