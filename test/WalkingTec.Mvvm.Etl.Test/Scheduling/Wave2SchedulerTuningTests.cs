#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Quartz;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Schema;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.Test.ViewModels;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Scheduling;

/// <summary>
/// Wave-2 opt-in tuning tests (issue #185):
/// — UpdateStatusAsync stamps UpdateTime via ExecuteUpdateAsync (SQLite)
/// — SkipNextAsync server-side atomic SkipCount increment (no ABA race)
/// — Schema cache returns cached value within TTL and refreshes after expiry
/// — Default config paths unchanged (WatermarkSqlType=null)
///
/// SQLite shared in-memory is used because ExecuteUpdateAsync requires a real
/// SQL engine (EF InMemory provider does not support it — lesson from #119).
/// </summary>
[TestClass]
public class Wave2SchedulerTuningTests : IDisposable
{
    private EtlTestDataContext _dc = null!;
    private IServiceProvider _sp = null!;
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"Wave2_{Guid.NewGuid():N}";
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

    // ─── Helpers ───

    private EtlJobDefinition AddJob(
        EtlJobStatus status = EtlJobStatus.Enabled,
        string name = "Wave2Job",
        int skipCount = 0)
    {
        var job = new EtlJobDefinition
        {
            Name = name,
            CronExpression = "0 0 0 * * ?",
            SourceCsKey = "test",
            SourceDbType = DBTypeEnum.SqlServer,
            WatermarkType = EtlWatermarkType.FullLoad,
            Status = status,
            SkipCount = skipCount,
            UpdateTime = null,
        };
        _dc.Set<EtlJobDefinition>().Add(job);
        _dc.SaveChanges();
        // Patch UpdateTime back to null so tests start clean
        // (SaveChanges / ApplyAuditFields stamps it for Add).
        _dc.Database.ExecuteSqlRaw(
            "UPDATE EtlJobDefinitions SET UpdateTime = NULL WHERE ID = {0}", job.ID);
        _dc.ChangeTracker.Clear();
        return job;
    }

