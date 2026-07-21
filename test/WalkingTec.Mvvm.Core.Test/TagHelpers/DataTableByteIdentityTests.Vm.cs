#nullable enable
using System.Collections.Generic;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

// ---------------------------------------------------------------------------
// Issue #470 Slice O1 stage 1: ListVM fixtures for DataTableByteIdentityTests.
// Each VM is deliberately minimal — only what is needed to reach the specific
// DataTableTagHelper.Process() code path the matching test config exercises.
// GridAction.Url is a computed property (ControllerName/Area/ActionName), so
// every test action here leaves ControllerName/ActionName unset (Url == "")
// to satisfy AddSubButton's `string.IsNullOrEmpty(item.Url)` gate without
// needing a real IWtmAuthorizationService — Area alone still gives each
// action a distinct `lay-event` (Area+ControllerName+ActionName+QueryString).
// ---------------------------------------------------------------------------

/// <summary>
/// Two plain columns (LoginName, Name). Reused by every config that only
/// varies DataTableTagHelper attributes (default, UseLocalData,
/// EnableHeaderFilter, EnableAnalysis, IsInSelector, AutoSearch,
/// SearcherExpanded, DetailGridPrix, EnableClientExport) — the column/action
/// shape is irrelevant to those flags, so one VM shape keeps the fixture
/// matrix honest about what's actually varying.
/// </summary>
public class PlainColumnsListVM : BasePagedListVM<Student, BaseSearcher>
{
    protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
    {
        return new List<GridColumn<Student>>
        {
            this.MakeGridHeader(x => x.LoginName),
            this.MakeGridHeader(x => x.Name),
        };
    }
}

/// <summary>
/// One plain column + one AggregateType=Sum column (#431 BuildAggregateFooterScript /
/// NeedShowTotal / totalRow:true).
/// </summary>
public class AggregateColumnsListVM : BasePagedListVM<AuditedProduct, BaseSearcher>
{
    protected override IEnumerable<IGridColumn<AuditedProduct>> InitGridHeader()
    {
        return new List<GridColumn<AuditedProduct>>
        {
            this.MakeGridHeader(x => x.Name),
            this.MakeGridHeader(x => x.Price)
                .SetAggregate(GridAggregateTypeEnum.Sum)
                .SetShowTotal(),
        };
    }
}

/// <summary>
/// One column per rich display type (#432): Progress, Tag (with colour), Image
/// (with size), Currency (fixed format), Currency (per-row CurrencyCodeField).
/// </summary>
public class RichColumnsListVM : BasePagedListVM<Student, BaseSearcher>
{
    protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
    {
        return new List<GridColumn<Student>>
        {
            this.MakeGridHeader(x => x.LoginName)
                .SetRichColumnType(GridRichColumnTypeEnum.Progress),
            this.MakeGridHeader(x => x.Name)
                .SetRichColumnType(GridRichColumnTypeEnum.Tag, tagColor: "blue"),
            this.MakeGridHeader(x => x.Email)
                .SetRichColumnType(GridRichColumnTypeEnum.Image, imageSize: 48),
            this.MakeGridHeader(x => x.CellPhone)
                .SetRichColumnType(GridRichColumnTypeEnum.Currency, currencyFormat: "0.00"),
            this.MakeGridHeader(x => x.ZipCode)
                .SetRichColumnType(GridRichColumnTypeEnum.Currency, currencyCodeField: "Address"),
        };
    }
}

/// <summary>
/// One plain column + one bool column (Student.IsValid, inherited from
/// PersistPoco) with no SetFormat — exercises the isBoolColumn hasFormat=true
/// branch in generateColHeaderCore (#461).
/// </summary>
public class BoolColumnListVM : BasePagedListVM<Student, BaseSearcher>
{
    protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
    {
        return new List<GridColumn<Student>>
        {
            this.MakeGridHeader(x => x.LoginName),
            this.MakeGridHeader(x => x.IsValid),
        };
    }
}

