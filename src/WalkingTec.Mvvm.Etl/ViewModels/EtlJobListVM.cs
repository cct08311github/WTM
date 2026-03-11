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
            this.MakeGridHeader(x => x.LastError!).SetHeader("最後錯誤").SetWidth(200),
            this.MakeGridHeaderAction(width: 380)
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
