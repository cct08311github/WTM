using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;

public class WidgetDefinition
{
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public WidgetSourceDefinition Source { get; set; } = new();
    public Dictionary<string, object?> Config { get; set; } = new();

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
    /// Server-side REST widget options. When set, these options are authoritative and
    /// completely override any <c>options</c> parameter supplied by the HTTP request.
    /// Security-sensitive fields (<see cref="RestWidgetDataSourceOptions.AllowPrivateNetwork"/>
    /// and <see cref="RestWidgetDataSourceOptions.AllowHttp"/>) can only be enabled here —
    /// they are always forced to their safe defaults when options originate from the request.
    /// </summary>
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