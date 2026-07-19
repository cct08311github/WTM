using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:combobox", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class ComboBoxTagHelper : BaseFieldTag
    {
        public string EmptyText { get; set; }

        public bool AutoComplete { get; set; }

        public string YesText { get; set; }

        public string NoText { get; set; }

        /// <summary>
        /// 启用搜索
        /// 注意：多选与搜索不能同时启用
        /// </summary>
        public bool? EnableSearch { get; set; }

        public ModelExpression Items { get; set; }

        public ModelExpression LinkField { get; set; }

        public string LinkId { get; set; }
        public string TriggerUrl { get; set; }

        /// <summary>
        /// 是否多选
        /// 默认根据Field 绑定的值类型进行判断。Array or List 即多选，否则单选
        /// </summary>
        public bool? MultiSelect { get; set; }
        public bool AutoRow { get; set; }

        /// <summary>
        /// 改变选择时触发的js函数，func(data)格式;
        /// <para>
        /// data.elem得到select原始DOM对象;
        /// </para>
        /// <para>
        /// data.value得到被选中的值;
        /// </para>
        /// <para>
        /// data.othis得到美化后的DOM对象;
        /// </para>
        /// </summary>
        public string ChangeFunc { get; set; }

        /// <summary>
        /// 启用远程搜索：填入URL后，用户输入时会向该URL发送 ?q=&lt;keyword&gt; 请求，返回值格式与 ItemUrl 相同。
        /// 注意：RemoteUrl 与本地 Items 互斥，启用时本地 Items 数据将被忽略。(#565)
        /// </summary>
        public string RemoteUrl { get; set; }

        // Issue #633 (#470-F): System.Text.Json's default encoder escapes '<', '>',
        // and '&', making the JSON payload safe to embed inside a <script> block
        // without risk of </script> injection — same pattern as SliderTagHelper's
        // _islandJsonOptions (#552). WhenWritingNull drops the optional shared
        // LoadComboItemsIslandAction.Disabled field entirely when unset (this
        // TagHelper never sets it — only CheckBoxTagHelper does).
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Issue #470 Slice J: identifier check for the opt-in 'renderSelect'
        // island decision below — the SAME identifier class framework_layui.js's
        // ff._resolveGuardedWindowFn enforces (a bare JS identifier, nothing
        // else). Mirrors TextBoxTagHelper's _identifierRegex (#601) exactly,
        // including the `\z` (not `$`) end anchor — see that field's comment
        // for why `$` would silently disagree with the client-side
        // /^[A-Za-z_$][\w$]*$/ regex on a value ending in '\n'.
        private static readonly Regex _identifierRegex = new(@"^[A-Za-z_$][\w$]*\z", RegexOptions.Compiled);

        private WTMContext _wtm;
        public ComboBoxTagHelper(IOptionsMonitor<Configs> configs, WTMContext wtm)
        {
            if (EmptyText == null)
            {
                EmptyText = THProgram._localizer["Sys.PleaseSelect"];
            }
            if (EnableSearch == null)
            {
                EnableSearch = configs.CurrentValue.UIOptions.ComboBox.DefaultEnableSearch;
            }
            _wtm = wtm;
        }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.Attributes.Add("id", Id);
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Attributes.Add("name", Field.Name);
            output.Attributes.Add("wtm-name", Field.Name);
            output.Attributes.Add("wtm-ctype", "combo");
            if (Disabled == true)
            {
                output.Attributes.Add("disabled", "disabled");

            }

            var modeltype = Field.Metadata.ModelType;
            if (MultiSelect == null)
            {
                MultiSelect = false;
                if (Field.Name.Contains("[") || modeltype.IsArray || modeltype.IsList())// Array or List
                {
                    MultiSelect = true;
                }
            }
            output.Attributes.Add("wtm-multi", MultiSelect.ToString().ToLower());
            if (string.IsNullOrEmpty(ChangeFunc) == false)
            {
                output.Attributes.Add("wtm-cf", FormatFuncName(ChangeFunc, false));
            }
            // Issue #470 Slice J: hoisted out of the `if` block (was a
            // block-scoped `var linkto`) so the opt-in 'renderSelect' island
            // decision further down can reuse the same value instead of
            // re-deriving it — purely a scope widening, the attribute-add
            // below is unchanged.
            string linkto = null;
            if (LinkField != null || string.IsNullOrEmpty(LinkId) == false)
            {
                if (string.IsNullOrEmpty(LinkId))
                {
                    linkto = Core.Utils.GetIdByName(LinkField.ModelExplorer.Container.ModelType.Name + "." + LinkField.Name);
                }
                else
                {
                    linkto = LinkId;
                }
                output.Attributes.Add("wtm-linkto", $"{linkto}");
            }
            if (TriggerUrl != null)
            {
                output.Attributes.Add("wtm-turl", TriggerUrl);
            }
            var contentBuilder = new StringBuilder();

            output.PreElement.AppendHtml($@"<input type=""hidden"" name=""_DONOTUSE_{Field.Name}"" value=""1"" />");


            #region 添加下拉数据 并 设置默认选中

            List<ComboSelectListItem> listItems = [];
            List<string> selectVal = [];
            if (Field.Name.Contains("[") && modeltype.IsList() == false && modeltype.IsArray == false)
            {
                //默认多对多不必填
                if (Required == null)
                {
                    Required = false;
                }
                selectVal.AddRange(Field.ModelExplorer.Container.Model.GetPropertySiblingValues(Field.Name));
            }
            else if (Field.Model != null)
            {
                if (modeltype.IsArray || (modeltype.IsGenericType && typeof(List<>).IsAssignableFrom(modeltype.GetGenericTypeDefinition())))
                {
                    foreach (var item in Field.Model as dynamic)
                    {
                        selectVal.Add(item.ToString());
                    }
                }
                else
                {
                    selectVal.Add(Field.Model.ToString());
                }
            }

            if (selectVal.Count == 0)
            {
                if (string.IsNullOrEmpty(DefaultValue) == false)
                {
                    selectVal.AddRange(DefaultValue.Split(','));
                }
            }

            // Issue #470 Slice J (always-on, flag-independent): the SAME
            // data-wtm-defaults attribute CheckBoxTagHelper/RadioTagHelper
            // already emit (#632) — ff._readFieldDefaults (framework_layui.js)
            // already prefers this attribute over the window[Id+'defaultvalues']
            // global. Present at HTML-parse time on every path (full page,
            // dialog replay, fragment), decoupled from the widget's own render
            // timing, unlike the legacy global which is only set once the
            // (synchronous or island-deferred) render script actually runs.
            // Value equals the existing global's value — behavior-preserving.
            output.Attributes.Add("data-wtm-defaults", LayuiIslandJson.Serialize(selectVal, _islandJsonOptions));

            if (string.IsNullOrEmpty(ItemUrl) == false)
            {
                //if (_wtm.HttpContext?.Request?.Host != null)
                //{
                //    ItemUrl = _wtm.HttpContext.Request.IsHttps ? "https://" : "http://" + _wtm.HttpContext?.Request?.Host.ToString() + ItemUrl;
                //}
                foreach (var item in selectVal)
                {
                    listItems.Add(new ComboSelectListItem
                    {
                        Text = "",
                        Value = item?.ToString(),
                        Selected = true
                    });

                }
                // Issue #633 (#470-F): eval-free JSON island — thin re-expression of
                // ff.LoadComboItems('combo', url, id, field, selectVal), which the
                // 'loadComboItems' DispatchAction case (framework_layui.js, #551)
                // already knows how to replay. System.Text.Json's default encoder
                // escapes '<', '>', '&', making the payload safe to embed inside a
                // <script> block (no </script> breakout) — this supersedes the #108
                // defensive JS-encode of ItemUrl, since JSON string escaping covers
                // the same quote/backslash breakout risk.
                // Timing: LoadComboItems only ever mutates the widget from inside an
                // async $.get callback (a network round trip), so it is safe whether
                // it fires before or after the always-unconditional inline xmSelect
                // render script below (untouched by this slice) — by the time the
                // ajax response arrives, window[Id] (assigned synchronously by that
                // script) already exists on every path (full page, dialog replay,
                // fragment). See the #633 PR body for the full ordering analysis.
                var loadComboItemsAction = new LoadComboItemsIslandAction
                {
                    ControlType = "combo",
                    Url = ItemUrl,
                    Id = Id,
                    Field = Field.Name,
                    SelectVal = selectVal
                };
                output.PostElement.AppendHtml($@"<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(loadComboItemsAction, _islandJsonOptions)}</script>");
            }

            else
            {
                if (Items?.Model == null) // 添加默认下拉数据源
                {
                    var checktype = modeltype;
                    if ((modeltype.IsGenericType && typeof(List<>).IsAssignableFrom(modeltype.GetGenericTypeDefinition())))
                    {
                        checktype = modeltype.GetGenericArguments()[0];
                    }

                    if (checktype.IsEnumOrNullableEnum())
                    {
                        listItems = checktype.ToListItems(Field.Model?? DefaultValue);
                    }
                    else if (checktype == typeof(bool) || checktype == typeof(bool?))
                    {
                        bool? df = null;
                        if (bool.TryParse(DefaultValue ?? "", out bool test) == true)
                        {
                            df = test;
                        }
                        listItems = Utils.GetBoolCombo(BoolComboTypes.Custom, (bool?)Field.Model??df, YesText, NoText);
                    }
                }
                else // 添加用户设置的设置源
                {
                    if (typeof(IEnumerable<ComboSelectListItem>).IsAssignableFrom(Items.Metadata.ModelType))
                    {
                        if (typeof(IEnumerable<TreeSelectListItem>).IsAssignableFrom(Items.Metadata.ModelType))
                        {
                            listItems = (Items.Model as IEnumerable<TreeSelectListItem>).FlatTreeSelectList().Cast<ComboSelectListItem>().ToList();
                        }
                        else
                        {
                            listItems = (Items.Model as IEnumerable<ComboSelectListItem>).ToList();
                        }
                        if (selectVal.Count > 0)
                        {
                            foreach (var item in listItems)
                            {
                                item.Selected = selectVal.Contains(item.Value?.ToString());
                            }
                        }
                    }
                    else if (Items.Metadata.ModelType.IsList())
                    {
                        var exports = (Items.Model as IList);
                        foreach (var item in exports)
                        {
                            listItems.Add(new ComboSelectListItem
                            {
                                Text = item?.ToString(),
                                Value = item?.ToString(),
                                Selected = selectVal.Contains(item?.ToString())
                            });
                        }
                    }
                }
            }

            // Issue #470 Slice J: opt-in (UIConfig.UseSelectIslandRender,
            // default OFF) eval-free 'renderSelect' island render — see
            // WtmUIOptions.UseSelectIslandRender and
            // ff._renderSelectAction (framework_layui.js) for the full
            // rationale. ChangeFuncName mirrors TextBoxTagHelper's #601
            // 3-way decision: FormatFuncName(..., appendparameter:false)
            // truncates any explicit call-argument syntax down to the bare
            // function name; a plain-identifier name (or no ChangeFunc at
            // all) is safe to carry as JSON island data (resolved
            // client-side via the SAME guarded window[name] lookup every
            // other named-callback action uses), a non-identifier name is
            // NOT — it keeps the exact legacy inline render below, so a
            // developer's arbitrary callback expression is never silently
            // dropped.
            string changeFuncName = string.IsNullOrEmpty(ChangeFunc) ? null : FormatFuncName(ChangeFunc, false);
            bool changeIsIdentifier = changeFuncName != null && _identifierRegex.IsMatch(changeFuncName);
            bool useSelectIsland = UIConfig.UseSelectIslandRender && (changeFuncName == null || changeIsIdentifier);

            // Issue #470 Slice J follow-up (2nd HIGH defect fix): tell
            // BaseFieldTag.Process (called via base.Process(...) at the end
            // of this method) whether this field's widget is being rendered
            // via the island — see IsUsingSelectIslandRender's doc comment
            // for why that changes how required-validation must be wired.
            IsUsingSelectIslandRender = useSelectIsland;

            if (useSelectIsland)
            {
                var renderSelectAction = new RenderSelectIslandAction
                {
                    Widget = "combo",
                    Id = Id,
                    El = "#" + Id,
                    Name = Field.Name,
                    Tips = EmptyText,
                    Disabled = Disabled,
                    Language = THProgram._localizer["Sys.LayuiDateLan"] == "CN" ? "zn" : "en",
                    AutoRow = AutoRow,
                    Filterable = string.IsNullOrEmpty(RemoteUrl) ? (EnableSearch == true) : true,
                    MultiSelect = MultiSelect == true,
                    ShowToolbar = true,
                    Height = "400px",
                    RemoteUrl = string.IsNullOrEmpty(RemoteUrl) ? null : RemoteUrl,
                    Items = GetLayuiTree(listItems, selectVal),
                    ChangeFunc = changeIsIdentifier ? changeFuncName : null,
                    LinkTo = linkto,
                    TriggerUrl = TriggerUrl,
                    ChainInitial = selectVal?.Count > 0 && linkto != null,
                    DefaultValues = selectVal
                };
                // Issue #470 Slice J follow-up (2nd HIGH defect fix): carry
                // the required-validation state into the island payload —
                // IsFieldRequired() is the EXACT condition BaseFieldTag.Process
                // gates its required-validation block on. ff._renderSelectAction
                // applies these right after xmSelect.render(...), the same
                // window[Id].update({layVerify, layReqText}) call the
                // (now island-path-skipped) inline script used to make.
                if (IsFieldRequired())
                {
                    renderSelectAction.LayVerify = "required";
                    renderSelectAction.LayReqText = $"{THProgram._localizer["Validate.{0}required", Field?.Metadata?.DisplayName ?? Field?.Metadata?.Name]}";
                }
                output.PostElement.AppendHtml($@"<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(renderSelectAction, _islandJsonOptions)}</script>");
            }
            else
            {
                // Issue #470 Slice J: when the flag is ON but ChangeFunc is a
                // non-identifier expression (island render skipped for this
                // one field — see useSelectIsland above), surface a
                // deprecation nudge in the browser console so the fallback
                // is visible during migration. ChangeFunc is a compile-time,
                // developer-authored Razor literal (never request/field
                // data — same trust class as every other named-callback
                // attribute in this file), but is still run through
                // JavaScriptEncoder here as defense-in-depth since it is
                // spliced into a JS string literal.
                // Note: when empty, this contributes ZERO characters (not even
                // a blank line) to `script` below, so the flag-OFF / identifier
                // path stays byte-for-byte identical to the pre-Slice-J inline
                // render — the trailing "\n" is embedded IN the string itself
                // (only present when the warning fires) rather than as a
                // separate literal line in the `script` template.
                var deprecationWarn = (UIConfig.UseSelectIslandRender && changeFuncName != null && !changeIsIdentifier)
                    ? $"console.warn('[WTM] ComboBoxTagHelper #{Id}: ChangeFunc \\'{JavaScriptEncoder.Default.Encode(ChangeFunc)}\\' is not a plain identifier — UseSelectIslandRender is ON but island render was skipped for this field; keeping the legacy inline script. See #470 Slice J.');\n"
                    : "";

                var script = $@"
<script>
{deprecationWarn}var {Id} = xmSelect.render({{
    el: '#{Id}',
    name:'{Field.Name}',
    tips:'{EmptyText}',
    disabled: {Disabled.ToString().ToLower()},
    {(THProgram._localizer["Sys.LayuiDateLan"] =="CN"? "language:'zn'," : "language:'en',")}
	autoRow: {AutoRow.ToString().ToLower()},
	filterable: {(string.IsNullOrEmpty(RemoteUrl) ? EnableSearch.ToString().ToLower() : "true")},
    {(string.IsNullOrEmpty(RemoteUrl) ? "" : $@"
    remoteSearch: true,
    remoteMethod: function(val, cb) {{
        $.get('{JavaScriptEncoder.Default.Encode(RemoteUrl)}', {{ q: val }}, function(data) {{
            cb(ff.getComboItems(data.Data, []));
        }});
    }},
    ")}
    template({{ item, sels, name, value }}){{
        if(item.icon !== undefined && item.icon != """"&& item.icon != null){{
			return '<i class=""'+item.icon+'""></i>' + item.name;
        }}
        else{{
            return item.name;
        }}
	}},
    {(MultiSelect == false ? $@"
    radio: true,
    clickClose: true,
    model: {{
        label: {{
            type: 'abc' ,
            abc: {{
                template: function(item, sels){{
                    if(sels[0].icon !== undefined && sels[0].icon != """" && sels[0].icon != null){{
                        return '<i class=""'+sels[0].icon+'""></i>' + sels[0].name;
                    }}
                    else{{
                        return sels[0].name;
                    }}
                }}
            }}
        }}
    }},
    toolbar: {{
        show: true,
        list: ['CLEAR']}}," : $@"
        toolbar: {{show: true,list: ['ALL', 'REVERSE', 'CLEAR']}},
        model: {{
		label: {{
			block: {{
				template: function(item, sels){{
                    if(item.icon !== undefined && item.icon != """"&& item.icon != null){{
					    return '<i class=""'+item.icon+'""></i>' + item.name;
                    }}
                    else{{
                        return item.name;
                    }}
				}},
			}},
		}}
	}},
")}
	height: '400px',
    on:function(data){{
        {((LinkField != null || string.IsNullOrEmpty(LinkId) == false)?@$"
            if ({(string.IsNullOrEmpty(ChangeFunc)?"true":FormatFuncName(ChangeFunc))} != false) {{
                var u = ""{JavaScriptEncoder.Default.Encode(TriggerUrl??"")}"";
                if (u.indexOf(""?"") == -1) {{
                    u += ""?t="" + new Date().getTime();
                }}
                for (var i = 0; i < data.arr.length; i++) {{
                    u += ""&id="" + data.arr[i].value;
                }}
                ff.ChainChange(u, $('#{Id}')[0])
        }}" : FormatFuncName(ChangeFunc))}
   }},
	data:  {LayuiIslandJson.Serialize(GetLayuiTree(listItems,selectVal))}
}});
     {Id}defaultvalues = {LayuiIslandJson.Serialize(selectVal)};
        {(selectVal?.Count>0 && (LinkField != null || string.IsNullOrEmpty(LinkId) == false) ? @$"
                var {Id}u = ""{JavaScriptEncoder.Default.Encode(TriggerUrl ?? "")}"";
                if ({Id}u.indexOf(""?"") == -1) {{
                    {Id}u += ""?t="" + new Date().getTime();
                }}
                var {Id}data = {LayuiIslandJson.Serialize(selectVal)};
                for (var i = 0; i < {Id}data.length; i++) {{
                    {Id}u += ""&id="" + {Id}data[i];
                }};
                setTimeout(function(){{
                    ff.ChainChange({Id}u, $('#{Id}')[0], true);
                }},100);
        " : "")}
</script>
";
                output.PostElement.AppendHtml(script);
            }
            #endregion


            base.Process(context, output);
        }

        private List<LayuiTreeItem> GetLayuiTree(IEnumerable<ComboSelectListItem> tree, List<string> values)
        {
            List<LayuiTreeItem> rv = [];
            foreach (var s in tree)
            {
                var news = new LayuiTreeItem
                {
                    Id = s.Value.ToString(),
                    Title = s.Text,
                    Disabled = s.Disabled,
                    Checked = s.Selected,
                    Icon = s.Icon
                };
                if (values.Contains(s.Value.ToString()))
                {
                    news.Checked = true;
                }
                rv.Add(news);
            }
            return rv;
        }

    }

    // Issue #633 (#470-F): DTO for the bare (non-wrapped) loadComboItems JSON
    // island — {"type":"loadComboItems","controlType":"...","url":"...","id":"...",
    // "field":"...","selectVal":[...],"disabled":true}. Shared by ComboBoxTagHelper
    // (this file), CheckBoxTagHelper, RadioTagHelper, and TransferTagHelper — all
    // four ItemUrl branches emit the SAME action shape the 'loadComboItems'
    // DispatchAction case (framework_layui.js, #551) already knew how to replay
    // onto ff.LoadComboItems(controlType, url, id, field, selectVal[, cb, disabled]).
    // ff._normalizeIslandPayload wraps this into the {actions:[...]} shape
    // ff.DispatchAction expects; not part of the public API surface.
    internal sealed class LoadComboItemsIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "loadComboItems";

        [JsonPropertyName("controlType")]
        public string ControlType { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("field")]
        public string Field { get; set; }

        [JsonPropertyName("selectVal")]
        public List<string> SelectVal { get; set; }

        // Issue #633: CheckBoxTagHelper is the only current emitter that sets this —
        // mirrors the legacy inline script's always-explicit 7th positional arg
        // (`,undefined,{Disabled.ToString().ToLower()}`). Omitted (never emitted as
        // null, via _islandJsonOptions' WhenWritingNull) for ComboBox/Radio/Transfer,
        // matching their legacy calls, which never passed a 7th arg at all (JS
        // leaves an omitted trailing arg `undefined`, same effective result).
        [JsonPropertyName("disabled")]
        public bool? Disabled { get; set; }
    }

    // Issue #470 Slice J: DTO for the bare (non-wrapped) 'renderSelect' JSON
    // island — the opt-in (WtmUIOptions.UseSelectIslandRender, default OFF)
    // eval-free replacement for the inline xmSelect.render(...) <script>
    // ComboBoxTagHelper/TreeTagHelper otherwise emit. Shared by both
    // TagHelpers (this file and TreeTagHelper.cs) — same shape, `Widget`
    // discriminates 'combo' vs 'tree' the same way LoadComboItemsIslandAction's
    // ControlType does. ff._renderSelectAction (framework_layui.js) is the
    // sole consumer; ff._normalizeIslandPayload wraps this into the
    // {actions:[...]} shape ff.DispatchAction expects. Not part of the public
    // API surface.
    //
    // TRUST BOUNDARY: ChangeFunc is ALWAYS a compile-time, developer-authored
    // Razor literal (the ChangeFunc TagHelper attribute value) — NEVER
    // field/request/model data, the same trust class as bindSubmit's
    // beforeSubmit (#558) / laydate's readyFn/changeFn/doneFn (#470 Slice H).
    // The emitters only ever set this when the resolved name is already a
    // plain identifier (see ComboBoxTagHelper/TreeTagHelper.Process); a
    // non-identifier name keeps the legacy inline <script> instead and this
    // field stays null for that field.
    internal sealed class RenderSelectIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "renderSelect";

        [JsonPropertyName("widget")]
        public string Widget { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("el")]
        public string El { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("tips")]
        public string Tips { get; set; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; set; }

        [JsonPropertyName("language")]
        public string Language { get; set; }

        [JsonPropertyName("autoRow")]
        public bool AutoRow { get; set; }

        [JsonPropertyName("filterable")]
        public bool Filterable { get; set; }

        [JsonPropertyName("multiSelect")]
        public bool MultiSelect { get; set; }

        // Tree's single-select toolbar visibility is developer-configurable
        // (TreeTagHelper.ShowToolbar); combo hard-codes true in the legacy
        // inline render, reproduced the same way here.
        [JsonPropertyName("showToolbar")]
        public bool ShowToolbar { get; set; } = true;

        [JsonPropertyName("height")]
        public string Height { get; set; }

        [JsonPropertyName("remoteUrl")]
        public string RemoteUrl { get; set; }

        [JsonPropertyName("lazyUrl")]
        public string LazyUrl { get; set; }

        [JsonPropertyName("items")]
        public List<LayuiTreeItem> Items { get; set; }

        [JsonPropertyName("changeFunc")]
        public string ChangeFunc { get; set; }

        [JsonPropertyName("linkTo")]
        public string LinkTo { get; set; }

        [JsonPropertyName("triggerUrl")]
        public string TriggerUrl { get; set; }

        [JsonPropertyName("chainInitial")]
        public bool ChainInitial { get; set; }

        [JsonPropertyName("defaultValues")]
        public List<string> DefaultValues { get; set; }

        // Issue #470 Slice J follow-up (2nd HIGH defect fix): carries the
        // required-validation state into the island payload so
        // ff._renderSelectAction can apply it right after
        // xmSelect.render(...) — window[Id].update({layVerify, layReqText}),
        // the SAME call BaseFieldTag's (now island-path-skipped) inline
        // <script> used to make. Only ComboBoxTagHelper/TreeTagHelper ever
        // set this (when IsFieldRequired() is true AND the island path is
        // taken); every other emitter leaves it null, and
        // _islandJsonOptions' WhenWritingNull omits it from the JSON
        // entirely, matching LoadComboItemsIslandAction.Disabled's existing
        // convention.
        [JsonPropertyName("layVerify")]
        public string LayVerify { get; set; }

        [JsonPropertyName("layReqText")]
        public string LayReqText { get; set; }
    }

    // Issue #651: ff.OpenDialog2 (the <wt:selector> search-panel dialog opener,
    // SelectorTagHelper.cs) tokenizes the WHOLE composed search-panel template
    // with GLOBAL regex replaces — $$dialoginit$$/$$#dialoginit$$ (a
    // wtm-dialog-init island's own open/close tag) and $$script$$/$$#script$$
    // (any bare <script> tag) — and framework_layui.js's OpenDialog2 rehydrates
    // those same tokens back into real markup with an equally global regex
    // (`.replace(/[$]{2}dialoginit[$]{2}/img, ...)` etc.), over the ENTIRE
    // template string, not scoped to the sentinel occurrences SelectorTagHelper
    // itself placed. The four loadComboItemsAction islands below (Combo/CheckBox/
    // Radio/Transfer) and the data-wtm-defaults attribute (CheckBox/Radio) carry
    // MODEL-DERIVED data (selectVal, url, the defaults array) inside that
    // template. _islandJsonOptions' default encoder escapes '<', '>', '&' (safe
    // against a raw </script> breakout) but NOT '$' — so a stored value
    // containing e.g. "$$#dialoginit$$$$script$$window.evil=1$$#script$$" would
    // survive JSON serialization intact and, once the composed template reaches
    // OpenDialog2, get tokenized/rehydrated exactly like a real sentinel,
    // yielding attacker-controlled markup/script execution (stored XSS).
    //
    // The fix: after serializing, replace every '$' character with the 6-char
    // JSON Unicode escape sequence for it (backslash, 'u', '0', '0', '2', '4').
    // This is always a valid, reversible transform for these payloads — '$' can
    // only ever appear inside JSON STRING VALUES here (the structural JSON:
    // braces, the fixed property names, the defaults array shape, never contains
    // '$'). The client's JSON.parse (ff.LoadComboItems's dispatch,
    // ff._readFieldDefaults) already decodes that escape sequence back to a
    // plain '$' per the JSON spec, so callers see the exact original value. With
    // no literal '$$' anywhere in the payload, the $$dialoginit$$/$$script$$
    // global replaces can only ever match the real sentinels SelectorTagHelper
    // placed. Route every wtm-dialog-init island / data-wtm-defaults attribute
    // serialization through this helper — do not call JsonSerializer.Serialize
    // directly for those payloads.
    //
    // Issue #651 (follow-up — same collision, inline &lt;script&gt; bodies): the
    // wtm-dialog-init island and the data-wtm-defaults attribute are NOT the only
    // model-derived JSON that lands inside the tokenized selector-panel template.
    // These same four TagHelpers ALSO emit INLINE &lt;script&gt; bodies carrying
    // JsonSerializer.Serialize output — ComboBox/CheckBox/Radio's
    // `{Id}defaultvalues = [...]`, ComboBox's xmSelect `data:` and setTimeout
    // `{Id}data`, Transfer's `defaultVal` value and `data:`. When such a field is
    // a searcher inside a &lt;wt:selector&gt; panel, that inline &lt;script&gt; is
    // tokenized to $$script$$...$$#script$$ by SelectorTagHelper too, so an
    // unescaped '$$#script$$' in the serialized model data breaks out of the
    // inline script exactly as it would out of an island. Those call sites pass
    // DEFAULT options (not _islandJsonOptions); the parameterless overload below
    // keeps them on the identical '$'-escaping invariant. This is transparent in
    // a plain inline-JS context as well: the JS parser decodes a $ inside a
    // string literal back to '$' at parse time, so a NORMAL (non-selector) form
    // sees byte-identical runtime values — only the WIRE encoding changes, never
    // behaviour. (Partially addresses #652.)
    internal static class LayuiIslandJson
    {
        internal static string Serialize<TValue>(TValue value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(value, options).Replace("$", "\\u0024");

        // Default-options overload for the inline-&lt;script&gt; sites described
        // above (they call JsonSerializer.Serialize(value) with no options).
        internal static string Serialize<TValue>(TValue value) =>
            JsonSerializer.Serialize(value).Replace("$", "\\u0024");
    }
}
