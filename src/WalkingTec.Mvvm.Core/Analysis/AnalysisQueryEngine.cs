#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 動態 GroupBy 聚合引擎。
    /// 驗證白名單 → 套用 Filter → 委託 IGroupByStrategy 執行 GroupBy + 聚合 → 強制截斷。
    /// </summary>
    public class AnalysisQueryEngine
    {
        private const int MaxRows = 10_000;

        private readonly GroupByStrategyResolver _resolver;
        private readonly IAnalysisCache? _cache;

        /// <summary>
        /// 使用預設 Resolver、不帶快取（向後相容）。
        /// </summary>
        public AnalysisQueryEngine() : this(GroupByStrategyResolver.Default, null) { }

        /// <summary>
        /// 使用指定的 Resolver 和快取。
        /// </summary>
        public AnalysisQueryEngine(GroupByStrategyResolver resolver, IAnalysisCache? cache = null)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _cache = cache;
        }

        /// <summary>
        /// 執行分析查詢，回傳聚合結果。
        /// </summary>
        public AnalysisQueryResponse Execute<TModel>(
            IQueryable<TModel> baseQuery,
            AnalysisQueryRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist,
            DBTypeEnum dbType = DBTypeEnum.SQLite)
        {
            var wl = whitelist.ToDictionary(f => f.FieldName);
            ValidateFields(req, wl);

            // 先計算 hash，用於快取查詢（hash 僅由 request 決定，與資料無關）
            var queryHash = ComputeHash(req);

            // 快取命中時直接回傳
            if (_cache != null && _cache.TryGet(queryHash, out var cached) && cached != null)
                return cached;

            var filtered = ApplyFilters(baseQuery, req.Filters, wl);

            var strategy = _resolver.Resolve(dbType, req);
            var rows = strategy.Execute(filtered, req, wl);

            int totalCount = rows.Count;   // 截斷前的真實筆數（I-3）
            bool truncated = rows.Count > MaxRows;
            if (truncated) rows = rows.Take(MaxRows).ToList();

            var columns = req.Dimensions
                .Concat(req.Measures.Select(m => $"{m.Field}_{m.Func}"))
                .ToList();

            var response = new AnalysisQueryResponse
            {
                Columns = columns,
                Rows = rows,
                TotalCount = totalCount,
                Truncated = truncated,
                QueryHash = queryHash
            };

            _cache?.Set(queryHash, response);

            return response;
        }

        /// <summary>
        /// 非泛型入口，供 Controller 使用（IQueryable 無型別參數時）。
        /// </summary>
        public AnalysisQueryResponse ExecuteDynamic(
            IQueryable baseQuery,
            AnalysisQueryRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist,
            DBTypeEnum dbType = DBTypeEnum.SQLite)
        {
            var elementType = baseQuery.ElementType;
            var method = typeof(AnalysisQueryEngine)
                .GetMethod(nameof(Execute));
            if (method is null)
                throw new InvalidOperationException("Execute method not found.");
            method = method.MakeGenericMethod(elementType);
            try
            {
                var result = method.Invoke(this, new object[] { baseQuery, req, whitelist, dbType }) as AnalysisQueryResponse;
                if (result is null)
                    throw new InvalidOperationException("ExecuteDynamic did not return a valid AnalysisQueryResponse.");
                return result;
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
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
                        if (meta.ClrType != typeof(string))
                            throw new InvalidOperationException(
                                $"Contains 只適用於字串欄位，'{filter.Field}' 的型別為 {meta.ClrType.Name}。");
                        var containsMethod = typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) });
                        if (containsMethod is null)
                            throw new InvalidOperationException("string.Contains(string) method not found.");
                        body = Expression.Call(prop,
                            containsMethod,
                            constant);
                        break;
                    default:
                        throw new InvalidOperationException($"Operator '{filter.Operator}' is not supported.");
                }

                query = query.Where(Expression.Lambda<Func<TModel, bool>>(body, param));
            }
            return query;
        }

        private static object? ConvertValue(string value, Type targetType)
        {
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            try
            {
                return Convert.ChangeType(value, underlying);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidOperationException($"Cannot convert '{value}' to {underlying.Name}.", ex);
            }
        }

        private static string ComputeHash(AnalysisQueryRequest req)
        {
            var raw = System.Text.Json.JsonSerializer.Serialize(req);
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes).Substring(0, 16);
        }
    }
}
