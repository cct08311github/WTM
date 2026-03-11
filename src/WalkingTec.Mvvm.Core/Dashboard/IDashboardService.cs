using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;

public interface IDashboardService
{
    Task<DashboardDefinition?> GetAsync(string dashboardId);
    Task<IReadOnlyList<DashboardSummary>> ListAsync(string userId, string[] userRoles);
    Task<string> CreateAsync(DashboardDefinition dashboard);
    Task UpdateAsync(DashboardDefinition dashboard);
    Task DeleteAsync(string dashboardId);
    Task<WidgetDataResult> GetWidgetDataAsync(string dashboardId, string widgetId, Dictionary<string, string>? filters = null, CancellationToken ct = default);
    bool CanAccess(DashboardDefinition dashboard, string userId, string[] userRoles);
    bool CanEdit(DashboardDefinition dashboard, string userId, string[] userRoles);
}