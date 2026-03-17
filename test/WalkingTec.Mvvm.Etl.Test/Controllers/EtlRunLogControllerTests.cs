#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.Test.ViewModels;
using WalkingTec.Mvvm.Etl.ViewModels;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Controllers;

[TestClass]
public class EtlRunLogControllerTests
{
    private Mock<EtlSchedulerService> _mockScheduler = null!;
    private _EtlRunLogController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        var mockSp = new Mock<IServiceProvider>();
        _mockScheduler = new Mock<EtlSchedulerService>(mockSp.Object);

        _controller = new _EtlRunLogController(_mockScheduler.Object);
        _controller.Wtm = MockWtmContext.CreateWtmContext();

        var mockHttpContext = new Mock<HttpContext>();
        var mockSession = new MockHttpSession();
        mockHttpContext.Setup(s => s.Session).Returns(mockSession);
        mockHttpContext.Setup(x => x.Request).Returns(new DefaultHttpContext().Request);
        _controller.ControllerContext.HttpContext = mockHttpContext.Object;
        _controller.Wtm.MSD = new ModelStateServiceProvider(_controller.ModelState);
    }

    [TestMethod]
    public async Task Rerun_calls_scheduler_and_returns_200()
    {
        var runLogId = Guid.NewGuid();
        _mockScheduler.Setup(x => x.RerunFromSnapshotAsync(runLogId)).Returns(Task.CompletedTask);

        var result = await _controller.Rerun(runLogId) as OkObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(200, result!.StatusCode);
        _mockScheduler.Verify(x => x.RerunFromSnapshotAsync(runLogId), Times.Once);
    }

    [TestMethod]
    public async Task Rerun_returns_400_when_scheduler_throws_InvalidOperationException()
    {
        var runLogId = Guid.NewGuid();
        _mockScheduler.Setup(x => x.RerunFromSnapshotAsync(runLogId))
            .ThrowsAsync(new InvalidOperationException($"RunLog {runLogId} not found or job state prevents rerun."));

        var result = await _controller.Rerun(runLogId) as BadRequestObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(400, result!.StatusCode);
        _mockScheduler.Verify(x => x.RerunFromSnapshotAsync(runLogId), Times.Once);
    }

    // ─── Index (#356) ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Index_no_jobId_returns_partial_view()
    {
        var result = _controller.Index() as PartialViewResult;

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void Index_with_jobId_sets_searcher_JobId()
    {
        var jobId = Guid.NewGuid();

        var result = _controller.Index(jobId) as PartialViewResult;

        Assert.IsNotNull(result);
        var vm = result!.Model as EtlRunLogListVM;
        Assert.IsNotNull(vm);
        Assert.AreEqual(jobId, vm!.Searcher.JobId);
    }

    // ─── Search (#356) ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Search_returns_content_json()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);

        // Seed a job and a run log so the query has data to work with
        var job = new EtlJobDefinition { ID = Guid.NewGuid(), Name = "TestJob" };
        dc.EtlJobDefinitions.Add(job);
        dc.EtlRunLogs.Add(new EtlRunLog
        {
            ID = Guid.NewGuid(),
            JobId = job.ID,
            Trigger = EtlRunTrigger.Manual,
            Result = EtlRunResult.Success
        });
        dc.SaveChanges();

        _controller.Wtm = MockWtmContext.CreateWtmContext(dc);
        _controller.Wtm.MSD = new ModelStateServiceProvider(_controller.ModelState);

        var result = _controller.Search(new EtlRunLogSearcher()) as ContentResult;

        Assert.IsNotNull(result);
        Assert.IsNotNull(result!.Content);
    }
}
