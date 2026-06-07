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