#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Analysis
{
    public partial class AnalysisQueryEngine
    {
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
    }
}
