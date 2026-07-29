#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Dashboard;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.Test.ViewModels;

namespace WalkingTec.Mvvm.Etl.Test.Dashboard;

/// <summary>
/// EtlDashboardService — KPI / status / trend / failures / slowest /
/// running aggregates against an in-memory EF Core context. Each
/// section is exercised independently so a regression in one chart
/// panel doesn't mask another.
/// </summary>
[TestClass]
public class EtlDashboardServiceTests
{
    private EtlTestDataContext _dc = null!;
    private EtlProgressTracker _tracker = null!;
    private EtlDashboardService _svc = null!;

    [TestInitialize]
    public void Setup()
    {
        _dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        _tracker = new EtlProgressTracker();
        _svc = new EtlDashboardService(_tracker);
    }

    private Guid SeedJob(string name, EtlJobStatus status = EtlJobStatus.Enabled)
    {
        var id = Guid.NewGuid();
        _dc.EtlJobDefinitions.Add(new EtlJobDefinition { ID = id, Name = name, Status = status });
        _dc.SaveChanges();
        return id;
    }

    private void SeedRun(Guid jobId, EtlRunResult result, DateTime startedAt,
        long elapsedMs = 1000, int loadedRows = 100, string? error = null)
    {
        _dc.EtlRunLogs.Add(new EtlRunLog
        {
            ID = Guid.NewGuid(),
            JobId = jobId,
            Result = result,
            StartedAt = startedAt,
            ElapsedMs = elapsedMs,
            LoadedRows = loadedRows,
            ErrorMessage = error,
        });
        _dc.SaveChanges();
    }

    // ── KPI ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Kpi_counts_jobs_by_status()
    {
        SeedJob("a", EtlJobStatus.Enabled);
        SeedJob("b", EtlJobStatus.Enabled);
        SeedJob("c", EtlJobStatus.Disabled);
        SeedJob("d", EtlJobStatus.Failed);

        var s = _svc.BuildSummary(_dc, callerTenantCode: null);

        Assert.AreEqual(4, s.Kpi.TotalJobs);
        Assert.AreEqual(2, s.Kpi.ActiveJobs);
        // Disabled + Failed both counted as "non-running" → 2
        Assert.AreEqual(2, s.Kpi.DisabledJobs);
    }

    [TestMethod]
    public void Kpi_running_count_pulls_from_tracker_not_DB()
    {
        var jobId = SeedJob("a");
        _tracker.Update(new EtlProgress { JobId = jobId, JobName = "a", ProcessedRows = 10, StartedAt = DateTime.UtcNow });

        var s = _svc.BuildSummary(_dc, callerTenantCode: null);

        Assert.AreEqual(1, s.Kpi.RunningNow);
    }

    [TestMethod]
    public void Kpi_success_rate_computed_for_window()
    {
        var jobId = SeedJob("a");
        var now = DateTime.UtcNow;
        // 4 success + 1 failed within 7 days → 80%
        SeedRun(jobId, EtlRunResult.Success, now.AddHours(-1));
        SeedRun(jobId, EtlRunResult.Success, now.AddHours(-2));
        SeedRun(jobId, EtlRunResult.Success, now.AddHours(-3));
        SeedRun(jobId, EtlRunResult.Success, now.AddHours(-4));
        SeedRun(jobId, EtlRunResult.Failed,  now.AddHours(-5));

        var s = _svc.BuildSummary(_dc, callerTenantCode: null, windowDays: 7);

        Assert.AreEqual(5, s.Kpi.RunsInWindow);
        Assert.AreEqual(0.8m, s.Kpi.SuccessRate);
    }

    [TestMethod]
    public void Kpi_success_rate_null_when_no_runs()
    {
        SeedJob("a");
        var s = _svc.BuildSummary(_dc, callerTenantCode: null);
        Assert.IsNull(s.Kpi.SuccessRate);
    }

