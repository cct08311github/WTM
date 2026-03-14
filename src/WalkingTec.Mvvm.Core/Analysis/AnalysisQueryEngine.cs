#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 分析模式查詢引擎。
    /// 串接 ListVM 的 IQueryable 與 IGroupByStrategy 執行動態聚合。
    /// </summary>
    public class AnalysisQueryEngine
    {
        private readonly IGroupByStrategyResolver _resolver;
        private readonly IAnalysisCache? _cache;

        public AnalysisQueryEngine(IGroupByStrategyResolver resolver, IAnalysisCache? cache = null)
        {
            _resolver = resolver;
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

            var totalCount = rows.Count;
            var truncated = false;
            // 與 IGroupByStrategy 內的 MaxRows 保持一致，若結果達到上限則標記截斷
            if (totalCount > 10_000)
            {
                rows = rows.Take(10_000).ToList();
                truncated = true;
            }

            var response = new AnalysisQueryResponse
            {
                Columns = req.Dimensions.Concat(req.Measures.Select(m => $"{m.Field}_{m.Func}")).ToList(),
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
                    throw new InvalidOperationException($"Function '{m.Func}' is not allowed for field '{m.Field}'.");
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
                if (!whitelist.ContainsKey(f.Field)) continue;

                var prop = Expression.Property(param, f.Field);
                var val = Expression.Constant(f.Value);
                // 簡單轉換，實際 WTM 會有更複雜的類型匹配邏輯
                Expression filterExpr = f.Operator switch
                {
                    FilterOperator.Eq => Expression.Equal(prop, Expression.Convert(val, prop.Type)),
                    FilterOperator.Gt => Expression.GreaterThan(prop, Expression.Convert(val, prop.Type)),
                    FilterOperator.Gte => Expression.GreaterThanOrEqual(prop, Expression.Convert(val, prop.Type)),
                    FilterOperator.Lt => Expression.LessThan(prop, Expression.Convert(val, prop.Type)),
                    FilterOperator.Lte => Expression.LessThanOrEqual(prop, Expression.Convert(val, prop.Type)),
                    FilterOperator.Contains => Expression.Call(prop, typeof(string).GetMethod("Contains", new[] { typeof(string) })!, val),
                    _ => throw new NotSupportedException($"Operator {f.Operator} not supported in current analysis engine version.")
                };

                body = body == null ? filterExpr : Expression.AndAlso(body, filterExpr);
            }

            if (body == null) return query;
            return query.Where(Expression.Lambda<Func<TModel, bool>>(body, param));
        }

        private static string String(object? val) => val?.ToString() ?? string.Empty;

        private static string ComputeHash(AnalysisQueryRequest req, string? identityKey = null)
        {
            var raw = System.Text.Json.JsonSerializer.Serialize(req);
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
