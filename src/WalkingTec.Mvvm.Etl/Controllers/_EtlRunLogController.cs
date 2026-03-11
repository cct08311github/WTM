#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.ViewModels;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Mvc;

[ActionDescription("ETL 執行記錄")]
public class _EtlRunLogController : BaseController
{
    private readonly EtlSchedulerService _scheduler;

    public _EtlRunLogController(EtlSchedulerService scheduler)
    {
        _scheduler = scheduler;
    }

    [ActionDescription("執行記錄")]
    public IActionResult Index(Guid? jobId = null)
    {
        var vm = Wtm.CreateVM<EtlRunLogListVM>();
        if (jobId.HasValue)
            vm.Searcher.JobId = jobId;
        return PartialView(vm);
    }

    [ActionDescription("執行記錄")]
    [HttpPost]
    public IActionResult Search(EtlRunLogSearcher searcher)
    {
        var vm = Wtm.CreateVM<EtlRunLogListVM>();
        vm.Searcher = searcher;
        return Content(vm.GetJson());
    }

    [ActionDescription("重跑")]
    [HttpPost]
    public async Task<IActionResult> Rerun(Guid runLogId)
    {
        await _scheduler.RerunFromSnapshotAsync(runLogId);
        return Ok(new { success = true, message = "已從該點重跑" });
    }
}
