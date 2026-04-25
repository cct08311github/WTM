#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// Drill-through helper: given an analysis result row's dimension
    /// values, rebuild the underlying <see cref="IQueryable{TModel}"/>
    /// of source rows that aggregated into that group. Powers the
    /// "click a group → see the raw rows behind it" UX in dashboards
    /// without callers having to manually reconstruct filter expressions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Behaviour:
    /// </para>
    /// <list type="bullet">
    /// <item>The original request's <c>Filters</c> are preserved (so the
    /// drill stays scoped to whatever filtered universe the dashboard
    /// was looking at).</item>
    /// <item>Each requested dimension turns into an <c>Eq</c> filter on
    /// the corresponding value.</item>
    /// <item>For dimensions with a <see cref="DateHierarchy"/> applied
    /// (Year / Quarter / Month / Day), the human-readable label
    /// ("2026 Q1") is reversed via
    /// <see cref="DateTruncator.TryParseLabel"/> back into a half-open
    /// <c>[start, endExclusive)</c> range expressed as
    /// <c>Gte</c> + <c>Lt</c> filters — single-point Eq comparison
    /// against a millisecond-precision DateTime would always fail.</item>
    /// <item>Unparseable date labels short-circuit to an empty queryable
    /// (rather than throwing) so the dashboard can render "no rows"
    /// gracefully — same posture as a dimension whose value happens to
    /// not exist in the underlying data.</item>
    /// </list>
    /// <para>
    /// Drill-through never enforces TopN / Sort / GrandTotal /
    /// CompareWith / Insights — those describe the *aggregated* view.
    /// Callers typically follow the returned queryable with their own
    /// <c>OrderBy</c> + <c>Take</c> for "show me the first 100 raw
    /// orders behind this region".
    /// </para>
    /// </remarks>
    public static class AnalysisDrillThrough
    {
        /// <summary>
        /// Build the drill-through queryable. Returns the original
        /// <paramref name="baseQuery"/> with the request's existing
        /// filters AND one equality filter per dimension applied.
        /// </summary>
        /// <typeparam name="TModel">Source-row entity type.</typeparam>
        /// <param name="baseQuery">Base queryable from the ListVM
        /// (typically <c>vm.GetSearchQuery()</c>).</param>
        /// <param name="originalReq">The <see cref="AnalysisQueryRequest"/>
        /// that produced the aggregated row the user clicked. Filters
        /// and DimensionHierarchies are honoured.</param>
        /// <param name="groupValues">Map from dimension name → cell value
        /// taken from the result row. Values are read via
        /// <c>ToString()</c> and re-typed by the engine's filter pipeline
        /// (same path as <c>FilterCondition.Value</c>).</param>
        /// <param name="whitelist">The same field whitelist that
        /// <see cref="AnalysisQueryEngine.Execute"/> uses; required so
        /// dimension references go through the same validation.</param>
        public static IQueryable<TModel> BuildQuery<TModel>(
            IQueryable<TModel> baseQuery,
            AnalysisQueryRequest originalReq,
            IReadOnlyDictionary<string, object?> groupValues,
            IEnumerable<AnalysisFieldMeta> whitelist)
        {
            ArgumentNullException.ThrowIfNull(baseQuery);
            ArgumentNullException.ThrowIfNull(originalReq);
            ArgumentNullException.ThrowIfNull(groupValues);
            ArgumentNullException.ThrowIfNull(whitelist);

            var wl = whitelist.ToDictionary(f => f.FieldName);

            // Start with the original Filters (preserve dashboard scope)
            // and append per-dimension equality / range filters.
            var combinedFilters = new List<FilterCondition>(originalReq.Filters ?? new List<FilterCondition>());

            foreach (var dim in originalReq.Dimensions)
            {
                if (!wl.TryGetValue(dim, out var meta) || meta.Kind != AnalysisFieldKind.Dimension)
                {
                    throw new AnalysisFieldNotFoundException(dim, "Dimension");
                }
                if (!groupValues.TryGetValue(dim, out var raw))
                {
                    // No value provided for this dim → caller asking to
                    // drill to "all rows under any value of this dim".
                    // Skip filter for this dim.
                    continue;
                }

                // Date-hierarchy reversal: "2026 Q1" / "2026-03" /
                // "2026-03-15" must become a [start, endExclusive) range.
                if (originalReq.DimensionHierarchies != null &&
                    originalReq.DimensionHierarchies.TryGetValue(dim, out var hierarchy) &&
                    hierarchy != DateHierarchy.None)
                {
                    var label = raw?.ToString();
                    if (!DateTruncator.TryParseLabel(label, hierarchy, out var start, out var endExclusive))
                    {
                        // Can't parse the label back — return an empty
                        // queryable instead of unfiltered (which would
                        // be wrong) or throwing (UI gets nothing useful).
                        return baseQuery.Take(0);
                    }
                    combinedFilters.Add(new FilterCondition
                    {
                        Field = dim,
                        Operator = FilterOperator.Gte,
                        Value = start.ToString("yyyy-MM-dd HH:mm:ss"),
                    });
                    combinedFilters.Add(new FilterCondition
                    {
                        Field = dim,
                        Operator = FilterOperator.Lt,
                        Value = endExclusive.ToString("yyyy-MM-dd HH:mm:ss"),
                    });
                    continue;
                }

                // Plain dimension equality. Null values become "is null"
                // semantics — express via Eq with an empty value, since
                // FilterCondition.Value is non-nullable string. The
                // existing ApplyFilters pipeline handles this consistently.
                combinedFilters.Add(new FilterCondition
                {
                    Field = dim,
                    Operator = FilterOperator.Eq,
                    Value = raw?.ToString() ?? string.Empty,
                });
            }

            return AnalysisQueryEngine.ApplyFilters(baseQuery, combinedFilters, wl);
        }
    }
}
