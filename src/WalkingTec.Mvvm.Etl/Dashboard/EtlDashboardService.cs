#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;

namespace WalkingTec.Mvvm.Etl.Dashboard;

/// <summary>
/// Computes <see cref="EtlDashboardSummary"/> in one call. Reads the
/// <see cref="EtlRunLog"/> + <see cref="EtlJobDefinition"/> tables
/// from the supplied <see cref="IDataContext"/> and merges in live
/// running-job state from <see cref="EtlProgressTracker"/>.
/// </summary>
/// <remarks>
/// Service is stateless and side-effect-free — the same call is safe
/// to issue from a polling front-end every few seconds. Heavy lifting
/// is one <c>GROUP BY</c> per chart panel + one in-memory join with
/// the job-name dictionary, so even with hundreds of thousands of
/// run-log rows the response budget is dominated by DB latency, not
/// the service code.
/// </remarks>
public class EtlDashboardService
{
    private readonly EtlProgressTracker _tracker;

    public EtlDashboardService(EtlProgressTracker tracker)
    {
        _tracker = tracker;
    }

    /// <summary>
    /// Build the full dashboard payload for the trailing
    /// <paramref name="windowDays"/> period (clamped 1..90 to keep
    /// queries bounded). <paramref name="topN"/> bounds the
    /// "recent failures" + "slowest jobs" lists.
    /// </summary>
    public EtlDashboardSummary BuildSummary(
        IDataContext dc,
        int windowDays = 7,
        int topN = 10)
    {
        ArgumentNullException.ThrowIfNull(dc);
        windowDays = Math.Clamp(windowDays, 1, 90);
        topN = Math.Clamp(topN, 1, 100);

        var nowUtc = DateTime.UtcNow;
        var since = nowUtc.AddDays(-windowDays);

        var jobs = dc.Set<EtlJobDefinition>()
            .Select(j => new { j.ID, j.Name, j.Status })
            .ToList();

        var jobNameById = jobs.ToDictionary(j => j.ID, j => j.Name);

        var runs = dc.Set<EtlRunLog>()
            .Where(r => r.StartedAt >= since)
            .Select(r => new {
                r.ID,
                r.JobId,
                r.Result,
                r.StartedAt,
                r.ElapsedMs,
                r.LoadedRows,
                r.ErrorMessage,
            })
            .ToList();

        var summary = new EtlDashboardSummary
        {
            WindowDays = windowDays,
            GeneratedAtUtc = nowUtc,
        };

        // ── KPI ────────────────────────────────────────────────────────
        var totalRuns = runs.Count;
        var successRuns = runs.Count(r => r.Result == EtlRunResult.Success);
        summary.Kpi = new EtlDashboardKpi
        {
            TotalJobs = jobs.Count,
            ActiveJobs = jobs.Count(j => j.Status == EtlJobStatus.Enabled),
            DisabledJobs = jobs.Count(j => j.Status == EtlJobStatus.Disabled
                                         || j.Status == EtlJobStatus.Failed),
            RunningNow = _tracker.GetAll().Count,
            RunsInWindow = totalRuns,
            SuccessRate = totalRuns == 0
                ? null
                : Math.Round((decimal)successRuns / totalRuns, 4),
            TotalRowsLoadedInWindow = runs.Sum(r => (long)r.LoadedRows),
        };

        // ── Status distribution ───────────────────────────────────────
        summary.StatusDistribution = runs
            .GroupBy(r => r.Result)
            .Select(g => new EtlDashboardStatusSlice(g.Key, g.Count()))
            .OrderBy(s => s.Result)
            .ToList();

        // ── Daily stacked trend ───────────────────────────────────────
        // Always emit `windowDays` data points (oldest first) even
        // for days with zero runs — front-end charts assume a dense
        // x-axis. Group by date in UTC.
        var grouped = runs
            .GroupBy(r => r.StartedAt.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        var trend = new List<EtlDashboardDailyPoint>(windowDays);
        for (var i = windowDays - 1; i >= 0; i--)
        {
            var day = nowUtc.Date.AddDays(-i);
            grouped.TryGetValue(day, out var dayRuns);
            dayRuns ??= new();
            trend.Add(new EtlDashboardDailyPoint(
                Date: day,
                Success: dayRuns.Count(r => r.Result == EtlRunResult.Success),
                Failed:  dayRuns.Count(r => r.Result == EtlRunResult.Failed),
                Aborted: dayRuns.Count(r => r.Result == EtlRunResult.Aborted),
                Skipped: dayRuns.Count(r => r.Result == EtlRunResult.Skipped),
                RowsLoaded: dayRuns.Sum(r => (long)r.LoadedRows)));
        }
        summary.DailyTrend = trend;

        // ── Live running ──────────────────────────────────────────────
        summary.Running = _tracker.GetAll()
            .Select(p => new EtlDashboardRunning(
                JobId: p.JobId,
                JobName: string.IsNullOrEmpty(p.JobName)
                    ? (jobNameById.TryGetValue(p.JobId, out var n) ? n : "")
                    : p.JobName,
                ProcessedRows: p.ProcessedRows,
                TotalRows: p.TotalRows,
                Phase: p.Phase ?? "",
                StartedAtUtc: p.StartedAt,
                RowsPerSecond: p.RowsPerSecond ?? 0d))
            .OrderByDescending(r => r.StartedAtUtc)
            .ToList();

        // ── Recent failures ───────────────────────────────────────────
        summary.RecentFailures = runs
            .Where(r => r.Result == EtlRunResult.Failed)
            .OrderByDescending(r => r.StartedAt)
            .Take(topN)
            .Select(r => new EtlDashboardFailure(
                RunLogId: r.ID,
                JobId: r.JobId,
                JobName: jobNameById.TryGetValue(r.JobId, out var n) ? n : "",
                StartedAtUtc: r.StartedAt,
                ElapsedMs: r.ElapsedMs,
                ErrorMessage: r.ErrorMessage))
            .ToList();

        // ── Slowest jobs ──────────────────────────────────────────────
        // Only count successful runs — failed runs often abort early
        // and would skew the average toward "fast", giving a misleading
        // "this slow job is actually pretty fast" reading.
        summary.SlowestJobs = runs
            .Where(r => r.Result == EtlRunResult.Success)
            .GroupBy(r => r.JobId)
            .Select(g => new EtlDashboardSlow(
                JobId: g.Key,
                JobName: jobNameById.TryGetValue(g.Key, out var n) ? n : "",
                AvgElapsedMs: (long)g.Average(r => r.ElapsedMs),
                MaxElapsedMs: g.Max(r => r.ElapsedMs),
                RunCount: g.Count()))
            .OrderByDescending(s => s.AvgElapsedMs)
            .Take(topN)
            .ToList();

        return summary;
    }
}
