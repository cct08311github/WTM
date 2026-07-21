#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.TagHelpers;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    // Issue #470 Slice O1: opt-in (WtmUIOptions.UseSelectIslandRender, default OFF —
    // the SAME flag Slices J/K/L/M/N1 use) eval-free 'renderGrid' JSON island render
    // for <wt:grid>/DataTableTagHelper. Design authority: Gitea issue #470 comment
    // 18118 ("Slice O design brief"). This file is intentionally split from
    // DataTableTagHelper.cs (which stays completely UNTOUCHED except for the
    // `partial` keyword and the single branch point in Process()) so the flag-OFF
    // byte-identity guard (DataTableByteIdentityTests) can never be disturbed by
    // island-only edits — every method here is either net-new or an early-return
    // gated on the flag, never a modification of existing legacy code.
    //
    // Containment (brief §2 point 3/5, invariant 5): the whole grid falls back to
    // the EXACT legacy BuildTableOptionsScript path (+ a console.warn naming the
    // reason) when any of: IsInSelector (Selector.cshtml grids stay legacy in O1 —
    // smaller blast radius, matches the brief's "recommended" containment),
    // UseLocalData (localData island deferred to O3), EnableAnalysis (inline
    // onclick toggle button until O2/O3), or a developer-authored
    // DoneFunc/CheckedFunc/GridAction.OnClickFunc that is not a bare JS identifier
    // (an arbitrary call/dotted expression can't be safely JSON-expressed — same
    // 3-way decision Slices J/K/L/N1 already use).
    //
    // GridActions toolbar/row-button (brief §2 O1 point 4, invariant 7): even when
    // a grid islandifies, the wtToolBarFunc_{Id} inline dispatcher + the two laytpl
    // <script type="text/html"> templates are STILL emitted verbatim (character-for-
    // character identical to the legacy chunk) — O1 only islandifies the table
    // RENDER core; toolbar/row-button descriptor islandification is O2 scope.
    public partial class DataTableTagHelper
    {
        // Same identifier class ff._resolveGuardedWindowFn (framework_layui.js) and
        // every other #470 slice's containment gate enforces: a bare JS identifier,
        // nothing else. Mirrors ComboBoxTagHelper's/TreeContainerTagHelper's
        // _identifierRegex exactly, including the `\z` (not `$`) end anchor.
        private static readonly Regex _islandIdentifierRegex = new(@"^[A-Za-z_$][\w$]*\z", RegexOptions.Compiled);

        /// <summary>
        /// Result of the flag-ON containment analysis: whether this grid instance is
        /// eligible for island render, and — when the global flag is ON but this
        /// grid still fell back — the human-readable reason (surfaced via
        /// console.warn so the fallback is visible during migration, Slice J
        /// precedent).
        /// </summary>
        private readonly struct GridIslandDecision(bool useIsland, string? flagOnFallbackReason)
        {
            public bool UseIsland { get; } = useIsland;
            public string? FlagOnFallbackReason { get; } = flagOnFallbackReason;
        }

        private GridIslandDecision DetermineGridIslandDecision(List<GridAction>? actionCol)
        {
            if (!WtmUIOptionsHolder.Options.UseSelectIslandRender)
            {
                return new GridIslandDecision(false, null);
            }
            if (IsInSelector)
            {
                return new GridIslandDecision(false, "IsInSelector grids stay legacy in O1 (Selector.cshtml / #655 blast-radius containment)");
            }
            if (UseLocalData)
            {
                return new GridIslandDecision(false, "UseLocalData (localData island deferred to Slice O3)");
            }
            if (EnableAnalysis)
            {
                return new GridIslandDecision(false, "EnableAnalysis (inline analysis-toggle button is legacy until Slice O2/O3)");
            }
            if (!string.IsNullOrEmpty(DoneFunc) && !_islandIdentifierRegex.IsMatch(DoneFunc))
            {
                return new GridIslandDecision(false, $"DoneFunc '{DoneFunc}' is not a plain identifier");
            }
            if (!string.IsNullOrEmpty(CheckedFunc) && !_islandIdentifierRegex.IsMatch(CheckedFunc))
            {
                return new GridIslandDecision(false, $"CheckedFunc '{CheckedFunc}' is not a plain identifier");
            }
            var badOnClick = FindNonIdentifierOnClickFunc(actionCol);
            if (badOnClick != null)
            {
                return new GridIslandDecision(false, $"GridAction.OnClickFunc '{badOnClick}' is not a plain identifier");
            }
            return new GridIslandDecision(true, null);
        }

        private static string? FindNonIdentifierOnClickFunc(IEnumerable<GridAction>? actions)
        {
            if (actions == null)
            {
                return null;
            }
            foreach (var action in actions)
            {
                if (!string.IsNullOrEmpty(action.OnClickFunc) && !_islandIdentifierRegex.IsMatch(action.OnClickFunc))
                {
                    return action.OnClickFunc;
                }
                var nested = FindNonIdentifierOnClickFunc(action.SubActions);
                if (nested != null)
                {
                    return nested;
                }
            }
            return null;
        }

        /// <summary>
        /// Builds the flag-ON island render output: the legacy toolbar-dispatcher
        /// script + laytpl templates (invariant 7 / brief §2 O1 point 4, verbatim),
        /// the <c>renderGrid</c> JSON island (invariant 8: via
        /// <see cref="LayuiIslandJson"/>), and the same trailing blocks
        /// (DetailGridPrix hidden input / button-group script / SearcherExpanded
        /// fold) <c>BuildTableOptionsScript</c> emits — duplicated rather than
        /// shared so the legacy method can stay 100% untouched for the
        /// byte-identity harness.
        /// </summary>
        private void BuildTableIslandScript(
            TagHelperOutput output,
            int maxDepth,
            List<string> aggregateFields,
            Dictionary<string, object> where,
            int lefttoolbarmergin,
            StringBuilder rowBtnStrBuilder,
            StringBuilder toolBarBtnStrBuilder,
            StringBuilder gridBtnEventStrBuilder,
            bool hasButtonGroup,
            bool page)
        {
            var action = BuildRenderGridAction(maxDepth, aggregateFields, where, page, toolBarBtnStrBuilder);

            // Invariant 7 / brief §2 O1 point 4: toolbar dispatcher + laytpl
            // templates stay legacy, byte-for-byte identical in SHAPE to the
            // corresponding fragment of BuildTableOptionsScript (only the
            // surrounding `<script>var {Id}option=null; layui.use(...){...}</script>`
            // render wrapper is replaced by the JSON island below).
            output.PostElement.AppendHtml($@"
<script>
function wtToolBarFunc_{Id}(obj){{ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter=""对应的值""
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象
{(gridBtnEventStrBuilder.Length == 0 ? string.Empty : $@"var ids; var objs;switch(layEvent){{{gridBtnEventStrBuilder}default:break;}}")}
return;
}}
</script>
<script type=""text / html"" id=""{ToolBarId}2"" >
<div  id=""{Id}buttons""style=""text-align:right;margin-right:{lefttoolbarmergin}px"">{toolBarBtnStrBuilder}</div>
</script>
<!-- Grid 行内按钮 -->
<script type=""text/html"" id=""{ToolBarId}"">{rowBtnStrBuilder}</script>
<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(action, _jsonOptions)}</script>
");

            // Trailing blocks — copied verbatim from BuildTableOptionsScript's own
            // trailing-block emission (DetailGridPrix / button-group / SearcherExpanded).
            // EnableAnalysis's own trailing block is intentionally OMITTED here: that
            // condition forces the whole grid to the legacy path (see
            // DetermineGridIslandDecision), so this method is never reached with
            // EnableAnalysis == true.
            output.PostElement.AppendHtml($@"
{(string.IsNullOrEmpty(ListVM.DetailGridPrix) ? string.Empty : $"<input type=\"hidden\" name=\"{Vm.Name}.DetailGridPrix\" value=\"{ListVM.DetailGridPrix}\"/>")}
");
            if (hasButtonGroup == true)
            {
                output.PostElement.AppendHtml($@"<script>
                        setTimeout(function(){{
                            var form = layui.form, $ = layui.jquery;
                            $("".downpanel"").on(""click"", "".layui-select-title"", function(e) {{
                                $("".layui-form-select"").not($(this).parents("".layui-form-select"")).removeClass(""layui-form-selected"");
                                $(this).parents("".layui-form-select"").toggleClass(""layui-form-selected"");
                                            e.stopPropagation();
                                        }});
                            $(document).click(function(event) {{
                            var _con2 = $("".downpanel"");
                            if (!_con2.is (event.target) && (_con2.has(event.target).length === 0)) {{
                            _con2.removeClass(""layui-form-selected"");
                            }}
                            }});
                            }},500);</script>");
            }

            if (SearcherExpanded.HasValue)
            {
                var foldBool = SearcherExpanded.Value ? "false" : "true";
                output.PostElement.AppendHtml($@"<script>
layui.use(['element'], function() {{
  setTimeout(function() {{
    var filter = $('#{SearchPanelId} .layui-collapse').attr('lay-filter');
    if (filter) {{ layui.element.fold(filter, {foldBool}); }}
  }}, 0);
}});
</script>");
            }
        }

        private RenderGridIslandAction BuildRenderGridAction(
            int maxDepth,
            List<string> aggregateFields,
            Dictionary<string, object> where,
            bool page,
            StringBuilder toolBarBtnStrBuilder)
        {
            // Mirrors Process()'s own `toolbardef` gate exactly (:510) — a grid gets a
            // toolbar element when it has row/toolbar buttons OR any of the three
            // built-in defaultToolbar icons (filter/print/client-export).
            bool hasToolbar = toolBarBtnStrBuilder.Length > 0 || NeedShowFilter == true || NeedShowPrint == true || EnableClientExport;

            var defaultToolbar = new List<string>(3);
            if (NeedShowFilter == true) defaultToolbar.Add("filter");
            if (NeedShowPrint == true) defaultToolbar.Add("print");
            if (EnableClientExport) defaultToolbar.Add("exports");

            string? heightMode = null;
            int? heightValue = null;
            if (Height.HasValue)
            {
                if (Height.Value >= 0)
                {
                    heightMode = "fixed";
                    heightValue = Height.Value;
                }
                else
                {
                    heightMode = "full";
                    heightValue = Height.Value;
                }
            }

            GridPageOptions? pageOptions = null;
            if (page)
            {
                pageOptions = new GridPageOptions
                {
                    Rpptext = THProgram._localizer["Sys.RecordsPerPage"],
                    Totaltext = THProgram._localizer["Sys.Total"],
                    Recordtext = THProgram._localizer["Sys.Record"],
                    Gototext = THProgram._localizer["Sys.Goto"],
                    Pagetext = THProgram._localizer["Sys.Page"],
                    Oktext = THProgram._localizer["Sys.GotoButtonText"],
                };
            }

            var action = new RenderGridIslandAction
            {
                GridId = Id,
                TableJsVar = TableJSVar,
                Elem = "#" + Id,
                Id = Id,
                Text = new GridTextOptions { None = THProgram._localizer["Sys.NoData"] },
                // Non-null only when !IsInSelector, mirroring BuildTableOptionsScript's
                // own `IsInSelector==false? ... : ""` conditional (:721). IsInSelector
                // forces the whole grid to the legacy path in O1 (see
                // DetermineGridIslandDecision), so this is always non-null on the
                // island path today — kept conditional for schema/forward-compat
                // correctness once O2/O3 lift the IsInSelector containment.
                Request = IsInSelector ? null : new GridRequestOptions(),
                Toolbar = hasToolbar ? $"#{ToolBarId}2" : null,
                // Always the (possibly EMPTY) list — never coerced to null. Mirrors
                // BuildDefaultToolbar, which ALWAYS emits `,defaultToolbar: [...]`
                // (never omits it), explicitly disabling layui's own built-in
                // filter/print/exports icons when the list is empty. Omitting this
                // field when the grid still has a toolbar (custom buttons only, no
                // built-in icons wanted) would let layui fall back to ITS OWN
                // default icon set instead of showing none — a real behavior
                // regression the flag-OFF harness can't catch (island-only field).
                DefaultToolbar = defaultToolbar,
                TotalRow = NeedShowTotal,
                // Pre-serialized with the SAME default (no-options) JsonSerializer call
                // BuildTableOptionsScript uses at :726 (`JsonSerializer.Serialize(where)`)
                // — preserves exact value semantics (e.g. enum-as-number vs
                // enum-as-camelCase-string) regardless of the outer DTO's own
                // _jsonOptions (which carries a JsonStringEnumConverter that would
                // otherwise re-shape any un-attributed enum inside `where`).
                Where = where.Count == 0 ? (JsonElement?)null : JsonSerializer.SerializeToElement(where),
                Method = Method == null ? "post" : Method.Value.ToString().ToLower(),
                Loading = (Loading ?? true) == false ? (bool?)false : null,
                Page = pageOptions,
                Limit = page ? Limit!.Value : 0,
                Limits = page && Limits != null && Limits.Length > 0 ? Limits : null,
                Width = Width,
                HeightMode = heightMode,
                HeightValue = heightValue,
                Cols = BuildColumnDescriptors(maxDepth),
                Skin = Skin.HasValue ? Skin.Value.ToString().ToLower() : null,
                Even = (Even.HasValue && !Even.Value) ? (bool?)false : null,
                Size = Size.HasValue ? Size.Value.ToString().ToLower() : null,
                Url = Url,
                ExportFileName = ExportFileName ?? Id,
                EnableClientExport = EnableClientExport,
                IsInSelector = IsInSelector,
                DetailGridPrix = string.IsNullOrEmpty(ListVM.DetailGridPrix) ? null : ListVM.DetailGridPrix,
                SearchPanelId = SearchPanelId,
                FieldPre = fieldPre,
                AutoSearch = AutoSearch,
                MobileLayout = page,
                Done = new GridDoneOptions
                {
                    HeightAuto = !Height.HasValue,
                    MaxDepth = maxDepth,
                    LineHeight = LineHeight,
                    MultiLine = MultiLine,
                    EnableHeaderFilter = EnableHeaderFilter,
                    AggregateFields = aggregateFields.Count > 0 ? aggregateFields : null,
                    TitleError = THProgram._localizer["Sys.Error"],
                    TitleColumnFilter = THProgram._localizer["Sys.ColumnFilter"],
                    TitlePrint = THProgram._localizer["Sys.Print"],
                    // Guaranteed to already be a bare identifier (or null) — a
                    // non-identifier value forces the legacy path before this method
                    // is ever reached (see DetermineGridIslandDecision).
                    DoneFn = string.IsNullOrEmpty(DoneFunc) ? null : DoneFunc,
                    CheckedFn = string.IsNullOrEmpty(CheckedFunc) ? null : CheckedFunc,
                },
            };
            return action;
        }

        /// <summary>
        /// Descriptor mirror of <see cref="BuildColumns"/> — walks the SAME
        /// (idempotently cached) <c>ListVM.GetHeaders()</c> tree, but instead of
        /// baking each column's <c>Templet</c> into a raw JS function string
        /// (getTemplate/GetRichTemplate), it captures the STRUCTURED descriptor data
        /// those functions were built from. <c>ff.gridTemplets</c>
        /// (framework_layui.js) rebuilds the equivalent function client-side from
        /// this descriptor — see the class doc for the templet-registry rationale.
        /// Does NOT touch <c>NeedShowTotal</c> beyond a same-value re-OR (the field
        /// is already correctly set by the earlier, unconditional
        /// <c>BuildColumns()</c> call every Process() path makes).
        /// </summary>
        private List<List<LayuiColumnDescriptor>> BuildColumnDescriptors(int maxDepth)
        {
            // ListVM is guaranteed non-null here — Process() throws before either
            // Build*Script path is reached if it is null (same invariant BuildColumns()
            // relies on for its own ListVM?.GetHeaders() call).
            var rawCols = ListVM!.GetHeaders();
            List<List<LayuiColumnDescriptor>> layuiCols = [];

            List<LayuiColumnDescriptor> tempCols = [];
            layuiCols.Add(tempCols);
            if (!HiddenCheckbox)
            {
                var checkboxHeader = new LayuiColumnDescriptor
                {
                    Type = LayuiColumnTypeEnum.Checkbox,
                    LAY_CHECKED = CheckedAll,
                    Rowspan = maxDepth,
                    Fixed = GridColumnFixedEnum.Left,
                    UnResize = true,
                };
                if (LineHeight != null)
                {
                    checkboxHeader.Style = $"height:{LineHeight}px";
                }
                tempCols.Add(checkboxHeader);
            }
            if (!HiddenGridIndex)
            {
                var gridIndex = new LayuiColumnDescriptor
                {
                    Type = LayuiColumnTypeEnum.Numbers,
                    Rowspan = maxDepth,
                    Fixed = GridColumnFixedEnum.Left,
                    UnResize = true,
                };
                if (LineHeight != null)
                {
                    gridIndex.Style = $"height:{LineHeight}px";
                }
                tempCols.Add(gridIndex);
            }

            List<IGridColumn<TopBasePoco>> nextCols = [];
            generateColHeaderDescriptors(rawCols, nextCols, tempCols, maxDepth, 0);
            if (nextCols.Count > 0)
            {
                CalcChildColDescriptors(layuiCols, nextCols, maxDepth, 1);
            }

            if (layuiCols.Count > 0 && layuiCols[0].Count > 0)
            {
                layuiCols[0][0].TotalRowText = ListVM?.TotalText;
            }

            return layuiCols;
        }

        private void CalcChildColDescriptors(List<List<LayuiColumnDescriptor>> layuiCols, List<IGridColumn<TopBasePoco>> rawCols, int maxDepth, int depth)
        {
            List<LayuiColumnDescriptor> tempCols = [];
            layuiCols.Add(tempCols);

            List<IGridColumn<TopBasePoco>> nextCols = [];
            generateColHeaderDescriptors(rawCols, nextCols, tempCols, maxDepth, depth);

            if (nextCols.Count > 0)
            {
                CalcChildColDescriptors(layuiCols, nextCols, maxDepth, depth + 1);
            }
        }

        private void generateColHeaderDescriptors(
            IEnumerable<IGridColumn<TopBasePoco>> rawCols,
            List<IGridColumn<TopBasePoco>> nextCols,
            List<LayuiColumnDescriptor> tempCols,
            int maxDepth, int depth)
        {
            var leftCols = new List<IGridColumn<TopBasePoco>>();
            var midCols = new List<IGridColumn<TopBasePoco>>();
            var rightCols = new List<IGridColumn<TopBasePoco>>();
            foreach (var col in rawCols)
            {
                if (col.Fixed == GridColumnFixedEnum.Left) leftCols.Add(col);
                else if (col.Fixed == GridColumnFixedEnum.Right) rightCols.Add(col);
                else midCols.Add(col);
            }
            generateColHeaderCoreDescriptors(leftCols, nextCols, tempCols, maxDepth, depth);
            generateColHeaderCoreDescriptors(midCols, nextCols, tempCols, maxDepth, depth);
            generateColHeaderCoreDescriptors(rightCols, nextCols, tempCols, maxDepth, depth);
        }

        private void generateColHeaderCoreDescriptors(
            IEnumerable<IGridColumn<TopBasePoco>> rawCols,
            List<IGridColumn<TopBasePoco>> nextCols,
            List<LayuiColumnDescriptor> tempCols,
            int maxDepth, int depth)
        {
            string random = System.Guid.NewGuid().ToString().Replace("-", "");

            foreach (var item in rawCols)
            {
                var resolvedFixed = item.Fixed;
                if (item.Field != null && _fixedLeftFieldSet?.Contains(item.Field) == true)
                    resolvedFixed = GridColumnFixedEnum.Left;
                else if (item.Field != null && _fixedRightFieldSet?.Contains(item.Field) == true)
                    resolvedFixed = GridColumnFixedEnum.Right;

                var tempCol = new LayuiColumnDescriptor
                {
                    Title = item.Title,
                    Field = item.Field,
                    Width = item.Width,
                    Sort = item.Sort,
                    Fixed = resolvedFixed,
                    Align = item.Align,
                    Event = item.Event,
                    UnResize = item.UnResize,
                    Hide = item.Hide,
                    ShowTotal = item.ShowTotal
                };

                if (DisableColumnResize && tempCol.Type == null && tempCol.UnResize != false)
                    tempCol.UnResize = true;

                if (LineHeight != null && item.Fixed.HasValue)
                {
                    tempCol.Style = $"height:{LineHeight}px";
                }

                if ((string.IsNullOrEmpty(ListVM.DetailGridPrix) == true && string.IsNullOrEmpty(item.Field) == false) || item.Field == "BatchError")
                {
                    if (item.RichColumnType != GridRichColumnTypeEnum.Default)
                    {
                        tempCol.Templet = BuildRichTempletDescriptor(item.Field!, item.RichColumnType,
                            item.CurrencyFormat, item.TagColor, item.ImageSize, item.CurrencyCodeField);
                    }
                    else
                    {
                        var isBoolColumn = item.FieldType == typeof(bool) || item.FieldType == typeof(bool?);
                        var hasFormat = item.HasFormat() || isBoolColumn;
                        tempCol.Templet = new GridTempletDescriptor
                        {
                            Tpl = isBoolColumn ? "bool" : "plain",
                            Field = item.Field,
                            Random = random,
                            HasFormat = hasFormat,
                            EncodeFormat = item.EncodeFormat,
                        };
                    }
                }

                switch (item.ColumnType)
                {
                    case GridColumnTypeEnum.Space:
                        tempCol.Type = LayuiColumnTypeEnum.Space;
                        break;
                    case GridColumnTypeEnum.Action:
                        tempCol.Toolbar = $"#{ToolBarId}";
                        break;
                }
                if (item.Children != null && item.Children.Any())
                {
                    tempCol.Colspan = item.ChildrenLength;
                }
                if (maxDepth > 1 && (item.Children == null || !item.Children.Any()))
                {
                    if (maxDepth - depth > 1)
                    {
                        tempCol.Rowspan = maxDepth - depth;
                    }
                }
                tempCols.Add(tempCol);
                if (item.Children != null && item.Children.Any())
                    nextCols.AddRange(item.Children);
            }
        }

        private static GridTempletDescriptor BuildRichTempletDescriptor(
            string field,
            GridRichColumnTypeEnum richType,
            string? currencyFormat,
            string? tagColor,
            int? imageSize,
            string? currencyCodeField)
        {
            return richType switch
            {
                GridRichColumnTypeEnum.Progress => new GridTempletDescriptor { Tpl = "progress", Field = field },
                GridRichColumnTypeEnum.Tag => new GridTempletDescriptor { Tpl = "tag", Field = field, TagColor = tagColor },
                GridRichColumnTypeEnum.Image => new GridTempletDescriptor { Tpl = "image", Field = field, ImageSize = imageSize },
                GridRichColumnTypeEnum.Currency => string.IsNullOrEmpty(currencyCodeField)
                    ? new GridTempletDescriptor { Tpl = "currency", Field = field, CurrencyFormat = currencyFormat }
                    : new GridTempletDescriptor { Tpl = "currencyRow", Field = field, CurrencyCodeField = currencyCodeField },
                _ => new GridTempletDescriptor { Tpl = "plain", Field = field, HasFormat = false },
            };
        }
    }

    // ── Island DTOs ──────────────────────────────────────────────────────────
    // All 'renderGrid' payload shapes. ff._renderGridAction (framework_layui.js)
    // is the sole consumer; ff._normalizeIslandPayload wraps this into the
    // {actions:[...]} shape ff.DispatchAction expects. Not part of the public
    // API surface — internal, same pattern as every other #470 slice's *IslandAction
    // DTOs (RenderSelectIslandAction, RenderTransferIslandAction, etc.).

    internal sealed class RenderGridIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "renderGrid";

        [JsonPropertyName("gridId")]
        public string? GridId { get; set; }

        [JsonPropertyName("tableJsVar")]
        public string? TableJsVar { get; set; }

        [JsonPropertyName("elem")]
        public string? Elem { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("text")]
        public GridTextOptions? Text { get; set; }

        [JsonPropertyName("request")]
        public GridRequestOptions? Request { get; set; }

        [JsonPropertyName("toolbar")]
        public string? Toolbar { get; set; }

        [JsonPropertyName("defaultToolbar")]
        public List<string>? DefaultToolbar { get; set; }

        [JsonPropertyName("totalRow")]
        public bool TotalRow { get; set; }

        [JsonPropertyName("where")]
        public JsonElement? Where { get; set; }

        [JsonPropertyName("method")]
        public string? Method { get; set; }

        [JsonPropertyName("loading")]
        public bool? Loading { get; set; }

        [JsonPropertyName("page")]
        public GridPageOptions? Page { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("limits")]
        public int[]? Limits { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("heightMode")]
        public string? HeightMode { get; set; }

        [JsonPropertyName("heightValue")]
        public int? HeightValue { get; set; }

        [JsonPropertyName("cols")]
        public List<List<LayuiColumnDescriptor>>? Cols { get; set; }

        [JsonPropertyName("skin")]
        public string? Skin { get; set; }

        [JsonPropertyName("even")]
        public bool? Even { get; set; }

        [JsonPropertyName("size")]
        public string? Size { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("exportFileName")]
        public string? ExportFileName { get; set; }

        [JsonPropertyName("enableClientExport")]
        public bool EnableClientExport { get; set; }

        [JsonPropertyName("isInSelector")]
        public bool IsInSelector { get; set; }

        [JsonPropertyName("detailGridPrix")]
        public string? DetailGridPrix { get; set; }

        [JsonPropertyName("searchPanelId")]
        public string? SearchPanelId { get; set; }

        [JsonPropertyName("fieldPre")]
        public string? FieldPre { get; set; }

        [JsonPropertyName("autoSearch")]
        public bool AutoSearch { get; set; }

        [JsonPropertyName("mobileLayout")]
        public bool MobileLayout { get; set; }

        [JsonPropertyName("done")]
        public GridDoneOptions? Done { get; set; }
    }

    internal sealed class GridTextOptions
    {
        [JsonPropertyName("none")]
        public string? None { get; set; }
    }

    internal sealed class GridRequestOptions
    {
        [JsonPropertyName("pageName")]
        public string PageName { get; set; } = "Page";

        [JsonPropertyName("limitName")]
        public string LimitName { get; set; } = "Limit";
    }

    internal sealed class GridPageOptions
    {
        [JsonPropertyName("rpptext")]
        public string? Rpptext { get; set; }

        [JsonPropertyName("totaltext")]
        public string? Totaltext { get; set; }

        [JsonPropertyName("recordtext")]
        public string? Recordtext { get; set; }

        [JsonPropertyName("gototext")]
        public string? Gototext { get; set; }

        [JsonPropertyName("pagetext")]
        public string? Pagetext { get; set; }

        [JsonPropertyName("oktext")]
        public string? Oktext { get; set; }
    }

    internal sealed class GridDoneOptions
    {
        [JsonPropertyName("heightAuto")]
        public bool HeightAuto { get; set; }

        [JsonPropertyName("maxDepth")]
        public int MaxDepth { get; set; }

        [JsonPropertyName("lineHeight")]
        public int? LineHeight { get; set; }

        [JsonPropertyName("multiLine")]
        public bool MultiLine { get; set; }

        [JsonPropertyName("enableHeaderFilter")]
        public bool EnableHeaderFilter { get; set; }

        [JsonPropertyName("aggregateFields")]
        public List<string>? AggregateFields { get; set; }

        [JsonPropertyName("titleError")]
        public string? TitleError { get; set; }

        [JsonPropertyName("titleColumnFilter")]
        public string? TitleColumnFilter { get; set; }

        [JsonPropertyName("titlePrint")]
        public string? TitlePrint { get; set; }

        [JsonPropertyName("doneFn")]
        public string? DoneFn { get; set; }

        [JsonPropertyName("checkedFn")]
        public string? CheckedFn { get; set; }
    }

    internal sealed class LayuiColumnDescriptor
    {
        [JsonPropertyName("LAY_CHECKED")]
        public bool? LAY_CHECKED { get; set; }

        [JsonPropertyName("toolbar")]
        public string? Toolbar { get; set; }

        [JsonPropertyName("type")]
        public LayuiColumnTypeEnum? Type { get; set; }

        [JsonPropertyName("field")]
        public string? Field { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("event")]
        public string? Event { get; set; }

        [JsonPropertyName("colspan")]
        public int? Colspan { get; set; }

        [JsonPropertyName("rowspan")]
        public int? Rowspan { get; set; }

        [JsonPropertyName("sort")]
        public bool? Sort { get; set; }

        [JsonPropertyName("fixed")]
        public GridColumnFixedEnum? Fixed { get; set; }

        [JsonPropertyName("align")]
        public GridColumnAlignEnum? Align { get; set; }

        [JsonPropertyName("unresize")]
        public bool? UnResize { get; set; }

        [JsonPropertyName("hide")]
        public bool? Hide { get; set; }

        [JsonPropertyName("style")]
        public string? Style { get; set; }

        [JsonPropertyName("totalRow")]
        public bool? ShowTotal { get; set; }

        [JsonPropertyName("totalRowText")]
        public string? TotalRowText { get; set; }

        [JsonPropertyName("templet")]
        public GridTempletDescriptor? Templet { get; set; }
    }

    /// <summary>
    /// Client-registered templet descriptor (brief §2 O1 point 1 / §3): the
    /// framework-internal templet registry replacement for a raw JS function
    /// string. <c>tpl</c> selects the <c>ff.gridTemplets[tpl]</c> builder
    /// (framework_layui.js) — 'plain' | 'bool' | 'progress' | 'tag' | 'image' |
    /// 'currency' | 'currencyRow'. Action columns (GridColumnTypeEnum.Action) do
    /// NOT get a templet descriptor at all — they keep the legacy
    /// <c>toolbar: '#{ToolBarId}'</c> column property (see
    /// <c>generateColHeaderCoreDescriptors</c>'s Action case), so 'actionCol' is
    /// documented here for schema completeness but never actually emitted by O1
    /// (toolbar/row-button descriptor islandification is O2 scope, invariant 7).
    /// </summary>
    internal sealed class GridTempletDescriptor
    {
        [JsonPropertyName("tpl")]
        public string? Tpl { get; set; }

        [JsonPropertyName("field")]
        public string? Field { get; set; }

        [JsonPropertyName("random")]
        public string? Random { get; set; }

        [JsonPropertyName("hasFormat")]
        public bool? HasFormat { get; set; }

        [JsonPropertyName("encodeFormat")]
        public bool? EncodeFormat { get; set; }

        [JsonPropertyName("tagColor")]
        public string? TagColor { get; set; }

        [JsonPropertyName("imageSize")]
        public int? ImageSize { get; set; }

        [JsonPropertyName("currencyFormat")]
        public string? CurrencyFormat { get; set; }

        [JsonPropertyName("currencyCodeField")]
        public string? CurrencyCodeField { get; set; }
    }
}
