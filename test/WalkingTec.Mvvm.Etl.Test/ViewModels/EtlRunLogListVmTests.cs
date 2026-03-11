#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.ViewModels;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.ViewModels;

[TestClass]
public class EtlRunLogListVmTests
{
    private WTMContext _wtm = null!;
    private Guid _jobId1;
    private Guid _jobId2;

    [TestInitialize]
    public void Setup()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        _wtm = MockWtmContext.CreateWtmContext(dc);

        _jobId1 = Guid.NewGuid();
        _jobId2 = Guid.NewGuid();

        // Seed jobs
        dc.Set<EtlJobDefinition>().AddRange(
            new EtlJobDefinition
            {
                ID = _jobId1, Name = "Job1", CronExpression = "0 0 * * *",
                SourceCsKey = "cs1", SourceDbType = DBTypeEnum.SqlServer,
                WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Enabled
            },
            new EtlJobDefinition
            {
                ID = _jobId2, Name = "Job2", CronExpression = "0 0 * * *",
                SourceCsKey = "cs2", SourceDbType = DBTypeEnum.SqlServer,
                WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Enabled
            }
        );

        // Seed run logs
        dc.Set<EtlRunLog>().AddRange(
            new EtlRunLog
            {
                JobId = _jobId1, Trigger = EtlRunTrigger.Scheduled,
                Result = EtlRunResult.Success, ExtractedRows = 1000, LoadedRows = 1000,
                StartedAt = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc),
                FinishedAt = new DateTime(2026, 3, 10, 0, 5, 0, DateTimeKind.Utc), ElapsedMs = 300000
            },
            new EtlRunLog
            {
                JobId = _jobId1, Trigger = EtlRunTrigger.Manual,
                Result = EtlRunResult.Failed, ErrorMessage = "Connection timeout",
                StartedAt = new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc),
                FinishedAt = new DateTime(2026, 3, 11, 0, 1, 0, DateTimeKind.Utc), ElapsedMs = 60000
            },
            new EtlRunLog
            {
                JobId = _jobId2, Trigger = EtlRunTrigger.Scheduled,
                Result = EtlRunResult.Success, ExtractedRows = 500, LoadedRows = 500,
                StartedAt = new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc),
                FinishedAt = new DateTime(2026, 3, 12, 0, 2, 0, DateTimeKind.Utc), ElapsedMs = 120000
            }
        );
        dc.SaveChanges();
    }

    private EtlRunLogListVM CreateVm()
    {
        return _wtm.CreateVM<EtlRunLogListVM>();
    }

    [TestMethod]
    public void Search_by_job_id_filters_correctly()
    {
        var vm = CreateVm();
        vm.Searcher.JobId = _jobId1;
        vm.DoSearch();

        Assert.AreEqual(2, vm.EntityList.Count);
        Assert.IsTrue(vm.EntityList.All(x => x.JobId == _jobId1));
    }

    [TestMethod]
    public void Search_by_result_filters_correctly()
    {
        var vm = CreateVm();
        vm.Searcher.Result = EtlRunResult.Failed;
        vm.DoSearch();

        Assert.AreEqual(1, vm.EntityList.Count);
        Assert.AreEqual("Connection timeout", vm.EntityList[0].ErrorMessage);
    }

    [TestMethod]
    public void Search_by_date_range_filters_correctly()
    {
        var vm = CreateVm();
        vm.Searcher.StartedAtBegin = new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc);
        vm.Searcher.StartedAtEnd = new DateTime(2026, 3, 12, 23, 59, 59, DateTimeKind.Utc);
        vm.DoSearch();

        Assert.AreEqual(2, vm.EntityList.Count);
    }

    [TestMethod]
    public void List_orders_by_started_at_desc()
    {
        var vm = CreateVm();
        vm.DoSearch();

        Assert.AreEqual(3, vm.EntityList.Count);
        Assert.IsTrue(vm.EntityList[0].StartedAt >= vm.EntityList[1].StartedAt);
        Assert.IsTrue(vm.EntityList[1].StartedAt >= vm.EntityList[2].StartedAt);
    }

    [TestMethod]
    public void Search_by_trigger_filters_correctly()
    {
        var vm = CreateVm();
        vm.Searcher.Trigger = EtlRunTrigger.Manual;
        vm.DoSearch();

        Assert.AreEqual(1, vm.EntityList.Count);
        Assert.AreEqual(EtlRunTrigger.Manual, vm.EntityList[0].Trigger);
    }
}
