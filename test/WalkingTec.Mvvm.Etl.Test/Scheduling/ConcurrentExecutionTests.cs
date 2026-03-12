#nullable enable
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Quartz;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;

namespace WalkingTec.Mvvm.Etl.Test.Scheduling;

/// <summary>
/// 並行觸發保護測試 — 驗證 Quartz [DisallowConcurrentExecution] attribute
/// 和 ShouldExecute 的 Running 狀態檢查雙重保護機制。
/// </summary>
[TestClass]
public class ConcurrentExecutionTests
{
    [TestMethod]
    public void EtlQuartzJob_has_DisallowConcurrentExecution_attribute()
    {
        var attr = typeof(EtlQuartzJob)
            .GetCustomAttribute<DisallowConcurrentExecutionAttribute>();

        Assert.IsNotNull(attr,
            "EtlQuartzJob must have [DisallowConcurrentExecution] to prevent parallel runs of the same job");
    }

    [TestMethod]
    public void ShouldExecute_rejects_Running_status()
    {
        var job = new EtlJobDefinition
        {
            Name = "concurrent-test",
            CronExpression = "0 0 * * *",
            SourceCsKey = "test",
            SourceDbType = Core.DBTypeEnum.SqlServer,
            WatermarkType = EtlWatermarkType.FullLoad,
            Status = EtlJobStatus.Running,
            SkipCount = 0
        };

        Assert.IsFalse(EtlSchedulerService.ShouldExecute(job),
            "Running jobs must not be re-executed (application-level protection)");
    }

    [TestMethod]
    public void ShouldExecute_accepts_Enabled_after_previous_run_completes()
    {
        var job = new EtlJobDefinition
        {
            Name = "concurrent-test",
            CronExpression = "0 0 * * *",
            SourceCsKey = "test",
            SourceDbType = Core.DBTypeEnum.SqlServer,
            WatermarkType = EtlWatermarkType.FullLoad,
            Status = EtlJobStatus.Enabled,
            SkipCount = 0,
            LastRunAt = DateTime.UtcNow.AddMinutes(-5)
        };

        Assert.IsTrue(EtlSchedulerService.ShouldExecute(job),
            "Enabled jobs with completed previous runs should execute");
    }
}
