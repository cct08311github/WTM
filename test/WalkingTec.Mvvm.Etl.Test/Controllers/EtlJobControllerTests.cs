#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Controllers;

[TestClass]
public class EtlJobControllerTests
{
    private Mock<EtlSchedulerService> _mockScheduler = null!;
    private _EtlJobController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        var mockSp = new Mock<IServiceProvider>();
        _mockScheduler = new Mock<EtlSchedulerService>(mockSp.Object);

        _controller = new _EtlJobController(_mockScheduler.Object);
        _controller.Wtm = MockWtmContext.CreateWtmContext();

        var mockHttpContext = new Mock<HttpContext>();
        var mockSession = new MockHttpSession();
        mockHttpContext.Setup(s => s.Session).Returns(mockSession);
        mockHttpContext.Setup(x => x.Request).Returns(new DefaultHttpContext().Request);
        _controller.ControllerContext.HttpContext = mockHttpContext.Object;
        _controller.Wtm.MSD = new ModelStateServiceProvider(_controller.ModelState);
    }

    [TestMethod]
    public async Task TriggerNow_calls_scheduler()
    {
        var id = Guid.NewGuid();
        _mockScheduler.Setup(x => x.TriggerNowAsync(id)).Returns(Task.CompletedTask);

        var result = await _controller.TriggerNow(id) as OkObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(200, result!.StatusCode);
        _mockScheduler.Verify(x => x.TriggerNowAsync(id), Times.Once);
    }

    [TestMethod]
    public async Task Pause_calls_scheduler()
    {
        var id = Guid.NewGuid();
        _mockScheduler.Setup(x => x.PauseAsync(id)).Returns(Task.CompletedTask);

        var result = await _controller.Pause(id) as OkObjectResult;

        Assert.IsNotNull(result);
        _mockScheduler.Verify(x => x.PauseAsync(id), Times.Once);
    }

    [TestMethod]
    public async Task Resume_calls_scheduler()
    {
        var id = Guid.NewGuid();
        _mockScheduler.Setup(x => x.ResumeAsync(id)).Returns(Task.CompletedTask);

        var result = await _controller.Resume(id) as OkObjectResult;

        Assert.IsNotNull(result);
        _mockScheduler.Verify(x => x.ResumeAsync(id), Times.Once);
    }

    [TestMethod]
    public async Task Abort_calls_scheduler()
    {
        var id = Guid.NewGuid();
        _mockScheduler.Setup(x => x.AbortAsync(id)).Returns(Task.CompletedTask);

        var result = await _controller.Abort(id) as OkObjectResult;

        Assert.IsNotNull(result);
        _mockScheduler.Verify(x => x.AbortAsync(id), Times.Once);
    }

    [TestMethod]
    public async Task SkipNext_calls_scheduler()
    {
        var id = Guid.NewGuid();
        _mockScheduler.Setup(x => x.SkipNextAsync(id)).Returns(Task.CompletedTask);

        var result = await _controller.SkipNext(id) as OkObjectResult;

        Assert.IsNotNull(result);
        _mockScheduler.Verify(x => x.SkipNextAsync(id), Times.Once);
    }

    [TestMethod]
    public async Task Reschedule_rejects_invalid_cron()
    {
        var id = Guid.NewGuid();

        var result = await _controller.Reschedule(id, "not-a-cron") as BadRequestObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(400, result!.StatusCode);
        _mockScheduler.Verify(x => x.RescheduleAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task Reschedule_accepts_valid_cron()
    {
        var id = Guid.NewGuid();
        var validCron = "0 0 0 * * ?";
        _mockScheduler.Setup(x => x.RescheduleAsync(id, validCron)).Returns(Task.CompletedTask);

        var result = await _controller.Reschedule(id, validCron) as OkObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(200, result!.StatusCode);
        _mockScheduler.Verify(x => x.RescheduleAsync(id, validCron), Times.Once);
    }
}
