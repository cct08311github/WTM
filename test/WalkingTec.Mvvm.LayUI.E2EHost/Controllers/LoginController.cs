using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.HomeVMs;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Controllers
{
    [AllRights]
    public class LoginController : BaseController
    {
        [Public]
        [ActionDescription("Login")]
        public IActionResult Login()
        {
            var vm = Wtm.CreateVM<LoginVM>();
            vm.Redirect = HttpContext.Request.Query["ReturnUrl"];
            return View(vm);
        }

        [Public]
        [HttpPost]
        public async Task<ActionResult> Login(LoginVM vm)
        {
            var user = Wtm.DoLogin(vm.ITCode, vm.Password, vm.Tenant);
            if (user == null)
            {
                vm.MSD.AddModelError("", Localizer["Sys.LoginFailed"]);
                return View(vm);
            }
            Wtm.LoginUserInfo = user;
            var principal = user.CreatePrincipal();
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, null);
            // only follow redirects that stay on this site
            var target = Url.IsLocalUrl(vm.Redirect) ? vm.Redirect : "/";
            return Redirect(target);
        }

        [AllRights]
        [ActionDescription("Logout")]
        public async Task<IActionResult> Logout()
        {
            HttpContext.Session.Clear();
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Redirect("/");
        }
    }
}
