using Elsa;
using Elsa.Activities.Workflows.Workflow;
using Elsa.Models;
using Elsa.Persistence;
using Elsa.Persistence.Specifications;
using Elsa.Server.Api.Models;
using Elsa.Services;
using Elsa.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetBox.Extensions;
using NodaTime;
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
        public IActionResult GetWorkflowUsers([FromQuery]string[] itcode)
        {
            if (ConfigInfo.HasMainHost)
            {
                return Request.RedirectCall(Wtm, "/_WorkflowApi/GetWorkflowUsers").Result;
            }
            return Ok(WorkflowLookup.GetUsers(Wtm, itcode));
        }

        [HttpGet("[action]")]
        [NoLog]
        public IActionResult GetWorkflowGroups([FromQuery] string[] ids)
        {
            if (ConfigInfo.HasMainHost)
            {
                return Request.RedirectCall(Wtm, "/_WorkflowApi/GetWorkflowGroups").Result;
            }
            return Ok(WorkflowLookup.GetGroups(Wtm, ids));
        }

        [HttpGet("[action]")]
        [NoLog]
        public IActionResult GetWorkflowGroupManagers([FromQuery] string[] ids)
        {
            if (ConfigInfo.HasMainHost)
            {
                return Request.RedirectCall(Wtm, "/_WorkflowApi/GetWorkflowGroupManagers").Result;
            }
            return Ok(WorkflowLookup.GetGroupManagers(Wtm, ids));
        }

        [HttpGet("[action]")]
        [NoLog]
        public IActionResult GetWorkflowMyGroupManagers([FromQuery] string itcode)
        {
            if (ConfigInfo.HasMainHost)
            {
                return Request.RedirectCall(Wtm, "/_WorkflowApi/GetWorkflowMyGroupManagers").Result;
            }
            return Ok(WorkflowLookup.GetMyGroupManagers(Wtm, itcode));
        }

        [HttpGet("[action]")]
        [NoLog]
        public IActionResult GetWorkflowRoles([FromQuery] string[] ids)
        {
            if (ConfigInfo.HasMainHost)
            {
                return Request.RedirectCall(Wtm, "/_WorkflowApi/GetWorkflowRoles").Result;
            }
            return Ok(WorkflowLookup.GetRoles(Wtm, ids));
        }


        [HttpGet("[action]")]
        [NoLog]
        public async Task<IActionResult> GetTimeLine(string flowname,string entitytype,string entityid)
        {
            return Ok(await WorkflowQuery.GetTimeLineAsync(Wtm, flowname, entitytype, entityid));
        }

        [HttpGet("[action]")]
        [NoLog]
        public async Task<IActionResult> GetWorkflow(string flowname, string entitytype, string entityid)
        {
            return Ok(await WorkflowQuery.GetWorkflowAsync(Wtm, flowname, entitytype, entityid));
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

