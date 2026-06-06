#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.Test.ViewModels;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Scheduling;

/// <summary>
/// 測試 EtlSchedulerService.ResetGhostRunningJobsAsync()：
/// 服務啟動時將上次崩潰遺留的 Running Job 重置為 Failed。
///
/// SQLite shared in-memory (instead of EF InMemory) so that
/// ExecuteUpdateAsync in scheduler paths works correctly.
/// </summary>
[TestClass]
public class GhostRunningJobsTests : IDisposable
{
    private EtlTestDataContext _dc = null!;
    private IServiceProvider _sp = null!;
    // Keep-alive connection holds the shared SQLite in-memory DB alive across
    // the EtlTestDataContext scope (which may open/close its own connection).
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"GhostJobs_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        _dc = new EtlTestDataContext($"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        _dc.Database.EnsureCreated();
        var wtm = MockWtmContext.CreateWtmContext(_dc);

        var services = new ServiceCollection();
        services.AddSingleton(wtm);
        _sp = services.BuildServiceProvider();
    }

    private EtlJobDefinition AddJob(EtlJobStatus status, string name = "TestJob")
    {
        var job = new EtlJobDefinition
        {
            Name = name,
            CronExpression = "0 0 * * *",
            SourceCsKey = "test",
            SourceDbType = DBTypeEnum.SqlServer,
            WatermarkType = EtlWatermarkType.FullLoad,
            Status = status,
        };
        _dc.Set<EtlJobDefinition>().Add(job);
        _dc.SaveChanges();
        return job;
    }

    [TestMethod]
    public async Task ResetGhostRunningJobs_sets_Running_to_Failed()
    {
        var job = AddJob(EtlJobStatus.Running);

        var svc = new EtlSchedulerService(_sp);
        await svc.ResetGhostRunningJobsAsync();

        var updated = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);
        Assert.AreEqual(EtlJobStatus.Failed, updated!.Status,
            "Running Job 應被重置為 Failed");
        Assert.IsNotNull(updated.LastError, "LastError 應記錄崩潰原因");
    }

    [TestMethod]
    public async Task ResetGhostRunningJobs_writes_RunLog_for_each_ghost()
    {
        var job1 = AddJob(EtlJobStatus.Running, "Ghost1");
        var job2 = AddJob(EtlJobStatus.Running, "Ghost2");

        var svc = new EtlSchedulerService(_sp);
        await svc.ResetGhostRunningJobsAsync();

        var logs = _dc.Set<EtlRunLog>().Where(l => l.Result == EtlRunResult.Failed).ToList();
        Assert.AreEqual(2, logs.Count, "每個幽靈 Job 應各有一筆 Failed RunLog");
        Assert.IsTrue(logs.Any(l => l.JobId == job1.ID), "Ghost1 應有 RunLog");
        Assert.IsTrue(logs.Any(l => l.JobId == job2.ID), "Ghost2 應有 RunLog");
    }

    [TestMethod]
    public async Task ResetGhostRunningJobs_does_not_affect_non_Running_jobs()
    {
        AddJob(EtlJobStatus.Enabled, "Active");
        AddJob(EtlJobStatus.Failed, "AlreadyFailed");
        AddJob(EtlJobStatus.Disabled, "Disabled");

        var svc = new EtlSchedulerService(_sp);
        await svc.ResetGhostRunningJobsAsync();

        var all = _dc.Set<EtlJobDefinition>().ToList();
        Assert.IsTrue(all.Any(j => j.Name == "Active"    && j.Status == EtlJobStatus.Enabled),   "Enabled 不應被改");
        Assert.IsTrue(all.Any(j => j.Name == "AlreadyFailed" && j.Status == EtlJobStatus.Failed), "Failed 不應被改");
        Assert.IsTrue(all.Any(j => j.Name == "Disabled"  && j.Status == EtlJobStatus.Disabled),  "Disabled 不應被改");
        Assert.AreEqual(0, _dc.Set<EtlRunLog>().Count(), "無幽靈 Job，不應寫入 RunLog");
    }

    [TestMethod]
    public async Task ResetGhostRunningJobs_no_jobs_completes_without_error()
    {
        // 完全空白的 DB — 應靜默完成
        var svc = new EtlSchedulerService(_sp);
        await svc.ResetGhostRunningJobsAsync(); // should not throw
        Assert.AreEqual(0, _dc.Set<EtlRunLog>().Count());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dc?.Dispose();
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();
}
