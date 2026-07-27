#nullable enable
namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// Process-wide tunables for the analysis pipeline. Lifted from
    /// previously-hardcoded constants spread across
    /// <see cref="InProcessGroupByStrategy"/>,
    /// <see cref="ServerSideGroupByStrategy"/>, and
    /// <see cref="AnalysisQueryEngine"/> so apps with bigger reporting
    /// surfaces (monthly SKU breakdowns, multi-tenant aggregations, …)
    /// can raise the caps without forking the framework.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Set once at startup.</b> Mutating these properties at
    /// runtime is supported but not intended — the values are read on
    /// every query, so changing mid-flight is racy with concurrent
    /// requests. The standard wiring is to set them in <c>Program.cs</c>
    /// before the host starts handling traffic; tests can override per
    /// case via <c>try / finally</c>.
    /// </para>
    /// <para>
    /// Conservative defaults match the values the framework shipped
    /// with through 10.4.x (10,000 result rows / 50,000 raw rows), so
    /// callers that don't touch this class get the same behaviour.
    /// </para>
    /// </remarks>
    public static class AnalysisLimits
    {
        /// <summary>
        /// Maximum number of GroupBy result rows returned to the
        /// caller. When an aggregation produces more than this many
        /// distinct groups, the response is truncated and
        /// <see cref="AnalysisQueryResponse.Truncated"/> is set to
        /// <c>true</c>. Also serves as the upper bound for
        /// <see cref="AnalysisQueryRequest.TopN"/> validation. Default
        /// 10,000 — large enough for a region/product/month pivot, small
        /// enough to keep a single response under the typical browser
        /// table-rendering budget.
        /// </summary>
        public static int MaxResultRows { get; set; } = 10_000;

        /// <summary>
        /// Maximum number of raw rows the in-process GroupBy strategy
        /// will load into memory before running the LINQ-to-Objects
        /// aggregation. When the source query produces more, the
        /// response is flagged via
        /// <see cref="AnalysisQueryResponse.DataTruncated"/> and the
        /// aggregation runs over the truncated subset. Default 50,000 —
        /// guards against a 10M-row scan blowing up the host. Apps
        /// that have moved to the server-side strategy can raise this
        /// for the rare in-process fallback paths; apps still on
        /// in-process should leave it conservative.
        /// </summary>
        public static int MaxMaterializeRows { get; set; } = 50_000;

        /// <summary>
        /// Maximum number of clauses allowed in <see cref="AnalysisQueryRequest.Filters"/>,
        /// <see cref="AnalysisQueryRequest.HavingFilters"/>, <see cref="AnalysisQueryRequest.Sort"/>,
        /// and <see cref="AnalysisQueryRequest.CompareWith"/>'s <c>Filters</c>. Enforced by
        /// <see cref="AnalysisQueryEngine"/> itself (see its private <c>ValidateFields</c>) so
        /// every caller of <c>Execute</c>/<c>ExecuteAsync</c>/<c>ExecuteDynamic*</c> inherits the
        /// guard — this used to be a check only <c>_AnalysisController</c> performed, which let a
        /// second HTTP path (the Dashboard analysis widget data source) reach the engine with an
        /// unbounded clause list. Each clause feeds a left-deep <c>Expression.AndAlso</c> tree;
        /// deep enough and the CLR expression-tree evaluator throws
        /// <see cref="StackOverflowException"/>, which .NET cannot catch — one authenticated
        /// request would otherwise terminate the whole worker process (#795). Default 50 is
        /// generous for real dashboards.
        /// </summary>
        public static int MaxFilterClauses { get; set; } = 50;

        /// <summary>
        /// Maximum number of fields allowed in <see cref="AnalysisQueryRequest.Dimensions"/>
        /// and <see cref="AnalysisQueryRequest.Measures"/>. Enforced by
        /// <see cref="AnalysisQueryEngine"/> itself (see its private <c>ValidateClauseCounts</c>)
        /// so every caller inherits the guard, not just <c>_AnalysisController</c>'s own
        /// friendly pre-flight checks (which additionally cap at 3 for UX reasons on the
        /// controller's own endpoints — that cap is unrelated to this one and stays put).
        /// <c>AnalysisWidgetDataSource</c> — a second HTTP path into the same engine whose
        /// caller can override the stored widget's <c>dimensions</c>/<c>measures</c> via the
        /// request body — had neither the controller's UX cap nor any other bound on these two
        /// lists, so a large-enough list reached <c>GroupBy</c>/projection with an unbounded
        /// column count (#795). Default 32 is far above any real dashboard's dimension/measure
        /// count and far below the point where a wide GroupBy projection becomes a DoS vector.
        /// </summary>
        public static int MaxGroupByFields { get; set; } = 32;
    }
}
