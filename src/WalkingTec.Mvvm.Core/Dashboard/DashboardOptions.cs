using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;
public class DashboardOptions
{
    public string DashboardDirectory { get; set; } = "App_Data/dashboards";
    public bool EnableEditing { get; set; } = true;
    public int DefaultRefreshInterval { get; set; } = 60;
    public bool AllowIframeSameOrigin { get; set; } = false;

    /// <summary>
    /// Role names that are treated as administrators for Dashboard access.
    /// Administrators can view and edit all dashboards regardless of ownership or sharing settings.
    /// Defaults to ["Admin"] for backward compatibility.
    /// Configure this if your deployment uses a different admin role name (e.g. "SystemAdmin", "超級管理員").
    /// </summary>
    public string[] AdminRoles { get; set; } = ["Admin"];

    /// <summary>
    /// Allowlist of widget UI type strings (i.e. <see cref="WidgetDefinition.Type"/>).
    /// When non-empty, Create/Update requests whose widget <c>Type</c> is not in this set
    /// are rejected with HTTP 400.
    /// When null or empty, any non-empty <c>Type</c> string is accepted (default — backward compatible).
    /// Example: <c>["chart", "kpi", "table", "stat", "text", "iframe", "gauge"]</c>
    /// </summary>
    public string[]? AllowedWidgetTypes { get; set; }

    /// <summary>
    /// Short-TTL in-memory result cache for <c>AnalysisWidgetDataSource</c>.
    /// Each unique combination of (widgetId, tenant, user, filter values) is cached for this many seconds.
    /// Set to 0 to disable caching (opt-out).
    /// Default: 10 seconds.
    /// </summary>
    public int AnalysisWidgetCacheTtlSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of seconds each widget data fetch is allowed to run before it is cancelled.
    /// When a widget times out, a per-widget error result is returned instead of failing the whole dashboard.
    /// Set to 0 to disable the per-widget timeout (not recommended in production).
    /// Default: 30 seconds.
    /// </summary>
    public int WidgetDataTimeoutSeconds { get; set; } = 30;
}