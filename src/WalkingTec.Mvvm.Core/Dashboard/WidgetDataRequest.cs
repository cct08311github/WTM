using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;
public class WidgetDataRequest
{
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string? TenantId { get; set; }

    /// <summary>
    /// Issue #948-F8: the dashboard this widget fetch belongs to, when known. Populated by
    /// <c>JsonFileDashboardService</c>/<c>EfCoreDashboardService.GetWidgetDataAsync</c> (which
    /// always know both ids from their own method parameters) and threaded through to
    /// <see cref="RestWidgetDataSource"/> so it can populate
    /// <see cref="DashboardEgressDestination.DashboardId"/>. <c>null</c> for any caller that
    /// constructs a <see cref="WidgetDataRequest"/> directly without setting it — purely
    /// additive, existing callers are unaffected.
    /// </summary>
    public string? DashboardId { get; set; }

    /// <summary>Issue #948-F8: the widget this fetch belongs to, when known. See <see cref="DashboardId"/>.</summary>
    public string? WidgetId { get; set; }
}