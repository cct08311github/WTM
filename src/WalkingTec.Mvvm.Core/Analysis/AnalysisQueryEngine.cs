#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 分析模式查詢引擎。
    /// 串接 ListVM 的 IQueryable 與 IGroupByStrategy 執行動態聚合。
    /// </summary>
    public partial class AnalysisQueryEngine
    {
        private static readonly ILogger? _logger =
            CoreProgram.GetLogger(nameof(AnalysisQueryEngine));

        // ── ExecuteDynamic* reflection caches ─────────────────────────────────
        // One separate dictionary per entry-point because each resolves a different
        // open generic method. Keyed by runtime elementType.
        private static readonly ConcurrentDictionary<Type, MethodInfo> _executeMethodCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> _executeAsyncMethodCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> _executePivotMethodCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> _executePivotAsyncMethodCache = new();

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

            // 先計算 hash，用於快取查詢（hash 僅由 request 決定，與資料無關）。
            // M29 fix: ComputeHash returns null when identityKey is absent —
            // null means "do not cache", so both get and set are skipped.
            var queryHash = ComputeHash(req, identityKey);

            // 快取命中時直接回傳（僅在 queryHash 非 null 時才查快取）
            if (queryHash != null && _cache != null && _cache.TryGet(queryHash, out var cached) && cached != null)
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
                // SQL 翻譯失敗 → fallback to in-process.
                // InProcessGroupByStrategy materialises N+1 rows internally and
                // exposes LastMaterializeCount, so NO second COUNT(*) probe is needed.
                var fbStrategy = new InProcessGroupByStrategy();
                rows = fbStrategy.Execute(filtered, req, wl, cancellationToken);
                dataTruncated = fbStrategy.LastMaterializeCount > InProcessGroupByStrategy.MaxMaterializeRows;
            }

            // In-process path: read truncation flag from the strategy instance —
            // InProcessGroupByStrategy already materialised N+1 rows and recorded
            // LastMaterializeCount, so no additional DB round-trip is needed.
            if (strategy is InProcessGroupByStrategy ip)
            {
                dataTruncated = ip.LastMaterializeCount > InProcessGroupByStrategy.MaxMaterializeRows;
            }

            // Resolve enum dimension values to their [Display] names
            ResolveEnumDisplayNames(rows, req.Dimensions, wl);

            // SQL-standard pipeline: GROUP BY → HAVING → ORDER BY → LIMIT.
            // Apply HAVING before the totalCount snapshot so "showing X of Y"
            // counts the post-filter groups (the user filtered them out, so
            // they shouldn't show in the cardinality report).
            rows = ApplyHavingFilters(rows, req);

            // Period-over-period comparison. Run the second query with the
            // alternate filter set, then augment rows in-place with
            // _Compare / _Delta / _ChangePct columns. Done BEFORE Sort+TopN
            // so the user can sort by the derived columns.
            if (req.CompareWith != null)
            {
                var compareReq = BuildComparisonSubRequest(req);
                var compareResp = Execute(baseQuery, compareReq, whitelist, dbType, identityKey, cancellationToken);
                rows = AugmentWithComparison(rows, compareResp.Rows, req);
            }

            // Grand total covers the same post-HAVING universe as TotalCount
            // (computed here, BEFORE TopN trims the visible rows, so the
            // total is "of the user's filtered universe" not "of what's on
            // screen").
            var grandTotal = req.IncludeGrandTotal ? ComputeGrandTotal(rows, req) : null;

            var totalCount = rows.Count;
            var truncated = false;
            // Cap shared with IGroupByStrategy via AnalysisLimits; if the
            // operator raised it for a bigger reporting surface, both the
            // strategy-side Take(MaxRows + 1) and this guard see the same
            // value.
            var maxResultRows = AnalysisLimits.MaxResultRows;
            if (totalCount > maxResultRows)
            {
                rows = [.. rows.Take(maxResultRows)];
                truncated = true;
            }

            // Sort + TopN. TotalCount above already reflects the post-having
            // group cardinality, so the client can render "showing 3 of 5"
            // even when TopN trims the visible rows.
            rows = ApplySortAndTopN(rows, req);

            var displayNames = BuildColumnDisplayNames(req, wl);
            var columnFormats = BuildColumnFormats(req, wl);

            // Auto-generated BI insights (operator-friendly Chinese
            // sentences). Computed AFTER Sort+TopN so the narrative
            // reflects what the user actually sees on the dashboard.
            var insights = req.IncludeInsights
                ? BuildInsights(rows, req, displayNames)
                : null;

            var response = new AnalysisQueryResponse
            {
                Columns = BuildResponseColumns(req),
                Rows = rows,
                TotalCount = totalCount,
                Truncated = truncated,
                DataTruncated = dataTruncated,
                DataTruncatedMessage = dataTruncated
                    ? "聚合結果僅基於前 50,000 筆原始資料，可能不代表完整數據。建議縮小篩選條件或聯繫管理員啟用 ServerSide 策略。"
                    : null,
                QueryHash = queryHash,
                ColumnDisplayNames = displayNames,
                ColumnFormats = columnFormats,
                GrandTotalRow = grandTotal,
                Insights = insights,
            };

            // Forecasting (10.5.0+) — 在所有資料增益完成後外推 N 期。失敗
            // 條件下 (無時間維度、實際列不足) 靜默 no-op；不影響 TotalCount /
            // Insights / GrandTotalRow。
            if (req.Forecast != null)
            {
                AnalysisForecastEngine.ApplyForecast(
                    response, req.Forecast, req.Dimensions,
                    BuildMeasureColumnNames(req), req.DimensionHierarchies);
            }

            // M29 fix: only populate the cache when queryHash is non-null (i.e. identityKey was present)
            if (queryHash != null)
                _cache?.Set(queryHash, response, _defaultTtl);

            return response;
        }

        /// <summary>
        /// 執行 Pivot 樞紐分析。
        /// </summary>

        /// <summary>
        /// 非同步執行分析查詢，回傳聚合結果。
        /// </summary>
        public async Task<AnalysisQueryResponse> ExecuteAsync<TModel>(
            IQueryable<TModel> baseQuery,
            AnalysisQueryRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist,
            DBTypeEnum dbType = DBTypeEnum.SQLite,
            string? identityKey = null,
            CancellationToken cancellationToken = default)
        {
            var wl = whitelist.ToDictionary(f => f.FieldName);
            ValidateFields(req, wl);

            // M29 fix: null queryHash means "do not cache" (identity-less request)
            var queryHash = ComputeHash(req, identityKey);

            if (queryHash != null && _cache != null && _cache.TryGet(queryHash, out var cached) && cached != null)
                return cached;

            var filtered = ApplyFilters(baseQuery, req.Filters, wl);

            var strategy = _resolver.Resolve(dbType, req);
            bool dataTruncated = false;
            List<Dictionary<string, object?>> rows;
            try
            {
                rows = await strategy.ExecuteAsync(filtered, req, wl, cancellationToken);
            }
            catch (InvalidOperationException) when (strategy is ServerSideGroupByStrategy)
            {
                // SQL 翻譯失敗 → fallback to in-process.
                // InProcessGroupByStrategy materialises N+1 rows internally and
                // exposes LastMaterializeCount, so NO second COUNT(*) probe is needed.
                var fbStrategy = new InProcessGroupByStrategy();
                rows = await fbStrategy.ExecuteAsync(filtered, req, wl, cancellationToken);
                dataTruncated = fbStrategy.LastMaterializeCount > InProcessGroupByStrategy.MaxMaterializeRows;
            }

            // In-process path: read truncation flag from the strategy instance —
            // no additional async DB round-trip needed.
            if (strategy is InProcessGroupByStrategy ipAsync)
            {
                dataTruncated = ipAsync.LastMaterializeCount > InProcessGroupByStrategy.MaxMaterializeRows;
            }

            ResolveEnumDisplayNames(rows, req.Dimensions, wl);

            // SQL-standard pipeline: GROUP BY → HAVING → ORDER BY → LIMIT.
            rows = ApplyHavingFilters(rows, req);

            // Period-over-period comparison — same semantics as sync path.
            if (req.CompareWith != null)
            {
                var compareReq = BuildComparisonSubRequest(req);
                var compareResp = await ExecuteAsync(baseQuery, compareReq, whitelist, dbType, identityKey, cancellationToken)
                    .ConfigureAwait(false);
                rows = AugmentWithComparison(rows, compareResp.Rows, req);
            }

            // Grand total — same post-HAVING / pre-TopN semantics as sync path.
            var grandTotal = req.IncludeGrandTotal ? ComputeGrandTotal(rows, req) : null;

            var totalCount = rows.Count;
            var truncated = false;
            var maxResultRowsAsync = AnalysisLimits.MaxResultRows;
            if (totalCount > maxResultRowsAsync)
            {
                rows = [.. rows.Take(maxResultRowsAsync)];
                truncated = true;
            }

            // Sort + TopN — same semantics as the sync path.
            rows = ApplySortAndTopN(rows, req);

            var displayNames = BuildColumnDisplayNames(req, wl);
            var columnFormats = BuildColumnFormats(req, wl);

            // Auto-generated BI insights — same semantics as sync path.
            var insights = req.IncludeInsights
                ? BuildInsights(rows, req, displayNames)
                : null;

            var response = new AnalysisQueryResponse
            {
                Columns = BuildResponseColumns(req),
                Rows = rows,
                TotalCount = totalCount,
                Truncated = truncated,
                DataTruncated = dataTruncated,
                DataTruncatedMessage = dataTruncated
                    ? "聚合結果僅基於前 50,000 筆原始資料，可能不代表完整數據。建議縮小篩選條件或聯繫管理員啟用 ServerSide 策略。"
                    : null,
                QueryHash = queryHash,
                ColumnDisplayNames = displayNames,
                ColumnFormats = columnFormats,
                GrandTotalRow = grandTotal,
                Insights = insights,
            };

            if (req.Forecast != null)
            {
                AnalysisForecastEngine.ApplyForecast(
                    response, req.Forecast, req.Dimensions,
                    BuildMeasureColumnNames(req), req.DimensionHierarchies);
            }

            // M29 fix: only populate the cache when queryHash is non-null (i.e. identityKey was present)
            if (queryHash != null)
                _cache?.Set(queryHash, response, _defaultTtl);

            return response;
        }

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
            List<string> rowDims = [.. req.Dimensions.Where(d => d != req.PivotDimension)];
            List<string> measureNames = [.. req.Measures.Select(m => $"{m.Field}_{m.Func}")];

            // 3. Collect unique values of the pivot dimension
            List<string> pivotValues = [.. rawRows
                .Select(r => String(r[req.PivotDimension]))
                .Distinct()
                .OrderBy(v => v)];

            // 4. Transform into pivot format
            // Group raw rows by the combination of RowDimensions.
            // M3 fix: escape each segment so that a literal '|' in a dimension value
            // cannot collide with the '|' join delimiter.
            var pivotRowsMap = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var row in rawRows)
            {
                var rowKey = string.Join("|", rowDims.Select(d => EscapeKeySeg(String(row[d]))));
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
            List<string> columns = [.. rowDims];
            foreach (var pv in pivotValues)
            {
                foreach (var m in measureNames) columns.Add($"{pv}_{m}");
            }

            return new AnalysisPivotResponse
            {
                RowDimensions = rowDims,
                PivotValues = pivotValues,
                MeasureNames = measureNames,
                Rows = [.. pivotRowsMap.Values],
                Columns = columns,
                Truncated = groupRes.Truncated
            };
        }

        /// <summary>
        /// 非同步執行 Pivot 樞紐分析。
        /// </summary>
        public async Task<AnalysisPivotResponse> ExecutePivotAsync<TModel>(
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

            var groupRes = await ExecuteAsync(baseQuery, req, whitelist, dbType, identityKey, cancellationToken);
            var rawRows = groupRes.Rows;

            List<string> rowDims = [.. req.Dimensions.Where(d => d != req.PivotDimension)];
            List<string> measureNames = [.. req.Measures.Select(m => $"{m.Field}_{m.Func}")];

            List<string> pivotValues = [.. rawRows
                .Select(r => String(r[req.PivotDimension]))
                .Distinct()
                .OrderBy(v => v)];

            // M3 fix: escape each segment so that a literal '|' in a dimension value
            // cannot collide with the '|' join delimiter.
            var pivotRowsMap = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var row in rawRows)
            {
                var rowKey = string.Join("|", rowDims.Select(d => EscapeKeySeg(String(row[d]))));
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

            List<string> columns = [.. rowDims];
            foreach (var pv in pivotValues)
            {
                foreach (var m in measureNames) columns.Add($"{pv}_{m}");
            }

            return new AnalysisPivotResponse
            {
                RowDimensions = rowDims,
                PivotValues = pivotValues,
                MeasureNames = measureNames,
                Rows = [.. pivotRowsMap.Values],
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
            var method = _executeMethodCache.GetOrAdd(elementType, t =>
            {
                var m = typeof(AnalysisQueryEngine).GetMethod(nameof(Execute));
                if (m is null)
                    throw new InvalidOperationException("Execute method not found.");
                return m.MakeGenericMethod(t);
            });
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
        /// 非泛型非同步入口，供 Controller 使用。
        /// </summary>
        public async Task<AnalysisQueryResponse> ExecuteDynamicAsync(
            IQueryable baseQuery,
            AnalysisQueryRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist,
            DBTypeEnum dbType = DBTypeEnum.SQLite,
            string? identityKey = null,
            CancellationToken cancellationToken = default)
        {
            var elementType = baseQuery.ElementType;
            var method = _executeAsyncMethodCache.GetOrAdd(elementType, t =>
            {
                var m = typeof(AnalysisQueryEngine).GetMethod(nameof(ExecuteAsync));
                if (m is null)
                    throw new InvalidOperationException("ExecuteAsync method not found.");
                return m.MakeGenericMethod(t);
            });
            try
            {
                var task = method.Invoke(this, new object?[] { baseQuery, req, whitelist, dbType, identityKey, cancellationToken }) as Task<AnalysisQueryResponse>;
                if (task is null)
                    throw new InvalidOperationException("ExecuteDynamicAsync did not return a valid Task<AnalysisQueryResponse>.");
                return await task;
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
            var method = _executePivotMethodCache.GetOrAdd(elementType, t =>
            {
                var m = typeof(AnalysisQueryEngine).GetMethod(nameof(ExecutePivot));
                if (m is null)
                    throw new InvalidOperationException("ExecutePivot method not found.");
                return m.MakeGenericMethod(t);
            });
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

        /// <summary>
        /// 非泛型非同步 Pivot 入口，供 Controller 使用。
        /// </summary>
        public async Task<AnalysisPivotResponse> ExecutePivotDynamicAsync(
            IQueryable baseQuery,
            AnalysisPivotRequest req,
            IEnumerable<AnalysisFieldMeta> whitelist,
            DBTypeEnum dbType = DBTypeEnum.SQLite,
            string? identityKey = null,
            CancellationToken cancellationToken = default)
        {
            var elementType = baseQuery.ElementType;
            var method = _executePivotAsyncMethodCache.GetOrAdd(elementType, t =>
            {
                var m = typeof(AnalysisQueryEngine).GetMethod(nameof(ExecutePivotAsync));
                if (m is null)
                    throw new InvalidOperationException("ExecutePivotAsync method not found.");
                return m.MakeGenericMethod(t);
            });
            try
            {
                var task = method.Invoke(this, new object?[] { baseQuery, req, whitelist, dbType, identityKey, cancellationToken }) as Task<AnalysisPivotResponse>;
                if (task is null)
                    throw new InvalidOperationException("ExecutePivotDynamicAsync did not return a valid Task<AnalysisPivotResponse>.");
                return await task;
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static void ValidateFields(AnalysisQueryRequest req, Dictionary<string, AnalysisFieldMeta> whitelist)
        {
            ValidateClauseCounts(req);
            foreach (var d in req.Dimensions)
            {
                if (!whitelist.TryGetValue(d, out var meta) || meta.Kind != AnalysisFieldKind.Dimension)
                    throw new AnalysisFieldNotFoundException(d, "Dimension");
            }
            foreach (var m in req.Measures)
            {
                if (!whitelist.TryGetValue(m.Field, out var meta) || meta.Kind != AnalysisFieldKind.Measure)
                    throw new AnalysisFieldNotFoundException(m.Field, "Measure");
                if ((meta.AllowedFuncs & m.Func) == 0)
                    throw new NotSupportedException($"Function '{m.Func}' is not allowed for field '{m.Field}'.");
            }
            ValidateSortAndTopN(req);
            ValidateHavingFilters(req);
        }

        /// <summary>
        /// Engine-boundary cap on clause-list and field-list sizes (#795). See
        /// <see cref="AnalysisLimits.MaxFilterClauses"/> / <see cref="AnalysisLimits.MaxGroupByFields"/>
        /// for the rationale — these used to be enforced only by <c>_AnalysisController</c>'s own
        /// pre-checks, so any other caller of this engine (e.g. the Dashboard analysis widget data
        /// source, whose caller can override the stored widget's <c>Dimensions</c>/<c>Measures</c>
        /// via the request body) reached <see cref="ApplyFilters"/> / <see cref="ApplyHavingFilters"/>
        /// / <see cref="ApplySortAndTopN"/> / the GroupBy projection with unbounded lists. Running
        /// it here means every current and future caller is covered without having to remember to
        /// copy the check.
        /// </summary>
        private static void ValidateClauseCounts(AnalysisQueryRequest req)
        {
            var max = AnalysisLimits.MaxFilterClauses;
            if (req.Filters?.Count > max)
                throw new AnalysisException($"Filters must not exceed {max} clauses.");
            if (req.HavingFilters?.Count > max)
                throw new AnalysisException($"HavingFilters must not exceed {max} clauses.");
            if (req.Sort?.Count > max)
                throw new AnalysisException($"Sort must not exceed {max} clauses.");
            if (req.CompareWith?.Filters?.Count > max)
                throw new AnalysisException($"CompareWith.Filters must not exceed {max} clauses.");

            var maxFields = AnalysisLimits.MaxGroupByFields;
            if (req.Dimensions?.Count > maxFields)
                throw new AnalysisException($"Dimensions must not exceed {maxFields} fields.");
            if (req.Measures?.Count > maxFields)
                throw new AnalysisException($"Measures must not exceed {maxFields} fields.");
        }

        /// <summary>
        /// 取得每個 measure 的 result column key（<c>{Field}_{Func}</c>）— 預測
        /// 引擎用來決定要外推哪些欄。不含維度與對比衍生欄。
        /// </summary>
        internal static List<string> BuildMeasureColumnNames(AnalysisQueryRequest req)
        {
            var cols = new List<string>(req.Measures.Count);
            foreach (var m in req.Measures)
            {
                cols.Add($"{m.Field}_{m.Func}");
            }
            return cols;
        }

        internal static List<string> BuildResponseColumns(AnalysisQueryRequest req)
        {
            var cols = new List<string>(req.Dimensions);
            var hasCompare = req.CompareWith != null;
            foreach (var m in req.Measures)
            {
                var key = $"{m.Field}_{m.Func}";
                cols.Add(key);
                if (hasCompare)
                {
                    cols.Add($"{key}_Compare");
                    cols.Add($"{key}_Delta");
                    cols.Add($"{key}_ChangePct");
                }
            }
            return cols;
        }

        /// <summary>
        /// Build the secondary AnalysisQueryRequest used for the
        /// period-over-period comparison query. Same dimensions /
        /// measures / dimension hierarchies as the primary; the
        /// alternate <see cref="ComparisonRequest.Filters"/> replaces
        /// the primary's. Sort / TopN / HavingFilters / IncludeGrandTotal /
        /// CompareWith are all dropped on the secondary so the engine
        /// doesn't recurse infinitely or produce a partial mismatch.
        /// Public for unit-test determinism.
        /// </summary>
        internal static AnalysisQueryRequest BuildComparisonSubRequest(AnalysisQueryRequest primary)
        {
            return new AnalysisQueryRequest
            {
                ListVmType = primary.ListVmType,
                SearcherFormData = primary.SearcherFormData,
                Dimensions = primary.Dimensions,
                Measures = primary.Measures,
                Filters = primary.CompareWith?.Filters ?? new List<FilterCondition>(),
                DimensionHierarchies = primary.DimensionHierarchies,
                // Intentionally clear: avoid infinite recursion / mismatched
                // shape between primary and comparison row sets.
                Sort = null,
                TopN = null,
                HavingFilters = null,
                IncludeGrandTotal = false,
                CompareWith = null,
            };
        }

        private static string String(object? val) => val?.ToString() ?? string.Empty;
    }
}
