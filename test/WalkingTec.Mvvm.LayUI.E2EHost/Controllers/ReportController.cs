using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Controllers
{
    /// <summary>
    /// A page only the administrator role may open (menu privilege), whose grid is filled by the generic data endpoint of the framework
    /// (test only). The view model is the one of the Gadget list, which the other roles can only reach through their own privileges.
    /// </summary>
    [ActionDescription("Report")]
    public class ReportController : BaseController
    {
        [ActionDescription("Sys.Search")]
        public ActionResult Index()
        {
            var vm = Wtm.CreateVM<GadgetListVM>();
            return PartialView(vm);
        }
    }
}
