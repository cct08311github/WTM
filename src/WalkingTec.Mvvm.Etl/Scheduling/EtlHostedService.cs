#nullable enable
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

        // 從 DB 載入所有 Enabled 的 ETL Job
        await _schedulerService.LoadJobsFromDbAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_scheduler != null)
            await _scheduler.Shutdown(cancellationToken);
    }
}
