#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Impl;

namespace WalkingTec.Mvvm.Etl.Scheduling;

/// <summary>
/// ETL 排程 HostedService — 在應用啟動後初始化 Quartz Scheduler 並載入 DB 中的 ETL Job。
/// 獨立於 WTM 的 QuartzHostService，專門服務 ETL 排程。
/// </summary>
public class EtlHostedService : IHostedService
{
    private readonly EtlSchedulerService _schedulerService;
    private IScheduler? _scheduler;

    public EtlHostedService(EtlSchedulerService schedulerService)
    {
        _schedulerService = schedulerService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = new StdSchedulerFactory();
        _scheduler = await factory.GetScheduler(cancellationToken);
        _schedulerService.SetScheduler(_scheduler);

        await _scheduler.Start(cancellationToken);

        // 重置幽靈 Running Job（上次程序崩潰遺留），再載入 Enabled/Failed Job
        // DB 可能尚未完成 DataInit（SyncDb seeding），延遲重試避免 race condition
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _schedulerService.ResetGhostRunningJobsAsync();
                await _schedulerService.LoadJobsFromDbAsync();
                return;
            }
            catch (Exception) when (attempt < 3)
            {
                await Task.Delay(2000 * attempt, cancellationToken);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_scheduler != null)
            await _scheduler.Shutdown(cancellationToken);
    }
}
