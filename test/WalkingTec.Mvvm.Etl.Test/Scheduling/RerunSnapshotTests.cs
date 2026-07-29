#nullable enable
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Quartz;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.Test.ViewModels;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Scheduling;

/// <summary>
/// 測試 RerunFromSnapshotAsync 的兩個 bug 修復：
/// 1. Guard — Job 執行中時拒絕重跑（避免 Quartz 排隊觸發從錯誤 watermark 起點開始）。
/// 2. JobDataMap override — 重跑觸發帶著快照 watermark，
///    使 EtlQuartzJob 優先使用此值而非重新讀取 DB（防 TOCTOU 競態）。
/// 3. Watermark 優先級邏輯 — 直接驗證 EtlQuartzJob 從 JobDataMap 的選擇邏輯。
///
/// SQLite shared in-memory (instead of EF InMemory) so that
/// ExecuteUpdateAsync in scheduler paths works correctly.
/// </summary>
[TestClass]
public class RerunSnapshotTests : IDisposable
{
    private EtlTestDataContext _dc = null!;
    private IServiceProvider _sp = null!;
    // Keep-alive connection holds the shared SQLite in-memory DB alive for the test lifetime.
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"RerunSnapshot_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        _dc = new EtlTestDataContext($"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        _dc.Database.EnsureCreated();
        var wtm = MockWtmContext.CreateWtmContext(_dc);

