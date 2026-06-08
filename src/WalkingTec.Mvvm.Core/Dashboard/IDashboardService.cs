using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;

public interface IDashboardService
{
    Task<DashboardDefinition?> GetAsync(string dashboardId, string? tenantId = null);
    Task<IReadOnlyList<DashboardSummary>> ListAsync(string userId, string[] userRoles, string? tenantId = null);
    Task<string> CreateAsync(DashboardDefinition dashboard);
    Task UpdateAsync(DashboardDefinition dashboard);
    Task DeleteAsync(string dashboardId);

    /// <summary>
    /// Tenant-aware delete overload added as a default interface member so that existing
    /// external implementations of <see cref="IDashboardService"/> do not need to be updated.
    /// The default body delegates to the tenant-unaware original for back-compat.
    /// Concrete implementations (e.g. <see cref="EfCoreDashboardService"/> and
    /// <see cref="JsonFileDashboardService"/>) override this with a real tenant-scoped lookup
    /// so a caller cannot delete another tenant's dashboard.
    /// </summary>
    Task DeleteAsync(string dashboardId, string? tenantId)
        => DeleteAsync(dashboardId);

    /// <summary>
    /// Original (backward-compatible) overload. External implementers implement this method.
    /// Callers that do not need tenant-aware routing should use this overload.
    /// </summary>
    Task<WidgetDataResult> GetWidgetDataAsync(string dashboardId, string widgetId, Dictionary<string, string>? filters = null, CancellationToken ct = default);

    /// <summary>
    /// Tenant-aware overload added as a default interface member so that existing
    /// external implementations of <see cref="IDashboardService"/> do not need to be
    /// updated. The <paramref name="tenantId"/> parameter is intentionally non-optional
    /// to avoid overload-resolution ambiguity with the original 4-arg overload above.
    /// The default body delegates to the tenant-unaware original; concrete implementations
    /// (e.g. <see cref="JsonFileDashboardService"/>) can override it with real tenant logic.
    /// </summary>
    Task<WidgetDataResult> GetWidgetDataAsync(string dashboardId, string widgetId, Dictionary<string, string>? filters, string? tenantId, CancellationToken ct = default)
        => GetWidgetDataAsync(dashboardId, widgetId, filters, ct);

    bool CanAccess(DashboardDefinition dashboard, string userId, string[] userRoles);
    bool CanEdit(DashboardDefinition dashboard, string userId, string[] userRoles);
}