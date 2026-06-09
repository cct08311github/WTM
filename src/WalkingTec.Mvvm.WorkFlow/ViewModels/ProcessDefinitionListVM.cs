#nullable enable
using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.ViewModels;

// ── Searcher ─────────────────────────────────────────────────────────────────

/// <summary>
/// Search parameters for the <see cref="ProcessDefinitionListVM"/> admin grid.
/// </summary>
public class ProcessDefinitionSearcher : BaseSearcher
{
    /// <summary>Filter by definition code (partial match).</summary>
    public string? Code { get; set; }

    /// <summary>Filter by definition name (partial match).</summary>
    public string? Name { get; set; }

    /// <summary>Filter by category.</summary>
    public string? Category { get; set; }

    /// <summary>Filter by enabled / disabled state.  <c>null</c> = all.</summary>
    public bool? IsEnabled { get; set; }
}

// ── ListVM ───────────────────────────────────────────────────────────────────

/// <summary>
/// Admin grid VM for browsing <see cref="ProcessDefinition"/> entries.
///
/// <para>Read-only browse + drill to versions.  No in-grid editing — definitions are
/// published via the API/designer endpoint
/// (<c>POST /api/_workflow/definitions/{id}/publish</c>).</para>
///
/// <para>Tenant-isolation is automatic: <see cref="ProcessDefinition"/> is a
/// DIRECT <c>: PersistPoco, ITenant</c> descendant, so <c>DataContext</c> applies the
/// <c>TenantCode == this.TenantCode</c> query filter before this VM's
/// <see cref="GetSearchQuery"/> runs.</para>
/// </summary>
public class ProcessDefinitionListVM : BasePagedListVM<ProcessDefinition, ProcessDefinitionSearcher>
{
    protected override IEnumerable<IGridColumn<ProcessDefinition>> InitGridHeader()
    {
        return new List<GridColumn<ProcessDefinition>>
        {
            this.MakeGridHeader(x => x.Code).SetHeader("流程代碼"),
            this.MakeGridHeader(x => x.Name).SetHeader("流程名稱"),
            this.MakeGridHeader(x => x.Category).SetHeader("類別"),
            this.MakeGridHeader(x => x.IsEnabled).SetHeader("啟用").SetWidth(70),
            this.MakeGridHeaderAction(width: 300),
        };
    }

    protected override List<GridAction> InitGridAction()
    {
        return new List<GridAction>
        {
            // View definition detail (read-only).
            new GridAction
            {
                Name             = "查看",
                IconCls          = "layui-icon layui-icon-search",
                ControllerName   = "_WfProcessDefinition",
                ActionName       = "Details",
                ParameterType    = GridActionParameterTypesEnum.SingleId,
                ShowInRow        = true,
                HideOnToolBar    = true,
                ShowDialog       = true,
            },
            // View published version history.
            new GridAction
            {
                Name             = "版本歷程",
                IconCls          = "layui-icon layui-icon-list",
                ShowInRow        = true,
                HideOnToolBar    = true,
                OnClickFunc      = @"function(ids,data){var id=ids&&ids.length>0?ids[0]:'';ff.OpenDialog('/_WfProcessDefinition/Versions?definitionId='+id,null,'版本歷程',800,null,undefined,false);}",
            },
        };
    }

    /// <summary>
    /// Returns tenant-scoped definitions filtered by the searcher criteria.
    /// Tenant isolation is automatically applied by the DataContext query filter.
    /// </summary>
    public override IOrderedQueryable<ProcessDefinition> GetSearchQuery()
    {
        var query = DC.Set<ProcessDefinition>().AsQueryable();

        if (!string.IsNullOrWhiteSpace(Searcher.Code))
            query = query.Where(x => x.Code.Contains(Searcher.Code));

        if (!string.IsNullOrWhiteSpace(Searcher.Name))
            query = query.Where(x => x.Name.Contains(Searcher.Name));

        if (!string.IsNullOrWhiteSpace(Searcher.Category))
            query = query.Where(x => x.Category != null && x.Category.Contains(Searcher.Category));

        if (Searcher.IsEnabled.HasValue)
            query = query.Where(x => x.IsEnabled == Searcher.IsEnabled.Value);

        return query.OrderByDescending(x => x.CreateTime);
    }
}
