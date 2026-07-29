#nullable enable
using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Mvc;

/// <remarks>
/// Issue #841: <see cref="Running"/> reads <see cref="EtlProgressTracker"/>'s in-memory
/// <c>ConcurrentDictionary&lt;Guid, EtlProgress&gt;</c>, which has no tenant dimension of its
/// own -- until this fix the controller carried no role gate at all, only page-level
/// URL-RBAC. Reuses the same admin gate as <c>_EtlJobController</c>/
/// <c>_EtlDashboardController</c>: <c>RoleCode = Admin</c> or <c>ETLAdmin</c>;
/// <c>IsQuickDebug</c> bypasses RBAC the same way. The tracker's own lack of a tenant
/// dimension (it still returns every tenant's running jobs to any admin who reaches it) is
/// tracked separately -- see #841's own body, item 4 -- and is not fixed by this gate.
/// </remarks>
[ActionDescription("ETL 監控")]
public class _EtlMonitorController : BaseController
{
    private readonly EtlProgressTracker _tracker;

    public _EtlMonitorController(EtlProgressTracker tracker)
    {
        _tracker = tracker;
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
