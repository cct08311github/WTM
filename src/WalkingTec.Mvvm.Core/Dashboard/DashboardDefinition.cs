using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;

[JsonNumberHandling(JsonNumberHandling.Strict)]
public class DashboardDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Owner { get; set; } = "";
    public string? TenantId { get; set; }
    public SharingDefinition Sharing { get; set; } = new();
    public int RefreshInterval { get; set; } = 60;
    public List<LayoutItem> Layout { get; set; } = new();
    public Dictionary<string, WidgetDefinition> Widgets { get; set; } = new();
    public List<DashboardFilter>? Filters { get; set; }
    public List<WidgetLink>? Links { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SharingDefinition
{
    public string Mode { get; set; } = "private";
    public List<string>? Roles { get; set; }
}

[JsonNumberHandling(JsonNumberHandling.Strict)]
public class LayoutItem
{
    public string Id { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 3;
    public int H { get; set; } = 1;

    /// <summary>
    /// Optional per-breakpoint overrides for X/Y/W/H.
    /// When null the base X/Y/W/H values are used for all breakpoints.
    /// xs (mobile) falls back to single-column stacking if not specified.
    /// </summary>
    public LayoutBreakpoints? Breakpoints { get; set; }
}

/// <summary>
/// Responsive breakpoint overrides for a single LayoutItem.
/// Each breakpoint is optional; missing ones inherit the item's base values.
/// Breakpoint widths (approximate): lg ≥ 1200px, md ≥ 992px, sm ≥ 768px, xs &lt; 768px.
/// </summary>
public class LayoutBreakpoints
{
    /// <summary>Large screens (≥ 1200 px). Null = use base LayoutItem values.</summary>
    public LayoutBreakpointItem? Lg { get; set; }

    /// <summary>Medium screens (≥ 992 px). Null = use base LayoutItem values.</summary>
    public LayoutBreakpointItem? Md { get; set; }

    /// <summary>Small screens (≥ 768 px). Null = use base LayoutItem values.</summary>
    public LayoutBreakpointItem? Sm { get; set; }

    /// <summary>
    /// Extra-small / mobile screens (&lt; 768 px).
    /// Null = auto single-column stacking (w=12, x=0).
    /// </summary>
    public LayoutBreakpointItem? Xs { get; set; }
}

[JsonNumberHandling(JsonNumberHandling.Strict)]
public class LayoutBreakpointItem
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 12;
    public int H { get; set; } = 1;
}

public class DashboardFilter
{
    public string Id { get; set; } = "";
    public string Field { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "select";
    public string? Default { get; set; }
    public List<string>? Options { get; set; }
}

public class WidgetLink
{
    public string SourceWidget { get; set; } = "";
    public string Event { get; set; } = "click";
    public string TargetWidget { get; set; } = "";
    public string Action { get; set; } = "filter";
    public string? Param { get; set; }
}

public class DashboardSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Owner { get; set; } = "";
    public string? TenantId { get; set; }
    public SharingDefinition Sharing { get; set; } = new();
    public DateTime UpdatedAt { get; set; }
}