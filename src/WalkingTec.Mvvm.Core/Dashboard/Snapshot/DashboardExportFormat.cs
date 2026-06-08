#nullable enable
namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Output format for a scheduled dashboard snapshot/export.
/// </summary>
public enum DashboardExportFormat
{
    /// <summary>
    /// Excel workbook (.xlsx) — one sheet per widget, built with NPOI.
    /// Available out of the box; no additional configuration required.
    /// </summary>
    Excel,

    /// <summary>
    /// PDF document — requires a host-registered <see cref="IDashboardRenderer"/>.
    /// The framework ships a <see cref="NotConfiguredDashboardRenderer"/> default that
    /// throws a clear configuration error when no renderer is registered.
    /// </summary>
    Pdf,

    /// <summary>
    /// PNG image — requires a host-registered <see cref="IDashboardRenderer"/>.
    /// The framework ships a <see cref="NotConfiguredDashboardRenderer"/> default that
    /// throws a clear configuration error when no renderer is registered.
    /// </summary>
    Png
}
