#nullable enable
using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.ViewModels;

public class EtlJobListVM : BasePagedListVM<EtlJobDefinition, EtlJobSearcher>
{
    protected override IEnumerable<IGridColumn<EtlJobDefinition>> InitGridHeader()
    {
        return new List<GridColumn<EtlJobDefinition>>
        {
            this.MakeGridHeader(x => x.Name),
            this.MakeGridHeader(x => x.CronExpression).SetHeader("排程"),
            this.MakeGridHeader(x => x.Status),
            this.MakeGridHeader(x => x.SourceDbType).SetHeader("來源 DB"),
            this.MakeGridHeader(x => x.LastRunAt).SetHeader("上次執行"),
            this.MakeGridHeader(x => x.NextFireAt).SetHeader("下次觸發"),
            this.MakeGridHeader(x => x.ConsecutiveFailureCount).SetHeader("連續失敗").SetWidth(80),
            this.MakeGridHeader(x => x.LastError!).SetHeader("最後錯誤").SetWidth(200),
            this.MakeGridHeaderAction(width: 460)
        };
    }

    protected override List<GridAction> InitGridAction()
    {
        return new List<GridAction>
        {
            this.MakeStandardAction("_EtlJob", GridActionStandardTypesEnum.Create, "新增 Job", dialogWidth: 900),
            this.MakeStandardAction("_EtlJob", GridActionStandardTypesEnum.Edit,   "編輯", dialogWidth: 900),
            this.MakeStandardAction("_EtlJob", GridActionStandardTypesEnum.Delete,  "刪除", dialogWidth: 500),
            // Row-level operations
            new GridAction
            {
                Name = "立即執行",
                IconCls = "layui-icon layui-icon-play",
                ControllerName = "_EtlJob",
                ActionName = "TriggerNow",
                ParameterType = GridActionParameterTypesEnum.SingleId,
                ShowInRow = true,
                HideOnToolBar = true,
                ShowDialog = false,
                ForcePost = true,
                PromptMessage = "確定立即執行此 Job？"
            },
            new GridAction
            {
                Name = "暫停",
                IconCls = "layui-icon layui-icon-pause",
                ControllerName = "_EtlJob",
                ActionName = "Pause",
                ParameterType = GridActionParameterTypesEnum.SingleId,
                ShowInRow = true,
                HideOnToolBar = true,
                ShowDialog = false,
                ForcePost = true,
                PromptMessage = "確定暫停此 Job？"
            },
            new GridAction
            {
                Name = "恢復",
                IconCls = "layui-icon layui-icon-play-circle",
                ControllerName = "_EtlJob",
                ActionName = "Resume",
                ParameterType = GridActionParameterTypesEnum.SingleId,
                ShowInRow = true,
                HideOnToolBar = true,
                ShowDialog = false,
                ForcePost = true,
                PromptMessage = "確定恢復此 Job？"
            },
            new GridAction
            {
                Name = "中止",
                IconCls = "layui-icon layui-icon-close",
                ControllerName = "_EtlJob",
                ActionName = "Abort",
                ParameterType = GridActionParameterTypesEnum.SingleId,
                ShowInRow = true,
                HideOnToolBar = true,
                ShowDialog = false,
                ForcePost = true,
                PromptMessage = "確定中止此 Job 的當前執行？"
            },
            new GridAction
            {
                Name = "執行記錄",
                IconCls = "layui-icon layui-icon-list",
                ShowInRow = true,
                HideOnToolBar = true,
                // Opens run-log dialog filtered by this job ID (#540)
                OnClickFunc = @"function(ids,data){var id=ids&&ids.length>0?ids[0]:'';ff.OpenDialog('/_EtlRunLog/Index?jobId='+id,null,'執行記錄',900,null,undefined,false);}"
            }
        };
    }

    public override IOrderedQueryable<EtlJobDefinition> GetSearchQuery()
    {
        var query = DC.Set<EtlJobDefinition>().AsQueryable();

        if (!string.IsNullOrEmpty(Searcher.Name))
            query = query.Where(x => x.Name.Contains(Searcher.Name));
        if (Searcher.Status.HasValue)
            query = query.Where(x => x.Status == Searcher.Status);
        if (Searcher.SourceDbType.HasValue)
            query = query.Where(x => x.SourceDbType == Searcher.SourceDbType);

        return query.OrderByDescending(x => x.CreateTime);
    }
}
