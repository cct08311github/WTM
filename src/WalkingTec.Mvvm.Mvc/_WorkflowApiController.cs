// using Elsa;
// using Elsa.Activities.Workflows.Workflow;
// using Elsa.Models;
// using Elsa.Persistence;
// using Elsa.Persistence.Specifications;
// using Elsa.Server.Api.Models;
// using Elsa.Services;
// using Elsa.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetBox.Extensions;
using NodaTime;
using NPOI.SS.Formula.Functions;
using Open.Linq.AsyncExtensions;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.WorkFlow;

namespace WalkingTec.Mvvm.Mvc
{
    [AuthorizeJwtWithCookie]
    [ApiController]
    [Route("_[controller]")]
    [ActionDescription("_Admin.WorkflowApi")]
    [AllRights]
    public class WorkflowApiController : BaseApiController
    {

        [HttpGet("[action]")]
        [NoLog]
        [Public]
        public async Task<IActionResult> GetWorkflowUsers([FromQuery]string[] itcode)
        {



                if (ConfigInfo.HasMainHost)
                {
                    return await Request.RedirectCall(Wtm, "/_WorkflowApi/GetWorkflowUsers");
                }
                var tenant = Wtm.LoginUserInfo?.CurrentTenant;
                var rv = Wtm.BaseUserQuery.IgnoreQueryFilters().CheckContain(itcode.ToList(), x => x.ITCode).Where(x => x.TenantCode == tenant)
                    .Select(x => new { x.ITCode, x.Name })
                    .OrderBy(x => x.ITCode)
                    .ToList().ToListItems(x => x.Name, x => x.ITCode);

                return Ok(rv);

        }

        [HttpGet("[action]")]
        [Public]
        [NoLog]
        public async Task<IActionResult> GetWorkflowGroups([FromQuery] string[] ids)
        {
                if (ConfigInfo.HasMainHost)
                {
                    return await Request.RedirectCall(Wtm, "/_WorkflowApi/GetWorkflowGroups");
                }
                var tenant = Wtm.LoginUserInfo?.CurrentTenant;
                var rv = Wtm.DC.Set<FrameworkGroup>().CheckIDs(ids.ToList())
                    .Select(x => new { x.ID, x.GroupName })
                    .OrderBy(x => x.GroupName)
                    .ToList().ToListItems(x => x.GroupName, x => x.ID);

                return Ok(rv);
        }

        [HttpGet("[action]")]
        [Public]
        [NoLog]
        public async Task<IActionResult> GetWorkflowGroupManagers([FromQuery] string[] ids)
        {
            if (ConfigInfo.HasMainHost)
            {
                return await Request.RedirectCall(Wtm, "/_WorkflowApi/GetWorkflowGroupManagers");
            }
            var tenant = Wtm.LoginUserInfo?.CurrentTenant;
            var rv = Wtm.DC.Set<FrameworkGroup>().CheckIDs(ids.ToList())
                .Select(x => new { x.ID, x.Manager })
                .OrderBy(x => x.Manager)
                .ToList().ToListItems(x => x.Manager, x => x.ID);

            return Ok(rv);
        }

        [HttpGet("[action]")]
        [Public]
        [NoLog]
        public async Task<IActionResult> GetWorkflowMyGroupManagers([FromQuery] string itcode)
        {
            if (ConfigInfo.HasMainHost)
            {
                return await Request.RedirectCall(Wtm, "/_WorkflowApi/GetWorkflowMyGroupManagers");
            }
            var tenant = Wtm.LoginUserInfo?.CurrentTenant;
            var rv = Wtm.DC.Set<FrameworkUserGroup>().Where(x=>x.UserCode == itcode)
                .Join(DC.Set<FrameworkGroup>(), x => x.GroupCode, y => y.GroupCode, (x, y) => y.Manager)
                .ToList().ToListItems(x => x, x => x);

            return Ok(rv);
        }

        [HttpGet("[action]")]
        [Public]
        [NoLog]
        public async Task<IActionResult> GetWorkflowRoles([FromQuery] string[] ids)
        {


                if (ConfigInfo.HasMainHost)
                {
                    return await Request.RedirectCall(Wtm, "/_WorkflowApi/GetWorkflowRoles");
                }
                var tenant = Wtm.LoginUserInfo?.CurrentTenant;
                var rv = Wtm.DC.Set<FrameworkRole>().CheckIDs(ids.ToList())
                    .Select(x => new { x.ID, x.RoleName })
                    .OrderBy(x => x.RoleName)
                    .ToList().ToListItems(x => x.RoleName, x => x.ID);

                return Ok(rv);

        }


        [HttpGet("[action]")]
        [NoLog]
        public async Task<IActionResult> GetTimeLine(string flowname,string entitytype,string entityid)
        {
            // Elsa removed: stubbed for now
            List<ApproveTimeLine> all = new List<ApproveTimeLine>();
            return await Task.FromResult(Ok(all));
        }

        [HttpGet("[action]")]
        [NoLog]
        public async Task<IActionResult> GetWorkflow(string flowname, string entitytype, string entityid)
        {
            // Elsa removed: stubbed for now
            return await Task.FromResult(Ok(new object()));
        }

        [HttpGet("[action]")]
        [NoLog]
        public async Task<IActionResult> GetMyApprove(string flowname, string entitytype, string entityid,string tag,int page=1,int take=20)
        {
            var roleids = Wtm.LoginUserInfo.Roles.Select(x => "r:" + x.ID).ToList();
            var groupids = Wtm.LoginUserInfo.Groups.Select(x => "g:" + x.ID).ToList();
            var rv = await DC.Set<FrameworkWorkflow>()
                .Where(x => x.UserCode == Wtm.LoginUserInfo.ITCode
                    || roleids.Contains(x.UserCode)
                    || groupids.Contains(x.UserCode))
                .Where(x=>x.TenantCode == Wtm.LoginUserInfo.CurrentTenant)
                .CheckEqual(flowname, x => x.WorkflowName)
                .CheckEqual(entitytype, x => x.ModelType)
                .CheckEqual(entityid, x => x.ModelID)
                .CheckEqual(tag, x => x.Tag)
                .OrderByDescending(x=>x.StartTime)
                .Skip((page-1)*take).Take(take)
                .ToListAsync();
            return Ok(rv);
        }

    }
}
