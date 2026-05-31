#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Scheduling;

/// <summary>
/// ETL 排程管理服務 — 封裝 Quartz IScheduler 操作，提供 UI 層呼叫的方法。
/// 使用 DB state + RAMJobStore 混合方案。
/// </summary>
public class EtlSchedulerService
{
    private IScheduler? _scheduler;
    private readonly IServiceProvider _sp;

    public EtlSchedulerService(IServiceProvider sp)
    {
        _sp = sp;
    }

    /// <summary>設定 Quartz Scheduler 參考（由 EtlHostedService 在啟動時注入）</summary>
    public void SetScheduler(IScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    /// <summary>啟動時重置幽靈 Running Job（上次程序崩潰遺留）為 Failed 狀態</summary>
    public async Task ResetGhostRunningJobsAsync()
    {
        using var scope = _sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();

        var ghostJobs = await wtm.DC.Set<EtlJobDefinition>()
            .Where(j => j.Status == EtlJobStatus.Running)
            .ToListAsync();

        if (ghostJobs.Count == 0) return;

        var now = (_sp.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        foreach (var job in ghostJobs)
        {
            job.Status = EtlJobStatus.Failed;
            job.LastError = "Process restarted while job was running — previous execution incomplete.";
            wtm.DC.Set<EtlJobDefinition>().Update(job);

            wtm.DC.Set<EtlRunLog>().Add(new EtlRunLog
            {
                JobId      = job.ID,
                Trigger    = EtlRunTrigger.Scheduled,
                Result     = EtlRunResult.Failed,
                ErrorMessage = "Process restarted while job was running — previous execution incomplete.",
                StartedAt  = now,
                FinishedAt = now,
            });
        }

        await wtm.DC.SaveChangesAsync();
    }

    /// <summary>啟動時從 DB 載入所有 Enabled 的 ETL Job 排程</summary>
    public async Task LoadJobsFromDbAsync()
    {
        if (_scheduler == null) return;

        using var scope = _sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
        var jobs = await wtm.DC.Set<EtlJobDefinition>()
            .Where(j => j.Status == EtlJobStatus.Enabled || j.Status == EtlJobStatus.Failed)
            .ToListAsync();

        foreach (var job in jobs)
        {
            await ScheduleJobAsync(job);
        }
    }

    /// <summary>▶ 立即執行（Trigger = Manual）</summary>
    /// <param name="jobId">Job ID。</param>
    /// <param name="watermarkOverride">
    /// 選填：傳入此值時，執行的 watermark 起始值將使用此覆蓋值而非 DB 中的
    /// <c>LastWatermarkValue</c>（defense-in-depth 防 TOCTOU 競態）。
    /// 正常手動觸發請保持 <c>null</c>。
    /// </param>
    public virtual async Task TriggerNowAsync(Guid jobId, string? watermarkOverride = null)
    {
        EnsureScheduler();

        var jobKey = GetJobKey(jobId);
        if (!await _scheduler!.CheckExists(jobKey))
        {
            // Job 可能是 Disabled 狀態，臨時建立一次性觸發
            using var scope = _sp.CreateScope();
            var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
            var jobDef = await wtm.DC.Set<EtlJobDefinition>().FindAsync(jobId);
            if (jobDef == null)
                throw new InvalidOperationException($"Job {jobId} not found.");
            await ScheduleJobAsync(jobDef);
        }

        var data = new JobDataMap();
        data.Put("EtlTriggerType", EtlRunTrigger.Manual.ToString());
        if (watermarkOverride != null)
            data.Put("EtlWatermarkOverride", watermarkOverride);
        await _scheduler!.TriggerJob(jobKey, data);
    }

    /// <summary>⏸ 暫停</summary>
    public virtual async Task PauseAsync(Guid jobId)
    {
        EnsureScheduler();
        await _scheduler!.PauseTrigger(GetTriggerKey(jobId));
        await UpdateStatusAsync(jobId, EtlJobStatus.Paused);
    }

    /// <summary>▶ 恢復</summary>
    public virtual async Task ResumeAsync(Guid jobId)
    {
        EnsureScheduler();
        await _scheduler!.ResumeTrigger(GetTriggerKey(jobId));
        await UpdateStatusAsync(jobId, EtlJobStatus.Enabled);
    }

    /// <summary>✏️ 修改排程</summary>
    public virtual async Task RescheduleAsync(Guid jobId, string newCron)
    {
        EnsureScheduler();

        var triggerKey = GetTriggerKey(jobId);
        var newTrigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .WithCronSchedule(newCron)
            .Build();

        await _scheduler!.RescheduleJob(triggerKey, newTrigger);

        // 同步更新 DB
        using var scope = _sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
        var jobDef = await wtm.DC.Set<EtlJobDefinition>().FindAsync(jobId);
        if (jobDef != null)
        {
            jobDef.CronExpression = newCron;
            jobDef.NextFireAt = newTrigger.GetNextFireTimeUtc()?.UtcDateTime;
            wtm.DC.Set<EtlJobDefinition>().Update(jobDef);
            await wtm.DC.SaveChangesAsync();
        }
    }

    /// <summary>⛔ 中止執行中的 Job（透過 CancellationToken）</summary>
    public virtual async Task AbortAsync(Guid jobId)
    {
        EnsureScheduler();
        var result = await _scheduler!.Interrupt(GetJobKey(jobId));
        if (!result)
            throw new InvalidOperationException($"Job {jobId} is not currently running.");
    }

    /// <summary>⏭ 跳過下次</summary>
    public virtual async Task SkipNextAsync(Guid jobId)
    {
        using var scope = _sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
        var jobDef = await wtm.DC.Set<EtlJobDefinition>().FindAsync(jobId);
        if (jobDef != null)
        {
            jobDef.SkipCount++;
            wtm.DC.Set<EtlJobDefinition>().Update(jobDef);
            await wtm.DC.SaveChangesAsync();
        }
    }

    /// <summary>🔄 啟用</summary>
    public virtual async Task EnableAsync(Guid jobId)
    {
        using var scope = _sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
        var jobDef = await wtm.DC.Set<EtlJobDefinition>().FindAsync(jobId);
        if (jobDef == null) return;

        jobDef.Status = EtlJobStatus.Enabled;
        wtm.DC.Set<EtlJobDefinition>().Update(jobDef);
        await wtm.DC.SaveChangesAsync();

        EnsureScheduler();
        await ScheduleJobAsync(jobDef);
    }

    /// <summary>🔄 停用</summary>
    public virtual async Task DisableAsync(Guid jobId)
    {
        EnsureScheduler();

        var jobKey = GetJobKey(jobId);
        if (await _scheduler!.CheckExists(jobKey))
            await _scheduler.DeleteJob(jobKey);

        await UpdateStatusAsync(jobId, EtlJobStatus.Disabled);
    }

    /// <summary>
    /// Dry-run (#834): execute the pipeline in preview mode — Extract + Transform
    /// only, skip EnsureStaging / Truncate / BulkLoad / Merge / watermark commit.
    /// Returns the first-batch preview + validation warnings without writing any
    /// row to the target DB. Bypasses Quartz entirely and does not write any
    /// <see cref="EtlRunLog"/> record (operator can preview without polluting
    /// the audit trail).
    /// </summary>
    /// <param name="jobId">Job definition ID to preview.</param>
    /// <param name="sampleSize">Max preview rows to return (default 10).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public virtual async Task<EtlExecutionResult> DryRunAsync(
        Guid jobId,
        int sampleSize = 10,
        CancellationToken cancellationToken = default)
    {
        using var scope = _sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();

        var jobDef = await wtm.DC.Set<EtlJobDefinition>().FindAsync(new object[] { jobId }, cancellationToken);
        if (jobDef == null)
            throw new InvalidOperationException($"Job {jobId} not found.");

        var sourceCs = wtm.ConfigInfo.Connections?
            .FirstOrDefault(c => c.Key == jobDef.SourceCsKey);
        if (sourceCs == null)
            throw new InvalidOperationException($"Connection key '{jobDef.SourceCsKey}' not found in Configs.Connections");

        var targetCsEntry = wtm.ConfigInfo.Connections?
            .FirstOrDefault(c => c.Key == jobDef.TargetCsKey);
        if (targetCsEntry == null)
            throw new InvalidOperationException($"Target connection key '{jobDef.TargetCsKey}' not found in Configs.Connections");

        using var source = EtlSourceFactory.CreateSource(jobDef.SourceDbType);
        var loader = EtlSourceFactory.CreateLoader(jobDef.TargetDbType);

        var watermarkValue = jobDef.LastWatermarkValue ?? jobDef.InitialWatermarkValue;
        var watermark = new WatermarkStrategy(
            jobDef.WatermarkType, jobDef.WatermarkColumn, watermarkValue, jobDef.WatermarkTimeZone);

        var config = new EtlPipelineConfig
        {
            JobId = jobId,
            JobName = jobDef.Name,
            SourceConnectionString = sourceCs.Value ?? "",
            TargetConnectionString = targetCsEntry.Value ?? "",
            QueryTemplate = jobDef.QueryTemplate,
            TargetTableName = jobDef.TargetTableName,
            MergeKeyColumn = jobDef.MergeKeyColumn,
            BatchSize = 1000, // dry-run caps batch size — preview, not full extract
            StagingTable = new StagingTableSpec($"STG_{jobDef.TargetTableName}_dryrun"),
            IsDryRun = true,
            DryRunPreviewSampleSize = Math.Max(0, sampleSize),
        };

        var executor = new EtlPipelineExecutor(source, loader);
        return await executor.ExecuteAsync(config, watermark, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>從 RunLog snapshot 重跑</summary>
    /// <exception cref="InvalidOperationException">
    /// 若 Job 目前正在執行（Status == Running），拒絕重跑以避免 TOCTOU 競態
    /// 導致重跑起點被正在執行的 job finally 寫覆。
    /// </exception>
    public virtual async Task RerunFromSnapshotAsync(Guid runLogId)
    {
        using var scope = _sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
        var runLog = await wtm.DC.Set<EtlRunLog>().FindAsync(runLogId);
        if (runLog == null) return;

        var jobDef = await wtm.DC.Set<EtlJobDefinition>().FindAsync(runLog.JobId);
        if (jobDef == null) return;

        // Guard 1: 拒絕在 Job 執行中觸發重跑。
        // 若允許，Quartz [DisallowConcurrentExecution] 會將新觸發排隊；
        // 正在執行的 job finally 寫回 result.NewWatermarkValue（"W2"），
        // 覆蓋掉剛寫進 DB 的 WatermarkSnapshot（"W0"），
        // 使排隊的重跑從 W2 開始而非 W0，靜默跳過 [W0, W2) 的資料。
        if (jobDef.Status == EtlJobStatus.Running)
            throw new InvalidOperationException(
                $"Cannot rerun job '{jobDef.Name}' ({jobDef.ID}) while it is running. " +
                "Wait for the current execution to finish, then retry.");

        // 設回 watermark 至快照值（設定合理 DB 基準；Guard 2 透過 JobDataMap 提供更強保護）
        jobDef.LastWatermarkValue = runLog.WatermarkSnapshot;
        wtm.DC.Set<EtlJobDefinition>().Update(jobDef);
        await wtm.DC.SaveChangesAsync();

        // Guard 2: 透過 JobDataMap 傳遞快照 watermark 作為覆蓋值（防 TOCTOU 殘餘競態）。
        // EtlQuartzJob.Execute 會優先使用此值而非重新讀取 DB 中的 LastWatermarkValue，
        // 確保即使 DB 值被再次修改，重跑仍從正確的 W0 開始。
        await TriggerNowAsync(runLog.JobId, runLog.WatermarkSnapshot);
    }

    /// <summary>判斷 Job 是否應該執行</summary>
    public static bool ShouldExecute(EtlJobDefinition job)
    {
        if (job.Status == EtlJobStatus.Disabled || job.Status == EtlJobStatus.Paused)
            return false;
        if (job.Status == EtlJobStatus.Running)
            return false;
        if (job.SkipCount > 0)
            return false;
        return true;
    }

    /// <summary>判斷本次失敗是否應觸發自動重試</summary>
    /// <param name="job">Job 定義（讀取 RetryCount）</param>
    /// <param name="currentAttempt">本次為第幾次嘗試（0-based）</param>
    public static bool ShouldRetry(EtlJobDefinition job, int currentAttempt)
        => job.RetryCount > 0 && currentAttempt < job.RetryCount;

    // ─── 內部輔助 ───

    private async Task ScheduleJobAsync(EtlJobDefinition jobDef)
    {
        var jobKey = GetJobKey(jobDef.ID);
        var triggerKey = GetTriggerKey(jobDef.ID);

        // 如果已存在，先刪除
        if (await _scheduler!.CheckExists(jobKey))
            await _scheduler.DeleteJob(jobKey);

        var jobDataMap = new JobDataMap();
        jobDataMap.Put("Sp", _sp);
        jobDataMap.Put("EtlJobDefinitionId", jobDef.ID.ToString());

        var job = JobBuilder.Create<EtlQuartzJob>()
            .WithIdentity(jobKey)
            .UsingJobData(jobDataMap)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .WithCronSchedule(jobDef.CronExpression)
            .Build();

        await _scheduler.ScheduleJob(job, trigger);

        // 更新 NextFireAt 並持久化到 DB
        jobDef.NextFireAt = trigger.GetNextFireTimeUtc()?.UtcDateTime;

        using var scope = _sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
        var tracked = await wtm.DC.Set<EtlJobDefinition>().FindAsync(jobDef.ID);
        if (tracked != null)
        {
            tracked.NextFireAt = jobDef.NextFireAt;
            wtm.DC.Set<EtlJobDefinition>().Update(tracked);
            await wtm.DC.SaveChangesAsync();
        }
    }

    private async Task UpdateStatusAsync(Guid jobId, EtlJobStatus status)
    {
        using var scope = _sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
        var jobDef = await wtm.DC.Set<EtlJobDefinition>().FindAsync(jobId);
        if (jobDef != null)
        {
            jobDef.Status = status;
            wtm.DC.Set<EtlJobDefinition>().Update(jobDef);
            await wtm.DC.SaveChangesAsync();
        }
    }

    private void EnsureScheduler()
    {
        if (_scheduler == null)
            throw new InvalidOperationException("ETL Scheduler not initialized. Call SetScheduler() first.");
    }

    private static JobKey GetJobKey(Guid jobId) => new($"etl-{jobId}", "etl-group");
    private static TriggerKey GetTriggerKey(Guid jobId) => new($"etl-trigger-{jobId}", "etl-group");
}
