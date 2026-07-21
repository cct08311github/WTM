using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:treecontainer", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.NormalOrSelfClosing)]
    public class TreeContainerTagHelper : BaseElementTag
    {
        protected const string REQUIRED_ATTR_NAME = "items";

        // Perf(#713): converted to [GeneratedRegex] accessors (LayUiRegexes) — same pattern
        // text as before; RegexOptions.Compiled is dropped as redundant with source
        // generation (the source-gen implementation is already compiled).
        private static readonly Regex _regStripSearcherPrefix = LayUiRegexes.SearcherPrefixStripRegex();
        private static readonly Regex _regSearchButtonId = LayUiRegexes.SearchButtonIdRegex();
        private static readonly Regex _regGridOptionVar = LayUiRegexes.GridOptionVarRegex();

        // Issue #470 Slice N1: identifier check for the opt-in 'renderTreeContainer'
        // island decision below — the SAME identifier class framework_layui.js's
        // ff._resolveGuardedWindowFn enforces (a bare JS identifier, nothing else).
        // Duplicated (not shared) per-TagHelper, mirroring ComboBoxTagHelper's/
        // TreeTagHelper's/TransferTagHelper's #470 Slice J/K _identifierRegex
        // exactly, including the `\z` (not `$`) end anchor.
        private static readonly Regex _identifierRegex = new(@"^[A-Za-z_$][\w$]*\z", RegexOptions.Compiled);

        // Issue #470 Slice N1 (mirrors ComboBoxTagHelper's _islandJsonOptions):
        // island DTOs omit null members so optional fields (clickFunc,
        // searchButtonId, gridId, autoLoadUrl) contribute zero JSON when unused.
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public ModelExpression Items { get; set; }
        /// <summary>
        /// 加载页面之前执行
        /// </summary>
        public string ClickFunc { get; set; }

        /// <summary>
        /// 自动加载首节点
        /// </summary>
        public bool AutoLoad { get; set; }

        /// <summary>
        /// 默认加载的页面
        /// </summary>
        public string AutoLoadUrl { get; set; }
        /// <summary>
        /// 加载页面之后执行
        /// </summary>
        public string AfterLoadEvent { get; set; }

        public bool ShowLine { get; set; } = true;

        public string Title { get; set; }

        //当嵌套Grid时使用，树会把点击节点的ID传递给绑定的IdField，比如Searcher.xxxId
        public ModelExpression IdField { get; set; }
        //当嵌套Grid时使用，树会把点击节点的层级（从0开始的整形）传递给绑定的LevelField，比如Searcher.level
        public ModelExpression LevelField { get; set; }


        private string GetFirstNodeUrl(IEnumerable<TreeSelectListItem> nodes)
        {
            if (nodes == null || !nodes.Any())
            {
                return string.Empty;
            }

            var node = nodes.FirstOrDefault();
            if (node.Children != null && node.Children.Any())
            {
                return GetFirstNodeUrl(node.Children);
            }
            else
            {
                return node.Url;
            }
        }
        public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.TagMode = TagMode.StartTagAndEndTag;
            Id = string.IsNullOrEmpty(Id) ? Guid.NewGuid().ToNoSplitString() : Id;
            output.Attributes.Add("id", "top" + Id);
            output.Attributes.Add("wtm-ctype", "tc");
            output.Attributes.Add("class", "layui-row donotuse_fill");
            if (Items.Model is List<TreeSelectListItem> mm)
            {
                if (AutoLoad && string.IsNullOrEmpty(AutoLoadUrl))
                {
                    AutoLoadUrl = GetFirstNodeUrl(mm);
                }
                var inside = await output.GetChildContentAsync();
                var insideContent = inside.GetContent();
                var idfieldname = IdField?.Name ?? "notsetid";
                idfieldname = _regStripSearcherPrefix.Replace(idfieldname, "");
                var levelfieldname = LevelField?.Name ?? "notsetlevel";
                levelfieldname = _regStripSearcherPrefix.Replace(levelfieldname, "");

                // Issue #470 Slice N1: opt-in (WtmUIOptions.UseSelectIslandRender,
                // default OFF — the SAME flag Slices J/K/L/M use) eval-free
                // 'renderTreeContainer' island decision. ClickFunc is ALWAYS a
                // compile-time, developer-authored Razor literal (same trust class
                // as bindSubmit's beforeSubmit / Slice J's renderSelect ChangeFunc)
                // — a plain-identifier ClickFunc (or none at all) is safe to carry
                // as JSON island data, resolved client-side via the SAME guarded
                // window[name] lookup every other named-callback action uses. A
                // non-identifier ClickFunc keeps the exact legacy inline render
                // below (never silently dropped) — mirrors ComboBoxTagHelper's/
                // TreeTagHelper's/TransferTagHelper's 3-way decision exactly.
                //
                // The OTHER click-wiring branches below (search-button click,
                // nested-grid table.reload, empty-content LoadPage1) never involve
                // a developer-authored JS expression — they are derived purely
                // from server-side regex analysis of the ALREADY-RENDERED nested
                // markup (search button id / grid option var name), so they carry
                // no additional trust concern and are always island-eligible.
                string clickFuncName = string.IsNullOrEmpty(ClickFunc) ? null : FormatFuncName(ClickFunc, false);
                bool clickIsIdentifier = clickFuncName != null && _identifierRegex.IsMatch(clickFuncName);
                bool useTreeContainerIsland = UIConfig.UseSelectIslandRender && (clickFuncName == null || clickIsIdentifier);

                if (useTreeContainerIsland)
                {
                    // Issue #470 Slice N1: click-mode analysis for the island —
                    // mirrors the legacy cusmtomclick decision in the `else`
                    // branch below EXACTLY (same regexes, same precedence,
                    // same insideContent input), but captures the RESULT as
                    // structured data instead of building a JS string. The
                    // island renderer (ff._renderTreeContainerAction in
                    // framework_layui.js) executes the equivalent client-side
                    // logic from this data.
                    string clickMode;
                    string searchButtonId = null;
                    string gridId = null;
                    bool gridExtendWhere = false;
                    if (!string.IsNullOrEmpty(ClickFunc))
                    {
                        clickMode = "custom";
                    }
                    else
                    {
                        var m3 = _regSearchButtonId.Match(insideContent);
                        if (m3.Success)
                        {
                            clickMode = "searchButton";
                            searchButtonId = m3.Groups[1].Value.Trim();
                        }
                        else
                        {
                            // Issue #470 Slice O1 (N-prereq): island-then-legacy probe
                            // order — try the island attribute FIRST. A nested
                            // DataTableTagHelper grid that rendered via the opt-in
                            // renderGrid island never emits the legacy "{gridid}option
                            // = {" text at all (see DataTableTagHelper.Island.cs), so
                            // _regGridOptionVar below would silently fail to detect it.
                            var mIsland = LayUiRegexes.IslandGridIdRegex().Match(insideContent);
                            if (mIsland.Success)
                            {
                                clickMode = "grid";
                                gridId = mIsland.Groups[1].Value.Trim();
                                // Invariant 3 (#470 Slice O1): island render ALWAYS
                                // writes window[gridId+'defaultfilter'] synchronously
                                // before table.render — the SAME guarantee the legacy
                                // "wtVar_ = table.render({gridid}option)" match below
                                // detects for the legacy path — so an island match is
                                // always the "extend" case.
                                gridExtendWhere = true;
                            }
                            else
                            {
                                var m = _regGridOptionVar.Match(insideContent);
                                if (m.Success)
                                {
                                    clickMode = "grid";
                                    gridId = m.Groups[1].Value.Trim();
                                    Regex r2 = new Regex($"(.*?) = table.render\\({gridId}option\\)", RegexOptions.Compiled);
                                    gridExtendWhere = r2.IsMatch(insideContent);
                                }
                                else if (string.IsNullOrEmpty(insideContent))
                                {
                                    clickMode = "loadPage";
                                }
                                else
                                {
                                    clickMode = "default";
                                }
                            }
                        }
                    }

                    var islandTreeItems = GetLayuiTree(mm);
                    var islandSelectedItem = GetSelectedItem(islandTreeItems);
                    // Mirrors the legacy inline render's AutoLoadUrl gate exactly:
                    // only auto-navigate when there IS an AutoLoadUrl and no node
                    // is already selected (a selected node's own click/setSelected
                    // wiring takes precedence).
                    string effectiveAutoLoadUrl = (string.IsNullOrEmpty(AutoLoadUrl) || islandSelectedItem != null)
                        ? null
                        : AutoLoadUrl;

                    var renderTreeContainerAction = new RenderTreeContainerIslandAction
                    {
                        Id = Id,
                        ElemId = "div" + Id,
                        GridDivId = "div_" + Id,
                        ShowLine = ShowLine,
                        IdFieldName = idfieldname,
                        LevelFieldName = levelfieldname,
                        Data = islandTreeItems,
                        SelectedItem = islandSelectedItem,
                        AutoLoadUrl = effectiveAutoLoadUrl,
                        ClickMode = clickMode,
                        ClickFunc = clickMode == "custom" ? clickFuncName : null,
                        SearchButtonId = clickMode == "searchButton" ? searchButtonId : null,
                        GridId = clickMode == "grid" ? gridId : null,
                        GridExtendWhere = gridExtendWhere
                    };

                    var islandContent = $@"
<div id=""div{Id}outer"" class=""layui-col-md2 donotuse_pdiv"" style=""padding-right:10px;border-right:solid 1px #aaa;"">
<div id=""div{Id}"" class=""donotuse_fill"" style=""overflow:auto;height:10px;"">
</div>
</div>
<div id=""div_{Id}"" style=""box-sizing:border-box"" class=""layui-col-md10 donotuse_pdiv"">{insideContent}</div>
<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(renderTreeContainerAction, _islandJsonOptions)}</script>
";
                    output.Content.SetHtmlContent(islandContent);
                }
                else
                {
                    // Issue #470 Slice N1: when the flag is ON but ClickFunc is a
                    // non-identifier expression (island render skipped for this
                    // one field — see useTreeContainerIsland above), surface a
                    // deprecation nudge in the browser console so the fallback is
                    // visible during migration. Mirrors ComboBoxTagHelper's/
                    // TransferTagHelper's deprecationWarn exactly, including the
                    // "contributes ZERO characters when empty" invariant that
                    // keeps the flag-OFF / identifier path byte-for-byte identical
                    // to the pre-Slice-N1 inline render.
                    var deprecationWarn = (UIConfig.UseSelectIslandRender && clickFuncName != null && !clickIsIdentifier)
                        ? $"console.warn('[WTM] TreeContainerTagHelper #{Id}: ClickFunc \\'{JavaScriptEncoder.Default.Encode(ClickFunc)}\\' is not a plain identifier — UseSelectIslandRender is ON but island render was skipped for this field; keeping the legacy inline script. See #470 Slice N1.');\n"
                        : "";

                    string cusmtomclick = $"top{Id}selected.{idfieldname}=data.data.id;top{Id}selected.{levelfieldname}=data.data.level;";
                    if (string.IsNullOrEmpty(ClickFunc))
                    {
                        var m3 = _regSearchButtonId.Match(insideContent);
                        if (m3.Success)
                        {
                            cusmtomclick += $@"
    $('#{m3.Groups[1].Value.Trim()}').click();
";
                        }
                        else
                        {
                            // Issue #470 Slice O1 (N-prereq): NO island-aware probe is
                            // added here (unlike the island-branch clickMode analysis
                            // above) — it would be unreachable dead code. This whole
                            // `if (string.IsNullOrEmpty(ClickFunc))` block only executes
                            // when TreeContainer ITSELF took the top-level legacy `else`
                            // branch (see the `useTreeContainerIsland` check above) WITH
                            // ClickFunc also empty — and `useTreeContainerIsland` is true
                            // whenever the flag is ON and ClickFunc is empty, so those two
                            // conditions together are only simultaneously true when the
                            // flag is OFF. A nested DataTableTagHelper grid never emits
                            // `data-wtm-grid-id` when the flag is OFF (see
                            // DataTableTagHelper.Island.cs), so an island-aware probe here
                            // could never match in practice. (When ClickFunc is non-empty
                            // — the OTHER way to reach TreeContainer's legacy branch while
                            // the flag is ON — this whole block is skipped entirely in
                            // favor of the `else` custom-click branch below, so it can't
                            // reach here either.)
                            var m = _regGridOptionVar.Match(insideContent);
                            if (m.Success)
                            {
                                var gridid = m.Groups[1].Value.Trim();
                                Regex r2 = new Regex($"(.*?) = table.render\\({gridid}option\\)", RegexOptions.Compiled);
                                var m2 = r2.Match(insideContent);
                                if (m2.Success)
                                {
                                    var gridvar = m2.Groups[1].Value.Trim();
                                    cusmtomclick = $@"
    $.extend({gridid}defaultfilter.where,{{'{idfieldname}':data.data.id, '{levelfieldname}':data.data.level }});
";
                                }
                                cusmtomclick += $@"
    layui.table.reload('{gridid}',{{url:{gridid}url, where: {gridid}defaultfilter.where}});
";

                            }
                            else if (string.IsNullOrEmpty(insideContent))
                            {
                                cusmtomclick = $"if(data.data.href!=null && data.data.href!=''){{ff.LoadPage1(data.data.href,'div_{Id}');}}";
                            }
                        }
                    }
                    else
                    {
                        cusmtomclick = $"{FormatFuncName(ClickFunc)};";
                    }
                    List<LayuiTreeItem2> treeitems = GetLayuiTree(mm);
                    var onclick = $@"
                ,click: function(data){{
                    var ele = null;
                    if(data.elem != undefined){{
                        ele = data.elem.find('.layui-tree-main:first');
                    }}
                    else{{
                        ele = $('#div{Id}').find(""div[data-id='""+data.data.id+""']"").find('.layui-tree-main:first');
                    }}
                    if(last{Id} != null){{
                        last{Id}.css('background-color','');
                        last{Id}.find('.layui-tree-txt').css('color','');
                    }}
                    if(last{Id} === ele){{
                        last{Id} = null;
                    }}
                    else{{
                        ele.css('background-color','#5fb878');
                        ele.find('.layui-tree-txt').css('color','#fff');
                        last{Id} = ele;
                    }}
                    {cusmtomclick}
                  }}
                ,setSelected: function(data){{
                    var ele = null;
                    if(data.elem != undefined){{
                        ele = data.elem.find('.layui-tree-main:first');
                    }}
                    else{{
                        ele = $('#div{Id}').find(""div[data-id='""+data.data.id+""']"").find('.layui-tree-main:first');
                    }}
                    if(last{Id} != null){{
                        last{Id}.css('background-color','');
                        last{Id}.find('.layui-tree-txt').css('color','');
                    }}
                    if(last{Id} === ele){{
                        last{Id} = null;
                    }}
                    else{{
                        ele.css('background-color','#5fb878');
                        ele.find('.layui-tree-txt').css('color','#fff');
                        last{Id} = ele;
                    }}
                  }}";

                    var selecteditem = GetSelectedItem(treeitems);


                    var script = $@"
<div id=""div{Id}outer"" class=""layui-col-md2 donotuse_pdiv"" style=""padding-right:10px;border-right:solid 1px #aaa;"">
<div id=""div{Id}"" class=""donotuse_fill"" style=""overflow:auto;height:10px;"">
</div>
</div>
<div id=""div_{Id}"" style=""box-sizing:border-box"" class=""layui-col-md10 donotuse_pdiv"">{insideContent}</div>
<script>
{deprecationWarn}var top{Id}selected = {{}};
{
    (selecteditem==null?"": @$"
    top{Id}selected.{idfieldname} = '{selecteditem.Id}';
    top{Id}selected.{levelfieldname} = {selecteditem.Level};
")
}
layui.use(['tree'],function(){{
  var last{Id} = null;
  var treecontainer{Id} = layui.tree.render({{
    id:'tree{Id}',elem: '#div{Id}',onlyIconControl:true, showCheckbox:false,showLine:{ShowLine.ToString().ToLower()}
    {onclick}
    ,data: {LayuiIslandJson.Serialize(treeitems)}
  }});
  {(selecteditem == null ? string.Empty : $@"treecontainer{Id}.config.setSelected({{
     data: {LayuiIslandJson.Serialize(selecteditem)}
    }});")}
  {(string.IsNullOrEmpty(AutoLoadUrl) || selecteditem != null ? string.Empty : $"ff.LoadPage1('{AutoLoadUrl}','div_{Id}');")}
}})
</script>
";
                    output.Content.SetHtmlContent(script);
                }
            }
            else
            {
                output.Content.SetContent("Error：items must be set and must be of type List<TreeSelectListItem>");
            }
            base.Process(context, output);
        }

        private List<LayuiTreeItem2> GetLayuiTree(IEnumerable<TreeSelectListItem> tree, int level = 0)
        {
            List<LayuiTreeItem2> rv = [];
            foreach (var s in tree)
            {
                var news = new LayuiTreeItem2
                {
                    Id = s.Value.ToString(),
                    Title = s.Text,
                    Url = s.Url,
                    Expand = s.Expended,
                    Level = level,
                    Checked = s.Selected
                    //Children = new List<LayuiTreeItem>()
                };
                if (s.Children != null && s.Children.Any())
                {
                    news.Children = GetLayuiTree(s.Children, level + 1);
                    if (news.Children.Any(x => x.Checked == true || x.Expand == true))
                    {
                        news.Expand = true;
                    }
                }
                rv.Add(news);
            }
            return rv;
        }

        private LayuiTreeItem2 GetSelectedItem(List<LayuiTreeItem2> tree)
        {
            foreach (var item in tree)
            {
                if (item.Id == IdField?.Model?.ToString())
                {
                    return item;
                }
                else
                {
                    if (item.Children?.Count > 0)
                    {
                        var rv = GetSelectedItem(item.Children);
                        if (rv != null)
                        {
                            return rv;
                        }
                    }
                }
            }
            return null;
        }

    }

    // Issue #470 Slice N1: DTO for the bare (non-wrapped) 'renderTreeContainer'
    // JSON island — the opt-in (WtmUIOptions.UseSelectIslandRender, default OFF
    // — the SAME flag #470 Slices J/K/L/M use) eval-free replacement for the
    // inline `layui.use(['tree'], function(){ layui.tree.render(...) })`
    // &lt;script&gt; TreeContainerTagHelper otherwise emits.
    // ff._renderTreeContainerAction (framework_layui.js) is the sole consumer;
    // ff._normalizeIslandPayload wraps this into the {actions:[...]} shape
    // ff.DispatchAction expects. Not part of the public API surface.
    //
    // TRUST BOUNDARY: ClickFunc is ALWAYS a compile-time, developer-authored
    // Razor literal (the ClickFunc TagHelper attribute value) — NEVER
    // field/request/model data, the same trust class as bindSubmit's
    // beforeSubmit (#558) / #470 Slice J's renderSelect ChangeFunc. The emitter
    // (TreeContainerTagHelper.ProcessAsync) only ever sets this when the
    // resolved name is already a plain identifier; a non-identifier name keeps
    // the legacy inline &lt;script&gt; instead and this field stays null.
    //
    // clickMode/searchButtonId/gridId/gridExtendWhere are NEVER developer/
    // request data — they are derived purely from server-side regex analysis
    // of the already-rendered nested markup (search-button id, grid option var
    // name), the same analysis the legacy inline render performs inline.
    internal sealed class RenderTreeContainerIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "renderTreeContainer";

        [JsonPropertyName("id")]
        public string Id { get; set; }

        // "div{Id}" — the element the layui tree itself renders into.
        [JsonPropertyName("elemId")]
        public string ElemId { get; set; }

        // "div_{Id}" — wraps the nested child content (grid/searchpanel/etc.)
        // and doubles as the ff.LoadPage1 navigation target (both the initial
        // AutoLoadUrl load and the per-node 'loadPage' click mode).
        [JsonPropertyName("gridDivId")]
        public string GridDivId { get; set; }

        [JsonPropertyName("showLine")]
        public bool ShowLine { get; set; }

        [JsonPropertyName("idFieldName")]
        public string IdFieldName { get; set; }

        [JsonPropertyName("levelFieldName")]
        public string LevelFieldName { get; set; }

        [JsonPropertyName("data")]
        public List<LayuiTreeItem2> Data { get; set; }

        [JsonPropertyName("selectedItem")]
        public LayuiTreeItem2 SelectedItem { get; set; }

        [JsonPropertyName("autoLoadUrl")]
        public string AutoLoadUrl { get; set; }

        // One of: "custom" | "searchButton" | "grid" | "loadPage" | "default".
        // See ff._renderTreeContainerAction for what each mode does.
        [JsonPropertyName("clickMode")]
        public string ClickMode { get; set; }

        [JsonPropertyName("clickFunc")]
        public string ClickFunc { get; set; }

        [JsonPropertyName("searchButtonId")]
        public string SearchButtonId { get; set; }

        [JsonPropertyName("gridId")]
        public string GridId { get; set; }

        [JsonPropertyName("gridExtendWhere")]
        public bool GridExtendWhere { get; set; }
    }
}
