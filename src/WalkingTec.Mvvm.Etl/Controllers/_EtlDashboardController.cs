#nullable enable
using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Dashboard;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Mvc;

/// <summary>
/// ETL operations dashboard (10.5+) — single-page visual overview of
/// what's running, what just failed, what's slow, and what loaded
/// over the trailing window.
/// </summary>
/// <remarks>
/// <para>
/// Endpoints:
/// </para>
/// <list type="bullet">
/// <item><c>GET /_EtlDashboard/Index</c> — render the dashboard view.</item>
/// <item><c>GET /_EtlDashboard/Stats?days=7&amp;topN=10</c> — JSON payload
/// matching <see cref="EtlDashboardSummary"/>. The view polls this every
/// few seconds to refresh KPI cards + the live-running panel.</item>
/// </list>
/// <para>
/// Reuses the same admin gate as <c>_EtlJobController</c>:
/// <c>RoleCode = Admin</c> or <c>ETLAdmin</c>; <c>IsQuickDebug</c>
/// bypasses RBAC the same way.
/// </para>
/// </remarks>
[ActionDescription("ETL 儀表板")]
public class _EtlDashboardController : BaseController
{
    private readonly EtlDashboardService _service;

    public _EtlDashboardController(EtlDashboardService service)
    {
        _service = service;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
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

    [ActionDescription("ETL 儀表板")]
    public IActionResult Index()
    {
        return PartialView();
    }

    /// <summary>
    /// JSON snapshot. <paramref name="days"/> clamped to 1..90;
    /// <paramref name="topN"/> clamped to 1..100.
    /// </summary>
    [ActionDescription("儀表板資料")]
    [HttpGet]
    public IActionResult Stats(int days = 7, int topN = 10)
    {
        // #883: caller's own tenant -- see EtlDashboardService.BuildSummary's doc comment.
        var summary = _service.BuildSummary(Wtm.DC, Wtm.LoginUserInfo?.CurrentTenant, days, topN);
        return Ok(summary);
    }
}
