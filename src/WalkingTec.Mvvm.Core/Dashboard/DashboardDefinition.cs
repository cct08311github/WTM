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
    
    // Responsive breakpoints: lg (desktop), md (tablet), sm (small tablet), xs (mobile)
    public int? LG { get; set; }  // Large desktop (>=1200px)
    public int? MD { get; set; }  // Tablet landscape (>=992px)
    public int? SM { get; set; }  // Tablet portrait (>=768px)
    public int? XS { get; set; }  // Mobile (<768px)
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