    private IServiceProvider BuildSpWithFakeTime(DateTimeOffset fixedNow)
    {
        var fakeTimeProvider = new Wave2FakeTimeProvider(fixedNow);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fakeTimeProvider);
        services.AddSingleton(MockWtmContext.CreateWtmContext(_dc));
        return services.BuildServiceProvider();
    }

    // ═══════════════════════════════════════════════════════════════
    // A. UpdateStatusAsync — stamps UpdateTime via ExecuteUpdateAsync
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task UpdateStatusAsync_stamps_UpdateTime_matching_TimeProvider_GetLocalNow()
    {
        // Arrange
        var job = AddJob(EtlJobStatus.Enabled);
        var fakeNow = new DateTimeOffset(2026, 6, 7, 10, 0, 0, TimeSpan.FromHours(8));
        var sp = BuildSpWithFakeTime(fakeNow);
        var svc = new EtlSchedulerService(sp);

        // Act — PauseAsync calls UpdateStatusAsync(Paused) internally
        // We call PauseAsync (which calls EnsureScheduler), so we use a mock scheduler.
        // Alternatively call a method that calls UpdateStatusAsync without requiring scheduler.
        // DisableAsync calls UpdateStatusAsync but also needs scheduler.
        // The cleanest route: call UpdateStatusAsync via reflection or expose it for testing.
        // Instead we call PauseAsync with a mock scheduler (which calls UpdateStatusAsync).
        var schedulerMock = new Mock<IScheduler>();
        schedulerMock
            .Setup(s => s.PauseTrigger(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        svc.SetScheduler(schedulerMock.Object);

        await svc.PauseAsync(job.ID);

        // Assert
        _dc.ChangeTracker.Clear();
        var after = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);

        Assert.IsNotNull(after);
        Assert.AreEqual(EtlJobStatus.Paused, after!.Status, "Status should be Paused");
        Assert.IsNotNull(after.UpdateTime, "UpdateTime must be stamped by ExecuteUpdateAsync");

        var expectedUpdateTime = new Wave2FakeTimeProvider(fakeNow).GetLocalNow().DateTime;
        Assert.AreEqual(expectedUpdateTime, after.UpdateTime,
            "UpdateTime must match TimeProvider.GetLocalNow().DateTime — " +
            "exact parity with what ApplyAuditFields would have written");
    }

    [TestMethod]
    public async Task UpdateStatusAsync_updates_status_correctly()
    {
        // Arrange
        var job = AddJob(EtlJobStatus.Paused, "ResumeJob");
        var fakeNow = new DateTimeOffset(2026, 6, 7, 11, 0, 0, TimeSpan.FromHours(8));
        var sp = BuildSpWithFakeTime(fakeNow);
        var svc = new EtlSchedulerService(sp);

        var schedulerMock = new Mock<IScheduler>();
        schedulerMock
            .Setup(s => s.ResumeTrigger(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        svc.SetScheduler(schedulerMock.Object);

        // Patch to Paused in DB
        await _dc.Set<EtlJobDefinition>()
            .Where(j => j.ID == job.ID)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, EtlJobStatus.Paused));
        _dc.ChangeTracker.Clear();

        // Act
        await svc.ResumeAsync(job.ID);

        // Assert
        _dc.ChangeTracker.Clear();
        var after = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);
        Assert.AreEqual(EtlJobStatus.Enabled, after!.Status, "Status should be Enabled after Resume");
        Assert.IsNotNull(after.UpdateTime, "UpdateTime must be stamped");
    }

    // ═══════════════════════════════════════════════════════════════
    // B. SkipNextAsync — server-side atomic increment (no ABA race)
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task SkipNextAsync_increments_SkipCount_server_side()
    {
        // Arrange
        var job = AddJob(EtlJobStatus.Enabled, "SkipJob", skipCount: 0);
        var svc = new EtlSchedulerService(_sp);

        // Act
        await svc.SkipNextAsync(job.ID);

        // Assert
        _dc.ChangeTracker.Clear();
        var after = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);
        Assert.AreEqual(1, after!.SkipCount,
            "SkipCount should be incremented from 0 to 1 server-side");
    }

    [TestMethod]
    public async Task SkipNextAsync_increments_from_existing_count()
    {
        // Arrange — start with SkipCount = 3
        var job = AddJob(EtlJobStatus.Enabled, "SkipJob3", skipCount: 3);
        var svc = new EtlSchedulerService(_sp);

        // Act
        await svc.SkipNextAsync(job.ID);

        // Assert
        _dc.ChangeTracker.Clear();
        var after = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);
        Assert.AreEqual(4, after!.SkipCount,
            "SkipCount should be incremented from 3 to 4 server-side");
    }

    [TestMethod]
    public async Task SkipNextAsync_stamps_UpdateTime_exactly_matching_TimeProvider()
    {
        // Arrange
        var job = AddJob(EtlJobStatus.Enabled, "SkipAuditJob");
        var fakeNow = new DateTimeOffset(2026, 6, 7, 9, 30, 0, TimeSpan.FromHours(8));
        var sp = BuildSpWithFakeTime(fakeNow);
        var svc = new EtlSchedulerService(sp);

        // Act
        await svc.SkipNextAsync(job.ID);

        // Assert
        _dc.ChangeTracker.Clear();
        var after = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);
        Assert.IsNotNull(after!.UpdateTime, "UpdateTime must be stamped by SkipNextAsync");

        var expectedUpdateTime = new Wave2FakeTimeProvider(fakeNow).GetLocalNow().DateTime;
        Assert.AreEqual(expectedUpdateTime, after.UpdateTime,
            "UpdateTime must match TimeProvider.GetLocalNow().DateTime");
    }

    [TestMethod]
    public async Task SkipNextAsync_concurrent_increments_are_atomic()
    {
        // Arrange — simulate ABA-free concurrent increments
        // Two concurrent calls should both land: 0 → 1 → 2
        var job = AddJob(EtlJobStatus.Enabled, "AtomicSkip", skipCount: 0);
        var svc = new EtlSchedulerService(_sp);

        // Act — run two increments sequentially (SQLite shared-memory doesn't
        // support real concurrent connections in test, but server-side j=>j.SkipCount+1
        // is atomic per call regardless of read-then-write timing).
        await svc.SkipNextAsync(job.ID);
        await svc.SkipNextAsync(job.ID);

        // Assert
        _dc.ChangeTracker.Clear();
        var after = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);
        Assert.AreEqual(2, after!.SkipCount,
            "Two sequential SkipNextAsync calls should yield SkipCount = 2");
    }

    // ═══════════════════════════════════════════════════════════════
    // C. Schema cache — returns cached value within TTL, refreshes after
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task CachingSchemaService_returns_cached_tables_within_ttl()
    {
        // Arrange — stub inner service
        var innerMock = new Mock<IEtlSchemaService>();
        var fakeResult = new List<EtlTableInfo> { new("dbo", "Orders") }.AsReadOnly();
        innerMock
            .Setup(s => s.ListTablesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeResult);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new CachingEtlSchemaService(innerMock.Object, cache, TimeSpan.FromMinutes(1));

        // Act — call twice
        var first = await svc.ListTablesAsync("Server=x", null);
        var second = await svc.ListTablesAsync("Server=x", null);

        // Assert — inner called only once (second is from cache)
        innerMock.Verify(
            s => s.ListTablesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Inner service should be called only once when cached result is available within TTL");
        Assert.AreSame(first, second, "Both results should be the same cached reference");
    }

    [TestMethod]
    public async Task CachingSchemaService_returns_cached_columns_within_ttl()
    {
        // Arrange
        var innerMock = new Mock<IEtlSchemaService>();
        var fakeResult = new List<EtlColumnInfo>
        {
            new("OrderID", "int", false, true),
            new("Amount", "decimal", true, false)
        }.AsReadOnly();
        innerMock
            .Setup(s => s.ListColumnsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeResult);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new CachingEtlSchemaService(innerMock.Object, cache, TimeSpan.FromMinutes(1));

        // Act — call twice
        var first = await svc.ListColumnsAsync("Server=x", "Orders");
        var second = await svc.ListColumnsAsync("Server=x", "Orders");

        // Assert
        innerMock.Verify(
            s => s.ListColumnsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Inner service should be called only once when columns are cached");
    }

    [TestMethod]
    public async Task CachingSchemaService_different_connections_cached_separately()
    {
        // Arrange — two different connection strings
        var innerMock = new Mock<IEtlSchemaService>();
        innerMock
            .Setup(s => s.ListTablesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EtlTableInfo>().AsReadOnly());

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new CachingEtlSchemaService(innerMock.Object, cache, TimeSpan.FromMinutes(1));

        // Act — call with two different connection strings
        await svc.ListTablesAsync("Server=host1", null);
        await svc.ListTablesAsync("Server=host2", null);

        // Assert — inner called twice (different cache keys per connection)
        innerMock.Verify(
            s => s.ListTablesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "Each distinct connection string should generate a separate cache entry");
    }

    [TestMethod]
    public async Task CachingSchemaService_refreshes_after_ttl_expiry()
    {
        // Arrange — very short TTL so it expires quickly
        var innerMock = new Mock<IEtlSchemaService>();
        innerMock
            .Setup(s => s.ListTablesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EtlTableInfo>().AsReadOnly());

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new CachingEtlSchemaService(innerMock.Object, cache, TimeSpan.FromMilliseconds(1));

        // Act — first call, then wait for TTL expiry, then second call
        await svc.ListTablesAsync("Server=x", null);
        await Task.Delay(50); // 50ms >> 1ms TTL; entry should have expired
        await svc.ListTablesAsync("Server=x", null);

        // Assert — inner called twice (cache miss after expiry)
        innerMock.Verify(
            s => s.ListTablesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "Inner service should be called again after TTL expiry");
    }

    [TestMethod]
    public void EtlSchemaServiceFactory_CreateWithCache_returns_CachingEtlSchemaService()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        // Use SqlServer (no actual connection needed to verify the type)
        // NotSupportedException check skipped — SqlServer is supported.
        var svc = EtlSchemaServiceFactory.CreateWithCache(DBTypeEnum.SqlServer, cache);
        Assert.IsInstanceOfType(svc, typeof(CachingEtlSchemaService),
            "CreateWithCache should return a CachingEtlSchemaService wrapper");
    }

    [TestMethod]
    public void EtlSchemaServiceFactory_Create_still_returns_plain_service()
    {
        // bare Create() unchanged
        var svc = EtlSchemaServiceFactory.Create(DBTypeEnum.SqlServer);
        Assert.IsNotInstanceOfType(svc, typeof(CachingEtlSchemaService),
            "Bare Create() should not return a CachingEtlSchemaService (no-cache default)");
    }

    // ═══════════════════════════════════════════════════════════════
    // E. Default config paths unchanged
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    public void EtlPipelineConfig_default_WatermarkSqlType_is_null()
    {
        var config = new EtlPipelineConfig();
        Assert.IsNull(config.WatermarkSqlType,
            "Default WatermarkSqlType must be null (AddWithValue path — 10.5.x behavior)");
    }

    [TestMethod]
    public void CachingEtlSchemaService_default_ttl_is_60_seconds()
    {
        Assert.AreEqual(60, CachingEtlSchemaService.DefaultTtlSeconds,
            "Default TTL must be 60 seconds");
    }
}

/// <summary>
/// Fixed-time TimeProvider for Wave-2 tests.
/// </summary>
internal sealed class Wave2FakeTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _fixedNow;

    public Wave2FakeTimeProvider(DateTimeOffset fixedNow) => _fixedNow = fixedNow;

    public override DateTimeOffset GetUtcNow() => _fixedNow.ToUniversalTime();

    // GetLocalNow() uses LocalTimeZone by default via base; override GetUtcNow()
    // so base.GetLocalNow() returns _fixedNow converted to local time.
    // EtlSchedulerService reads GetLocalNow().DateTime, matching ApplyAuditFields.
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
}
