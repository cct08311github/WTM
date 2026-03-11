#nullable enable
using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Controllers;

[TestClass]
public class EtlMonitorControllerTests
{
    private EtlProgressTracker _tracker = null!;
    private _EtlMonitorController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _tracker = new EtlProgressTracker();
        _controller = new _EtlMonitorController(_tracker);
        _controller.Wtm = MockWtmContext.CreateWtmContext();

        var mockHttpContext = new Mock<HttpContext>();
        var mockSession = new MockHttpSession();
        mockHttpContext.Setup(s => s.Session).Returns(mockSession);
        mockHttpContext.Setup(x => x.Request).Returns(new DefaultHttpContext().Request);
        _controller.ControllerContext.HttpContext = mockHttpContext.Object;
    }

    [TestMethod]
    public void Running_returns_all_tracked_jobs()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _tracker.Update(new EtlProgress { JobId = id1, Phase = "Extract", ProcessedRows = 100 });
        _tracker.Update(new EtlProgress { JobId = id2, Phase = "Load", ProcessedRows = 200 });

        var result = _controller.Running() as OkObjectResult;

        Assert.IsNotNull(result);
        var list = result!.Value as System.Collections.Generic.IReadOnlyList<EtlProgress>;
        Assert.IsNotNull(list);
        Assert.AreEqual(2, list!.Count);
    }

    [TestMethod]
    public void Progress_returns_404_for_idle_job()
    {
        var result = _controller.Progress(Guid.NewGuid()) as NotFoundObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(404, result!.StatusCode);
    }

    [TestMethod]
    public void Progress_returns_200_for_running_job()
    {
        var jobId = Guid.NewGuid();
        _tracker.Update(new EtlProgress { JobId = jobId, Phase = "Extract", ProcessedRows = 500, TotalRows = 1000 });

        var result = _controller.Progress(jobId) as OkObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(200, result!.StatusCode);
        var progress = result.Value as EtlProgress;
        Assert.IsNotNull(progress);
        Assert.AreEqual(500, progress!.ProcessedRows);
        Assert.AreEqual("Extract", progress.Phase);
    }

    [TestMethod]
    public void Running_returns_empty_when_no_jobs()
    {
        var result = _controller.Running() as OkObjectResult;

        Assert.IsNotNull(result);
        var list = result!.Value as System.Collections.Generic.IReadOnlyList<EtlProgress>;
        Assert.IsNotNull(list);
        Assert.AreEqual(0, list!.Count);
    }
}
