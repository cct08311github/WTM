#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Quartz;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.Test.ViewModels;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Scheduling;

/// <summary>
/// Regression tests proving that the targeted ExecuteUpdateAsync calls in
/// EtlSchedulerService.ScheduleJobAsync (via LoadJobsFromDbAsync) continue
/// to stamp <see cref="WalkingTec.Mvvm.Core.Models.BasePoco.UpdateTime"/> —
/// the audit field that SaveChanges / ApplyAuditFields previously applied.
///
/// Background: the PR converted two writes to ExecuteUpdateAsync for perf.
/// ExecuteUpdateAsync bypasses EF ChangeTracker, so ApplyAuditFields is NOT
/// called.  Without an explicit .SetProperty(j => j.UpdateTime, …), UpdateTime
/// would stay null / stale — an observable behaviour regression.
///
/// SQLite shared in-memory is used because ExecuteUpdateAsync requires a real
/// SQL engine (EF InMemory provider does not support it).
/// </summary>
[TestClass]
public class ScheduleJobAuditFieldTests : IDisposable
{
    private EtlTestDataContext _dc = null!;
    private IServiceProvider _sp = null!;
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"AuditFields_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        _dc = new EtlTestDataContext($"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        _dc.Database.EnsureCreated();
        var wtm = MockWtmContext.CreateWtmContext(_dc);

        var services = new ServiceCollection();
        services.AddSingleton(wtm);
        _sp = services.BuildServiceProvider();
    }

    private EtlJobDefinition AddJob(EtlJobStatus status = EtlJobStatus.Enabled, string name = "AuditJob")
    {
        var job = new EtlJobDefinition
        {
            Name = name,
            // Quartz requires 6-field cron: sec min hour day month weekday
            CronExpression = "0 0 0 * * ?",
            SourceCsKey = "test",
            SourceDbType = DBTypeEnum.SqlServer,
            WatermarkType = EtlWatermarkType.FullLoad,
            Status = status,
            UpdateTime = null,   // explicit null — the field we are proving gets stamped
        };
        _dc.Set<EtlJobDefinition>().Add(job);
        _dc.SaveChanges();
        // Reset UpdateTime to null so the row enters the test clean
        // (SaveChanges calls ApplyAuditFields which would set it for the Add case).
        // We patch it back to null directly to simulate a pre-existing row with no UpdateTime.
        _dc.Database.ExecuteSqlRaw(
            "UPDATE EtlJobDefinitions SET UpdateTime = NULL WHERE ID = {0}", job.ID);
        _dc.ChangeTracker.Clear();
        return job;
    }

    /// <summary>
    /// Build a minimal IScheduler mock that satisfies the two calls made by
    /// ScheduleJobAsync: CheckExists (returns false so DeleteJob is skipped)
    /// and ScheduleJob (returns an arbitrary DateTimeOffset).
    /// </summary>
    private static IScheduler BuildMockScheduler()
    {
        var mock = new Mock<IScheduler>();

        mock.Setup(s => s.CheckExists(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        mock.Setup(s => s.ScheduleJob(
                It.IsAny<IJobDetail>(),
                It.IsAny<ITrigger>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow);

        return mock.Object;
    }

    // ─── Core regression: UpdateTime IS stamped after ScheduleJobAsync ───

    /// <summary>
    /// After LoadJobsFromDbAsync (which calls ScheduleJobAsync internally),
    /// the job's UpdateTime in the DB must be non-null — matching the behaviour
    /// of the old Update + SaveChangesAsync path that called ApplyAuditFields.
    /// </summary>
    [TestMethod]
    public async Task ScheduleJobAsync_stamps_UpdateTime_via_ExecuteUpdateAsync()
    {
        var job = AddJob(EtlJobStatus.Enabled);

        // Verify baseline: UpdateTime is null before the call
        var before = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);
        Assert.IsNull(before!.UpdateTime, "Precondition: UpdateTime should start as null");

        var svc = new EtlSchedulerService(_sp);
        svc.SetScheduler(BuildMockScheduler());

        var beforeCall = DateTime.Now;
        await svc.LoadJobsFromDbAsync();

        // Re-fetch — the ExecuteUpdateAsync writes directly to the DB, bypassing
        // the tracked entity, so we must clear the local cache first.
        _dc.ChangeTracker.Clear();
        var after = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);

        Assert.IsNotNull(after!.UpdateTime,
            "UpdateTime must be stamped by ScheduleJobAsync's ExecuteUpdateAsync " +
            "(regression: old code stamped it via ApplyAuditFields, new code must replicate this)");

        // UpdateTime should be >= the local time just before the call
        Assert.IsTrue(after.UpdateTime >= beforeCall,
            $"UpdateTime {after.UpdateTime:o} should be >= {beforeCall:o} (local time just before call)");
    }

    /// <summary>
    /// With a custom TimeProvider the stamped UpdateTime matches exactly the
    /// local-time value from TimeProvider.GetLocalNow().DateTime — same as
    /// ApplyAuditFields uses.
    /// </summary>
    [TestMethod]
    public async Task ScheduleJobAsync_stamps_UpdateTime_matching_TimeProvider_GetLocalNow()
    {
        var job = AddJob(EtlJobStatus.Enabled);

        // Use a fixed FakeTimeProvider so we can assert the exact value
        var fakeNow = new DateTimeOffset(2025, 6, 7, 12, 0, 0, TimeSpan.FromHours(8));
        var fakeTimeProvider = new FakeTimeProvider(fakeNow);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fakeTimeProvider);
        services.AddSingleton(MockWtmContext.CreateWtmContext(_dc));
        var sp = services.BuildServiceProvider();

        var svc = new EtlSchedulerService(sp);
        svc.SetScheduler(BuildMockScheduler());

        await svc.LoadJobsFromDbAsync();

        _dc.ChangeTracker.Clear();
        var after = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);

        // ApplyAuditFields stamps TimeProvider.GetLocalNow().DateTime (local time)
        var expectedUpdateTime = fakeTimeProvider.GetLocalNow().DateTime;
        Assert.AreEqual(expectedUpdateTime, after!.UpdateTime,
            "UpdateTime must equal TimeProvider.GetLocalNow().DateTime — " +
            "exactly matching what ApplyAuditFields would have written");
    }

    /// <summary>
    /// A Failed job (also eligible for scheduling via LoadJobsFromDbAsync)
    /// should also receive an UpdateTime stamp.
    /// </summary>
    [TestMethod]
    public async Task ScheduleJobAsync_stamps_UpdateTime_for_Failed_job()
    {
        var job = AddJob(EtlJobStatus.Failed, "FailedJob");

        var svc = new EtlSchedulerService(_sp);
        svc.SetScheduler(BuildMockScheduler());

        await svc.LoadJobsFromDbAsync();

        _dc.ChangeTracker.Clear();
        var after = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);

        Assert.IsNotNull(after!.UpdateTime,
            "Failed jobs scheduled via LoadJobsFromDbAsync must also have UpdateTime stamped");
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

/// <summary>
/// Minimal TimeProvider implementation for testing — returns a fixed point in time.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _fixedNow;

    public FakeTimeProvider(DateTimeOffset fixedNow)
    {
        _fixedNow = fixedNow;
    }

    public override DateTimeOffset GetUtcNow() => _fixedNow.ToUniversalTime();

    // GetLocalNow() uses LocalTimeZone by default — we override GetUtcNow() so
    // base.GetLocalNow() returns _fixedNow converted to local time.
    // EtlSchedulerService reads GetLocalNow().DateTime, matching ApplyAuditFields.
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
}
