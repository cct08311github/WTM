using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Elsa.Models;
using Elsa.Persistence;
using Elsa.Persistence.Specifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json.Linq;
using NodaTime;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.WorkFlow;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// #207: BaseCRUDVM reads the workflow timeline / instance in-process. The former HTTP self-call only forwarded a remote token
    /// (never the sign-in cookie), so it came back empty (or failed outright) as soon as that token was missing or expired.
    /// </summary>
    [TestClass]
    public class WorkflowQueryTest
    {
        private const string InstanceId = "wf-1";

        private static (WTMContext wtm, Mock<IHttpClientFactory> http, School school) Create(bool seedInstance = true)
        {
            var seed = Guid.NewGuid().ToString();
            var school = new School { ID = Guid.NewGuid(), SchoolCode = "s1", SchoolName = "n1" };
            if (seedInstance)
            {
                using var dc = new DataContext(seed, DBTypeEnum.Memory);
                dc.Set<Elsa_WorkflowInstance>().Add(new Elsa_WorkflowInstance
                {
                    ID = InstanceId,
                    DefinitionId = "d",
                    DefinitionVersionId = "dv",
                    CorrelationId = "c",
                    Name = "flow1",
                    ContextType = typeof(School).FullName,
                    ContextId = school.ID.ToString(),
                    CreatedAt = DateTimeOffset.UtcNow
                });
                dc.SaveChanges();
            }
            var wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "user");

            var now = Instant.FromUtc(2026, 1, 2, 3, 4, 5);
            var instance = new WorkflowInstance { Id = InstanceId, CreatedAt = now, Variables = new Variables(), FinishedAt = now.Plus(Duration.FromMinutes(5)) };
            instance.Variables.Set("Submitter", "alice");
            instance.ActivityData["a1"] = new Dictionary<string, object>
            {
                [nameof(WtmApproveActivity.ApprovedBy)] = "bob",
                [nameof(WtmApproveActivity.Remark)] = "ok"
            };
            var instances = new Mock<IWorkflowInstanceStore>();
            instances.Setup(x => x.FindAsync(It.IsAny<ISpecification<WorkflowInstance>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ISpecification<WorkflowInstance> spec, CancellationToken _) => spec.IsSatisfiedBy(instance) ? instance : null);
            var logs = new Mock<IWorkflowExecutionLogStore>();
            logs.Setup(x => x.FindManyAsync(It.IsAny<ISpecification<WorkflowExecutionLogRecord>>(), It.IsAny<IOrderBy<WorkflowExecutionLogRecord>>(), It.IsAny<IPaging>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ISpecification<WorkflowExecutionLogRecord> spec, IOrderBy<WorkflowExecutionLogRecord> o, IPaging p, CancellationToken _) =>
                    (IEnumerable<WorkflowExecutionLogRecord>)new[]
                    {
                        new WorkflowExecutionLogRecord { WorkflowInstanceId = InstanceId, ActivityId = "a1", ActivityType = nameof(WtmApproveActivity), EventName = "Executed", Timestamp = now.Plus(Duration.FromMinutes(1)), Data = new JObject() },
                        new WorkflowExecutionLogRecord { WorkflowInstanceId = InstanceId, ActivityId = "a1", ActivityType = nameof(WtmApproveActivity), EventName = "Executing", Timestamp = now, Data = new JObject() },
                        new WorkflowExecutionLogRecord { WorkflowInstanceId = InstanceId, ActivityId = "a1", ActivityType = nameof(WtmApproveActivity), EventName = "Faulted", Timestamp = now.Plus(Duration.FromMinutes(2)), Data = new JObject { ["Outcomes"] = new JArray("同意") } }
                    }.Where(r => spec.IsSatisfiedBy(r)).ToList());

            var http = new Mock<IHttpClientFactory>(MockBehavior.Strict);
            var services = new ServiceCollection();
            services.AddSingleton(instances.Object);
            services.AddSingleton(logs.Object);
            services.AddSingleton(http.Object);
            wtm.SetServiceProvider(services.BuildServiceProvider());
            return (wtm, http, school);
        }

        [TestMethod]
        public async Task GetTimeLine_ReturnsStartApprovalAndFinishEntries()
        {
            var (wtm, _, school) = Create();
            var rv = await WorkflowQuery.GetTimeLineAsync(wtm, "flow1", typeof(School).FullName, school.ID.ToString());
            CollectionAssert.AreEqual(new[] { "_start", "等待审批", "同意", "_finish" }, rv.Select(x => x.Action).ToArray());
            Assert.AreEqual("alice", rv[0].Approved);
            Assert.AreEqual("bob", rv[2].Approved);
            Assert.AreEqual("ok", rv[2].Remark);
            Assert.AreEqual("", rv[1].Remark);
        }

        [TestMethod]
        public async Task GetTimeLine_NoMatchingInstance_IsEmptyNotNull()
        {
            var (wtm, _, school) = Create(seedInstance: false);
            var rv = await WorkflowQuery.GetTimeLineAsync(wtm, "flow1", typeof(School).FullName, school.ID.ToString());
            Assert.IsNotNull(rv);
            Assert.AreEqual(0, rv.Count);
        }

        [TestMethod]
        public async Task GetWorkflow_ReturnsInstance_OrNullWhenMissing()
        {
            var (wtm, _, school) = Create();
            var found = await WorkflowQuery.GetWorkflowAsync(wtm, "flow1", typeof(School).FullName, school.ID.ToString());
            Assert.AreEqual(InstanceId, found.Id);
            var none = await WorkflowQuery.GetWorkflowAsync(wtm, "flow1", typeof(School).FullName, Guid.NewGuid().ToString());
            Assert.IsNull(none);
        }

        [TestMethod]
        public async Task BaseCRUDVM_SingleHost_ReadsTimelineAndInstanceWithoutHttpSelfCall()
        {
            var (wtm, http, school) = Create();
            Assert.IsFalse(wtm.ConfigInfo.HasMainHost);
            var vm = new BaseCRUDVM<School> { Wtm = wtm, Entity = school };

            var timeline = await vm.GetWorkflowTimeLineAsync("flow1");
            var instance = await vm.GetWorkflowInstanceAsync("flow1");

            Assert.AreEqual(4, timeline.Count);
            Assert.AreEqual(InstanceId, instance.Id);
            //Strict mock: any CallAPI (which asks the factory for a client) would have thrown and been swallowed into an empty result above
            http.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task BaseCRUDVM_UsesVmModelTypeAndEntityId()
        {
            var (wtm, _, school) = Create();
            var other = new BaseCRUDVM<School> { Wtm = wtm, Entity = new School { ID = Guid.NewGuid(), SchoolCode = "x", SchoolName = "x" } };
            Assert.AreEqual(0, (await other.GetWorkflowTimeLineAsync("flow1")).Count);
            Assert.IsNull(await other.GetWorkflowInstanceAsync("flow1"));
        }
    }
}
