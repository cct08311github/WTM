#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;

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
    public virtual async Task TriggerNowAsync(Guid jobId)
    {
        EnsureScheduler();

        var jobKey = GetJobKey(jobId);
        if (!await _scheduler!.CheckExists(jobKey))
        {
            // Job 可能是 Disabled 狀態，臨時建立一次性觸發
            using var scope = _sp.CreateScope();
            var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
            var jobDef = await wtm.DC.Set<EtlJobDefinition>().FindAsync(jobId);
            if (jobDef == null) return;
            await ScheduleJobAsync(jobDef);
        }

        var data = new JobDataMap();
        data.Put("EtlTriggerType", EtlRunTrigger.Manual.ToString());
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

    /// <summary>從 RunLog snapshot 重跑</summary>
    public virtual async Task RerunFromSnapshotAsync(Guid runLogId)
    {
        using var scope = _sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
        var runLog = await wtm.DC.Set<EtlRunLog>().FindAsync(runLogId);
        if (runLog == null) return;

        var jobDef = await wtm.DC.Set<EtlJobDefinition>().FindAsync(runLog.JobId);
        if (jobDef == null) return;

        // 暫時設回 watermark
        jobDef.LastWatermarkValue = runLog.WatermarkSnapshot;
        wtm.DC.Set<EtlJobDefinition>().Update(jobDef);
        await wtm.DC.SaveChangesAsync();

        await TriggerNowAsync(runLog.JobId);
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

        // 更新 NextFireAt
        jobDef.NextFireAt = trigger.GetNextFireTimeUtc()?.UtcDateTime;
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
