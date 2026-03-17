#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Etl.Scheduling;
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
}
