using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    [AllRights]
    [ActionDescription("Dashboard")]
    public class _DashboardPageController : BaseController
    {
        [ActionDescription("Dashboard管理")]
        public IActionResult Index() => View();

        [ActionDescription("Dashboard預覽")]
        public IActionResult Render(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest("Dashboard ID is required.");
            ViewBag.DashboardId = id;
            return View();
        }
    }
}
