#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Dashboard;

/// <summary>
/// EF entity — one row per dashboard definition.
/// Widgets are stored in a separate <see cref="WidgetRecord"/> table so that CRUD
/// on individual widgets does not require re-serialising the entire dashboard.
/// </summary>
public class DashboardRecord
{
    public string Id { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    public string Title { get; set; } = "";
    public string Owner { get; set; } = "";
    public string? TenantId { get; set; }

    // Sharing
    public string SharingMode { get; set; } = "private";
    /// <summary>JSON array of role strings. Null when SharingMode != "roles".</summary>
    public string? SharingRolesJson { get; set; }

    public int RefreshInterval { get; set; } = 60;

    /// <summary>JSON array of LayoutItem.</summary>
    public string LayoutJson { get; set; } = "[]";

    /// <summary>JSON array of DashboardFilter. Null when no filters defined.</summary>
    public string? FiltersJson { get; set; }

    /// <summary>JSON array of WidgetLink. Null when no links defined.</summary>
    public string? LinksJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// EF entity — one row per widget on a dashboard.
/// </summary>
public class WidgetRecord
{
    public string DashboardId { get; set; } = "";
    public string WidgetId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>JSON-serialised <see cref="WidgetSourceDefinition"/>.</summary>
    public string SourceJson { get; set; } = "{}";

    /// <summary>JSON-serialised <c>Dictionary&lt;string,object?&gt;</c> config bag.</summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>JSON-serialised <c>List&lt;DrillDownLink&gt;</c>. Null when no drill-down links.</summary>
    public string? DrillDownJson { get; set; }
}
