#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Abstraction for rendering a dashboard to a binary format (PDF, PNG, …).
/// </summary>
/// <remarks>
/// <para>
/// The WTM framework intentionally does <strong>not</strong> ship a built-in implementation
/// because rendering pixel-accurate dashboards requires a headless browser (e.g. Playwright
/// or Puppeteer), which would make the Core package heavyweight and introduce a large
/// dependency that many host applications do not need.
/// </para>
/// <para>
/// To enable PDF/PNG exports, register your own implementation in the DI container:
/// <code>
/// services.AddSingleton&lt;IDashboardRenderer, MyPlaywrightDashboardRenderer&gt;();
/// </code>
/// If no implementation is registered, the framework falls back to
/// <see cref="NotConfiguredDashboardRenderer"/> which throws a descriptive
/// <see cref="System.NotSupportedException"/>.
/// </para>
/// </remarks>
public interface IDashboardRenderer
{
    /// <summary>
    /// Renders the specified dashboard to raw bytes in the requested format.
    /// </summary>
    /// <param name="dashboardId">ID of the dashboard to render.</param>
    /// <param name="format">
    /// Target output format. Implementations should throw <see cref="System.NotSupportedException"/>
    /// when a format is not supported.
    /// </param>
    /// <param name="tenantId">Optional tenant context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Raw bytes for the rendered output (e.g. PDF or PNG binary).</returns>
    Task<byte[]> RenderAsync(
        string dashboardId,
        DashboardExportFormat format,
        string? tenantId = null,
        CancellationToken ct = default);
}
