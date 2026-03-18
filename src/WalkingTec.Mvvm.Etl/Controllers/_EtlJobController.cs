#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Quartz;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.ViewModels;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Mvc;

[ApiController]
[ActionDescription("ETL Job 管理")]
public class _EtlJobController : BaseController
{
    private readonly EtlSchedulerService _scheduler;

    public _EtlJobController(EtlSchedulerService scheduler)
    {
        _scheduler = scheduler;
    }

    // ─── Authorization gate ───────────────────────────────────────────────────

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var roles = Wtm?.LoginUserInfo?.Roles?.Select(r => r.RoleCode).ToArray() ?? Array.Empty<string>();
        var isAdmin = roles.Any(r =>
            string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r, "ETLAdmin", StringComparison.OrdinalIgnoreCase));

        if (!isAdmin)
        {
            context.Result = Forbid();
            return;
        }

        base.OnActionExecuting(context);
    }

    // ─── CRUD ───

    [ActionDescription("Job 列表")]
    public IActionResult Index()
    {
        var vm = Wtm.CreateVM<EtlJobListVM>();
        return PartialView(vm);
    }

    [ActionDescription("Job 列表")]
    [HttpPost]
    public IActionResult Search(EtlJobSearcher searcher)
    {
        var vm = Wtm.CreateVM<EtlJobListVM>();
        vm.Searcher = searcher;
        return Content(vm.GetJson());
    }

    [ActionDescription("新增 Job")]
    public IActionResult Create()
    {
        var vm = Wtm.CreateVM<EtlJobDefinitionVM>();
        return PartialView(vm);
    }

    [ActionDescription("新增 Job")]
    [HttpPost]
    public IActionResult Create(EtlJobDefinitionVM vm)
    {
        if (!ModelState.IsValid)
            return PartialView(vm);
        vm.DoAdd();
        if (!ModelState.IsValid)
            return PartialView(vm);
        return FFResult().CloseDialog().RefreshGrid();
    }

    [ActionDescription("編輯 Job")]
    public IActionResult Edit(Guid id)
    {
        var vm = Wtm.CreateVM<EtlJobDefinitionVM>(id);
        return PartialView(vm);
    }

    [ActionDescription("編輯 Job")]
    [HttpPost]
    public IActionResult Edit(EtlJobDefinitionVM vm)
    {
        if (!ModelState.IsValid)
            return PartialView(vm);
        vm.DoEdit();
        if (!ModelState.IsValid)
            return PartialView(vm);
        return FFResult().CloseDialog().RefreshGrid();
    }

    [ActionDescription("刪除 Job")]
    public IActionResult Delete(Guid id)
    {
        var vm = Wtm.CreateVM<EtlJobDefinitionVM>(id);
        return PartialView(vm);
    }

    [ActionDescription("刪除 Job")]
    [HttpPost]
    public IActionResult Delete(Guid id, IFormCollection noUse)
    {
        var vm = Wtm.CreateVM<EtlJobDefinitionVM>(id);
        vm.DoDelete();
        if (!ModelState.IsValid)
            return PartialView(vm);
        return FFResult().CloseDialog().RefreshGrid();
    }

    // ─── 操作 API ───

    [ActionDescription("立即執行")]
    [HttpPost]
    public async Task<IActionResult> TriggerNow(Guid id)
    {
        try
        {
            await _scheduler.TriggerNowAsync(id);
            return Ok(new { success = true, message = "已觸發執行" });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [ActionDescription("暫停 Job")]
    [HttpPost]
    public async Task<IActionResult> Pause(Guid id)
    {
        try
        {
            await _scheduler.PauseAsync(id);
            return Ok(new { success = true, message = "已暫停" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [ActionDescription("恢復 Job")]
    [HttpPost]
    public async Task<IActionResult> Resume(Guid id)
    {
        try
        {
            await _scheduler.ResumeAsync(id);
            return Ok(new { success = true, message = "已恢復" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [ActionDescription("中止執行")]
    [HttpPost]
    public async Task<IActionResult> Abort(Guid id)
    {
        try
        {
            await _scheduler.AbortAsync(id);
            return Ok(new { success = true, message = "已中止" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [ActionDescription("跳過下次")]
    [HttpPost]
    public async Task<IActionResult> SkipNext(Guid id)
    {
        try
        {
            await _scheduler.SkipNextAsync(id);
            return Ok(new { success = true, message = "已設定跳過下次執行" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [ActionDescription("修改排程")]
    [HttpPost]
    public async Task<IActionResult> Reschedule(Guid id, [FromBody] RescheduleRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.NewCron))
            return BadRequest(new { error = "Cron 表達式不可為空" });
        if (!CronExpression.IsValidExpression(request.NewCron))
            return BadRequest(new { error = "無效的 Cron 表達式" });
        await _scheduler.RescheduleAsync(id, request.NewCron);
        return Ok(new { success = true, message = "排程已更新" });
    }
}

public record RescheduleRequest(string NewCron);
