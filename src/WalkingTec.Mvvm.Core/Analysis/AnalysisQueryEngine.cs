#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 分析模式查詢引擎。
    /// 串接 ListVM 的 IQueryable 與 IGroupByStrategy 執行動態聚合。
    /// </summary>
    public class AnalysisQueryEngine
    {
        private readonly GroupByStrategyResolver _resolver;
        private readonly IAnalysisCache? _cache;
        private readonly TimeSpan _defaultTtl;

        public AnalysisQueryEngine(
            GroupByStrategyResolver resolver,
            IAnalysisCache? cache = null,
            TimeSpan? defaultTtl = null)
        {
            _resolver = resolver;
            _cache = cache;
            _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5);
        }

        /// <summary>
        /// 執行分析查詢，回傳聚合結果。
        /// </summary>
        public AnalysisQueryResponse Execute<TModel>(
            IQueryable<TModel> baseQuery,
            AnalysisQueryRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist,
            DBTypeEnum dbType = DBTypeEnum.SQLite,
            string? identityKey = null,
            CancellationToken cancellationToken = default)
        {
            var wl = whitelist.ToDictionary(f => f.FieldName);
            ValidateFields(req, wl);

            // 先計算 hash，用於快取查詢（hash 僅由 request 決定，與資料無關）
            var queryHash = ComputeHash(req, identityKey);

            // 快取命中時直接回傳
            if (_cache != null && _cache.TryGet(queryHash, out var cached) && cached != null)
                return cached;

            var filtered = ApplyFilters(baseQuery, req.Filters, wl);

            var strategy = _resolver.Resolve(dbType, req);
            bool dataTruncated = false;
            List<Dictionary<string, object?>> rows;
            try
            {
                rows = strategy.Execute(filtered, req, wl, cancellationToken);
                // ServerSideGroupByStrategy issues a SQL GROUP BY — all rows are aggregated
                // at DB level, so source data is never truncated (#514).
            }
            catch (InvalidOperationException) when (strategy is ServerSideGroupByStrategy)
            {
                // SQL 翻譯失敗 → fallback to in-process; probe is now needed.
                // Take(N+1) 讓 DB/記憶體只掃到第 N+1 筆即停止，代價遠小於 COUNT(*)。
                var fbProbe = filtered.Take(InProcessGroupByStrategy.MaxMaterializeRows + 1).Count();
                dataTruncated = fbProbe > InProcessGroupByStrategy.MaxMaterializeRows;
                rows = new InProcessGroupByStrategy().Execute(filtered, req, wl, cancellationToken);
            }

            // In-process path: probe whether source exceeds MaxMaterializeRows.
            if (strategy is InProcessGroupByStrategy)
            {
                var probeCount = filtered.Take(InProcessGroupByStrategy.MaxMaterializeRows + 1).Count();
                dataTruncated = probeCount > InProcessGroupByStrategy.MaxMaterializeRows;
            }

            // Resolve enum dimension values to their [Display] names
            ResolveEnumDisplayNames(rows, req.Dimensions, wl);

            var totalCount = rows.Count;
            var truncated = false;
            // 與 IGroupByStrategy 內的 MaxRows 保持一致，若結果達到上限則標記截斷
            if (totalCount > 10_000)
            {
                rows = rows.Take(10_000).ToList();
                truncated = true;
            }

            var displayNames = BuildColumnDisplayNames(req, wl);

            var response = new AnalysisQueryResponse
            {
                Columns = req.Dimensions.Concat(req.Measures.Select(m => $"{m.Field}_{m.Func}")).ToList(),
                Rows = rows,
                TotalCount = totalCount,
                Truncated = truncated,
                DataTruncated = dataTruncated,
                QueryHash = queryHash,
                ColumnDisplayNames = displayNames
            };

            _cache?.Set(queryHash, response, _defaultTtl);

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
            string? identityKey = null,
            CancellationToken cancellationToken = default)
        {
            if (!req.Dimensions.Contains(req.PivotDimension))
            {
                throw new InvalidOperationException($"PivotDimension '{req.PivotDimension}' must be in Dimensions list.");
            }

            // 1. Get raw grouped data
            var groupRes = Execute(baseQuery, req, whitelist, dbType, identityKey, cancellationToken);
            var rawRows = groupRes.Rows;

            // 2. Identify row dimensions and pivot dimension
            var rowDims = req.Dimensions.Where(d => d != req.PivotDimension).ToList();
            var measureNames = req.Measures.Select(m => $"{m.Field}_{m.Func}").ToList();

            // 3. Collect unique values of the pivot dimension
            var pivotValues = rawRows
                .Select(r => String(r[req.PivotDimension]))
                .Distinct()
                .OrderBy(v => v)
                .ToList();

            // 4. Transform into pivot format
            // Group raw rows by the combination of RowDimensions
            var pivotRowsMap = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var row in rawRows)
            {
                var rowKey = string.Join("|", rowDims.Select(d => String(row[d])));
                if (!pivotRowsMap.TryGetValue(rowKey, out var pivotRow))
                {
                    pivotRowsMap[rowKey] = pivotRow = new Dictionary<string, object?>();
                    foreach (var d in rowDims) pivotRow[d] = row[d];
                }

                var pivotVal = String(row[req.PivotDimension]);
                foreach (var m in measureNames)
                {
                    pivotRow[$"{pivotVal}_{m}"] = row[m];
                }
            }

            // 5. Build final column list
            var columns = new List<string>(rowDims);
            foreach (var pv in pivotValues)
            {
                foreach (var m in measureNames) columns.Add($"{pv}_{m}");
            }

            return new AnalysisPivotResponse
            {
                RowDimensions = rowDims,
                PivotValues = pivotValues,
                MeasureNames = measureNames,
                Rows = pivotRowsMap.Values.ToList(),
                Columns = columns,
                Truncated = groupRes.Truncated
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
            string? identityKey = null,
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
                var result = method.Invoke(this, new object?[] { baseQuery, req, whitelist, dbType, identityKey, cancellationToken }) as AnalysisQueryResponse;
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
            string? identityKey = null,
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
                var result = method.Invoke(this, new object?[] { baseQuery, req, whitelist, dbType, identityKey, cancellationToken }) as AnalysisPivotResponse;
                if (result is null)
                    throw new InvalidOperationException("ExecutePivotDynamic did not return a valid AnalysisPivotResponse.");
                return result;
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static void ValidateFields(AnalysisQueryRequest req, Dictionary<string, AnalysisFieldMeta> whitelist)
        {
            foreach (var d in req.Dimensions)
            {
                if (!whitelist.TryGetValue(d, out var meta) || meta.Kind != AnalysisFieldKind.Dimension)
                    throw new InvalidOperationException($"Dimension field '{d}' is not enabled for analysis.");
            }
            foreach (var m in req.Measures)
            {
                if (!whitelist.TryGetValue(m.Field, out var meta) || meta.Kind != AnalysisFieldKind.Measure)
                    throw new InvalidOperationException($"Measure field '{m.Field}' is not enabled for analysis.");
                if ((meta.AllowedFuncs & m.Func) == 0)
                    throw new NotSupportedException($"Function '{m.Func}' is not allowed for field '{m.Field}'.");
            }
        }

        private static IQueryable<TModel> ApplyFilters<TModel>(
            IQueryable<TModel> query,
            List<FilterCondition>? filters,
            Dictionary<string, AnalysisFieldMeta> whitelist)
        {
            if (filters == null || filters.Count == 0) return query;

            var param = Expression.Parameter(typeof(TModel), "x");
            Expression? body = null;

            foreach (var f in filters)
            {
                if (!whitelist.TryGetValue(f.Field, out var meta))
                    throw new InvalidOperationException($"Filter field '{f.Field}' is not enabled for analysis.");

                var prop = Expression.Property(param, f.Field);
                var targetType = prop.Type;
                var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

                Expression? filterExpr = null;
                try
                {
                    if (f.Operator == FilterOperator.In || f.Operator == FilterOperator.NotIn)
                    {
                        System.Collections.IEnumerable? values = null;
                        if (f.Value is string s)
                        {
                            values = s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());
                        }
                        else
                        {
                            values = f.Value as System.Collections.IEnumerable;
                        }

                        if (values == null) throw new InvalidOperationException("Value for 'In'/'NotIn' operator must be an array, list or comma-separated string.");

                        var list = new System.Collections.ArrayList();
                        foreach (var v in values)
                        {
                            list.Add(ChangeType(v, underlyingType));
                        }
                        if (list.Count == 0) throw new InvalidOperationException("'In'/'NotIn' operator requires at least one value.");
                        if (list.Count > 100) throw new InvalidOperationException("'In'/'NotIn' operator supports up to 100 values.");

                        var typedList = Array.CreateInstance(underlyingType, list.Count);
                        list.CopyTo(typedList);
                        var listConst = Expression.Constant(typedList);

                        var containsMethod = typeof(Enumerable).GetMethods()
                            .First(m => m.Name == "Contains" && m.GetParameters().Length == 2)
                            .MakeGenericMethod(underlyingType);

                        Expression propForIn = prop;
                        if (Nullable.GetUnderlyingType(targetType) != null)
                        {
                            propForIn = Expression.Property(prop, "Value");
                            var nullGuard = Expression.NotEqual(prop, Expression.Constant(null, targetType));
                            var containsExpr = Expression.Call(containsMethod, listConst, propForIn);
                            if (f.Operator == FilterOperator.NotIn)
                            {
                                // NOT_NULL AND NOT_CONTAINS — keeps null rows excluded, consistent with
                                // In / NotEq / Gt etc. and SQL semantics (NULL NOT IN … → excluded). (#481)
                                filterExpr = Expression.AndAlso(nullGuard, Expression.Not(containsExpr));
                            }
                            else
                            {
                                filterExpr = Expression.AndAlso(nullGuard, containsExpr);
                            }
                        }
                        else
                        {
                            var inExpr = Expression.Call(containsMethod, listConst, propForIn);
                            filterExpr = f.Operator == FilterOperator.NotIn ? Expression.Not(inExpr) : inExpr;
                        }
                    }
                    else if (f.Operator == FilterOperator.Contains || f.Operator == FilterOperator.NotContains)
                    {
                        if (targetType != typeof(string))
                            throw new InvalidOperationException($"'Contains'/'NotContains' operator is only supported for string fields, not '{targetType.Name}'.");
                        var val = Expression.Constant(f.Value?.ToString() ?? "");
                        Expression containsExpr = Expression.Call(prop, typeof(string).GetMethod("Contains", new[] { typeof(string) })!, val);
                        filterExpr = f.Operator == FilterOperator.NotContains ? Expression.Not(containsExpr) : containsExpr;
                    }
                    else
                    {
                        var val = Expression.Constant(ChangeType(f.Value, underlyingType), underlyingType);
                        var propForCmp = prop;
                        if (Nullable.GetUnderlyingType(targetType) != null)
                        {
                            propForCmp = Expression.Property(prop, "Value");
                        }

                        Expression cmp = f.Operator switch
                        {
                            FilterOperator.Eq => Expression.Equal(propForCmp, val),
                            FilterOperator.NotEq => Expression.NotEqual(propForCmp, val),
                            FilterOperator.Gt => Expression.GreaterThan(propForCmp, val),
                            FilterOperator.Gte => Expression.GreaterThanOrEqual(propForCmp, val),
                            FilterOperator.Lt => Expression.LessThan(propForCmp, val),
                            FilterOperator.Lte => Expression.LessThanOrEqual(propForCmp, val),
                            _ => throw new InvalidOperationException($"Operator {f.Operator} not supported in current analysis engine version.")
                        };

                        if (Nullable.GetUnderlyingType(targetType) != null)
                        {
                            filterExpr = Expression.AndAlso(
                                Expression.NotEqual(prop, Expression.Constant(null, targetType)),
                                cmp
                            );
                        }
                        else
                        {
                            filterExpr = cmp;
                        }
                    }
                }
                catch (Exception ex) when (!(ex is InvalidOperationException))
                {
                    throw new InvalidOperationException($"欄位 '{f.Field}' 的篩選條件無效：{ex.Message}", ex);
                }

                body = body == null ? filterExpr : Expression.AndAlso(body, filterExpr);
            }

            if (body == null) return query;
            return query.Where(Expression.Lambda<Func<TModel, bool>>(body, param));
        }

        private static object? ChangeType(object? value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsEnum)
            {
                if (value is string s)
                {
                    // Fast path: member name ("Regular") or numeric string ("1")
                    if (Enum.TryParse(targetType, s, ignoreCase: true, out var parsed)) return parsed;

                    // Slow path: chart clicks send [Display(Name)] values (e.g. "一般") after
                    // ResolveEnumDisplayNames has translated them. Reverse-lookup. (#473)
                    foreach (var member in Enum.GetValues(targetType))
                    {
                        if (((Enum)member).GetEnumDisplayName() == s)
                            return member;
                    }
                    var validNames = Enum.GetValues(targetType)
                        .Cast<Enum>()
                        .Select(m => m.GetEnumDisplayName() ?? m.ToString())
                        .Distinct();
                    throw new ArgumentException($"'{s}' 不是有效的列舉值。有效值：{string.Join("、", validNames)}");
                }
                return Enum.ToObject(targetType, value);
            }
            return Convert.ChangeType(value, targetType);
        }

        private static string String(object? val) => val?.ToString() ?? string.Empty;

        /// <summary>
        /// Replace enum string values in dimension columns with their [Display(Name)] if available.
        /// </summary>
        private static void ResolveEnumDisplayNames(
            List<Dictionary<string, object?>> rows,
            IList<string> dimensions,
            Dictionary<string, AnalysisFieldMeta> wl)
        {
            // Build a map of dimension columns whose CLR type is an enum
            var enumDims = new Dictionary<string, Type>();
            foreach (var dim in dimensions)
            {
                if (wl.TryGetValue(dim, out var meta))
                {
                    var clr = Nullable.GetUnderlyingType(meta.ClrType) ?? meta.ClrType;
                    if (clr.IsEnum) enumDims[dim] = clr;
                }
            }
            if (enumDims.Count == 0) return;

            // Cache resolved names per enum type
            var displayCache = new Dictionary<string, Dictionary<string, string>>();
            foreach (var kvp in enumDims)
            {
                var cache = new Dictionary<string, string>();
                foreach (var val in Enum.GetValues(kvp.Value))
                {
                    var name = val.ToString()!;
                    var display = ((Enum)val).GetEnumDisplayName();
                    if (!string.IsNullOrEmpty(display) && display != name)
                        cache[name] = display;
                }
                if (cache.Count > 0) displayCache[kvp.Key] = cache;
            }
            if (displayCache.Count == 0) return;

            // Replace values in rows
            foreach (var row in rows)
            {
                foreach (var kvp in displayCache)
                {
                    if (row.TryGetValue(kvp.Key, out var val) && val is string s && kvp.Value.TryGetValue(s, out var display))
                        row[kvp.Key] = display;
                }
            }
        }

        /// <summary>
        /// 聚合函式名稱的中文對照（用於匯出標頭）。
        /// </summary>
        /// <summary>
        /// 固定序列化選項：確保 ComputeHash() 在所有環境、STJ 版本下產生相同的 JSON 字串。
        /// </summary>
        private static readonly System.Text.Json.JsonSerializerOptions _hashSerializerOptions =
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            };

        private static readonly Dictionary<AggregateFunc, string> _funcDisplayNames = new()
        {
            { AggregateFunc.Sum,   "合計" },
            { AggregateFunc.Count, "計數" },
            { AggregateFunc.Avg,   "平均" },
            { AggregateFunc.Max,   "最大" },
            { AggregateFunc.Min,   "最小" },
        };

        /// <summary>
        /// 建立欄位 key → 使用者友善顯示名稱的對照表。
        /// </summary>
        private static Dictionary<string, string> BuildColumnDisplayNames(
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> wl)
        {
            var map = new Dictionary<string, string>();

            // 維度欄位
            foreach (var dim in req.Dimensions)
            {
                var displayName = wl.TryGetValue(dim, out var meta) && !string.IsNullOrEmpty(meta.DisplayName)
                    ? meta.DisplayName
                    : dim;
                map[dim] = displayName;
            }

            // 量值欄位
            foreach (var m in req.Measures)
            {
                var key = $"{m.Field}_{m.Func}";
                var fieldDisplay = wl.TryGetValue(m.Field, out var meta) && !string.IsNullOrEmpty(meta.DisplayName)
                    ? meta.DisplayName
                    : m.Field;
                var funcDisplay = _funcDisplayNames.TryGetValue(m.Func, out var fd) ? fd : m.Func.ToString();
                map[key] = $"{fieldDisplay} {funcDisplay}";
            }

            return map;
        }

        private static string ComputeHash(AnalysisQueryRequest req, string? identityKey = null)
        {
            var raw = System.Text.Json.JsonSerializer.Serialize(req, _hashSerializerOptions);
            if (!string.IsNullOrEmpty(identityKey))
            {
                raw += "|" + identityKey;
            }
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes).Substring(0, 16);
        }
    }
}
