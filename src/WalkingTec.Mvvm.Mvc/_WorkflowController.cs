// using Elsa.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    [AuthorizeJwtWithCookie]
    [AllRights]
    public class _WorkflowController : BaseController
    {
        [ActionDescription("流程管理")]
        public IActionResult Inner()
        {
            if (Wtm.LoginUserInfo.Roles.Any(x => x.RoleCode == "001") ||
                Wtm.LoginUserInfo.Roles.Any(x => x.RoleName == "流程管理员"))
            {
                if (Wtm.LoginUserInfo.TenantCode != null)
                {
                    Response.Cookies.Append("workflowtenant", Wtm.LoginUserInfo.TenantCode);
                }
                else
                {
                    Response.Cookies.Delete("workflowtenant");
                }
                return View();
            }
            return Forbid();
        }

    }
}
