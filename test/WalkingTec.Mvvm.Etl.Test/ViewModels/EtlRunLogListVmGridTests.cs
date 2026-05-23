#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.ViewModels;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.ViewModels;

/// <summary>
/// Covers the grid-header and grid-action surface of EtlRunLogListVM
/// that is not exercised by the existing EtlRunLogListVmTests.
/// Together with EtlRunLogListVmTests these push line coverage above 75%.
/// </summary>
[TestClass]
public class EtlRunLogListVmGridTests
{
    private WTMContext _wtm = null!;
    private Guid _jobId = Guid.NewGuid();

    [TestInitialize]
    public void Setup()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        _wtm = MockWtmContext.CreateWtmContext(dc);

        dc.Set<EtlJobDefinition>().Add(new EtlJobDefinition
        {
            ID = _jobId, Name = "Job1", CronExpression = "0 0 * * *",
            SourceCsKey = "cs", SourceDbType = DBTypeEnum.SqlServer,
            WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Enabled
        });
        dc.Set<EtlRunLog>().Add(new EtlRunLog
        {
            JobId = _jobId, Trigger = EtlRunTrigger.Scheduled,
            Result = EtlRunResult.Success,
            StartedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            FinishedAt = new DateTime(2026, 4, 1, 0, 1, 0, DateTimeKind.Utc),
            ElapsedMs = 60000
        });
        dc.SaveChanges();
    }

    // ── InitGridHeader ────────────────────────────────────────────────────

    [TestMethod]
    public void InitGridHeader_returns_eleven_columns()
    {
        var vm = _wtm.CreateVM<EtlRunLogListVM>();
        vm.DoSearch();

        // 10 data columns + 1 action column = 11
        Assert.AreEqual(11, vm.GetHeaders().Count());
    }

    [TestMethod]
    public void InitGridHeader_includes_Result_column()
    {
        var vm = _wtm.CreateVM<EtlRunLogListVM>();
        vm.DoSearch();

        Assert.IsTrue(
            vm.GetHeaders().Any(h => h.FieldName == nameof(EtlRunLog.Result)),
            "Grid header must include the Result column.");
    }

    [TestMethod]
    public void InitGridHeader_includes_Trigger_column()
    {
        var vm = _wtm.CreateVM<EtlRunLogListVM>();
        vm.DoSearch();

        Assert.IsTrue(
            vm.GetHeaders().Any(h => h.FieldName == nameof(EtlRunLog.Trigger)),
            "Grid header must include the Trigger column.");
    }

    [TestMethod]
    public void InitGridHeader_includes_ExtractedRows_column()
    {
        var vm = _wtm.CreateVM<EtlRunLogListVM>();
        vm.DoSearch();

        Assert.IsTrue(
            vm.GetHeaders().Any(h => h.FieldName == nameof(EtlRunLog.ExtractedRows)),
            "Grid header must include the ExtractedRows column.");
    }

    [TestMethod]
    public void InitGridHeader_includes_LoadedRows_column()
    {
        var vm = _wtm.CreateVM<EtlRunLogListVM>();
        vm.DoSearch();

        Assert.IsTrue(
            vm.GetHeaders().Any(h => h.FieldName == nameof(EtlRunLog.LoadedRows)),
            "Grid header must include the LoadedRows column.");
    }

    // ── InitGridAction ────────────────────────────────────────────────────

    [TestMethod]
    public void InitGridAction_returns_at_least_one_action()
    {
        var vm = _wtm.CreateVM<EtlRunLogListVM>();
        vm.DoSearch();

        Assert.IsTrue(vm.GetGridActions().Count >= 1,
            "EtlRunLogListVM must have at least one action (Rerun).");
    }

    [TestMethod]
    public void InitGridAction_contains_Rerun_action()
    {
        var vm = _wtm.CreateVM<EtlRunLogListVM>();
        vm.DoSearch();

        var rerunAction = vm.GetGridActions().FirstOrDefault(a => a.ActionName == "Rerun");
        Assert.IsNotNull(rerunAction, "Rerun action must be registered.");
        Assert.AreEqual("_EtlRunLog", rerunAction!.ControllerName);
        Assert.IsTrue(rerunAction.ShowInRow, "Rerun must be a row-level action.");
        Assert.IsTrue(rerunAction.HideOnToolBar, "Rerun must not appear in the toolbar.");
        Assert.IsFalse(rerunAction.ShowDialog, "Rerun must be a non-dialog action.");
        Assert.IsTrue(rerunAction.ForcePost, "Rerun must force a POST.");
        Assert.AreEqual(GridActionParameterTypesEnum.SingleId, rerunAction.ParameterType);
    }

    [TestMethod]
    public void InitGridAction_Rerun_has_prompt_message()
    {
        var vm = _wtm.CreateVM<EtlRunLogListVM>();
        vm.DoSearch();

        var rerunAction = vm.GetGridActions().First(a => a.ActionName == "Rerun");
        Assert.IsFalse(string.IsNullOrWhiteSpace(rerunAction.PromptMessage),
            "Rerun action should have a confirmation prompt.");
    }

    // ── GetSearchQuery: additional combinations ───────────────────────────

    [TestMethod]
    public void Search_by_both_job_and_result_returns_intersection()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        var wtm = MockWtmContext.CreateWtmContext(dc);
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();

        dc.Set<EtlJobDefinition>().AddRange(
            new EtlJobDefinition
            {
                ID = jobA, Name = "A", CronExpression = "0 0 * * *",
                SourceCsKey = "cs", SourceDbType = DBTypeEnum.SqlServer,
                WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Enabled
            },
            new EtlJobDefinition
            {
                ID = jobB, Name = "B", CronExpression = "0 0 * * *",
                SourceCsKey = "cs", SourceDbType = DBTypeEnum.SqlServer,
                WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Enabled
            }
        );
        dc.Set<EtlRunLog>().AddRange(
            new EtlRunLog
            {
                JobId = jobA, Trigger = EtlRunTrigger.Scheduled,
                Result = EtlRunResult.Success,
                StartedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                FinishedAt = new DateTime(2026, 4, 1, 0, 1, 0, DateTimeKind.Utc)
            },
            new EtlRunLog
            {
                JobId = jobA, Trigger = EtlRunTrigger.Manual,
                Result = EtlRunResult.Failed,
                StartedAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc),
                FinishedAt = new DateTime(2026, 4, 2, 0, 1, 0, DateTimeKind.Utc)
            },
            new EtlRunLog
            {
                JobId = jobB, Trigger = EtlRunTrigger.Scheduled,
                Result = EtlRunResult.Success,
                StartedAt = new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc),
                FinishedAt = new DateTime(2026, 4, 3, 0, 1, 0, DateTimeKind.Utc)
            }
        );
        dc.SaveChanges();

        var vm = wtm.CreateVM<EtlRunLogListVM>();
        vm.Searcher.JobId = jobA;
        vm.Searcher.Result = EtlRunResult.Failed;
        vm.DoSearch();

        Assert.AreEqual(1, vm.EntityList.Count);
        Assert.AreEqual(EtlRunResult.Failed, vm.EntityList[0].Result);
    }

    [TestMethod]
    public void Search_with_only_StartedAtBegin_returns_from_that_date()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        var wtm = MockWtmContext.CreateWtmContext(dc);
        var jobId = Guid.NewGuid();

        dc.Set<EtlJobDefinition>().Add(new EtlJobDefinition
        {
            ID = jobId, Name = "J", CronExpression = "0 0 * * *",
            SourceCsKey = "cs", SourceDbType = DBTypeEnum.SqlServer,
            WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Enabled
        });
        dc.Set<EtlRunLog>().AddRange(
            new EtlRunLog
            {
                JobId = jobId, Trigger = EtlRunTrigger.Scheduled, Result = EtlRunResult.Success,
                StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FinishedAt = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)
            },
            new EtlRunLog
            {
                JobId = jobId, Trigger = EtlRunTrigger.Scheduled, Result = EtlRunResult.Success,
                StartedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                FinishedAt = new DateTime(2026, 3, 1, 0, 1, 0, DateTimeKind.Utc)
            }
        );
        dc.SaveChanges();

        var vm = wtm.CreateVM<EtlRunLogListVM>();
        vm.Searcher.StartedAtBegin = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        vm.DoSearch();

        Assert.AreEqual(1, vm.EntityList.Count);
        Assert.AreEqual(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), vm.EntityList[0].StartedAt);
    }

    [TestMethod]
    public void Search_with_only_StartedAtEnd_returns_up_to_that_date()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        var wtm = MockWtmContext.CreateWtmContext(dc);
        var jobId = Guid.NewGuid();

        dc.Set<EtlJobDefinition>().Add(new EtlJobDefinition
        {
            ID = jobId, Name = "J", CronExpression = "0 0 * * *",
            SourceCsKey = "cs", SourceDbType = DBTypeEnum.SqlServer,
            WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Enabled
        });
        dc.Set<EtlRunLog>().AddRange(
            new EtlRunLog
            {
                JobId = jobId, Trigger = EtlRunTrigger.Scheduled, Result = EtlRunResult.Success,
                StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FinishedAt = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)
            },
            new EtlRunLog
            {
                JobId = jobId, Trigger = EtlRunTrigger.Scheduled, Result = EtlRunResult.Success,
                StartedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                FinishedAt = new DateTime(2026, 5, 1, 0, 1, 0, DateTimeKind.Utc)
            }
        );
        dc.SaveChanges();

        var vm = wtm.CreateVM<EtlRunLogListVM>();
        vm.Searcher.StartedAtEnd = new DateTime(2026, 2, 28, 23, 59, 59, DateTimeKind.Utc);
        vm.DoSearch();

        Assert.AreEqual(1, vm.EntityList.Count);
        Assert.AreEqual(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), vm.EntityList[0].StartedAt);
    }
}
