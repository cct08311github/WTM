using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Controllers
{
    public class HomeController : BaseController
    {
        [AllRights]
        public IActionResult Index()
        {
            ViewData["title"] = "WTM E2E";
            return View();
        }

        [AllRights]
        [ActionDescription("FrontPage")]
        public IActionResult FrontPage()
        {
            return PartialView();
        }

        [AllRights]
        [ActionDescription("Layout")]
        public IActionResult Layout()
        {
            ViewData["debug"] = Wtm.ConfigInfo.IsQuickDebug;
            return PartialView();
        }
    }
}
