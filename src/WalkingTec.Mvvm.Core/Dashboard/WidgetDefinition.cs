using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.Dashboard.Alerting;

namespace WalkingTec.Mvvm.Core.Dashboard;

public class WidgetDefinition
{
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public WidgetSourceDefinition Source { get; set; } = new();
    public Dictionary<string, object?> Config { get; set; } = new();

    /// <summary>
    /// Optional KPI threshold alert rules for this widget.
    /// When non-empty and <see cref="DashboardAlertOptions.EvaluationIntervalSeconds"/> &gt; 0,
    /// <see cref="DashboardAlertHostedService"/> evaluates these rules on each tick and sends
    /// an alert via the registered sink on any breach (alert-on-transition de-duplicated).
    /// </summary>
    public List<WidgetThreshold>? Thresholds { get; set; }

    /// <summary>
    /// Optional cross-widget drill-down links.
    /// When a user clicks on a data point in this widget, each matching link can
    /// set a filter value on another widget (or on the global FilterBar).
    /// The JS handler emits a <c>widgetClicked</c> event carrying the clicked field
    /// and value; <c>DashboardManager._registerDrillDownLinks</c> applies the mapping.
    /// </summary>
    public List<DrillDownLink>? DrillDown { get; set; }
}

/// <summary>
/// Maps a click event on a source widget's data point to a filter applied to a target.
/// <para>
/// <b>SourceField</b>: The data field whose value is emitted when the user clicks.
/// For chart widgets this is typically the x-axis / category field; for table widgets
/// it is the column name.
/// </para>
/// <para>
/// <b>TargetFilterId</b>: The <see cref="DashboardFilter.Id"/> to update, or the
/// special value <c>"_global"</c> to update all widgets via the global FilterBar.
/// </para>
/// <para>
/// <b>TargetWidgetId</b>: When non-null, re-fetch only this widget after applying the
/// filter. When null, a global FilterBar update triggers re-fetch of all widgets.
/// </para>
/// </summary>
public class DrillDownLink
{
    /// <summary>Field name in the clicked data point whose value is forwarded.</summary>
    public string SourceField { get; set; } = "";

    /// <summary>
    /// FilterBar filter ID to update.
    /// Use <c>"_global"</c> to set the value on every FilterBar field with a matching name.
    /// </summary>
    public string TargetFilterId { get; set; } = "";

    /// <summary>
    /// Widget ID to refresh after the filter is applied.
    /// When null/empty, the standard FilterBar onChange fires and refreshes all widgets.
    /// </summary>
    public string? TargetWidgetId { get; set; }
}

public class WidgetSourceDefinition
{
    public string Kind { get; set; } = "custom";
    public string? Name { get; set; }
    public string? ListVmType { get; set; }
    public List<DimensionConfig>? Dimensions { get; set; }
    public List<MeasureConfig>? Measures { get; set; }
    public List<FilterConfig>? Filters { get; set; }

    /// <summary>
    /// REST widget options for this widget. When set, these options are authoritative
    /// and completely override any <c>options</c> parameter supplied on a
    /// <c>GetWidgetData</c> request (the query/body parameter used when
    /// <em>fetching</em> data for an already-persisted widget) — see
    /// <c>JsonFileDashboardService.GetWidgetDataAsync</c> /
    /// <c>EfCoreDashboardService.GetWidgetDataAsync</c>.
    /// </summary>
    /// <remarks>
    /// <b>Corrected (issue #948): this is NOT a caller-proof channel for
    /// <see cref="RestWidgetDataSourceOptions.AllowPrivateNetwork"/> /
    /// <see cref="RestWidgetDataSourceOptions.AllowHttp"/>.</b> An earlier version of
    /// this doc comment claimed those two fields "can only be enabled here" and are
    /// therefore safe from caller control — that was true only for the
    /// <c>GetWidgetData</c> request-parameter channel this summary describes above; it
    /// was false for this property itself, because <see cref="WidgetDefinition"/> (the
    /// containing type) is exactly what <c>_DashboardController.Create</c>,
    /// <c>_DashboardController.Update</c>, and <c>_DashboardDesignerController.Preview</c>
    /// all bind straight from the HTTP request body. Setting <c>RestOptions</c> here IS
    /// caller data on every write path this framework ships. The two security-sensitive
    /// fields are now validated (rejected) at write time by
    /// <c>JsonFileDashboardService</c>/<c>EfCoreDashboardService</c>'s
    /// <c>ValidateWidgetConfigs</c>, and — belt and suspenders — <see cref="RestWidgetDataSource"/>
    /// no longer honours them by themselves at fetch time either; see
    /// <see cref="IDashboardEgressPolicy"/> for the real, host-owned control.
    /// </remarks>
    public RestWidgetDataSourceOptions? RestOptions { get; set; }
}

public class DimensionConfig
{
    public string Field { get; set; } = "";
    public string? Hierarchy { get; set; }
}

public class MeasureConfig
{
    public string Field { get; set; } = "";
    public string Func { get; set; } = "Sum";
}

public class FilterConfig
{
    public string Field { get; set; } = "";

    /// <summary>
    /// Filter operator. Must be one of the allowlisted values (case-insensitive):
    /// eq, ne, gt, ge, lt, le, contains, notcontains, in, notin.
    /// Default: "eq".
    /// </summary>
    public string Op { get; set; } = "eq";

    public string Value { get; set; } = "";

    /// <summary>
    /// Allowlisted operator strings (lower-case canonical form).
    /// Maps to <see cref="WalkingTec.Mvvm.Core.Analysis.FilterOperator"/> members.
    /// </summary>
    public static readonly HashSet<string> AllowedOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "gt", "ge", "lt", "le", "contains", "notcontains", "in", "notin"
    };
}