#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Default <see cref="IScheduledDashboardJob"/> implementation.
/// Fetches all widget data for the target dashboard, then:
/// <list type="bullet">
/// <item>For <see cref="DashboardExportFormat.Excel"/>: builds a multi-sheet workbook with NPOI.</item>
/// <item>For <see cref="DashboardExportFormat.Pdf"/>/<see cref="DashboardExportFormat.Png"/>:
///   delegates to the registered <see cref="IDashboardRenderer"/>.</item>
/// </list>
/// </summary>
public sealed class DashboardSnapshotJob : IScheduledDashboardJob
{
    private readonly IDashboardService _dashboardService;
    private readonly IDashboardRenderer _renderer;
    private readonly ILogger<DashboardSnapshotJob> _logger;

    public DashboardSnapshotJob(
        IDashboardService dashboardService,
        IDashboardRenderer renderer,
        ILogger<DashboardSnapshotJob> logger)
    {
        _dashboardService = dashboardService ?? throw new ArgumentNullException(nameof(dashboardService));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<DashboardSnapshotResult> RunAsync(
        ScheduledDashboardJobConfig config,
        CancellationToken ct = default)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(config.DashboardId))
            throw new ArgumentException("DashboardId must not be empty.", nameof(config));

        _logger.LogInformation(
            "DashboardSnapshotJob starting: jobId={JobId}, dashboardId={DashboardId}, format={Format}",
            config.JobId, config.DashboardId, config.Format);

        var dashboard = await _dashboardService.GetAsync(config.DashboardId, config.TenantId);
        if (dashboard == null)
            throw new InvalidOperationException(
                $"Dashboard '{config.DashboardId}' not found (tenantId={config.TenantId ?? "null"}).");

        byte[] content;

        switch (config.Format)
        {
            case DashboardExportFormat.Excel:
                content = await BuildExcelAsync(dashboard, config.TenantId, ct);
                break;

            case DashboardExportFormat.Pdf:
            case DashboardExportFormat.Png:
                content = await _renderer.RenderAsync(config.DashboardId, config.Format, config.TenantId, ct);
                break;

            default:
                throw new NotSupportedException($"Unsupported export format: {config.Format}");
        }

        _logger.LogInformation(
            "DashboardSnapshotJob completed: jobId={JobId}, bytes={Bytes}",
            config.JobId, content.Length);

        return new DashboardSnapshotResult
        {
            JobId = config.JobId,
            DashboardId = config.DashboardId,
            Format = config.Format,
            Content = content
        };
    }

    // ── Excel builder ─────────────────────────────────────────────────────────

    private async Task<byte[]> BuildExcelAsync(
        DashboardDefinition dashboard,
        string? tenantId,
        CancellationToken ct)
    {
        var results = new Dictionary<string, WidgetDataResult>();

        foreach (var (widgetId, _) in dashboard.Widgets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await _dashboardService.GetWidgetDataAsync(
                    dashboard.Id, widgetId, null, tenantId, ct);
                results[widgetId] = result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Widget data fetch failed during Excel snapshot: dashboard={DashboardId}, widget={WidgetId}",
                    dashboard.Id, widgetId);

                results[widgetId] = new WidgetDataResult
                {
                    Error = $"Data fetch failed: {ex.Message}"
                };
            }
        }

        return DashboardExcelExporter.Export(dashboard, results);
    }
}
