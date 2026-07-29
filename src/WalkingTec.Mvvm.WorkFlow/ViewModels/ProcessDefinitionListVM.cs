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
/// <para><strong>Tenant-isolation is NOT currently automatic (#899).</strong>
/// <see cref="ProcessDefinition"/> is a DIRECT <c>: PersistPoco, ITenant</c> descendant, so
/// <c>DataContext</c> is intended to apply the <c>TenantCode == this.TenantCode</c> query
/// filter before this VM's <see cref="GetSearchQuery"/> runs, but that filter does not
/// currently reach <see cref="ProcessDefinition"/> -- do not rely on this until #899 lands.</para>
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
            // Open definition in the visual designer (replaces dead _WfProcessDefinition Details link).
            // Dead-link fix: _WfProcessDefinition controller does not exist anywhere in the codebase.
            // Repointed at /_workflow-designer per WF-21.7 / design §0 / CHANGELOG.
            new GridAction
            {
                Name             = "设计器",
                IconCls          = "layui-icon layui-icon-edit",
                ShowInRow        = true,
                HideOnToolBar    = true,
                OnClickFunc      = @"function(ids,data){var code=data&&data.Code?data.Code:'';if(code){window.open('/_workflow-designer?code='+encodeURIComponent(code),'_blank');}}",
            },
            // View version history in designer (replaces dead _WfProcessDefinition Versions link).
            new GridAction
            {
                Name             = "版本历程",
                IconCls          = "layui-icon layui-icon-list",
                ShowInRow        = true,
                HideOnToolBar    = true,
                OnClickFunc      = @"function(ids,data){var code=data&&data.Code?data.Code:'';if(code){window.open('/_workflow-designer?code='+encodeURIComponent(code)+'#versions','_blank');}}",
            },
        };
    }

    /// <summary>
    /// Returns definitions filtered by the searcher criteria.
    /// Intended to be tenant-scoped via the DataContext query filter, but that filter does not
    /// currently reach <see cref="ProcessDefinition"/> (#899) -- treat results as unscoped
    /// until it lands.
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
