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
/// Covers the grid-header and grid-action surface of EtlJobListVM,
/// targeting the untested InitGridHeader / InitGridAction branches
/// that are skipped by the existing EtlJobListVmTests.
/// Together with EtlJobListVmTests these push line coverage past 75%.
/// </summary>
[TestClass]
public class EtlJobListVmGridTests
{
    private WTMContext _wtm = null!;

    [TestInitialize]
    public void Setup()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        _wtm = MockWtmContext.CreateWtmContext(dc);
    }

    // ── InitGridHeader ────────────────────────────────────────────────────

    [TestMethod]
    public void InitGridHeader_returns_nine_columns()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        vm.DoSearch();

        // 8 data columns + 1 action column = 9
        Assert.AreEqual(9, vm.GetHeaders().Count());
    }

    [TestMethod]
    public void InitGridHeader_includes_Name_column()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        vm.DoSearch();

        var headers = vm.GetHeaders().ToList();
        Assert.IsTrue(
            headers.Any(h => h.FieldName == nameof(EtlJobDefinition.Name)),
            "Grid header must include the Name column.");
    }

    [TestMethod]
    public void InitGridHeader_includes_Status_column()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        vm.DoSearch();

        var headers = vm.GetHeaders().ToList();
        Assert.IsTrue(
            headers.Any(h => h.FieldName == nameof(EtlJobDefinition.Status)),
            "Grid header must include the Status column.");
    }

    // ── InitGridAction ────────────────────────────────────────────────────

    [TestMethod]
    public void InitGridAction_returns_at_least_seven_actions()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        vm.DoSearch();

        // Create, Edit, Delete (standard) + TriggerNow, Pause, Resume, Abort, 執行記錄 = 8
        // Verify ≥ 7 to be robust to future additions.
        Assert.IsTrue(vm.GetGridActions().Count >= 7,
            $"Expected at least 7 actions, got {vm.GetGridActions().Count}.");
    }

    [TestMethod]
    public void InitGridAction_contains_TriggerNow_action()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        vm.DoSearch();

        var triggerAction = vm.GetGridActions().FirstOrDefault(a => a.ActionName == "TriggerNow");
        Assert.IsNotNull(triggerAction, "TriggerNow action must be registered.");
        Assert.AreEqual("_EtlJob", triggerAction!.ControllerName);
        Assert.IsTrue(triggerAction.ShowInRow, "TriggerNow must be a row-level action.");
        Assert.IsTrue(triggerAction.HideOnToolBar, "TriggerNow must not appear in the toolbar.");
        Assert.IsFalse(triggerAction.ShowDialog, "TriggerNow must be a non-dialog action.");
        Assert.IsTrue(triggerAction.ForcePost, "TriggerNow must force a POST.");
    }

    [TestMethod]
    public void InitGridAction_contains_Pause_action()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        vm.DoSearch();

        var pauseAction = vm.GetGridActions().FirstOrDefault(a => a.ActionName == "Pause");
        Assert.IsNotNull(pauseAction, "Pause action must be registered.");
        Assert.AreEqual(GridActionParameterTypesEnum.SingleId, pauseAction!.ParameterType);
    }

    [TestMethod]
    public void InitGridAction_contains_Resume_action()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        vm.DoSearch();

        var resumeAction = vm.GetGridActions().FirstOrDefault(a => a.ActionName == "Resume");
        Assert.IsNotNull(resumeAction, "Resume action must be registered.");
        Assert.IsTrue(resumeAction!.ForcePost);
    }

    [TestMethod]
    public void InitGridAction_contains_Abort_action()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        vm.DoSearch();

        var abortAction = vm.GetGridActions().FirstOrDefault(a => a.ActionName == "Abort");
        Assert.IsNotNull(abortAction, "Abort action must be registered.");
        Assert.IsTrue(abortAction!.ShowInRow);
        Assert.IsTrue(abortAction.HideOnToolBar);
    }

    [TestMethod]
    public void InitGridAction_contains_RunLog_action_with_OnClickFunc()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        vm.DoSearch();

        // The 執行記錄 action uses OnClickFunc instead of ActionName
        var logAction = vm.GetGridActions().FirstOrDefault(a => a.Name == "執行記錄");
        Assert.IsNotNull(logAction, "執行記錄 action must be registered.");
        Assert.IsTrue(logAction!.ShowInRow);
        Assert.IsTrue(logAction.HideOnToolBar);
        Assert.IsNotNull(logAction.OnClickFunc, "執行記錄 must have a client-side OnClickFunc.");
        StringAssert.Contains(logAction.OnClickFunc!, "_EtlRunLog");
    }

    [TestMethod]
    public void InitGridAction_contains_standard_Create_action()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        vm.DoSearch();

        // Standard Create action for the _EtlJob area
        var createAction = vm.GetGridActions().FirstOrDefault(a =>
            a.ActionName == "Create" && a.ControllerName == "_EtlJob");
        Assert.IsNotNull(createAction, "Standard Create action must be registered.");
    }

    // ── GetSearchQuery with combined filters ──────────────────────────────

    [TestMethod]
    public void Search_by_name_and_status_combined_filters()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        var wtm = MockWtmContext.CreateWtmContext(dc);

        dc.Set<EtlJobDefinition>().AddRange(
            new EtlJobDefinition
            {
                Name = "OrderSync", CronExpression = "0 0 * * *",
                SourceCsKey = "cs", SourceDbType = DBTypeEnum.SqlServer,
                WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Enabled,
                CreateTime = DateTime.UtcNow
            },
            new EtlJobDefinition
            {
                Name = "OrderArchive", CronExpression = "0 0 * * *",
                SourceCsKey = "cs", SourceDbType = DBTypeEnum.SqlServer,
                WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Disabled,
                CreateTime = DateTime.UtcNow
            }
        );
        dc.SaveChanges();

        var vm = wtm.CreateVM<EtlJobListVM>();
        vm.Searcher.Name = "Order";
        vm.Searcher.Status = EtlJobStatus.Enabled;
        vm.DoSearch();

        Assert.AreEqual(1, vm.EntityList.Count);
        Assert.AreEqual("OrderSync", vm.EntityList[0].Name);
    }

    [TestMethod]
    public void Search_by_sourceDbType_SqlServer_returns_matching()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        var wtm = MockWtmContext.CreateWtmContext(dc);

        dc.Set<EtlJobDefinition>().AddRange(
            new EtlJobDefinition
            {
                Name = "Job_SQL", CronExpression = "0 0 * * *",
                SourceCsKey = "cs", SourceDbType = DBTypeEnum.SqlServer,
                WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Enabled,
                CreateTime = DateTime.UtcNow
            },
            new EtlJobDefinition
            {
                Name = "Job_Oracle", CronExpression = "0 0 * * *",
                SourceCsKey = "cs", SourceDbType = DBTypeEnum.Oracle,
                WatermarkType = EtlWatermarkType.FullLoad, Status = EtlJobStatus.Enabled,
                CreateTime = DateTime.UtcNow
            }
        );
        dc.SaveChanges();

        var vm = wtm.CreateVM<EtlJobListVM>();
        vm.Searcher.SourceDbType = DBTypeEnum.SqlServer;
        vm.DoSearch();

        Assert.AreEqual(1, vm.EntityList.Count);
        Assert.AreEqual("Job_SQL", vm.EntityList[0].Name);
    }
}