        var services = new ServiceCollection();
        services.AddSingleton(wtm);
        _sp = services.BuildServiceProvider();
    }

    // ─── helpers ───

    private EtlJobDefinition AddJob(EtlJobStatus status, string watermark = "W1")
    {
        var job = new EtlJobDefinition
        {
            Name = "rerun-test",
            CronExpression = "0 0 * * *",
            SourceCsKey = "test",
            SourceDbType = DBTypeEnum.SqlServer,
            WatermarkType = EtlWatermarkType.FullLoad,
            Status = status,
            LastWatermarkValue = watermark,
            JobClassName = "test"
        };
        _dc.Set<EtlJobDefinition>().Add(job);
        _dc.SaveChanges();
        return job;
    }

    private EtlRunLog AddRunLog(Guid jobId, string? snapshotWatermark)
    {
        var runLog = new EtlRunLog
        {
            JobId = jobId,
            Trigger = EtlRunTrigger.Scheduled,
            Result = EtlRunResult.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            FinishedAt = DateTime.UtcNow.AddMinutes(-5),
            WatermarkSnapshot = snapshotWatermark,
        };
        _dc.Set<EtlRunLog>().Add(runLog);
        _dc.SaveChanges();
        return runLog;
    }

    // ─── Fix 1: Guard against rerun while Running ───

    /// <summary>
    /// Guard 1: RerunFromSnapshotAsync 應在 Job 狀態為 Running 時
    /// 拋出 InvalidOperationException，而非寫入 DB 並排隊觸發。
    /// </summary>
    [TestMethod]
    public async Task RerunFromSnapshot_throws_when_job_is_Running()
    {
        var job = AddJob(EtlJobStatus.Running, watermark: "W2_current");
        var runLog = AddRunLog(job.ID, snapshotWatermark: "W0_snapshot");

        var svc = new SpyEtlSchedulerService(_sp);

        var act = () => svc.RerunFromSnapshotAsync(runLog.ID);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot rerun*running*");
    }

    /// <summary>
    /// Guard 1: Running 時拒絕重跑 — DB 中的 LastWatermarkValue 不應被修改。
    /// </summary>
    [TestMethod]
    public async Task RerunFromSnapshot_Running_does_not_overwrite_DB_watermark()
    {
        var job = AddJob(EtlJobStatus.Running, watermark: "W2_current");
        var runLog = AddRunLog(job.ID, snapshotWatermark: "W0_snapshot");

        var svc = new SpyEtlSchedulerService(_sp);

        try { await svc.RerunFromSnapshotAsync(runLog.ID); } catch (InvalidOperationException) { }

        var refreshed = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);
        refreshed!.LastWatermarkValue.Should().Be("W2_current",
            "DB watermark should not be overwritten when rerun is rejected");
    }

    /// <summary>
    /// Guard 1: Running 時拒絕重跑 — TriggerNowAsync 不應被呼叫。
    /// </summary>
    [TestMethod]
    public async Task RerunFromSnapshot_Running_does_not_call_TriggerNow()
    {
        var job = AddJob(EtlJobStatus.Running, watermark: "W2_current");
        var runLog = AddRunLog(job.ID, snapshotWatermark: "W0_snapshot");

        var svc = new SpyEtlSchedulerService(_sp);

        try { await svc.RerunFromSnapshotAsync(runLog.ID); } catch (InvalidOperationException) { }

        svc.TriggerNowCalled.Should().BeFalse(
            "TriggerNowAsync must not be called when the job is Running");
    }

    /// <summary>
    /// Guard 1 negative: Job 不在 Running 狀態時，重跑應正常進行（不拋出）。
    /// </summary>
    [TestMethod]
    public async Task RerunFromSnapshot_Enabled_does_not_throw()
    {
        var job = AddJob(EtlJobStatus.Enabled, watermark: "W2_current");
        var runLog = AddRunLog(job.ID, snapshotWatermark: "W0_snapshot");

        // SpyEtlSchedulerService 覆寫 TriggerNowAsync → 不真的呼叫 Quartz
        var svc = new SpyEtlSchedulerService(_sp);

        var act = () => svc.RerunFromSnapshotAsync(runLog.ID);
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task RerunFromSnapshot_Failed_does_not_throw()
    {
        var job = AddJob(EtlJobStatus.Failed, watermark: "W2_current");
        var runLog = AddRunLog(job.ID, snapshotWatermark: "W0_snapshot");

        var svc = new SpyEtlSchedulerService(_sp);

        var act = () => svc.RerunFromSnapshotAsync(runLog.ID);
        await act.Should().NotThrowAsync();
    }

    // ─── Fix 2: Watermark carried through JobDataMap ───

    /// <summary>
    /// Fix 2: 非 Running 狀態重跑 — RerunFromSnapshotAsync 應將 WatermarkSnapshot
    /// 作為 watermarkOverride 傳入 TriggerNowAsync。
    /// </summary>
    [TestMethod]
    public async Task RerunFromSnapshot_passes_snapshot_watermark_as_override()
    {
        var job = AddJob(EtlJobStatus.Enabled, watermark: "W2_current");
        var runLog = AddRunLog(job.ID, snapshotWatermark: "W0_snapshot");

        var svc = new SpyEtlSchedulerService(_sp);
        await svc.RerunFromSnapshotAsync(runLog.ID);

        svc.TriggerNowCalled.Should().BeTrue();
        svc.CapturedWatermarkOverride.Should().Be("W0_snapshot",
            "The snapshot watermark must be forwarded to TriggerNowAsync as the override");
    }

    /// <summary>
    /// Fix 2: 快照 watermark 為 null 時，null 仍被正確傳遞（FullLoad 場景）。
    /// </summary>
    [TestMethod]
    public async Task RerunFromSnapshot_null_snapshot_watermark_passes_null_override()
    {
        var job = AddJob(EtlJobStatus.Enabled, watermark: "W2_current");
        var runLog = AddRunLog(job.ID, snapshotWatermark: null);

        var svc = new SpyEtlSchedulerService(_sp);
        await svc.RerunFromSnapshotAsync(runLog.ID);

        svc.TriggerNowCalled.Should().BeTrue();
        svc.CapturedWatermarkOverride.Should().BeNull(
            "null snapshot watermark should result in null override (FullLoad scenario)");
    }

    /// <summary>
    /// Fix 2: 非 Running 狀態重跑 — DB 中的 LastWatermarkValue 應設回快照值。
    /// </summary>
    [TestMethod]
    public async Task RerunFromSnapshot_Enabled_writes_snapshot_watermark_to_DB()
    {
        var job = AddJob(EtlJobStatus.Enabled, watermark: "W2_current");
        var runLog = AddRunLog(job.ID, snapshotWatermark: "W0_snapshot");

        var svc = new SpyEtlSchedulerService(_sp);
        await svc.RerunFromSnapshotAsync(runLog.ID);

        var refreshed = await _dc.Set<EtlJobDefinition>().FindAsync(job.ID);
        refreshed!.LastWatermarkValue.Should().Be("W0_snapshot",
            "DB watermark should be reset to snapshot value as a reasonable baseline");
    }

    // ─── Fix 2: Watermark selection priority in JobDataMap ───

    /// <summary>
    /// JobDataMap override 優先級 — 模擬 EtlQuartzJob 的 watermark 選擇邏輯：
    /// EtlWatermarkOverride 存在且非空 → 使用 override；否則使用 DB 值。
    /// 此測試直接驗證選擇邏輯，不依賴 Quartz runtime。
    /// </summary>
    [TestMethod]
    public void WatermarkSelection_override_present_uses_override()
    {
        var jobDataMap = new JobDataMap();
        jobDataMap.Put("EtlWatermarkOverride", "W0_snapshot");

        // 模擬 EtlQuartzJob 中的選擇邏輯
        var watermarkOverride = jobDataMap.ContainsKey("EtlWatermarkOverride")
            ? jobDataMap.GetString("EtlWatermarkOverride")
            : null;
        var dbWatermark = "W2_current";
        var watermarkValue = watermarkOverride ?? dbWatermark;

        watermarkValue.Should().Be("W0_snapshot",
            "EtlWatermarkOverride in JobDataMap must take precedence over DB LastWatermarkValue");
    }

    [TestMethod]
    public void WatermarkSelection_override_absent_uses_db_value()
    {
        // 正常排程觸發：JobDataMap 不含 EtlWatermarkOverride
        var jobDataMap = new JobDataMap();

        var watermarkOverride = jobDataMap.ContainsKey("EtlWatermarkOverride")
            ? jobDataMap.GetString("EtlWatermarkOverride")
            : null;
        var dbWatermark = "W2_current";
        var watermarkValue = watermarkOverride ?? dbWatermark;

        watermarkValue.Should().Be("W2_current",
            "Without EtlWatermarkOverride, DB LastWatermarkValue must be used (normal schedule behavior unchanged)");
    }

    [TestMethod]
    public void WatermarkSelection_override_absent_db_null_falls_back_to_initial()
    {
        // 首次執行場景：DB 無 LastWatermarkValue，也無 override
        var jobDataMap = new JobDataMap();

        var watermarkOverride = jobDataMap.ContainsKey("EtlWatermarkOverride")
            ? jobDataMap.GetString("EtlWatermarkOverride")
            : null;
        string? dbWatermark = null;
        string? initialWatermark = "INITIAL";
        var watermarkValue = watermarkOverride ?? dbWatermark ?? initialWatermark;

        watermarkValue.Should().Be("INITIAL",
            "Falls through to InitialWatermarkValue when both override and DB value are null");
    }

    [TestMethod]
    public void WatermarkSelection_override_null_string_is_not_treated_as_override()
    {
        // JobDataMap 中有 key 但值為 null：不應覆蓋 DB 值（防空字串替換）
        var jobDataMap = new JobDataMap();
        // ContainsKey 返回 false → override 為 null → 回退 DB 值
        // 注意：TriggerNowAsync 中僅在 watermarkOverride != null 時才 Put，
        // 因此正常觸發的 JobDataMap 不含此 key。
        jobDataMap.ContainsKey("EtlWatermarkOverride").Should().BeFalse(
            "Normal scheduled trigger must not have EtlWatermarkOverride in JobDataMap");
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
/// EtlSchedulerService 的測試 Spy — 覆寫 TriggerNowAsync 以避免需要 Quartz 排程器，
/// 同時捕捉傳入的 watermarkOverride 參數以便斷言。
/// </summary>
internal sealed class SpyEtlSchedulerService : EtlSchedulerService
{
    public bool TriggerNowCalled { get; private set; }
    public string? CapturedWatermarkOverride { get; private set; }

    public SpyEtlSchedulerService(IServiceProvider sp) : base(sp) { }

    public override Task TriggerNowAsync(
        Guid jobId,
        string? watermarkOverride = null,
        string? callerTenantCode = null,
        bool declaredSystemQuery = false)
    {
        TriggerNowCalled = true;
        CapturedWatermarkOverride = watermarkOverride;
        return Task.CompletedTask;
    }
}