    [TestMethod]
    public void Kpi_TotalRowsLoadedInWindow_sums_loadedRows()
    {
        var jobId = SeedJob("a");
        var now = DateTime.UtcNow;
        SeedRun(jobId, EtlRunResult.Success, now, loadedRows: 1000);
        SeedRun(jobId, EtlRunResult.Success, now, loadedRows: 2500);
        SeedRun(jobId, EtlRunResult.Failed,  now, loadedRows: 0);

        var s = _svc.BuildSummary(_dc, callerTenantCode: null);

        Assert.AreEqual(3500L, s.Kpi.TotalRowsLoadedInWindow);
    }

    // ── Window scope ───────────────────────────────────────────────────

    [TestMethod]
    public void Runs_outside_window_excluded()
    {
        var jobId = SeedJob("a");
        var now = DateTime.UtcNow;
        SeedRun(jobId, EtlRunResult.Success, now.AddDays(-1));
        SeedRun(jobId, EtlRunResult.Success, now.AddDays(-50)); // outside default 7-day window

        var s = _svc.BuildSummary(_dc, callerTenantCode: null, windowDays: 7);
        Assert.AreEqual(1, s.Kpi.RunsInWindow);
    }

    [TestMethod]
    public void Window_clamped_between_1_and_90()
    {
        SeedJob("a");
        var s1 = _svc.BuildSummary(_dc, callerTenantCode: null, windowDays: -5);
        Assert.AreEqual(1, s1.WindowDays, "Negative clamps up to 1.");
        var s2 = _svc.BuildSummary(_dc, callerTenantCode: null, windowDays: 1000);
        Assert.AreEqual(90, s2.WindowDays, "Out-of-bounds clamps down to 90.");
    }

    // ── Status distribution ────────────────────────────────────────────

    [TestMethod]
    public void StatusDistribution_groups_by_result()
    {
        var jobId = SeedJob("a");
        var now = DateTime.UtcNow;
        SeedRun(jobId, EtlRunResult.Success, now);
        SeedRun(jobId, EtlRunResult.Success, now);
        SeedRun(jobId, EtlRunResult.Failed,  now);
        SeedRun(jobId, EtlRunResult.Aborted, now);

        var s = _svc.BuildSummary(_dc, callerTenantCode: null);

        var byResult = s.StatusDistribution.ToDictionary(x => x.Result, x => x.Count);
        Assert.AreEqual(2, byResult[EtlRunResult.Success]);
        Assert.AreEqual(1, byResult[EtlRunResult.Failed]);
        Assert.AreEqual(1, byResult[EtlRunResult.Aborted]);
    }

    // ── Daily trend ────────────────────────────────────────────────────

    [TestMethod]
    public void DailyTrend_emits_one_point_per_day_oldest_first()
    {
        SeedJob("a");
        var s = _svc.BuildSummary(_dc, callerTenantCode: null, windowDays: 7);

        Assert.AreEqual(7, s.DailyTrend.Count);
        for (int i = 1; i < s.DailyTrend.Count; i++)
        {
            Assert.IsTrue(s.DailyTrend[i].Date >= s.DailyTrend[i - 1].Date,
                "Trend points must be sorted ascending by date.");
        }
    }

    [TestMethod]
    public void DailyTrend_buckets_runs_by_calendar_day_in_UTC()
    {
        var jobId = SeedJob("a");
        var today = DateTime.UtcNow.Date;
        SeedRun(jobId, EtlRunResult.Success, today.AddHours(2));
        SeedRun(jobId, EtlRunResult.Success, today.AddHours(15));
        SeedRun(jobId, EtlRunResult.Failed,  today.AddDays(-1).AddHours(10));

        var s = _svc.BuildSummary(_dc, callerTenantCode: null, windowDays: 3);
        var todayPoint = s.DailyTrend.Single(p => p.Date == today);
        var yesterdayPoint = s.DailyTrend.Single(p => p.Date == today.AddDays(-1));

        Assert.AreEqual(2, todayPoint.Success);
        Assert.AreEqual(1, yesterdayPoint.Failed);
        Assert.AreEqual(0, todayPoint.Failed);
    }

    // ── Recent failures ────────────────────────────────────────────────

