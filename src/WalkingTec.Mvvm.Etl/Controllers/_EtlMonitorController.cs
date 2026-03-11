#nullable enable
using System;
using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Mvc;

[ActionDescription("ETL 監控")]
public class _EtlMonitorController : BaseController
{
    private readonly EtlProgressTracker _tracker;

    public _EtlMonitorController(EtlProgressTracker tracker)
    {
        _tracker = tracker;
    }

    [ActionDescription("執行中 Job")]
    [HttpGet]
    public IActionResult Running()
    {
        var running = _tracker.GetAll();
        return Ok(running);
    }

    [ActionDescription("Job 進度")]
    [HttpGet]
    public IActionResult Progress(Guid jobId)
    {
        var progress = _tracker.Get(jobId);
        if (progress == null)
            return NotFound(new { error = "Job 不在執行中" });
        return Ok(progress);
    }
}
