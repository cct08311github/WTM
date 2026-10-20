using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Elsa.Models;
using Elsa.Persistence;
using Elsa.Persistence.Specifications;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;

namespace WalkingTec.Mvvm.Core.WorkFlow
{
    /// <summary>
    /// In-process reads of the workflow instance and its approval timeline (the logic behind <c>_WorkflowApi/GetTimeLine</c> and
    /// <c>_WorkflowApi/GetWorkflow</c>). <c>BaseCRUDVM</c> and the controller both call these, so the VM no longer needs an HTTP
    /// self-call that only carried a (possibly expired) remote token instead of the sign-in cookie.
    /// Workflow instances live in the local database even in a multi-host deployment (the controller never forwarded these two
    /// actions to the main host), so no HasMainHost forwarding is needed here.
    /// </summary>
    public static class WorkflowQuery
    {
        public static async Task<List<ApproveTimeLine>> GetTimeLineAsync(WTMContext wtm, string flowname, string entitytype, string entityid)
        {
            List<ApproveTimeLine> all = [];
            var lp = wtm.ServiceProvider.GetRequiredService<IWorkflowInstanceStore>();
            var log = wtm.ServiceProvider.GetRequiredService<IWorkflowExecutionLogStore>();
            var workflowIds = wtm.DC.Set<Elsa_WorkflowInstance>().CheckEqual(flowname, x => x.Name).CheckEqual(entitytype, x => x.ContextType).CheckEqual(entityid, x => x.ContextId)
                .OrderBy(x => x.CreatedAt).Select(x => x.ID).ToList();
            foreach (var workflowId in workflowIds)
            {
                var instance = await lp.FindAsync(new InstanceIdSpecification(workflowId));
                if (instance != null)
                {
                    var specification = new LogRecordInstanceIdSpecification(workflowId);
                    var orderBy = OrderBySpecification.OrderBy<WorkflowExecutionLogRecord>(x => x.Timestamp);
                    var records = (await log.FindManyAsync(specification, orderBy)).ToList();
                    var rv = records.Where(x => x.ActivityType == nameof(WtmApproveActivity) && x.EventName != "Executing" && x.EventName != "Resuming" && x.EventName != "Suspended")
                        .Select(x =>
                        new ApproveTimeLine
                        {
                            Id = x.ActivityId,
                            Time = x.Timestamp.InZone(DateTimeZoneProviders.Tzdb.GetSystemDefault()).ToString("yyyy-MM-dd HH:mm:ss", null),
                            Action = x.EventName == "Executed" ? "等待审批" : x.Data.ContainsKey("Outcomes") ? x.Data["Outcomes"].Values<string>().FirstOrDefault() : "",
                            Remark = "",
                            Approvers = "",
                            Approved = ""
                        }
                    ).ToList();
                    rv = rv.Where(x => string.IsNullOrEmpty(x.Action) == false).ToList();
                    foreach (var record in rv)
                    {
                        var ad = instance.ActivityData[record.Id];
                        object approved, remark, approvers;
                        ad.TryGetValue(nameof(WtmApproveActivity.ApprovedBy), out approved);
                        ad.TryGetValue(nameof(WtmApproveActivity.Remark), out remark);
                        ad.TryGetValue(nameof(WtmApproveActivity.ApproveUsersFullText), out approvers);
                        record.Approved = (approved as string) ?? "";
                        record.Approvers = (approvers as List<string>)?.ToSepratedString() ?? "";
                        if (record.Action != "等待审批")
                        {
                            record.Remark = (remark as string) ?? "";
                        }
                    }
                    ApproveTimeLine first = new ApproveTimeLine
                    {
                        Action = "_start",
                        Approved = instance.Variables.Get("Submitter")?.ToString() ?? "",
                        Approvers = "",
                        Id = "",
                        Remark = "",
                        Time = instance.CreatedAt.InZone(DateTimeZoneProviders.Tzdb.GetSystemDefault()).ToString("yyyy-MM-dd HH:mm:ss", null)
                    };
                    rv.Insert(0, first);
                    if (instance.FinishedAt != null)
                    {
                        ApproveTimeLine last = new ApproveTimeLine
                        {
                            Action = "_finish",
                            Approved = "",
                            Approvers = "",
                            Id = "",
                            Remark = "",
                            Time = instance.FinishedAt.Value.InZone(DateTimeZoneProviders.Tzdb.GetSystemDefault()).ToString("yyyy-MM-dd HH:mm:ss", null)
                        };
                        rv.Add(last);
                    }
                    all.AddRange(rv);
                }
            }
            return all;
        }

        public static async Task<WorkflowInstance> GetWorkflowAsync(WTMContext wtm, string flowname, string entitytype, string entityid)
        {
            var workflowId = wtm.DC.Set<Elsa_WorkflowInstance>().CheckEqual(flowname, x => x.Name).CheckEqual(entitytype, x => x.ContextType).CheckEqual(entityid, x => x.ContextId)
                .Select(x => x.ID).FirstOrDefault();
            var lp = wtm.ServiceProvider.GetRequiredService<IWorkflowInstanceStore>();
            return await lp.FindAsync(new InstanceIdSpecification(workflowId));
        }

        //Elsa.Core (EntityIdSpecification / WorkflowInstanceIdSpecification) is not referenced by WalkingTec.Mvvm.Core, only Elsa.Abstractions is; these are the same two predicates
        private sealed class InstanceIdSpecification : Specification<WorkflowInstance>
        {
            private readonly string _id;
            public InstanceIdSpecification(string id) { _id = id; }
            public override Expression<Func<WorkflowInstance, bool>> ToExpression() => x => x.Id == _id;
        }

        private sealed class LogRecordInstanceIdSpecification : Specification<WorkflowExecutionLogRecord>
        {
            private readonly string _id;
            public LogRecordInstanceIdSpecification(string id) { _id = id; }
            public override Expression<Func<WorkflowExecutionLogRecord, bool>> ToExpression() => x => x.WorkflowInstanceId == _id;
        }
    }
}
