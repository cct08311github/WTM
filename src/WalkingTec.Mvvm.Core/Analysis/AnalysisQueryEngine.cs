#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

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
        /// 建立不帶快取的引擎，使用預設 Resolver（向後相容）。
        /// </summary>
        public AnalysisQueryEngine() : this(GroupByStrategyResolver.Default, null) { }

        /// <summary>
        /// 使用指定的 Resolver，不帶快取。
        /// </summary>
        public AnalysisQueryEngine(GroupByStrategyResolver resolver) : this(resolver, null) { }

        /// <summary>
        /// 使用指定的快取，預設 Resolver。
        /// </summary>
        public AnalysisQueryEngine(IAnalysisCache? cache) : this(GroupByStrategyResolver.Default, cache) { }

        /// <summary>
        /// 使用指定的 Resolver 和快取。
        /// </summary>
        public AnalysisQueryEngine(GroupByStrategyResolver resolver, IAnalysisCache? cache)
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
            DBTypeEnum dbType = DBTypeEnum.SQLite,
            CancellationToken cancellationToken = default)
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
            List<Dictionary<string, object?>> rows;
            try
            {
                rows = strategy.Execute(filtered, req, wl, cancellationToken);
            }
            catch (InvalidOperationException) when (strategy is ServerSideGroupByStrategy)
            {
                // Server-side SQL 翻譯失敗 → fallback to in-process
                rows = new InProcessGroupByStrategy().Execute(filtered, req, wl, cancellationToken);
            }

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
        /// 執行 Pivot 樞紐分析。
        /// </summary>
        public AnalysisPivotResponse ExecutePivot<TModel>(
            IQueryable<TModel> baseQuery,
            AnalysisPivotRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist,
            DBTypeEnum dbType = DBTypeEnum.SQLite,
            CancellationToken cancellationToken = default)
        {
            if (!req.Dimensions.Contains(req.PivotDimension))
            {
                throw new InvalidOperationException($"PivotDimension '{req.PivotDimension}' must be in Dimensions list.");
            }

            // 1. Get raw grouped data
            var groupRes = Execute(baseQuery, req, whitelist, dbType, cancellationToken);
            var rawRows = groupRes.Rows;

            // 2. Identify row dimensions and pivot dimension
            var rowDims = req.Dimensions.Where(d => d != req.PivotDimension).ToList();
            var measureNames = req.Measures.Select(m => $"{m.Field}_{m.Func}").ToList();

            // 3. Extract unique PivotValues
            var pivotValues = rawRows
                .Select(r => r[req.PivotDimension]?.ToString() ?? string.Empty)
                .Distinct()
                .OrderBy(v => v)
                .ToList();

            if (pivotValues.Count > 50)
            {
                throw new InvalidOperationException($"Pivot dimension '{req.PivotDimension}' has {pivotValues.Count} unique values. Maximum allowed is 50.");
            }

            // 4. Build pivoted rows
            var pivotRowsMap = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var row in rawRows)
            {
                var rowKey = string.Join('\0', rowDims.Select(d => row[d]?.ToString() ?? string.Empty));
                
                if (!pivotRowsMap.TryGetValue(rowKey, out var pivotRow))
                {
                    pivotRow = new Dictionary<string, object?>();
                    foreach (var d in rowDims)
                    {
                        pivotRow[d] = row[d];
                    }
                    
                    // Initialize all pivot cells with 0/null
                    foreach (var pv in pivotValues)
                    {
                        foreach (var m in measureNames)
                        {
                            pivotRow[$"{pv}_{m}"] = 0m;
                        }
                    }
                    pivotRowsMap[rowKey] = pivotRow;
                }

                var pvValue = row[req.PivotDimension]?.ToString() ?? string.Empty;
                foreach (var m in measureNames)
                {
                    pivotRow[$"{pvValue}_{m}"] = row[$"{m}"];
                }
            }

            var columns = new List<string>(rowDims);
            foreach (var pv in pivotValues)
            {
                foreach (var m in measureNames)
                {
                    columns.Add($"{pv}_{m}");
                }
            }

            return new AnalysisPivotResponse
            {
                RowDimensions = rowDims,
                PivotValues = pivotValues,
                MeasureNames = measureNames,
                Rows = pivotRowsMap.Values.ToList(),
                Columns = columns
            };
        }

        /// <summary>
        /// 非泛型入口，供 Controller 使用（IQueryable 無型別參數時）。
        /// </summary>
        public AnalysisQueryResponse ExecuteDynamic(
            IQueryable baseQuery,
            AnalysisQueryRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist,
            DBTypeEnum dbType = DBTypeEnum.SQLite,
            CancellationToken cancellationToken = default)
        {
            var elementType = baseQuery.ElementType;
            var method = typeof(AnalysisQueryEngine)
                .GetMethod(nameof(Execute));
            if (method is null)
                throw new InvalidOperationException("Execute method not found.");
            method = method.MakeGenericMethod(elementType);
            try
            {
                var result = method.Invoke(this, new object[] { baseQuery, req, whitelist, dbType, cancellationToken }) as AnalysisQueryResponse;
                if (result is null)
                    throw new InvalidOperationException("ExecuteDynamic did not return a valid AnalysisQueryResponse.");
                return result;
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        /// <summary>
        /// 非泛型 Pivot 入口，供 Controller 使用。
        /// </summary>
        public AnalysisPivotResponse ExecutePivotDynamic(
            IQueryable baseQuery,
            AnalysisPivotRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist,
            DBTypeEnum dbType = DBTypeEnum.SQLite,
            CancellationToken cancellationToken = default)
        {
            var elementType = baseQuery.ElementType;
            var method = typeof(AnalysisQueryEngine)
                .GetMethod(nameof(ExecutePivot));
            if (method is null)
                throw new InvalidOperationException("ExecutePivot method not found.");
            method = method.MakeGenericMethod(elementType);
            try
            {
                var result = method.Invoke(this, new object[] { baseQuery, req, whitelist, dbType, cancellationToken }) as AnalysisPivotResponse;
                if (result is null)
                    throw new InvalidOperationException("ExecutePivotDynamic did not return a valid AnalysisPivotResponse.");
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
                
                Expression body;
                if (filter.Operator == FilterOperator.In)
                {
                    var valueList = filter.Values ?? filter.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (valueList.Count == 0)
                        throw new InvalidOperationException($"In 運算子至少需要一個值。({filter.Field})");
                    if (valueList.Count > 100)
                        throw new InvalidOperationException($"In 運算子最多支援 100 個值，實際傳入 {valueList.Count} 個。({filter.Field})");

                    var listType = typeof(List<>).MakeGenericType(meta.ClrType);
                    var typedList = Activator.CreateInstance(listType) as System.Collections.IList;
                    if (typedList != null)
                    {
                        foreach (var v in valueList)
                        {
                            typedList.Add(ConvertValue(v, meta.ClrType));
                        }
                    }

                    body = Expression.Call(
                        typeof(Enumerable), "Contains", new[] { meta.ClrType },
                        Expression.Constant(typedList, listType), prop);
                }
                else
                {
                    var converted = ConvertValue(filter.Value, meta.ClrType);
                    var constant = Expression.Constant(converted, meta.ClrType);

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
                if (underlying.IsEnum)
                    return Enum.Parse(underlying, value, ignoreCase: true);

                return Convert.ChangeType(value, underlying);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
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
