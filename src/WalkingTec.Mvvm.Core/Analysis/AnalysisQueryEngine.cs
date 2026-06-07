#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
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
    public class AnalysisQueryEngine
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

        // ── ApplyFilters Contains MethodInfo caches ────────────────────────────
        // Open generic methods resolved once; closed specialisations cached per CLR type.
        private static readonly MethodInfo _enumerableContainsOpenMethod =
            typeof(Enumerable)
                .GetMethods()
                .First(m => m.Name == "Contains" && m.GetParameters().Length == 2);

        private static readonly MethodInfo _stringContainsMethod =
            typeof(string).GetMethod("Contains", new[] { typeof(string) })!;

        private static readonly ConcurrentDictionary<Type, MethodInfo> _enumerableContainsClosedCache = new();

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
        /// Validate <see cref="AnalysisQueryRequest.HavingFilters"/>: each
        /// filter's <c>Field</c> must reference a requested
        /// <c>{measure.Field}_{measure.Func}</c> result column;
        /// <c>Operator</c> must be one of the numeric-comparison set
        /// (Eq / NotEq / Gt / Gte / Lt / Lte) — Contains/In/etc. don't
        /// apply to scalar aggregate values.
        /// </summary>
        internal static void ValidateHavingFilters(AnalysisQueryRequest req)
        {
            if (req.HavingFilters == null || req.HavingFilters.Count == 0) { return; }

            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in req.Measures) { allowed.Add($"{m.Field}_{m.Func}"); }

            foreach (var h in req.HavingFilters)
            {
                if (string.IsNullOrWhiteSpace(h.Field))
                {
                    throw new AnalysisException("HavingFilter.Field must not be empty.");
                }
                if (!allowed.Contains(h.Field))
                {
                    throw new AnalysisException(
                        $"HavingFilter field '{h.Field}' is not in the requested Measures. " +
                        $"Use the '{{Field}}_{{Func}}' name (e.g. 'Amount_Sum').");
                }
                switch (h.Operator)
                {
                    case FilterOperator.Eq:
                    case FilterOperator.NotEq:
                    case FilterOperator.Gt:
                    case FilterOperator.Gte:
                    case FilterOperator.Lt:
                    case FilterOperator.Lte:
                        break;
                    default:
                        throw new AnalysisException(
                            $"HavingFilter operator '{h.Operator}' is not supported. " +
                            "HAVING applies to scalar aggregate values; allowed operators are Eq, NotEq, Gt, Gte, Lt, Lte.");
                }
            }
        }

        /// <summary>
        /// Compute the grand-total row for the supplied
        /// <paramref name="rows"/> per the requested measures. Runs
        /// after <see cref="ApplyHavingFilters"/> and before
        /// <see cref="ApplySortAndTopN"/> so the total reflects the
        /// HAVING-filtered universe (matches
        /// <see cref="AnalysisQueryResponse.TotalCount"/> semantics);
        /// TopN-trimming the visible rows does NOT shrink the total.
        /// </summary>
        /// <remarks>
        /// Aggregation rules (per measure column <c>{Field}_{Func}</c>):
        /// <list type="bullet">
        /// <item><c>Sum</c> / <c>Count</c> → sum of group values.</item>
        /// <item><c>Max</c> → max of group values.</item>
        /// <item><c>Min</c> → min of group values.</item>
        /// <item><c>Avg</c> → null. A meaningful weighted average
        /// requires per-group counts, which the GroupBy result drops;
        /// emitting a "simple average of group averages" would be
        /// silently wrong.</item>
        /// <item><c>DistinctCount</c> → null. Grand-total distinct
        /// would require re-querying the raw rows; summing per-group
        /// distincts is wrong because the same value can repeat across
        /// groups.</item>
        /// </list>
        /// All dimension columns receive <c>null</c> so the client is
        /// free to append a "Total" / "總計" label anywhere it fits the
        /// rendering surface.
        /// </remarks>
        /// <summary>
        /// Assemble the response Columns list. Dimensions first, then
        /// per-measure result column, and (when comparison is on) the
        /// three derived columns (<c>_Compare</c>, <c>_Delta</c>,
        /// <c>_ChangePct</c>) interleaved per measure so the front end
        /// can render the four-column comparison group together rather
        /// than scattering across the row.
        /// </summary>
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

        /// <summary>
        /// Augment the primary query's rows with three derived columns
        /// per measure (<c>{Field}_{Func}_Compare</c>,
        /// <c>{Field}_{Func}_Delta</c>, <c>{Field}_{Func}_ChangePct</c>)
        /// using <paramref name="compareRows"/> as the comparison
        /// data set. Rows are joined by the dimension-tuple; rows that
        /// exist only in the comparison set are appended with primary
        /// measure values <c>null</c> so the client sees both sides
        /// of the picture. Public for unit-test determinism.
        /// </summary>
        internal static List<Dictionary<string, object?>> AugmentWithComparison(
            List<Dictionary<string, object?>> primaryRows,
            List<Dictionary<string, object?>> compareRows,
            AnalysisQueryRequest req)
        {
            // Build a dimension-key → comparison row lookup. Use the
            // same encoding as InProcessGroupByStrategy's group key so
            // join semantics are stable across both strategies.
            string KeyOf(Dictionary<string, object?> row)
            {
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < req.Dimensions.Count; i++)
                {
                    if (i > 0) { sb.Append('\0'); }
                    row.TryGetValue(req.Dimensions[i], out var v);
                    sb.Append(v?.ToString() ?? "");
                }
                return sb.ToString();
            }

            var compareLookup = new Dictionary<string, Dictionary<string, object?>>();
            foreach (var c in compareRows)
            {
                compareLookup[KeyOf(c)] = c;
            }

            var measureKeys = req.Measures.Select(m => $"{m.Field}_{m.Func}").ToList();
            var matchedCompareKeys = new HashSet<string>();

            foreach (var row in primaryRows)
            {
                var key = KeyOf(row);
                compareLookup.TryGetValue(key, out var compareRow);
                if (compareRow != null) { matchedCompareKeys.Add(key); }

                foreach (var mKey in measureKeys)
                {
                    object? primaryVal = row.TryGetValue(mKey, out var pv) ? pv : null;
                    object? compareVal = compareRow != null && compareRow.TryGetValue(mKey, out var cv) ? cv : null;

                    row[$"{mKey}_Compare"] = compareVal;
                    row[$"{mKey}_Delta"] = ComputeDelta(primaryVal, compareVal);
                    row[$"{mKey}_ChangePct"] = ComputeChangePct(primaryVal, compareVal);
                }
            }

            // Comparison rows that didn't match any primary group still
            // matter — "本期沒有但對比期有的" is a legitimate finding
            // (e.g. a region that lost all sales). Append with primary
            // measure values null and Compare values populated.
            foreach (var compareRow in compareRows)
            {
                var key = KeyOf(compareRow);
                if (matchedCompareKeys.Contains(key)) { continue; }

                var newRow = new Dictionary<string, object?>();
                foreach (var d in req.Dimensions)
                {
                    compareRow.TryGetValue(d, out var dv);
                    newRow[d] = dv;
                }
                foreach (var mKey in measureKeys)
                {
                    compareRow.TryGetValue(mKey, out var cv);
                    newRow[mKey] = null;
                    newRow[$"{mKey}_Compare"] = cv;
                    newRow[$"{mKey}_Delta"] = ComputeDelta(null, cv);
                    newRow[$"{mKey}_ChangePct"] = ComputeChangePct(null, cv);
                }
                primaryRows.Add(newRow);
            }

            return primaryRows;

            static decimal? ComputeDelta(object? primary, object? compare)
            {
                if (!TryAsDecimal(primary, out var p)) { return null; }
                if (!TryAsDecimal(compare, out var c)) { return null; }
                return p - c;
            }

            static decimal? ComputeChangePct(object? primary, object? compare)
            {
                if (!TryAsDecimal(primary, out var p)) { return null; }
                if (!TryAsDecimal(compare, out var c)) { return null; }
                if (c == 0m) { return null; } // divide-by-zero guard — null beats Infinity in JSON
                return (p - c) / c;
            }

            static bool TryAsDecimal(object? v, out decimal d)
            {
                d = 0m;
                if (v == null) { return false; }
                switch (v)
                {
                    case decimal dec: d = dec; return true;
                    case int i: d = i; return true;
                    case long l: d = l; return true;
                    case short s: d = s; return true;
                    case double dbl when !double.IsNaN(dbl) && !double.IsInfinity(dbl): d = (decimal)dbl; return true;
                    case float f when !float.IsNaN(f) && !float.IsInfinity(f): d = (decimal)f; return true;
                    default: return false;
                }
            }
        }

        /// <summary>
        /// Build human-readable BI insight sentences. Operates on the
        /// first measure in <paramref name="req"/> (focus heuristic —
        /// multi-measure narrative gets noisy). Each heuristic runs in
        /// its own try/catch so a single failing rule doesn't lose the
        /// rest. Empty / single-row / all-null inputs degrade to an
        /// empty list (callers should handle that — UI just hides the
        /// callout box).
        /// </summary>
        /// <remarks>
        /// Output is unstable across versions — the engine is free to
        /// improve heuristics, change wording, add new lines. Callers
        /// must NOT parse the strings; use the underlying numeric
        /// columns when programmatic access is needed.
        /// </remarks>
        internal static List<string> BuildInsights(
            List<Dictionary<string, object?>> rows,
            AnalysisQueryRequest req,
            Dictionary<string, string> columnDisplayNames)
        {
            var insights = new List<string>();
            if (rows.Count == 0 || req.Measures.Count == 0) { return insights; }

            var primary = req.Measures[0];
            var measureKey = $"{primary.Field}_{primary.Func}";
            var measureLabel = columnDisplayNames.TryGetValue(measureKey, out var dn) ? dn : measureKey;

            // Pre-extract numeric values + their dim labels so each
            // heuristic operates on a consistent view.
            var samples = new List<(string DimLabel, decimal Value, Dictionary<string, object?> Row)>();
            foreach (var row in rows)
            {
                if (!row.TryGetValue(measureKey, out var raw) || raw == null) { continue; }
                if (!TryAsDecimal(raw, out var v)) { continue; }
                samples.Add((BuildDimLabel(row, req.Dimensions), v, row));
            }
            if (samples.Count == 0) { return insights; }

            // 1. Top performer + ratio to average
            try
            {
                var avg = samples.Average(s => s.Value);
                var top = samples.OrderByDescending(s => s.Value).First();
                if (avg != 0m && samples.Count >= 2)
                {
                    var ratio = top.Value / avg;
                    insights.Add(
                        $"本期最高: {top.DimLabel} ({measureLabel} = {FormatNumber(top.Value)})，為平均的 {FormatNumber(ratio, 2)} 倍");
                }
                else
                {
                    insights.Add($"本期最高: {top.DimLabel} ({measureLabel} = {FormatNumber(top.Value)})");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "AnalysisQueryEngine insight skipped: top performer");
            }

            // 2. Bottom performer (only when ≥ 2 samples; with 1 sample
            // it duplicates the Top line).
            try
            {
                if (samples.Count >= 2)
                {
                    var bottom = samples.OrderBy(s => s.Value).First();
                    insights.Add($"本期最低: {bottom.DimLabel} ({measureLabel} = {FormatNumber(bottom.Value)})");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "AnalysisQueryEngine insight skipped: lowest sample");
            }

            // 3. Period-over-period leaders (only when CompareWith on)
            try
            {
                if (req.CompareWith != null)
                {
                    var pctKey = measureKey + "_ChangePct";
                    var pctSamples = new List<(string DimLabel, decimal Pct)>();
                    foreach (var row in rows)
                    {
                        if (!row.TryGetValue(pctKey, out var raw) || raw == null) { continue; }
                        if (!TryAsDecimal(raw, out var v)) { continue; }
                        pctSamples.Add((BuildDimLabel(row, req.Dimensions), v));
                    }
                    if (pctSamples.Count >= 1)
                    {
                        var topGain = pctSamples.OrderByDescending(s => s.Pct).First();
                        var topLoss = pctSamples.OrderBy(s => s.Pct).First();
                        if (pctSamples.Count == 1 || topGain.DimLabel == topLoss.DimLabel)
                        {
                            insights.Add(
                                $"與對比期相比，{topGain.DimLabel} 變化 {FormatPct(topGain.Pct)}");
                        }
                        else
                        {
                            insights.Add(
                                $"與對比期相比，{topGain.DimLabel} 漲幅最大 ({FormatPct(topGain.Pct)})，{topLoss.DimLabel} 跌幅最大 ({FormatPct(topLoss.Pct)})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "AnalysisQueryEngine insight skipped: P-o-P leaders");
            }

            // 4. Pareto concentration: do the top 20% of groups carry
            // ≥ 80% of total? Skip when fewer than 5 groups (Pareto
            // ratio is not meaningful at small N).
            try
            {
                if (samples.Count >= 5)
                {
                    var sorted = samples.OrderByDescending(s => s.Value).ToList();
                    var totalSum = sorted.Sum(s => s.Value);
                    if (totalSum > 0m)
                    {
                        var topN = Math.Max(1, (int)Math.Ceiling(sorted.Count * 0.20));
                        var topSum = sorted.Take(topN).Sum(s => s.Value);
                        var topPct = topSum / totalSum;
                        if (topPct >= 0.80m)
                        {
                            insights.Add(
                                $"Top {topN} 群組佔總計的 {FormatPct(topPct, signed: false)}（Pareto 集中度高）");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "AnalysisQueryEngine insight skipped: Pareto concentration");
            }

            // 5. Outliers (z-score > 2). Skip when N < 4 — population
            // stddev on tiny samples is noise.
            try
            {
                if (samples.Count >= 4)
                {
                    var avg = samples.Average(s => s.Value);
                    var variance = samples.Sum(s => (s.Value - avg) * (s.Value - avg)) / samples.Count;
                    var stddev = (decimal)Math.Sqrt((double)variance);
                    if (stddev > 0m)
                    {
                        var outliers = samples
                            .Select(s => (s.DimLabel, s.Value, Z: (s.Value - avg) / stddev))
                            .Where(s => Math.Abs(s.Z) > 2m)
                            .OrderByDescending(s => Math.Abs(s.Z))
                            .ToList();
                        if (outliers.Count > 0)
                        {
                            var top = outliers.First();
                            insights.Add(
                                $"{outliers.Count} 個群組為異常離群值（|z| > 2.0）：{top.DimLabel} ({measureLabel}, z = {FormatNumber(top.Z, 2)})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "AnalysisQueryEngine insight skipped: z-score outliers");
            }

            return insights;

            static string BuildDimLabel(Dictionary<string, object?> row, List<string> dims)
            {
                if (dims.Count == 0) { return "(整體)"; }
                var parts = dims.Select(d =>
                {
                    row.TryGetValue(d, out var v);
                    return v?.ToString() ?? "(空)";
                });
                return string.Join(" / ", parts);
            }

            static string FormatNumber(decimal v, int decimals = 0)
                => v.ToString($"N{decimals}", System.Globalization.CultureInfo.InvariantCulture);

            static string FormatPct(decimal v, bool signed = true)
            {
                var pct = v * 100m;
                var sign = (signed && pct >= 0m) ? "+" : "";
                return sign + pct.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%";
            }

            static bool TryAsDecimal(object? v, out decimal d)
            {
                d = 0m;
                if (v == null) { return false; }
                switch (v)
                {
                    case decimal dec: d = dec; return true;
                    case int i: d = i; return true;
                    case long l: d = l; return true;
                    case short s: d = s; return true;
                    case double dbl when !double.IsNaN(dbl) && !double.IsInfinity(dbl): d = (decimal)dbl; return true;
                    case float f when !float.IsNaN(f) && !float.IsInfinity(f): d = (decimal)f; return true;
                    default: return false;
                }
            }
        }

        internal static Dictionary<string, object?> ComputeGrandTotal(
            List<Dictionary<string, object?>> rows,
            AnalysisQueryRequest req)
        {
            var total = new Dictionary<string, object?>();

            foreach (var d in req.Dimensions) { total[d] = null; }

            foreach (var m in req.Measures)
            {
                var key = $"{m.Field}_{m.Func}";
                total[key] = m.Func switch
                {
                    AggregateFunc.Sum   => SumOf(rows, key),
                    AggregateFunc.Count => SumOf(rows, key),
                    AggregateFunc.Max   => ExtremaOf(rows, key, max: true),
                    AggregateFunc.Min   => ExtremaOf(rows, key, max: false),
                    _                   => (object?)null, // Avg / DistinctCount intentionally null
                };
            }
            return total;

            static decimal? SumOf(List<Dictionary<string, object?>> rows, string key)
            {
                decimal sum = 0;
                bool anyNonNull = false;
                foreach (var row in rows)
                {
                    if (!row.TryGetValue(key, out var raw) || raw == null) { continue; }
                    if (TryAsDecimal(raw, out var d))
                    {
                        sum += d;
                        anyNonNull = true;
                    }
                }
                return anyNonNull ? sum : null;
            }

            static decimal? ExtremaOf(List<Dictionary<string, object?>> rows, string key, bool max)
            {
                decimal? extreme = null;
                foreach (var row in rows)
                {
                    if (!row.TryGetValue(key, out var raw) || raw == null) { continue; }
                    if (!TryAsDecimal(raw, out var d)) { continue; }
                    if (extreme is null
                        || (max && d > extreme.Value)
                        || (!max && d < extreme.Value))
                    {
                        extreme = d;
                    }
                }
                return extreme;
            }

            static bool TryAsDecimal(object v, out decimal d)
            {
                switch (v)
                {
                    case decimal dec: d = dec; return true;
                    case int i: d = i; return true;
                    case long l: d = l; return true;
                    case short s: d = s; return true;
                    case double dbl when !double.IsNaN(dbl) && !double.IsInfinity(dbl): d = (decimal)dbl; return true;
                    case float f when !float.IsNaN(f) && !float.IsInfinity(f): d = (decimal)f; return true;
                    default: d = 0; return false;
                }
            }
        }

        /// <summary>
        /// Apply <see cref="AnalysisQueryRequest.HavingFilters"/> to the
        /// materialised rows. Runs in-memory after the strategy produces
        /// rows but before <see cref="ApplySortAndTopN"/>, so the
        /// pipeline (GroupBy → HAVING → ORDER BY → LIMIT) matches SQL
        /// standard semantics.
        /// </summary>
        internal static List<Dictionary<string, object?>> ApplyHavingFilters(
            List<Dictionary<string, object?>> rows,
            AnalysisQueryRequest req)
        {
            if (req.HavingFilters == null || req.HavingFilters.Count == 0)
            {
                return rows;
            }

            // Pre-parse each filter's value to decimal once. A non-decimal
            // value is treated as "filter never matches" — conservative
            // rejection so a typo in the request body can't widen the
            // result set unexpectedly.
            var parsed = new List<(HavingFilter F, decimal V, bool Valid)>(req.HavingFilters.Count);
            foreach (var h in req.HavingFilters)
            {
                var ok = decimal.TryParse(h.Value, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var v);
                parsed.Add((h, v, ok));
            }

            var result = new List<Dictionary<string, object?>>(rows.Count);
            foreach (var row in rows)
            {
                bool keep = true;
                foreach (var (f, v, valid) in parsed)
                {
                    if (!valid)
                    {
                        keep = false;
                        break;
                    }
                    if (!row.TryGetValue(f.Field, out var raw) || raw == null)
                    {
                        // Comparing against null aggregate — only NotEq
                        // is meaningful (and it's true since v is decimal,
                        // raw is null). For all other operators reject.
                        if (f.Operator != FilterOperator.NotEq) { keep = false; }
                        break;
                    }
                    if (!TryAsDecimal(raw, out var rowDec))
                    {
                        keep = false;
                        break;
                    }
                    var cmp = rowDec.CompareTo(v);
                    bool match = f.Operator switch
                    {
                        FilterOperator.Eq    => cmp == 0,
                        FilterOperator.NotEq => cmp != 0,
                        FilterOperator.Gt    => cmp >  0,
                        FilterOperator.Gte   => cmp >= 0,
                        FilterOperator.Lt    => cmp <  0,
                        FilterOperator.Lte   => cmp <= 0,
                        _                    => false,
                    };
                    if (!match) { keep = false; break; }
                }
                if (keep) { result.Add(row); }
            }
            return result;

            static bool TryAsDecimal(object v, out decimal d)
            {
                switch (v)
                {
                    case decimal dec: d = dec; return true;
                    case int i: d = i; return true;
                    case long l: d = l; return true;
                    case short s: d = s; return true;
                    case double dbl when !double.IsNaN(dbl) && !double.IsInfinity(dbl): d = (decimal)dbl; return true;
                    case float f when !float.IsNaN(f) && !float.IsInfinity(f): d = (decimal)f; return true;
                    default: d = 0; return false;
                }
            }
        }

        /// <summary>
        /// Validate <see cref="AnalysisQueryRequest.Sort"/> and
        /// <see cref="AnalysisQueryRequest.TopN"/>. Sort fields must reference a
        /// requested dimension or a measure-result column (<c>{Field}_{Func}</c>);
        /// rejecting unknown sort fields prevents leaking whitelisted-but-not-selected
        /// columns and matches the explicit-allow-list posture of the rest of the
        /// engine. TopN is range-checked to keep the Take(N) bounded.
        /// </summary>
        internal static void ValidateSortAndTopN(AnalysisQueryRequest req)
        {
            // Upper bound tracks AnalysisLimits.MaxResultRows so that
            // raising the cap also raises the legal TopN range — they
            // describe the same dimension of "how many groups can the
            // response carry?"
            var maxTopN = AnalysisLimits.MaxResultRows;
            if (req.TopN is int topN && (topN <= 0 || topN > maxTopN))
            {
                throw new AnalysisException(
                    $"TopN must be between 1 and {maxTopN} (got {topN}).");
            }

            if (req.Sort == null || req.Sort.Count == 0) { return; }

            var allowedSortFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in req.Dimensions) { allowedSortFields.Add(d); }
            foreach (var m in req.Measures)
            {
                var key = $"{m.Field}_{m.Func}";
                allowedSortFields.Add(key);
                // When period-over-period comparison is on, the engine
                // augments rows with three derived columns per measure;
                // surface them as legal sort targets so users can do
                // "top 5 regions by ChangePct DESC" out of the box.
                if (req.CompareWith != null)
                {
                    allowedSortFields.Add($"{key}_Compare");
                    allowedSortFields.Add($"{key}_Delta");
                    allowedSortFields.Add($"{key}_ChangePct");
                }
            }

            foreach (var s in req.Sort)
            {
                if (string.IsNullOrWhiteSpace(s.Field))
                {
                    throw new AnalysisException("Sort.Field must not be empty.");
                }
                if (!allowedSortFields.Contains(s.Field))
                {
                    throw new AnalysisException(
                        $"Sort field '{s.Field}' is not in the requested Dimensions or Measures. " +
                        $"For measures, use '{{Field}}_{{Func}}' (e.g. 'Amount_Sum').");
                }
            }
        }

        /// <summary>
        /// Apply Sort + TopN to the materialised rows in-place semantics
        /// (returns a new list when the input requires mutation). Run after
        /// the strategy has produced rows and after the result-row hard cap
        /// — see <see cref="ApplySortAndTopN"/> call sites in
        /// <see cref="Execute"/> / <see cref="ExecuteAsync"/>.
        /// </summary>
        /// <remarks>
        /// Comparison handles mixed-type measure values (decimal / int /
        /// double / nullable) by using <see cref="Comparer{T}.Default"/>
        /// over <see cref="IComparable"/>; null values sort first on ASC
        /// (last on DESC) — the SQL convention. Strings use ordinal
        /// comparison for determinism across cultures.
        /// </remarks>
        internal static List<Dictionary<string, object?>> ApplySortAndTopN(
            List<Dictionary<string, object?>> rows,
            AnalysisQueryRequest req)
        {
            if (rows.Count <= 1 && req.TopN is null) { return rows; }

            IEnumerable<Dictionary<string, object?>> seq = rows;

            if (req.Sort != null && req.Sort.Count > 0)
            {
                IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;
                foreach (var s in req.Sort)
                {
                    if (ordered == null)
                    {
                        ordered = s.Descending
                            ? rows.OrderByDescending(r => Pluck(r, s.Field), SortValueComparer.Instance)
                            : rows.OrderBy(r => Pluck(r, s.Field), SortValueComparer.Instance);
                    }
                    else
                    {
                        ordered = s.Descending
                            ? ordered.ThenByDescending(r => Pluck(r, s.Field), SortValueComparer.Instance)
                            : ordered.ThenBy(r => Pluck(r, s.Field), SortValueComparer.Instance);
                    }
                }
                seq = ordered!;
            }

            if (req.TopN is int n)
            {
                seq = seq.Take(n);
            }

            // Materialise once. Avoid IEnumerable<T> bleed-through to callers
            // that re-enumerate the list (Excel / CSV exporters do).
            return seq is List<Dictionary<string, object?>> list ? list : seq.ToList();

            static object? Pluck(Dictionary<string, object?> row, string key)
                => row.TryGetValue(key, out var v) ? v : null;
        }

        /// <summary>
        /// Tolerant comparer for sort values that may be a mix of
        /// <see cref="decimal"/>, <see cref="int"/>, <see cref="long"/>,
        /// <see cref="double"/>, <see cref="DateTime"/>, <see cref="string"/>,
        /// or <c>null</c>. Falls back to ordinal string comparison when
        /// types disagree so the sort is deterministic instead of throwing.
        /// </summary>
        private sealed class SortValueComparer : IComparer<object?>
        {
            public static readonly SortValueComparer Instance = new();

            public int Compare(object? x, object? y)
            {
                if (ReferenceEquals(x, y)) { return 0; }
                if (x is null) { return -1; }
                if (y is null) { return 1; }

                // Unify numeric types via decimal where possible.
                if (TryAsDecimal(x, out var xd) && TryAsDecimal(y, out var yd))
                {
                    return xd.CompareTo(yd);
                }

                if (x is DateTime xt && y is DateTime yt)
                {
                    return xt.CompareTo(yt);
                }

                if (x is IComparable xc && x.GetType() == y.GetType())
                {
                    return xc.CompareTo(y);
                }

                // Mixed types — fall back to ordinal string comparison so
                // the sort still produces a stable order rather than
                // raising at runtime.
                return string.CompareOrdinal(x.ToString(), y.ToString());
            }

            private static bool TryAsDecimal(object v, out decimal d)
            {
                switch (v)
                {
                    case decimal dec: d = dec; return true;
                    case int i: d = i; return true;
                    case long l: d = l; return true;
                    case short s: d = s; return true;
                    case double dbl when !double.IsNaN(dbl) && !double.IsInfinity(dbl): d = (decimal)dbl; return true;
                    case float f when !float.IsNaN(f) && !float.IsInfinity(f): d = (decimal)f; return true;
                    default: d = 0; return false;
                }
            }
        }

        /// <summary>
        /// 將篩選條件中的相對日期 token（@today、@thisWeek 等）展開為 Gte/Lte 對。
        /// 不含 token 的條件直接原樣返回，不建立新物件。
        /// </summary>
        internal static List<FilterCondition> ResolveRelativeDates(List<FilterCondition> filters)
        {
            bool hasTokens = false;
            foreach (var f in filters)
                // M1 fix: f.Value may be null (STJ overrides the = string.Empty default with null)
                if ((f.Value ?? string.Empty).StartsWith("@", StringComparison.Ordinal)) { hasTokens = true; break; }
            if (!hasTokens) return filters;

            List<FilterCondition> result = [];
            var today = DateTime.Today;
            // ISO week offset: (dow + 6) % 7 maps Sun=0..Sat=6 → Mon=0..Sun=6.
            var mondayOffset = ((int)today.DayOfWeek + 6) % 7;

            foreach (var f in filters)
            {
                // M1 fix: guard against null Value — a null Value is not a relative-date token
                if (!(f.Value ?? string.Empty).StartsWith("@", StringComparison.Ordinal))
                {
                    result.Add(f);
                    continue;
                }

                DateTime start, end;
                switch ((f.Value ?? string.Empty).ToLowerInvariant())
                {
                    case "@today":
                        start = today; end = today;
                        break;
                    case "@yesterday":
                        start = today.AddDays(-1); end = today.AddDays(-1);
                        break;
                    case "@thisweek":
                        start = today.AddDays(-mondayOffset);
                        end = start.AddDays(6);
                        break;
                    case "@lastweek":
                        start = today.AddDays(-mondayOffset - 7);
                        end = start.AddDays(6);
                        break;
                    case "@nextweek":
                        start = today.AddDays(-mondayOffset + 7);
                        end = start.AddDays(6);
                        break;
                    case "@thismonth":
                        start = new DateTime(today.Year, today.Month, 1);
                        end = start.AddMonths(1).AddDays(-1);
                        break;
                    case "@lastmonth":
                        start = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                        end = new DateTime(today.Year, today.Month, 1).AddDays(-1);
                        break;
                    case "@nextmonth":
                        start = new DateTime(today.Year, today.Month, 1).AddMonths(1);
                        end = start.AddMonths(1).AddDays(-1);
                        break;
                    case "@last7days":
                        start = today.AddDays(-7);
                        end = today;
                        break;
                    case "@last30days":
                        start = today.AddDays(-30);
                        end = today;
                        break;
                    case "@last90days":
                        start = today.AddDays(-90);
                        end = today;
                        break;
                    case "@last365days":
                        start = today.AddDays(-365);
                        end = today;
                        break;
                    case "@thisquarter":
                        // Quarter-start through today (not quarter-end) —
                        // matches @ytd semantics: "this period to date".
                        start = new DateTime(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1);
                        end = today;
                        break;
                    case "@lastquarter":
                    {
                        // Full previous calendar quarter, regardless of today.
                        var thisQStart = new DateTime(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1);
                        start = thisQStart.AddMonths(-3);
                        end = thisQStart.AddDays(-1);
                        break;
                    }
                    case "@ytd":
                        // Year-to-date: Jan 1 of this year through today.
                        start = new DateTime(today.Year, 1, 1);
                        end = today;
                        break;
                    case "@thisyear":
                        // Full current calendar year (Jan 1 through Dec 31),
                        // distinct from @ytd which clamps to today.
                        start = new DateTime(today.Year, 1, 1);
                        end = new DateTime(today.Year, 12, 31);
                        break;
                    case "@lastyear":
                        // Full previous calendar year.
                        start = new DateTime(today.Year - 1, 1, 1);
                        end = new DateTime(today.Year - 1, 12, 31);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown relative date token '{f.Value}'.");
                }

                result.Add(new FilterCondition { Field = f.Field, Operator = FilterOperator.Gte, Value = start.ToString("yyyy-MM-dd") });
                result.Add(new FilterCondition { Field = f.Field, Operator = FilterOperator.Lte, Value = end.ToString("yyyy-MM-dd 23:59:59") });
            }
            return result;
        }

        /// <summary>
        /// Public for the <see cref="BuildDrillThroughQuery"/> entry point —
        /// drill-through reuses the same expression-tree filter pipeline
        /// (whitelist validation, type conversion, relative-date tokens)
        /// instead of duplicating it.
        /// </summary>
        public static IQueryable<TModel> ApplyFilters<TModel>(
            IQueryable<TModel> query,
            List<FilterCondition>? filters,
            Dictionary<string, AnalysisFieldMeta> whitelist)
        {
            if (filters == null || filters.Count == 0) return query;
            filters = ResolveRelativeDates(filters);

            var param = Expression.Parameter(typeof(TModel), "x");
            Expression? body = null;

            foreach (var f in filters)
            {
                if (!whitelist.TryGetValue(f.Field, out var meta))
                    throw new AnalysisFieldNotFoundException(f.Field, "Filter");

                var prop = Expression.Property(param, f.Field);
                var targetType = prop.Type;
                var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

                Expression? filterExpr = null;
                try
                {
                    if (f.Operator == FilterOperator.In || f.Operator == FilterOperator.NotIn)
                    {
                        // L1: honour the strongly-typed list form (f.Values) first.
                        // f.Values is populated by callers that already hold a List<string>
                        // (e.g. AnalysisQueryRequest.FilterCondition.Values).  Fall back to
                        // comma-splitting f.Value for callers that use the legacy string form.
                        System.Collections.IEnumerable? values = null;
                        if (f.Values is { Count: > 0 } valuesList)
                        {
                            values = valuesList;
                        }
                        else if (f.Value is string s && s.Length > 0)
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

                        var containsMethod = _enumerableContainsClosedCache.GetOrAdd(
                            underlyingType,
                            t => _enumerableContainsOpenMethod.MakeGenericMethod(t));

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
                        Expression containsExpr = Expression.Call(prop, _stringContainsMethod, val);
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

                        // Nullable properties need a NOT-NULL guard: SQL silently excludes NULL
                        // rows from comparisons, but LINQ-to-Objects would throw on .Value access.
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
        /// Escape a single pivot row-key segment so that a literal '|' or '\' in a
        /// dimension value cannot collide with the '|' join delimiter used in
        /// <see cref="ExecutePivot{TModel}"/> / <see cref="ExecutePivotAsync{TModel}"/>.
        /// Encoding: '\' → '\\', '|' → '\|'.  Decoding is not needed because the
        /// pivot map uses the full escaped key only as a dictionary key (no split).
        /// </summary>
        private static string EscapeKeySeg(string? seg)
        {
            if (seg == null) return string.Empty;
            return seg.Replace("\\", "\\\\").Replace("|", "\\|");
        }

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

        private static readonly FrozenDictionary<AggregateFunc, string> _funcDisplayNames = new Dictionary<AggregateFunc, string>
        {
            { AggregateFunc.Sum,           "合計" },
            { AggregateFunc.Count,         "計數" },
            { AggregateFunc.Avg,           "平均" },
            { AggregateFunc.Max,           "最大" },
            { AggregateFunc.Min,           "最小" },
            { AggregateFunc.DistinctCount, "不重複計數" },
        }.ToFrozenDictionary();

        /// <summary>
        /// 建立欄位 key → 使用者友善顯示名稱的對照表。
        /// </summary>
        private static Dictionary<string, MeasureFormat> BuildColumnFormats(
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> wl)
        {
            var map = new Dictionary<string, MeasureFormat>();
            foreach (var m in req.Measures)
            {
                var key = $"{m.Field}_{m.Func}";
                map[key] = wl.TryGetValue(m.Field, out var meta) ? meta.Format : MeasureFormat.Auto;
            }
            return map;
        }

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
            var compareLabel = req.CompareWith?.Label ?? "Compare";
            foreach (var m in req.Measures)
            {
                var key = $"{m.Field}_{m.Func}";
                var fieldDisplay = wl.TryGetValue(m.Field, out var meta) && !string.IsNullOrEmpty(meta.DisplayName)
                    ? meta.DisplayName
                    : m.Field;
                var funcDisplay = _funcDisplayNames.TryGetValue(m.Func, out var fd) ? fd : m.Func.ToString();
                var baseLabel = $"{fieldDisplay} {funcDisplay}";
                map[key] = baseLabel;

                // Period-over-period — surface the comparison columns
                // with human-readable headers so the front-end picker
                // can render "金額 合計 (上月)" / "差值" / "變化%" out
                // of the box without per-app i18n plumbing.
                if (req.CompareWith != null)
                {
                    map[$"{key}_Compare"]    = $"{baseLabel} ({compareLabel})";
                    map[$"{key}_Delta"]      = $"{baseLabel} 差值";
                    map[$"{key}_ChangePct"]  = $"{baseLabel} 變化%";
                }
            }

            return map;
        }

        /// <summary>
        /// 計算查詢快取 key。
        /// 當 <paramref name="identityKey"/> 為 null 或空字串時回傳 null，
        /// 表示「此請求不應寫入或讀取共用快取」（M29 修復：防止匿名請求共用快取）。
        /// </summary>
        // internal (not public) so Core.Test can call it directly via InternalsVisibleTo;
        // kept out of the public surface to preserve encapsulation.
        internal static string? ComputeHash(AnalysisQueryRequest req, string? identityKey = null)
        {
            // M29 fix: identity-less requests must never share a cache entry.
            // Returning null signals callers to skip both get and set.
            if (string.IsNullOrEmpty(identityKey))
                return null;

            var raw = System.Text.Json.JsonSerializer.Serialize(req, _hashSerializerOptions);
            raw += "|" + identityKey;

            // Use ArrayPool to avoid a heap allocation for the UTF-8 byte array.
            // GetByteCount + GetBytes produces the EXACT same byte sequence as
            // Encoding.UTF8.GetBytes(raw) — byte-identical hash guaranteed.
            int byteCount = System.Text.Encoding.UTF8.GetByteCount(raw);
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                System.Text.Encoding.UTF8.GetBytes(raw, 0, raw.Length, rented, 0);
                Span<byte> hash = stackalloc byte[32]; // SHA256 = 32 bytes
                System.Security.Cryptography.SHA256.HashData(rented.AsSpan(0, byteCount), hash);
                return Convert.ToHexString(hash)[..16];
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
