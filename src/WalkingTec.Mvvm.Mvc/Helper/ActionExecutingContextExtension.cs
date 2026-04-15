using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    public static class ActionExecutingContextExtension
    {
        public static void SetWtmContext(this ActionExecutingContext self)
        {
            var controller = self.Controller as IBaseController;
            if (controller == null)
            {
                return;
            }
            if (controller.Wtm == null)
            {
                controller.Wtm = self.HttpContext.RequestServices.GetRequiredService<WTMContext>();
            }
            try
            {
                controller.Wtm.MSD = new ModelStateServiceProvider(self.ModelState);
                controller.Wtm.Session = new SessionServiceProvider(self.HttpContext.Session);
            }
            catch (Exception ex)
            {
                self.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ActionExecutingContextExtension")?.LogDebug(ex, "SetWtmContext: attaching ModelState/Session providers failed (session may be disabled)");
            }
        }
    }
}
