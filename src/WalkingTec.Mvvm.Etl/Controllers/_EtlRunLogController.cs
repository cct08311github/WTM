#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.ViewModels;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Mvc;

/// <remarks>
/// Issue #841: <see cref="Rerun"/> starts a real pipeline re-execution from a watermark
/// snapshot, but until this fix the controller carried no role gate of its own -- only
/// page-level URL-RBAC. Reuses the same admin gate as <c>_EtlJobController</c>/
/// <c>_EtlDashboardController</c>: <c>RoleCode = Admin</c> or <c>ETLAdmin</c>;
/// <c>IsQuickDebug</c> bypasses RBAC the same way.
/// </remarks>
[ActionDescription("ETL 執行記錄")]
public class _EtlRunLogController : BaseController
{
    private readonly EtlSchedulerService _scheduler;

    public _EtlRunLogController(EtlSchedulerService scheduler)
    {
        _scheduler = scheduler;
    }

    // ─── Authorization gate (#841) ─────────────────────────────────────────────

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        // IsQuickDebug bypasses all RBAC (framework-wide convention).
        if (Wtm?.ConfigInfo?.IsQuickDebug == true)
        {
            base.OnActionExecuting(context);
            return;
        }

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
        try
        {
            await _scheduler.RerunFromSnapshotAsync(runLogId);
            return Ok(new { success = true, message = "已從該點重跑" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