/// <summary>
/// One plain column + one Action column, with InitGridAction() covering all 7
/// GridActionParameterTypesEnum values, an ActionsGroup (button-group), a
/// PromptMessage confirm wrap, a bare-identifier OnClickFunc, ShowDialog
/// (exercises the ff.OpenDialog Guid site), IsRedirect, IsExport, IsDownload,
/// ForcePost, and a BindVisiableColName row-button conditional.
/// </summary>
public class ActionsMatrixListVM : BasePagedListVM<Student, BaseSearcher>
{
    protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
    {
        return new List<GridColumn<Student>>
        {
            this.MakeGridHeader(x => x.LoginName),
            this.MakeGridHeaderAction(),
        };
    }

    protected override List<GridAction> InitGridAction()
    {
        var sub1 = new GridAction
        {
            Area = "GroupSub1",
            Name = "Sub Action One",
            ParameterType = GridActionParameterTypesEnum.NoId,
            ShowInRow = false,
        };
        var sub2 = new GridAction
        {
            Area = "GroupSub2",
            Name = "Sub Action Two",
            ParameterType = GridActionParameterTypesEnum.SingleId,
            ShowInRow = false,
        };
        // MakeActionsGroup auto-generates ButtonId = Guid.NewGuid().ToString() —
        // pin it immediately afterward so the fixture stays deterministic without
        // needing regex normalization (GridAction.ButtonId is a public settable
        // property, i.e. the "prefer pinning via public API" path).
        var group = this.MakeActionsGroup("Batch Group", new List<GridAction> { sub1, sub2 });
        group.ButtonId = "fixed-group-btn-01";

        return new List<GridAction>
        {
            new GridAction
            {
                Area = "ActImport",
                Name = "Import",
                ParameterType = GridActionParameterTypesEnum.NoId,
                ShowInRow = false,
                IconCls = "layui-icon layui-icon-upload",
            },
            new GridAction
            {
                Area = "ActEdit",
                Name = "Edit",
                ParameterType = GridActionParameterTypesEnum.SingleId,
                ShowDialog = true,
                DialogWidth = 700,
                DialogHeight = 500,
                DialogTitle = "Edit Item",
                BindVisiableColName = "IsValid",
                IconCls = "layui-icon layui-icon-edit",
            },
            new GridAction
            {
                Area = "ActBatchDelete",
                Name = "BatchDelete",
                ParameterType = GridActionParameterTypesEnum.MultiIds,
                IsDownload = true,
                PromptMessage = "Are you sure you want to delete these rows?",
                IconCls = "layui-icon layui-icon-delete",
            },
            new GridAction
            {
                Area = "ActDetails",
                Name = "Details",
                ParameterType = GridActionParameterTypesEnum.SingleIdWithNull,
                IsRedirect = true,
                DialogTitle = "Details",
                IconCls = "layui-icon layui-icon-search",
            },
            new GridAction
            {
                Area = "ActExport",
                Name = "Export",
                ParameterType = GridActionParameterTypesEnum.MultiIdWithNull,
                IsExport = true,
                IconCls = "layui-icon layui-icon-download-circle",
            },
            new GridAction
            {
                Area = "ActAddRow",
                Name = "AddRow",
                ParameterType = GridActionParameterTypesEnum.AddRow,
                ShowInRow = false,
                IconCls = "layui-icon layui-icon-add-1",
            },
            new GridAction
            {
                Area = "ActRemoveRow",
                Name = "RemoveRow",
                ParameterType = GridActionParameterTypesEnum.RemoveRow,
                ShowInRow = true,
                HideOnToolBar = true,
                IconCls = "layui-icon layui-icon-delete",
            },
            group,
            new GridAction
            {
                Area = "ActOnClick",
                Name = "CustomClick",
                ParameterType = GridActionParameterTypesEnum.SingleId,
                OnClickFunc = "myGridOnClickHandler",
                IconCls = "layui-icon layui-icon-face-smile",
            },
            new GridAction
            {
                Area = "ActForcePost",
                Name = "ForcePost",
                ParameterType = GridActionParameterTypesEnum.NoId,
                ForcePost = true,
                ShowInRow = false,
                IconCls = "layui-icon layui-icon-refresh",
            },
        };
    }
}
