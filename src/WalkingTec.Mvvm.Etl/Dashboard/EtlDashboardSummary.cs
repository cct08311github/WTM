#nullable enable
using System;
using System.Collections.Generic;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Dashboard;

/// <summary>
/// Aggregated ETL operations dashboard payload (10.5+). Returned by
/// <c>_EtlDashboardController.Stats</c> and rendered by the dashboard
/// view as KPI cards + charts + lists. Designed for one round-trip:
/// the front-end polls this endpoint every N seconds and re-renders
/// in place.
/// </summary>
public sealed class EtlDashboardSummary
{
    /// <summary>Top-of-page big-number cards.</summary>
    public EtlDashboardKpi Kpi { get; set; } = new();

    /// <summary>Status pie/donut for the trailing window.</summary>
    public IList<EtlDashboardStatusSlice> StatusDistribution { get; set; } = new List<EtlDashboardStatusSlice>();

    /// <summary>Per-day stacked-bar trend, oldest first.</summary>
    public IList<EtlDashboardDailyPoint> DailyTrend { get; set; } = new List<EtlDashboardDailyPoint>();

    /// <summary>Currently-executing jobs (from <c>EtlProgressTracker</c>).</summary>
    public IList<EtlDashboardRunning> Running { get; set; } = new List<EtlDashboardRunning>();

    /// <summary>Most recent failed runs with sanitised error message.</summary>
    public IList<EtlDashboardFailure> RecentFailures { get; set; } = new List<EtlDashboardFailure>();

    /// <summary>Slowest jobs by mean elapsed-ms over the trailing window.</summary>
    public IList<EtlDashboardSlow> SlowestJobs { get; set; } = new List<EtlDashboardSlow>();

    /// <summary>Trailing window in days the snapshot was computed over.</summary>
    public int WindowDays { get; set; }

    /// <summary>UTC moment the snapshot was generated. Surface to the UI as "last refreshed".</summary>
    public DateTime GeneratedAtUtc { get; set; }
}

public sealed class EtlDashboardKpi
{
    /// <summary>All-time job-definition row count.</summary>
    public int TotalJobs { get; set; }
    /// <summary>Jobs with <see cref="EtlJobStatus.Enabled"/>.</summary>
    public int ActiveJobs { get; set; }
    /// <summary>Jobs with <see cref="EtlJobStatus.Disabled"/> or <see cref="EtlJobStatus.Failed"/>.</summary>
    public int DisabledJobs { get; set; }
    /// <summary>Live count from <c>EtlProgressTracker</c>.</summary>
    public int RunningNow { get; set; }
    /// <summary>Trailing-window run count.</summary>
    public int RunsInWindow { get; set; }
    /// <summary>Successful runs ÷ total runs in the window. <c>null</c> when no runs.</summary>
    public decimal? SuccessRate { get; set; }
    /// <summary>Total <c>LoadedRows</c> across all runs in the window.</summary>
    public long TotalRowsLoadedInWindow { get; set; }
}

public sealed record EtlDashboardStatusSlice(EtlRunResult Result, int Count);

public sealed record EtlDashboardDailyPoint(
    DateTime Date,
    int Success,
    int Failed,
    int Aborted,
    int Skipped,
    long RowsLoaded);

public sealed record EtlDashboardRunning(
    Guid JobId,
    string JobName,
    int ProcessedRows,
    int? TotalRows,
    string Phase,
    DateTime StartedAtUtc,
    double RowsPerSecond);

public sealed record EtlDashboardFailure(
    Guid RunLogId,
    Guid JobId,
    string JobName,
    DateTime StartedAtUtc,
    long ElapsedMs,
    string? ErrorMessage);

public sealed record EtlDashboardSlow(
    Guid JobId,
    string JobName,
    long AvgElapsedMs,
    long MaxElapsedMs,
    int RunCount);
