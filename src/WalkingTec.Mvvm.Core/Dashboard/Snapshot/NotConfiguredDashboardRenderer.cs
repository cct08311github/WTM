#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Default <see cref="IDashboardRenderer"/> registered when no host implementation is provided.
/// Every call throws a <see cref="NotSupportedException"/> with a clear diagnostic message
/// so that misconfigured hosts receive an actionable error rather than a cryptic NullReferenceException.
/// </summary>
public sealed class NotConfiguredDashboardRenderer : IDashboardRenderer
{
    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always — no renderer has been configured.</exception>
    public Task<byte[]> RenderAsync(
        string dashboardId,
        DashboardExportFormat format,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException(
            $"Dashboard {format} rendering is not configured. " +
            "To enable PDF/PNG exports, register a custom IDashboardRenderer in the DI container " +
            "(e.g. a Playwright- or Puppeteer-based implementation). " +
            "Example: services.AddSingleton<IDashboardRenderer, MyPlaywrightDashboardRenderer>(). " +
            "The WTM framework does not bundle a headless browser to keep the Core package lightweight.");
    }
}
