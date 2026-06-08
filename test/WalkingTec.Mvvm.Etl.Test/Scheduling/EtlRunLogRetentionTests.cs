#nullable enable
// ETL-014: Run-log retention pruning tests
// Uses SQLite shared in-memory so ExecuteDeleteAsync works
// (EF InMemory provider does not support bulk-delete operations).
using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.Test.ViewModels;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Scheduling;

/// <summary>
/// ETL-014: Retention-pruning logic.
/// PruneRunLogsAsync must:
///   - delete only logs older than RetentionDays
///   - keep logs within the window untouched
///   - be a no-op when RetentionDays == 0 (default)
/// </summary>
[TestClass]
public class EtlRunLogRetentionTests : IDisposable
{
    private EtlTestDataContext _dc = null!;
    private IServiceProvider _sp = null!;
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"Retention_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        _dc = new EtlTestDataContext($"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        _dc.Database.EnsureCreated();
        var wtm = MockWtmContext.CreateWtmContext(_dc);

        var services = new ServiceCollection();
        services.AddSingleton(wtm);
        _sp = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dc?.Dispose();
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    // ─── helpers ────────────────────────────────────────────────────────────

    private EtlJobDefinition AddJob(string name = "pruning-test")
    {
        var job = new EtlJobDefinition
        {
            Name = name,
            CronExpression = "0 0 * * *",
            SourceCsKey = "src",
            SourceDbType = DBTypeEnum.SqlServer,
            TargetCsKey = "tgt",
            TargetTableName = "tbl",
            MergeKeyColumn = "id",
            QueryTemplate = "SELECT 1",
            WatermarkType = EtlWatermarkType.FullLoad,
            Status = EtlJobStatus.Enabled,
            JobClassName = "test"
        };
        _dc.Set<EtlJobDefinition>().Add(job);
        _dc.SaveChanges();
        return job;
    }

    private EtlRunLog AddLog(Guid jobId, DateTime startedAt) =>
        AddLogResult(jobId, startedAt, EtlRunResult.Success);

    private EtlRunLog AddLogResult(Guid jobId, DateTime startedAt, EtlRunResult result)
    {
        var log = new EtlRunLog
        {
            JobId = jobId,
            Trigger = EtlRunTrigger.Scheduled,
            Result = result,
            StartedAt = startedAt,
            FinishedAt = startedAt.AddSeconds(10)
        };
        _dc.Set<EtlRunLog>().Add(log);
        _dc.SaveChanges();
        return log;
    }

    private IServiceProvider BuildSpWithRetention(int retentionDays)
    {
        var wtm = MockWtmContext.CreateWtmContext(_dc);
        var services = new ServiceCollection();
        services.AddSingleton(wtm);
        services.Configure<EtlOptions>(o => o.RunLogRetentionDays = retentionDays);
        return services.BuildServiceProvider();
    }

    // ─── tests ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task PruneRunLogsAsync_RetentionDays_zero_keeps_all_logs()
    {
        var job = AddJob();
        var now = DateTime.UtcNow;
        AddLog(job.ID, now.AddDays(-100));
        AddLog(job.ID, now.AddDays(-200));

        // Default: RetentionDays = 0 (keep forever)
        var sp = BuildSpWithRetention(0);
        var svc = new EtlSchedulerService(sp);
        await svc.PruneRunLogsAsync();

        var count = _dc.Set<EtlRunLog>().Count();
        count.Should().Be(2, "RetentionDays=0 is no-op; all logs must survive");
    }

    [TestMethod]
    public async Task PruneRunLogsAsync_deletes_logs_older_than_retention_window()
    {
        var job = AddJob();
        var now = DateTime.UtcNow;

        // Stale: older than retention window
        AddLog(job.ID, now.AddDays(-91));
        AddLog(job.ID, now.AddDays(-180));

        var sp = BuildSpWithRetention(90);
        var svc = new EtlSchedulerService(sp);
        await svc.PruneRunLogsAsync();

        var remaining = _dc.Set<EtlRunLog>().Count();
        remaining.Should().Be(0, "Both logs are older than 90 days and should be pruned");
    }

    [TestMethod]
    public async Task PruneRunLogsAsync_keeps_logs_within_retention_window()
    {
        var job = AddJob();
        var now = DateTime.UtcNow;

        // Fresh: within retention window
        AddLog(job.ID, now.AddDays(-10));
        AddLog(job.ID, now.AddDays(-50));

        var sp = BuildSpWithRetention(90);
        var svc = new EtlSchedulerService(sp);
        await svc.PruneRunLogsAsync();

        var remaining = _dc.Set<EtlRunLog>().Count();
        remaining.Should().Be(2, "Both logs are within 90 days and must not be pruned");
    }

    [TestMethod]
    public async Task PruneRunLogsAsync_deletes_only_old_logs_keeps_recent()
    {
        var job = AddJob();
        var now = DateTime.UtcNow;

        // Old log — should be deleted
        var oldLog = AddLog(job.ID, now.AddDays(-200));
        // Recent log — must survive
        var recentLog = AddLog(job.ID, now.AddDays(-30));

        var sp = BuildSpWithRetention(90);
        var svc = new EtlSchedulerService(sp);
        await svc.PruneRunLogsAsync();

        var ids = _dc.Set<EtlRunLog>().Select(r => r.ID).ToList();
        ids.Should().NotContain(oldLog.ID, "log older than 90 days must be pruned");
        ids.Should().Contain(recentLog.ID, "log within 90 days must survive");
    }

    [TestMethod]
    public async Task PruneRunLogsAsync_negative_retention_is_treated_as_no_op()
    {
        var job = AddJob();
        AddLog(job.ID, DateTime.UtcNow.AddDays(-500));

        // Negative retention = misconfiguration → treat as 0 (no-op)
        var sp = BuildSpWithRetention(-10);
        var svc = new EtlSchedulerService(sp);
        await svc.PruneRunLogsAsync();

        _dc.Set<EtlRunLog>().Count().Should().Be(1);
    }
}
