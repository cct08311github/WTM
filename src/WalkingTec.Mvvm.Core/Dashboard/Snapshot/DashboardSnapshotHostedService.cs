#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

internal static class SnapshotContentTypeHelper
{
    internal static string GetContentType(DashboardExportFormat format) => format switch
    {
        DashboardExportFormat.Excel => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        DashboardExportFormat.Pdf   => "application/pdf",
        DashboardExportFormat.Png   => "image/png",
        _                          => "application/octet-stream"
    };

    internal static string BuildFileName(DashboardSnapshotResult result) =>
        $"{result.DashboardId}-{result.JobId}-{DateTime.UtcNow:yyyyMMddHHmmss}.{result.FileExtension}";
}

/// <summary>
/// Background service that runs scheduled dashboard snapshot jobs.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Opt-in: does nothing unless at least one job is configured in
///   <see cref="DashboardSnapshotOptions.Jobs"/> with a valid <see cref="ScheduledDashboardJobConfig.CronExpression"/>.</item>
/// <item>Uses Quartz's <see cref="CronExpression"/> (already a framework dependency) to compute
///   the next fire time from a 5-field UNIX cron string.</item>
/// <item>Each job runs independently; a failure in one job does not block others.</item>
/// <item>After each successful job, all registered <see cref="IDashboardSnapshotSink"/> instances
///   are invoked in order. Per-sink failures are caught and logged so one failing sink does not
///   block the others. When no sinks are registered the service falls back to log-only behaviour
///   (backward-compatible default).</item>
/// </list>
/// </remarks>
public sealed class DashboardSnapshotHostedService : BackgroundService
{
    private readonly IScheduledDashboardJob _job;
    private readonly IEnumerable<IDashboardSnapshotSink> _sinks;
    private readonly IOptions<DashboardSnapshotOptions> _options;
    private readonly ILogger<DashboardSnapshotHostedService> _logger;

    public DashboardSnapshotHostedService(
        IScheduledDashboardJob job,
        IEnumerable<IDashboardSnapshotSink> sinks,
        IOptions<DashboardSnapshotOptions> options,
        ILogger<DashboardSnapshotHostedService> logger)
    {
        _job     = job     ?? throw new ArgumentNullException(nameof(job));
        _sinks   = sinks   ?? throw new ArgumentNullException(nameof(sinks));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (opts.Jobs == null || opts.Jobs.Count == 0)
        {
            _logger.LogDebug("DashboardSnapshotHostedService: no jobs configured, service is inactive.");
            return;
        }

        // Build the scheduling state: per-job "next fire" time.
        var nextFire = new Dictionary<string, DateTimeOffset?>();
        foreach (var cfg in opts.Jobs)
        {
            if (string.IsNullOrWhiteSpace(cfg.CronExpression))
            {
                _logger.LogDebug(
                    "DashboardSnapshotHostedService: job {JobId} has no CronExpression, skipped.", cfg.JobId);
                nextFire[cfg.JobId] = null;
            }
            else
            {
                nextFire[cfg.JobId] = ComputeNext(cfg.CronExpression, DateTimeOffset.UtcNow);
            }
        }

        _logger.LogInformation(
            "DashboardSnapshotHostedService started with {Count} configured job(s).", opts.Jobs.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var cfg in opts.Jobs)
            {
                if (!nextFire.TryGetValue(cfg.JobId, out var nf) || nf == null)
                    continue;

                if (now >= nf.Value)
                {
                    // Fire the job in a fire-and-forget fashion so it doesn't block other jobs.
                    _ = RunJobSafeAsync(cfg, stoppingToken);

                    // Compute next fire time from the scheduled time (not from "now") to avoid drift.
                    nextFire[cfg.JobId] = ComputeNext(cfg.CronExpression!, nf.Value);
                }
            }

            // Poll every 10 seconds — sufficient resolution for minute-level cron schedules.
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunJobSafeAsync(ScheduledDashboardJobConfig cfg, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation(
                "DashboardSnapshotHostedService: running job {JobId} for dashboard {DashboardId} [{Format}].",
                cfg.JobId, cfg.DashboardId, cfg.Format);

            var result = await _job.RunAsync(cfg, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "DashboardSnapshotHostedService: job {JobId} completed, {Bytes} bytes [{Extension}].",
                cfg.JobId, result.Content.Length, result.FileExtension);

            await DeliverAsync(result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown — do not log as error.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DashboardSnapshotHostedService: job {JobId} failed.", cfg.JobId);
        }
    }

    /// <summary>
    /// Invokes each registered <see cref="IDashboardSnapshotSink"/> in order.
    /// Per-sink failures are caught and logged; one failing sink does not abort the others.
    /// When no sinks are registered this method is a no-op (backward-compatible log-only default).
    /// </summary>
    private async Task DeliverAsync(DashboardSnapshotResult result, CancellationToken ct)
    {
        var fileName    = SnapshotContentTypeHelper.BuildFileName(result);
        var contentType = SnapshotContentTypeHelper.GetContentType(result.Format);

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.DeliverAsync(result, result.Content, fileName, contentType, ct)
                          .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // re-throw so the outer handler recognises graceful shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DashboardSnapshotHostedService: sink {SinkType} failed for job {JobId} — continuing with remaining sinks.",
                    sink.GetType().Name, result.JobId);
            }
        }
    }

    // ── Cron helpers (Quartz CronExpression — 6-field with seconds) ──────────

    /// <summary>
    /// Converts a 5-field UNIX cron string (min hour day month weekday) to a Quartz 6-field
    /// expression (prepends "0" for the seconds field) and returns the next fire time after
    /// <paramref name="after"/>.
    /// Returns null when the expression is invalid or no next time exists.
    /// </summary>
    private DateTimeOffset? ComputeNext(string cronExpression, DateTimeOffset after)
    {
        try
        {
            // Convert 5-field UNIX cron to 6-field Quartz (prepend "0" seconds).
            var parts = cronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var quartzExpr = parts.Length == 5
                ? "0 " + string.Join(' ', parts)
                : cronExpression; // already 6-field (Quartz-native) or will fail validation

            var cron = new CronExpression(quartzExpr);
            return cron.GetNextValidTimeAfter(after);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "DashboardSnapshotHostedService: invalid cron expression '{Expr}', job disabled.",
                cronExpression);
            return null;
        }
    }
}
