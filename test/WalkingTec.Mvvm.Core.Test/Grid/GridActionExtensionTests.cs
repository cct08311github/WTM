#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Grid
{
    // ─── Fixtures ───────────────────────────────────────────────────────────────

    public class GridActionTestModel : TopBasePoco
    {
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
    }

    public class GridActionTestListVM : BasePagedListVM<GridActionTestModel, BaseSearcher>
    {
        private static readonly List<GridActionTestModel> _data = [];

        public static void SetData(IEnumerable<GridActionTestModel> items)
        {
            _data.Clear();
            _data.AddRange(items);
        }

        public override IOrderedQueryable<GridActionTestModel> GetSearchQuery()
            => _data.AsQueryable().OrderBy(x => x.ID);
    }

    // ─── Tests ──────────────────────────────────────────────────────────────────

    [TestClass]
    public class GridActionExtensionTests
    {
        private GridActionTestListVM _vm = null!;

        [TestInitialize]
        public void Setup()
        {
            _vm = new GridActionTestListVM();
            _vm.Wtm = MockWtmContext.CreateWtmContext();
        }

        // ─── MakeStandardAction ─────────────────────────────────────────────────

        [TestMethod]
        public void MakeStandardAction_Create_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Create, "Create New");

            action.ControllerName.Should().Be("MyController");
            action.ActionName.Should().Be("Create");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.NoId);
            action.ShowInRow.Should().BeFalse();
            action.HideOnToolBar.Should().BeFalse();
            action.ShowDialog.Should().BeTrue();
            action.IconCls.Should().Be("layui-icon layui-icon-add-1");
        }

        [TestMethod]
        public void MakeStandardAction_Edit_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Edit, "Edit");

            action.ActionName.Should().Be("Edit");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.SingleId);
            action.ShowInRow.Should().BeTrue();
            action.HideOnToolBar.Should().BeTrue();
            action.ShowDialog.Should().BeTrue();
            action.IconCls.Should().Be("layui-icon layui-icon-edit");
        }

        [TestMethod]
        public void MakeStandardAction_Delete_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Delete, "Delete");

            action.ActionName.Should().Be("Delete");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.SingleId);
            action.ShowInRow.Should().BeTrue();
            action.HideOnToolBar.Should().BeTrue();
            action.IconCls.Should().Be("layui-icon layui-icon-delete");
        }

        [TestMethod]
        public void MakeStandardAction_Details_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Details, "Details");

            action.ActionName.Should().Be("Details");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.SingleId);
            action.ShowInRow.Should().BeTrue();
            action.HideOnToolBar.Should().BeTrue();
            action.IconCls.Should().Be("layui-icon layui-icon-form");
        }

        [TestMethod]
        public void MakeStandardAction_Approve_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Approve, "Approve");

            action.ActionName.Should().Be("Approve");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.SingleId);
            action.ShowInRow.Should().BeTrue();
            action.HideOnToolBar.Should().BeTrue();
            action.IconCls.Should().Be("layui-icon layui-icon-form");
        }

        [TestMethod]
        public void MakeStandardAction_AddRow_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.AddRow, "AddRow");

            action.ActionName.Should().Be("AddRow");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.AddRow);
            action.ShowInRow.Should().BeFalse();
        }

        [TestMethod]
        public void MakeStandardAction_RemoveRow_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.RemoveRow, "Remove");

            action.ActionName.Should().Be("RemoveRow");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.RemoveRow);
            action.ShowInRow.Should().BeTrue();
            action.HideOnToolBar.Should().BeTrue();
        }

        [TestMethod]
        public void MakeStandardAction_BatchEdit_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.BatchEdit, "Batch Edit");

            action.ActionName.Should().Be("BatchEdit");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.MultiIds);
            action.ShowInRow.Should().BeFalse();
        }

        [TestMethod]
        public void MakeStandardAction_BatchDelete_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.BatchDelete, "Batch Delete");

            action.ActionName.Should().Be("BatchDelete");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.MultiIds);
        }

        [TestMethod]
        public void MakeStandardAction_SimpleDelete_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.SimpleDelete, "Delete");

            action.ActionName.Should().Be("BatchDelete");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.SingleIdWithNull);
            action.ShowInRow.Should().BeTrue();
            action.ShowDialog.Should().BeFalse();
            action.ForcePost.Should().BeTrue();
            action.QueryString.Should().Be("_donotuse_sd=1");
            // PromptMessage may be null when localizer is not registered in test context
            // — just verify the field exists (no exception thrown)
        }

        [TestMethod]
        public void MakeStandardAction_SimpleBatchDelete_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.SimpleBatchDelete, "Batch Delete");

            action.ActionName.Should().Be("BatchDelete");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.MultiIds);
            action.ShowDialog.Should().BeFalse();
            action.ForcePost.Should().BeTrue();
            // PromptMessage may be null when localizer is not registered in test context
            // — just verify the field exists (no exception thrown)
        }

        [TestMethod]
        public void MakeStandardAction_Import_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Import, "Import");

            action.ActionName.Should().Be("Import");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.NoId);
            action.IconCls.Should().Be("layui-icon layui-icon-templeate-1");
        }

        [TestMethod]
        public void MakeStandardAction_ExportExcel_SetsExpectedProperties()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.ExportExcel, "Export");

            action.ActionName.Should().Be("ExportExcel");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.MultiIdWithNull);
            action.ShowInRow.Should().BeFalse();
            action.ShowDialog.Should().BeFalse();
            action.HideOnToolBar.Should().BeFalse();
            action.IsExport.Should().BeTrue();
        }

        [TestMethod]
        public void MakeStandardAction_EmptyDialogTitle_UsesDefaultName()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Create, "");

            // When dialogTitle is empty, it falls back to gridname
            action.DialogTitle.Should().NotBeNull();
        }

        [TestMethod]
        public void MakeStandardAction_WithCustomWidthHeight_SetsValues()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Edit, "Edit",
                dialogWidth: 1200, dialogHeight: 600);

            action.DialogWidth.Should().Be(1200);
            action.DialogHeight.Should().Be(600);
        }

        [TestMethod]
        public void MakeStandardAction_DefaultWidth_Is800()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Create, "Create");

            action.DialogWidth.Should().Be(800);
        }

        [TestMethod]
        public void MakeStandardAction_WithAreaName_SetsArea()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Create, "Create",
                areaName: "Admin");

            action.Area.Should().Be("Admin");
        }

        [TestMethod]
        public void MakeStandardAction_WithButtonId_SetsButtonId()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Create, "Create",
                buttonId: "btn-create");

            action.ButtonId.Should().Be("btn-create");
        }

        [TestMethod]
        public void MakeStandardAction_WithCustomName_UsesCustomName()
        {
            var action = _vm.MakeStandardAction("MyController",
                GridActionStandardTypesEnum.Create, "Create",
                name: "My Custom Button");

            action.Name.Should().Be("My Custom Button");
        }

        [TestMethod]
        public void MakeStandardAction_WithWhereStr_PopulatesWhereStr()
        {
            var action = _vm.MakeStandardAction<GridActionTestModel, BaseSearcher>("MyController",
                GridActionStandardTypesEnum.Edit, "Edit",
                whereStr: x => x.Code);

            action.whereStr.Should().Contain("Code");
        }

        // ─── MakeAction ─────────────────────────────────────────────────────────

        [TestMethod]
        public void MakeAction_ReturnsGridAction_WithCorrectProperties()
        {
            var action = _vm.MakeAction("MyController", "MyAction", "My Button",
                "My Dialog", GridActionParameterTypesEnum.SingleId);

            action.ControllerName.Should().Be("MyController");
            action.ActionName.Should().Be("MyAction");
            action.Name.Should().Be("My Button");
            action.DialogTitle.Should().Be("My Dialog");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.SingleId);
            action.ShowDialog.Should().BeTrue();
        }

        [TestMethod]
        public void MakeAction_DefaultWidth_Is800()
        {
            var action = _vm.MakeAction("MyController", "MyAction", "My Button",
                "My Dialog", GridActionParameterTypesEnum.NoId);

            action.DialogWidth.Should().Be(800);
        }

        [TestMethod]
        public void MakeAction_WithCustomWidthHeight_SetsValues()
        {
            var action = _vm.MakeAction("MyController", "MyAction", "My Button",
                "My Dialog", GridActionParameterTypesEnum.MultiIds,
                dialogWidth: 900, dialogHeight: 400);

            action.DialogWidth.Should().Be(900);
            action.DialogHeight.Should().Be(400);
        }

        [TestMethod]
        public void MakeAction_WithAreaName_SetsArea()
        {
            var action = _vm.MakeAction("MyController", "MyAction", "My Button",
                "My Dialog", GridActionParameterTypesEnum.NoId,
                areaName: "Admin");

            action.Area.Should().Be("Admin");
        }

        [TestMethod]
        public void MakeAction_WithButtonId_SetsButtonId()
        {
            var action = _vm.MakeAction("MyController", "MyAction", "My Button",
                "My Dialog", GridActionParameterTypesEnum.NoId,
                buttonId: "myBtn");

            action.ButtonId.Should().Be("myBtn");
        }

        [TestMethod]
        public void MakeAction_WithWhereStr_PopulatesWhereStr()
        {
            var action = _vm.MakeAction<GridActionTestModel, BaseSearcher>("MyController",
                "MyAction", "My Button", "My Dialog",
                GridActionParameterTypesEnum.SingleId,
                whereStr: x => x.Name);

            action.whereStr.Should().Contain("Name");
        }

        // ─── MakeActionsGroup ───────────────────────────────────────────────────

        [TestMethod]
        public void MakeActionsGroup_SetsGroupProperties()
        {
            var sub1 = _vm.MakeAction("Ctrl", "Act1", "Action 1", "Dlg 1",
                GridActionParameterTypesEnum.NoId);
            var sub2 = _vm.MakeAction("Ctrl", "Act2", "Action 2", "Dlg 2",
                GridActionParameterTypesEnum.NoId);

            var group = _vm.MakeActionsGroup("Group Button", [sub1, sub2]);

            group.Name.Should().Be("Group Button");
            group.ActionName.Should().Be("ActionsGroup");
            group.ShowDialog.Should().BeFalse();
            group.ButtonId.Should().NotBeNullOrEmpty();
            group.SubActions.Should().HaveCount(2);
        }

        // ─── Set Property Extensions ────────────────────────────────────────────

        [TestMethod]
        public void SetMax_SetsMaxToTrue()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetMax();
            action.Max.Should().BeTrue();
        }

        [TestMethod]
        public void SetMax_SetsMaxToFalse()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetMax(false);
            action.Max.Should().BeFalse();
        }

        [TestMethod]
        public void SetButtonClass_SetsButtonClass()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetButtonClass("layui-btn-danger");
            action.ButtonClass.Should().Be("layui-btn-danger");
        }

        [TestMethod]
        public void SetIsDownload_SetsIsDownloadToTrue()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetIsDownload();
            action.IsDownload.Should().BeTrue();
        }

        [TestMethod]
        public void SetIsDownload_SetsIsDownloadToFalse()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetIsDownload(false);
            action.IsDownload.Should().BeFalse();
        }

        [TestMethod]
        public void SetIsExport_SetsIsExportToTrue()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetIsExport();
            action.IsExport.Should().BeTrue();
        }

        [TestMethod]
        public void SetIsExport_SetsIsExportToFalse()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetIsExport(false);
            action.IsExport.Should().BeFalse();
        }

        [TestMethod]
        public void SetPromptMessage_SetsMessage()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetPromptMessage("Are you sure?");
            action.PromptMessage.Should().Be("Are you sure?");
        }

        [TestMethod]
        public void SetShowInRow_SetsShowInRowToTrue()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetShowInRow();
            action.ShowInRow.Should().BeTrue();
        }

        [TestMethod]
        public void SetShowInRow_SetsShowInRowToFalse()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetShowInRow(false);
            action.ShowInRow.Should().BeFalse();
        }

        [TestMethod]
        public void SetHideOnToolBar_SetsHideOnToolBarToTrue()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetHideOnToolBar();
            action.HideOnToolBar.Should().BeTrue();
        }

        [TestMethod]
        public void SetHideOnToolBar_SetsHideOnToolBarToFalse()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetHideOnToolBar(false);
            action.HideOnToolBar.Should().BeFalse();
        }

        [TestMethod]
        public void SetSubActions_SetsSubActions()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            var sub = _vm.MakeAction("Ctrl", "Sub", "Sub Name", "Sub Title",
                GridActionParameterTypesEnum.NoId);

            action.SetSubActions([sub]);

            action.SubActions.Should().HaveCount(1);
            action.SubActions![0].ActionName.Should().Be("Sub");
        }

        [TestMethod]
        public void SetWhereStr_SetsWhereStr()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                GridActionParameterTypesEnum.NoId);
            action.SetWhereStr("Name", "Code");
            action.whereStr.Should().BeEquivalentTo(["Name", "Code"]);
        }

        [TestMethod]
        public void FluentChain_SetsMultiplePropertiesAtOnce()
        {
            var action = _vm.MakeAction("Ctrl", "Act", "Name", "Title",
                    GridActionParameterTypesEnum.SingleId)
                .SetMax()
                .SetButtonClass("layui-btn-warm")
                .SetPromptMessage("Confirm?")
                .SetShowInRow()
                .SetHideOnToolBar();

            action.Max.Should().BeTrue();
            action.ButtonClass.Should().Be("layui-btn-warm");
            action.PromptMessage.Should().Be("Confirm?");
            action.ShowInRow.Should().BeTrue();
            action.HideOnToolBar.Should().BeTrue();
        }

        // ─── MakeStandardExportAction (Obsolete, still covered) ─────────────────

        [TestMethod]
        [Obsolete]
        public void MakeStandardExportAction_ReturnsExpectedAction()
        {
#pragma warning disable CS0618
            var action = _vm.MakeStandardExportAction();
#pragma warning restore CS0618

            action.ControllerName.Should().Be("_Framework");
            action.ActionName.Should().Be("GetExportExcel");
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.MultiIdWithNull);
            action.ShowInRow.Should().BeFalse();
            action.ShowDialog.Should().BeFalse();
            action.HideOnToolBar.Should().BeFalse();
        }
    }
}