    [TestMethod]
    public void RecentFailures_lists_only_failed_ordered_desc_with_topN()
    {
        var jobId = SeedJob("ProcessOrders");
        var now = DateTime.UtcNow;
        SeedRun(jobId, EtlRunResult.Success, now.AddMinutes(-1));
        SeedRun(jobId, EtlRunResult.Failed,  now.AddMinutes(-10), error: "old failure");
        SeedRun(jobId, EtlRunResult.Failed,  now.AddMinutes(-5),  error: "newer failure");
        SeedRun(jobId, EtlRunResult.Aborted, now.AddMinutes(-3));

        var s = _svc.BuildSummary(_dc, callerTenantCode: null, topN: 5);

        Assert.AreEqual(2, s.RecentFailures.Count);
        Assert.AreEqual("newer failure", s.RecentFailures[0].ErrorMessage);
        Assert.AreEqual("old failure",   s.RecentFailures[1].ErrorMessage);
        Assert.IsTrue(s.RecentFailures.All(f => f.JobName == "ProcessOrders"));
    }

    [TestMethod]
    public void RecentFailures_topN_caps_list()
    {
        var jobId = SeedJob("a");
        for (var i = 0; i < 20; i++)
        {
            SeedRun(jobId, EtlRunResult.Failed, DateTime.UtcNow.AddMinutes(-i));
        }

        var s = _svc.BuildSummary(_dc, callerTenantCode: null, topN: 3);

        Assert.AreEqual(3, s.RecentFailures.Count);
    }

    // ── Slowest jobs ───────────────────────────────────────────────────

    [TestMethod]
    public void SlowestJobs_uses_only_successful_runs_for_average()
    {
        var slowJob = SeedJob("SlowImport");
        var fastJob = SeedJob("FastReport");
        var now = DateTime.UtcNow;
        // SlowImport: 3 successful runs averaging 5_000 ms
        SeedRun(slowJob, EtlRunResult.Success, now, elapsedMs: 4_000);
        SeedRun(slowJob, EtlRunResult.Success, now, elapsedMs: 5_000);
        SeedRun(slowJob, EtlRunResult.Success, now, elapsedMs: 6_000);
        // SlowImport: 1 failed run with HUGE elapsed should NOT be counted
        SeedRun(slowJob, EtlRunResult.Failed,  now, elapsedMs: 1_000_000);
        // FastReport: 2 successful runs averaging 100 ms
        SeedRun(fastJob, EtlRunResult.Success, now, elapsedMs: 90);
        SeedRun(fastJob, EtlRunResult.Success, now, elapsedMs: 110);

        var s = _svc.BuildSummary(_dc, callerTenantCode: null);

        // SlowImport must rank first; 5_000 not 1_000_000.
        Assert.AreEqual("SlowImport", s.SlowestJobs[0].JobName);
        Assert.AreEqual(5_000L, s.SlowestJobs[0].AvgElapsedMs);
        Assert.AreEqual(6_000L, s.SlowestJobs[0].MaxElapsedMs);
        Assert.AreEqual(3, s.SlowestJobs[0].RunCount);
        Assert.AreEqual("FastReport", s.SlowestJobs[1].JobName);
        Assert.AreEqual(100L, s.SlowestJobs[1].AvgElapsedMs);
    }

    // ── Running ────────────────────────────────────────────────────────

    [TestMethod]
    public void Running_resolves_jobName_from_tracker_or_DB()
    {
        var jobId = SeedJob("ImportOrders");
        // Tracker entry with empty JobName → service should backfill from DB.
        _tracker.Update(new EtlProgress { JobId = jobId, JobName = null!, ProcessedRows = 50, StartedAt = DateTime.UtcNow });

        var s = _svc.BuildSummary(_dc, callerTenantCode: null);

        Assert.AreEqual(1, s.Running.Count);
        Assert.AreEqual("ImportOrders", s.Running[0].JobName);
    }

    // ── Generated-at ───────────────────────────────────────────────────

    [TestMethod]
    public void GeneratedAtUtc_is_recent()
    {
        SeedJob("a");
        var before = DateTime.UtcNow;
        var s = _svc.BuildSummary(_dc, callerTenantCode: null);
        var after = DateTime.UtcNow;
        Assert.IsTrue(s.GeneratedAtUtc >= before && s.GeneratedAtUtc <= after);
    }
}
