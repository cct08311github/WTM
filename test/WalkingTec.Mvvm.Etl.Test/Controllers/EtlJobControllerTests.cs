#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
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
    public async Task Abort_returns_400_when_job_not_running()
    {
        var id = Guid.NewGuid();
        _mockScheduler.Setup(x => x.AbortAsync(id))
            .ThrowsAsync(new InvalidOperationException($"Job {id} is not currently running."));

        var result = await _controller.Abort(id) as BadRequestObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(400, result!.StatusCode);
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
    public async Task Reschedule_rejects_null_request()
    {
        var id = Guid.NewGuid();

        var result = await _controller.Reschedule(id, null) as BadRequestObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(400, result!.StatusCode);
        _mockScheduler.Verify(x => x.RescheduleAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task Reschedule_rejects_empty_cron()
    {
        var id = Guid.NewGuid();

        var result = await _controller.Reschedule(id, new RescheduleRequest("")) as BadRequestObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(400, result!.StatusCode);
    }

    [TestMethod]
    public async Task Reschedule_rejects_invalid_cron()
    {
        var id = Guid.NewGuid();

        var result = await _controller.Reschedule(id, new RescheduleRequest("not-a-cron")) as BadRequestObjectResult;

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

        var result = await _controller.Reschedule(id, new RescheduleRequest(validCron)) as OkObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(200, result!.StatusCode);
        _mockScheduler.Verify(x => x.RescheduleAsync(id, validCron), Times.Once);
    }

    // ─── CRUD tests (#357) ─────────────────────────────────────────────────

    // Helper: create a controller wired to an EtlTestDataContext with the given seed
    private _EtlJobController CreateControllerWithDb(string seed)
    {
        var dc = new EtlTestDataContext(seed, DBTypeEnum.Memory);
        var mockSp = new Mock<IServiceProvider>();
        var scheduler = new Mock<EtlSchedulerService>(mockSp.Object);

        var ctrl = new _EtlJobController(scheduler.Object);
        ctrl.Wtm = MockWtmContext.CreateWtmContext(dc);

        var mockHttp = new Mock<HttpContext>();
        var session = new MockHttpSession();
        mockHttp.Setup(s => s.Session).Returns(session);
        mockHttp.Setup(x => x.Request).Returns(new DefaultHttpContext().Request);
        ctrl.ControllerContext.HttpContext = mockHttp.Object;
        ctrl.Wtm.MSD = new ModelStateServiceProvider(ctrl.ModelState);
        return ctrl;
    }

    // Helper: seed one disabled job into the named in-memory DB
    private static EtlJobDefinition SeedJob(string seed)
    {
        var dc = new EtlTestDataContext(seed, DBTypeEnum.Memory);
        var job = new EtlJobDefinition
        {
            Name = "SeedJob",
            CronExpression = "0 0 * * * ?",
            JobClassName = "TestClass",
            SourceCsKey = "src",
            TargetCsKey = "tgt",
            TargetTableName = "Orders",
            MergeKeyColumn = "Id",
            QueryTemplate = "SELECT * FROM Orders",
            Status = EtlJobStatus.Disabled
        };
        dc.EtlJobDefinitions.Add(job);
        dc.SaveChanges();
        return job;
    }

    [TestMethod]
    public void Index_returns_partial_view()
    {
        var result = _controller.Index() as PartialViewResult;

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void Search_returns_content_json()
    {
        var seed = Guid.NewGuid().ToString();
        SeedJob(seed);
        var ctrl = CreateControllerWithDb(seed);

        var result = ctrl.Search(new EtlJobSearcher()) as ContentResult;

        Assert.IsNotNull(result);
        Assert.IsNotNull(result!.Content);
    }

    [TestMethod]
    public void Create_get_returns_partial_view_with_new_vm()
    {
        var result = _controller.Create() as PartialViewResult;

        Assert.IsNotNull(result);
        Assert.IsInstanceOfType(result!.Model, typeof(EtlJobDefinitionVM));
    }

    [TestMethod]
    public void Create_post_invalid_modelstate_returns_partial_view()
    {
        _controller.ModelState.AddModelError("Entity.Name", "Required");
        var vm = _controller.Wtm.CreateVM<EtlJobDefinitionVM>();

        var result = _controller.Create(vm) as PartialViewResult;

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void Create_post_valid_vm_adds_job_and_returns_fresult()
    {
        var seed = Guid.NewGuid().ToString();
        var ctrl = CreateControllerWithDb(seed);

        // Get VM from Create GET (so Wtm/DC/MSD are properly wired)
        var rv = ctrl.Create() as PartialViewResult;
        Assert.IsNotNull(rv);
        var vm = rv!.Model as EtlJobDefinitionVM;
        Assert.IsNotNull(vm);

        vm!.Entity.Name = "NewJob";
        vm.Entity.CronExpression = "0 0 * * * ?";
        vm.Entity.JobClassName = "TestClass";
        vm.Entity.SourceCsKey = "src";
        vm.Entity.TargetCsKey = "tgt";
        vm.Entity.TargetTableName = "Orders";
        vm.Entity.MergeKeyColumn = "Id";
        vm.Entity.QueryTemplate = "SELECT * FROM Orders";
        vm.Entity.Status = EtlJobStatus.Disabled;

        var result = ctrl.Create(vm) as ContentResult;

        Assert.IsNotNull(result, "Valid Create POST should return ContentResult (FFResult)");

        // Verify persisted
        var dc = new EtlTestDataContext(seed, DBTypeEnum.Memory);
        Assert.IsTrue(dc.EtlJobDefinitions.Any(j => j.Name == "NewJob"));
    }

    [TestMethod]
    public void Edit_get_returns_partial_view_with_loaded_entity()
    {
        var seed = Guid.NewGuid().ToString();
        var job = SeedJob(seed);
        var ctrl = CreateControllerWithDb(seed);

        var result = ctrl.Edit(job.ID) as PartialViewResult;

        Assert.IsNotNull(result);
        var vm = result!.Model as EtlJobDefinitionVM;
        Assert.IsNotNull(vm);
        Assert.AreEqual(job.ID, vm!.Entity.ID);
    }

    [TestMethod]
    public void Edit_post_invalid_modelstate_returns_partial_view()
    {
        _controller.ModelState.AddModelError("Entity.Name", "Required");
        var vm = _controller.Wtm.CreateVM<EtlJobDefinitionVM>();

        var result = _controller.Edit(vm) as PartialViewResult;

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void Edit_post_valid_vm_updates_job_and_returns_fresult()
    {
        var seed = Guid.NewGuid().ToString();
        var job = SeedJob(seed);
        var ctrl = CreateControllerWithDb(seed);

        // Get VM from Edit GET so DC is wired
        var rv = ctrl.Edit(job.ID) as PartialViewResult;
        var vm = rv!.Model as EtlJobDefinitionVM;
        Assert.IsNotNull(vm);

        vm!.Entity.Name = "UpdatedJob";
        vm.FC = new Dictionary<string, object> { ["Entity.Name"] = "" };

        var result = ctrl.Edit(vm) as ContentResult;

        Assert.IsNotNull(result, "Valid Edit POST should return ContentResult (FFResult)");

        var dc = new EtlTestDataContext(seed, DBTypeEnum.Memory);
        Assert.AreEqual("UpdatedJob", dc.EtlJobDefinitions.First(j => j.ID == job.ID).Name);
    }

    [TestMethod]
    public void Delete_get_returns_partial_view_with_loaded_entity()
    {
        var seed = Guid.NewGuid().ToString();
        var job = SeedJob(seed);
        var ctrl = CreateControllerWithDb(seed);

        var result = ctrl.Delete(job.ID) as PartialViewResult;

        Assert.IsNotNull(result);
        var vm = result!.Model as EtlJobDefinitionVM;
        Assert.IsNotNull(vm);
        Assert.AreEqual(job.ID, vm!.Entity.ID);
    }

    [TestMethod]
    public void Delete_post_valid_removes_job_and_returns_fresult()
    {
        var seed = Guid.NewGuid().ToString();
        var job = SeedJob(seed);
        var ctrl = CreateControllerWithDb(seed);
        var noUse = new FormCollection(new Dictionary<string, StringValues>());

        var result = ctrl.Delete(job.ID, noUse) as ContentResult;

        Assert.IsNotNull(result, "Valid Delete POST should return ContentResult (FFResult)");

        var dc = new EtlTestDataContext(seed, DBTypeEnum.Memory);
        Assert.IsFalse(dc.EtlJobDefinitions.Any(j => j.ID == job.ID));
    }

    [TestMethod]
    public void Delete_post_running_job_returns_partial_view_with_error()
    {
        // Arrange — seed a Running job (cannot use SeedJob helper which always Disabled)
        var seed = Guid.NewGuid().ToString();
        var seedDc = new EtlTestDataContext(seed, DBTypeEnum.Memory);
        var job = new EtlJobDefinition
        {
            Name = "RunningJob",
            CronExpression = "0 0 * * * ?",
            Status = EtlJobStatus.Running
        };
        seedDc.EtlJobDefinitions.Add(job);
        seedDc.SaveChanges();

        var ctrl = CreateControllerWithDb(seed);
        var noUse = new FormCollection(new Dictionary<string, StringValues>());

        // Act
        var result = ctrl.Delete(job.ID, noUse);

        // Assert — DoDelete guard → model error → PartialView, not FFResult
        Assert.IsInstanceOfType(result, typeof(PartialViewResult),
            "Running job Delete POST should return PartialView (model error), not FFResult");

        // Job must still exist in DB
        var checkDc = new EtlTestDataContext(seed, DBTypeEnum.Memory);
        Assert.IsTrue(checkDc.EtlJobDefinitions.Any(j => j.ID == job.ID),
            "Running job should not be deleted");
    }

    // ─── InvalidOperationException → 400 tests ─────────────────────────────
    // Abort 已有此覆蓋；TriggerNow/Pause/Resume/SkipNext 之前缺失。

    [TestMethod]
    public async Task TriggerNow_returns_400_when_scheduler_throws_InvalidOperationException()
    {
        var id = Guid.NewGuid();
        _mockScheduler.Setup(x => x.TriggerNowAsync(id))
            .ThrowsAsync(new InvalidOperationException($"Job {id} is already running."));

        var result = await _controller.TriggerNow(id) as BadRequestObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(400, result!.StatusCode);
    }

    [TestMethod]
    public async Task Pause_returns_400_when_scheduler_throws_InvalidOperationException()
    {
        var id = Guid.NewGuid();
        _mockScheduler.Setup(x => x.PauseAsync(id))
            .ThrowsAsync(new InvalidOperationException($"Job {id} is already paused."));

        var result = await _controller.Pause(id) as BadRequestObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(400, result!.StatusCode);
    }

    [TestMethod]
    public async Task Resume_returns_400_when_scheduler_throws_InvalidOperationException()
    {
        var id = Guid.NewGuid();
        _mockScheduler.Setup(x => x.ResumeAsync(id))
            .ThrowsAsync(new InvalidOperationException($"Job {id} is not paused."));

        var result = await _controller.Resume(id) as BadRequestObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(400, result!.StatusCode);
    }

    [TestMethod]
    public async Task SkipNext_returns_400_when_scheduler_throws_InvalidOperationException()
    {
        var id = Guid.NewGuid();
        _mockScheduler.Setup(x => x.SkipNextAsync(id))
            .ThrowsAsync(new InvalidOperationException($"Job {id} not found."));

        var result = await _controller.SkipNext(id) as BadRequestObjectResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(400, result!.StatusCode);
    }
}
