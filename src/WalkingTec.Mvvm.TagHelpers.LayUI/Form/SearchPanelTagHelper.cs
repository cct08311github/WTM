using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:searchpanel", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.NormalOrSelfClosing)]
    public class SearchPanelTagHelper : FormTagHelper
    {
        /// <summary>
        /// 搜索按钮前缀
        /// </summary>
        public const string SEARCH_BTN_ID_PREFIX = "wtSearchBtn_";
        /// <summary>
        /// 重置按钮前缀
        /// </summary>
        public const string RESET_BTN_ID_PREFIX = "wtResetBtn_";
        private IBasePagedListVM<TopBasePoco, BaseSearcher> _listVM;
        private IBasePagedListVM<TopBasePoco, BaseSearcher> ListVM
        {
            get
            {
                if (_listVM == null)
                {
                    _listVM = Vm?.Model as IBasePagedListVM<TopBasePoco, BaseSearcher>;
                }
                return _listVM;
            }
        }

        private BaseSearcher _searcherVM;
        private BaseSearcher SearcherVM
        {
            get
            {
                if(_searcherVM == null)
                {
                    if (ListVM == null)
                    {
                        _searcherVM = Vm?.Model as BaseSearcher;
                    }
                    else
                    {
                        _searcherVM = ListVM.Searcher;
                    }
                }
                return _searcherVM;
            }
        }

        private string _gridIdUserSet;

        private string _gridId;
        /// <summary>
        /// 关联的 Grid 组件的 Id
        /// </summary>
        public string GridId
        {
            get
            {
                if (string.IsNullOrEmpty(_gridId))
                {
                    if (_gridIdUserSet==null)
                    {
                        if (ListVM != null)
                        {
                            _gridId = $"{DataTableTagHelper.TABLE_ID_PREFIX}{ListVM?.UniqueId}";
                        }
                    }
                    else
                    {
                        _gridId = _gridIdUserSet;
                    }
                }
                return _gridId;
            }
            set
            {
                _gridId = value;
                _gridIdUserSet = value;
            }
        }

        /// <summary>
        /// 关联的 Chart 组件的 Id
        /// </summary>
        public string ChartId { get; set; }
        public string ChartPrefix { get; set; }
        private string _searchBtnId;
        /// <summary>
        /// 搜索按钮Id
        /// </summary>
        public string SearchBtnId
        {
            get
            {
                if (string.IsNullOrEmpty(_searchBtnId))
                {
                    _searchBtnId = $"{SEARCH_BTN_ID_PREFIX}{Id}";
                }
                return _searchBtnId;
            }
            set
            {
                _searchBtnId = value;
            }
        }

        /// <summary>
        /// Reset button Id
        /// </summary>
        // Visibility: internal (not private) so #470 Slice N2's byte-identity
        // test suite (SearchPanelIsland470SliceN2Tests, WalkingTec.Mvvm.Core.Test
        // — InternalsVisibleTo'd, see the .csproj) can assert the exact value
        // carried by the searchPanelInit island's resetBtnId field without
        // reflection. Purely a testability widening — no behavior change.
        internal string ResetBtnId => $"{RESET_BTN_ID_PREFIX}{SearcherVM?.UniqueId}";

        /// <summary>
        /// 重置按钮
        /// </summary>
        public bool ResetBtn { get; set; }

        /// <summary>
        /// Is expanded
        /// </summary>
        public bool? Expanded { get; set; }

        private Configs _configs;
        public SearchPanelTagHelper(IOptionsMonitor<Configs> configs)
        {
            _configs = configs.CurrentValue;
        }

        private bool IsInSelector = false;

        protected string fieldPre
        {
            get
            {
                string rv = "";
                if(string.IsNullOrEmpty(Vm?.Name) == false) {
                    rv = Vm?.Name;
                    if (ListVM != null)
                    {
                        rv += ".Searcher";
                    }
                }
                else
                {
                    if(ListVM != null)
                    {
                        rv = "Searcher";
                    }
                }
                if(IsInSelector == true)
                {
                    rv = rv.Replace(".Searcher", "").Replace("Searcher", "");
                }
                return rv;
            }
        }

        public override Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
        {
            if (context.Items.ContainsKey("inselector") == true)
            {
                IsInSelector = true;
            }
            if(OldPost == true)
            {
                output.Attributes.Add("oldpost", true);
            }

            // Issue #470 Slice N2: opt-in (WtmUIOptions.UseSelectIslandRender,
            // default OFF — the SAME flag Slices J-O1 use) eval-free delegated
            // click/myclick wiring for the search button, replacing the
            // per-panel inline <script> click/myclick handlers below with a
            // document-level jQuery delegated binding
            // ($(document).on('click myclick', 'a[IsSearchButton][data-wtm-search]', ...)
            // in framework_layui.js) plus a small 'searchPanelInit' JSON
            // island for the collapse/reset/IsExpanded pieces. Design
            // authority: Gitea issue #470 comment 18118 §5 ("SearchPanel N2
            // co-design").
            //
            // Containment mirrors the legacy behaviour exactly (never a
            // silent default change):
            //  - OldPost forms never get a click/myclick binding in the
            //    legacy path either (native form submit — see the
            //    OldPost-conditional empty string further below), so N2 must
            //    ALSO emit no data-wtm-search attribute/island in that mode.
            //  - Selector-hosted search panels (detected via the SAME
            //    "inselector" context item OpenDialog2/SelectorTagHelper.cs
            //    already sets for IsInSelector above) stay on the FULL
            //    legacy inline path — deferred to Slice P's sentinel
            //    retirement (same #655/#567-Phase-2 BMS gate O1 uses for
            //    IsInSelector grids).
            // When useSearchPanelIsland is false (flag OFF, OR OldPost, OR
            // selector-hosted) every line below this point is BYTE-IDENTICAL
            // to the pre-#470-N2 code — no new branch is taken anywhere else
            // in this method.
            bool useSearchPanelIsland = UIConfig.UseSelectIslandRender && OldPost == false && IsInSelector == false;

            var tempSearchTitleId = Guid.NewGuid().ToNoSplitString();
            bool show = false;
            if(SearcherVM?.IsExpanded != null)
            {
                Expanded = SearcherVM?.IsExpanded;
            }
            if(Expanded != null)
            {
                show = Expanded.Value;
            }
            else
            {
                show =_configs.UIOptions.SearchPanel.DefaultExpand;
            }

            string showpage = "";
            if(ListVM?.NeedPage == true)
            {
                showpage = $@",page:{{
        rpptext:'{THProgram._localizer["Sys.RecordsPerPage"]}',
        totaltext:'{THProgram._localizer["Sys.Total"]}',
        recordtext:'{THProgram._localizer["Sys.Record"]}',
        gototext:'{THProgram._localizer["Sys.Goto"]}',
        pagetext:'{THProgram._localizer["Sys.Page"]}',
        oktext:'{THProgram._localizer["Sys.GotoButtonText"]}',
    }}";
            }
            var layuiShow = show ? " layui-show" : string.Empty;

            // Issue #470 Slice N2: flag-ON-only marker/data attributes
            // carried by the search button itself — read at CLICK TIME by
            // framework_layui.js's document-level delegated listener (never
            // eval'd, never used to build a server-side JS string). Empty
            // string (zero characters) when useSearchPanelIsland is false —
            // this is what keeps the flag-OFF / OldPost / selector-hosted
            // markup byte-identical to before.
            string searchButtonIslandAttrs = string.Empty;
            if (useSearchPanelIsland)
            {
                var attrs = new StringBuilder();
                attrs.Append(" data-wtm-search");
                if (string.IsNullOrEmpty(GridId) == false)
                {
                    attrs.Append($" data-wtm-search-grids=\"{WebUtility.HtmlEncode(GridId)}\"");
                }
                if (string.IsNullOrEmpty(ChartId) == false)
                {
                    attrs.Append($" data-wtm-search-charts=\"{WebUtility.HtmlEncode(ChartId)}\"");
                    if (string.IsNullOrEmpty(ChartPrefix) == false)
                    {
                        attrs.Append($" data-wtm-chart-prefix=\"{WebUtility.HtmlEncode(ChartPrefix)}\"");
                    }
                }
                attrs.Append($" data-wtm-form=\"{WebUtility.HtmlEncode(Id)}\"");
                attrs.Append($" data-wtm-fieldpre=\"{WebUtility.HtmlEncode(fieldPre)}\"");
                searchButtonIslandAttrs = attrs.ToString();
            }

            output.PreContent.AppendHtml($@"
<div class=""layui-collapse"" style=""margin-bottom:5px;"" lay-filter=""{tempSearchTitleId}x"">
  <div class=""layui-colla-item"">
    <h2 class=""layui-colla-title"">{THProgram._localizer["Sys.SearchCondition"]}
      <div style=""text-align:right;margin-top:-43px;"" id=""{tempSearchTitleId}"">
        <a href=""javascript:void(0)"" class=""layui-btn layui-btn-sm"" id=""{SearchBtnId}"" IsSearchButton{searchButtonIslandAttrs}><i class=""layui-icon"">&#xe615;</i>{THProgram._localizer["Sys.Search"]}</a>
        {(!ResetBtn ? string.Empty : $@"<button type=""button"" class=""layui-btn layui-btn-sm"" id=""{ResetBtnId}"">{THProgram._localizer["Sys.Reset"]}</button>")}
      </div>
    </h2>
    <div class=""layui-colla-content{layuiShow}"" >
      <input type=""text"" style=""display: none;"">
");
            output.PostContent.AppendHtml($@"
    </div>
  </div>
</div>
");

            var refreshgridjs = "";
            if (string.IsNullOrEmpty(GridId) == false)
            {
                foreach (var item in GridId.Split(','))
                {
                    refreshgridjs += $@"
    var tempwhere{item} = {{}};
    $.extend(tempwhere{item},{item}defaultfilter.where);
    var page{item} = {item}filterback.page;
    if(keeppage ==null){{ page{item}.curr = 1}}
    table.reload('{item}',{{page: page{item},url:{item}url,where: $.extend(tempwhere{item},ff.GetSearchFormData('{Id}','{fieldPre}'))}});
";
                }
            }
            var refreshchartjs = "";
            if (string.IsNullOrEmpty(ChartId) == false)
            {
                output.Attributes.SetAttribute("chartlink",  ChartId);
                foreach (var item in ChartId.Split(','))
                {
                    refreshchartjs += $@"
    ff.RefreshChart('{item}',{(string.IsNullOrEmpty(ChartPrefix)==true? "undefined":$"'{ChartPrefix}'")});
";
                }
            }

            if (useSearchPanelIsland)
            {
                // Issue #470 Slice N2: the collapse handlers / reset-button
                // binding / IsExpanded hidden-input init below reproduce the
                // legacy inline <script> in the `else` branch EXACTLY (same
                // selectors, same 'collapse(...)'/'collapse(...)x' filters —
                // including the second `collapse({tempSearchTitleId})`
                // listener that targets a lay-filter no element in this
                // markup actually carries; framework_layui.js's
                // 'searchPanelInit' case reproduces that byte-for-byte, dead
                // code and all) via a single 'searchPanelInit' JSON island
                // action instead of a per-panel <script>. The search
                // button's click/myclick refresh — refreshgridjs/
                // refreshchartjs above — is intentionally NOT carried here:
                // it is handled once, globally, by the document-level
                // delegated jQuery listener in framework_layui.js reading
                // the data-wtm-search-* attributes emitted on the button
                // above (event-time reads, works whether each linked grid
                // is O1-island or legacy-rendered).
                var searchPanelInitAction = new SearchPanelInitIslandAction
                {
                    TitleId = tempSearchTitleId,
                    ResetBtnId = ResetBtnId,
                    Show = show
                };
                output.PostElement.AppendHtml(
                    $"<script type=\"application/json\" class=\"wtm-dialog-init\">{LayuiIslandJson.Serialize(searchPanelInitAction)}</script>");
            }
            else
            {
                output.PostElement.AppendHtml($@"
<script>
  layui.use(['table','element'], function () {{
    const table = layui.table;
    layui.element.init();
    $('#{tempSearchTitleId} .layui-btn').on('click',function(e){{e.stopPropagation();}})
    $('#{ResetBtnId}').on('click', function (btn) {{ff.resetForm(this.form.id);}});
    $('#{tempSearchTitleId}').parents('form').append(""<input type='hidden' name='IsExpanded' value='{show.ToString().ToLower()}' />"");
layui.element.on('collapse({tempSearchTitleId}x)', function(data){{
    $('#{tempSearchTitleId}').parents('form').find(""input[name='IsExpanded']"").val(data.show+'');
    ff.triggerResize();
}});

{(OldPost == true ? $"" : $@"
$('#{SearchBtnId}').on('click', function () {{
   var keeppage = null;
    {refreshgridjs}
    {refreshchartjs}
}});
$('#{SearchBtnId}').bind('myclick', function () {{
   var keeppage = true;
    {refreshgridjs}
    {refreshchartjs}
}});
    ")}
layui.element.on('collapse({tempSearchTitleId})', function(data){{ff.triggerResize()}});
}})
</script>");
            }
            return base.ProcessAsync(context, output);
        }
    }

    // Issue #470 Slice N2: DTO for the bare (non-wrapped) 'searchPanelInit'
    // JSON island — the opt-in (WtmUIOptions.UseSelectIslandRender, default
    // OFF — the SAME flag #470 Slices J-O1 use) eval-free replacement for
    // the collapse-handlers/reset-button/IsExpanded-hidden-input pieces of
    // the inline <script> SearchPanelTagHelper otherwise emits.
    // ff._dispatchIslandWhenReady wraps this via ff._normalizeIslandPayload
    // into the {actions:[...]} shape ff.DispatchAction expects (same
    // bare-single-action pattern as TreeContainerTagHelper's
    // RenderTreeContainerIslandAction). Not part of the public API surface.
    //
    // Every field here is server-side-derived (a Guid-based DOM id, another
    // DOM id, and a bool) — never field/request/model data — so no
    // additional trust-boundary comment is needed (contrast ClickFunc/
    // BeforeSubmit's developer-authored-literal trust class).
    internal sealed class SearchPanelInitIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "searchPanelInit";

        // "{tempSearchTitleId}" — the div wrapping the search/reset buttons
        // AND the id half of the outer <div class="layui-collapse"
        // lay-filter="{tempSearchTitleId}x">'s collapse filter (with 'x'
        // appended). Also used to locate the owning <form> for the
        // IsExpanded hidden input.
        [JsonPropertyName("titleId")]
        public string TitleId { get; set; }

        // "{ResetBtnId}" — bound unconditionally, exactly like the legacy
        // inline script (harmless jQuery no-op when ResetBtn is false and no
        // element with this id exists).
        [JsonPropertyName("resetBtnId")]
        public string ResetBtnId { get; set; }

        // The panel's initial expanded/collapsed state — mirrors the legacy
        // inline script's `value='{show.ToString().ToLower()}'` hidden input.
        [JsonPropertyName("show")]
        public bool Show { get; set; }
    }
}
