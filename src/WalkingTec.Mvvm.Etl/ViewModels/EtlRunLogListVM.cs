#nullable enable
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.ViewModels;

public class EtlRunLogListVM : BasePagedListVM<EtlRunLog, EtlRunLogSearcher>
{
    protected override IEnumerable<IGridColumn<EtlRunLog>> InitGridHeader()
    {
        return new List<GridColumn<EtlRunLog>>
        {
            this.MakeGridHeader(x => x.Job!.Name).SetHeader("Job 名稱").SetWidth(150),
            this.MakeGridHeader(x => x.StartedAt).SetHeader("執行時間"),
            this.MakeGridHeader(x => x.Trigger).SetHeader("觸發方式"),
            this.MakeGridHeader(x => x.Result),
            this.MakeGridHeader(x => x.ExtractedRows).SetHeader("擷取數"),
            this.MakeGridHeader(x => x.LoadedRows).SetHeader("載入數"),
            this.MakeGridHeader(x => x.ErrorRows).SetHeader("錯誤數"),
            this.MakeGridHeader(x => x.ElapsedMs).SetHeader("耗時(ms)"),
            this.MakeGridHeader(x => x.ErrorMessage!).SetHeader("錯誤訊息").SetWidth(200),
            this.MakeGridHeader(x => x.WatermarkSnapshot!).SetHeader("Watermark"),
            this.MakeGridHeaderAction(width: 120)
        };
    }

    public override IOrderedQueryable<EtlRunLog> GetSearchQuery()
    {
        var query = DC.Set<EtlRunLog>()
            .Include(x => x.Job)
            .AsQueryable();

        if (Searcher.JobId.HasValue)
            query = query.Where(x => x.JobId == Searcher.JobId);
        if (Searcher.Result.HasValue)
            query = query.Where(x => x.Result == Searcher.Result);
        if (Searcher.Trigger.HasValue)
            query = query.Where(x => x.Trigger == Searcher.Trigger);
        if (Searcher.StartedAtBegin.HasValue)
            query = query.Where(x => x.StartedAt >= Searcher.StartedAtBegin);
        if (Searcher.StartedAtEnd.HasValue)
            query = query.Where(x => x.StartedAt <= Searcher.StartedAtEnd);

        return query.OrderByDescending(x => x.StartedAt);
    }
}
