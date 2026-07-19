using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections.Generic;
using System.Net;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:tree",  TagStructure = TagStructure.WithoutEndTag)]
    public class TreeTagHelper : BaseFieldTag
    {
        // Issue #470 Slice G (the #633 miss): see ComboBoxTagHelper's
        // _islandJsonOptions for the full rationale (same shared
        // LoadComboItemsIslandAction DTO, defined there).
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Issue #470 Slice J: identifier check for the opt-in 'renderSelect'
        // island decision below — same class/rationale as ComboBoxTagHelper's
        // _identifierRegex (defined there; duplicated here rather than shared
        // to keep each TagHelper self-contained, matching the existing
        // per-TagHelper regex/JSON-options convention in this codebase).
        private static readonly Regex _identifierRegex = new(@"^[A-Za-z_$][\w$]*\z", RegexOptions.Compiled);

        public string EmptyText { get; set; }
        public ModelExpression Items { get; set; }

        // Issue #470 Slice J (#747): this property has NEVER been wired into
        // the render options below — the xmSelect `tree: { showLine: ... }`
        // option is hard-coded `true` unconditionally in both the legacy
        // inline render and the island render, regardless of this property's
        // value (unlike TreeContainerTagHelper's OWN ShowLine property, which
        // IS wired into its render — a different TagHelper, `<wt:treecontainer>`,
        // not this one). Wiring it now would be a silent behavior change for
        // any caller who set ShowLine=false expecting (correctly, today) no
        // effect — violating the "never silently change default behaviour"
        // red line. Marked deprecated rather than fixed; the property is
        // inert and does nothing. See docs/... and #747 for the audit finding.
        [Obsolete("TreeTagHelper.ShowLine has no effect — the underlying xmSelect render always uses showLine:true (see TreeContainerTagHelper.ShowLine for the equivalent property that IS wired, on a different tag helper). This property is dead and will be removed in a future major version. See #747.")]
        public bool ShowLine { get; set; } = true;
        /// <summary>
        /// 勾选事件
        /// </summary>
        /// <summary>
        /// 勾选时触发的js函数名，func(data)格式;
        /// <para>
        /// data.arr得到当前选中数据数组;
        /// </para>
        /// <para>
        /// data.change得到本次操作变化的数据数组
        /// </para>
        /// <para>
        /// data.isadd得到本次操作是增加还是删除
        /// </para>
        /// </summary>
        public string ChangeFunc { get; set; }
        public bool AutoRow { get; set; }
        public bool? EnableSearch { get; set; }
        public bool? ShowToolbar { get; set; } = true;
        public ModelExpression LinkField { get; set; }

        public string LinkId { get; set; }
        public string TriggerUrl { get; set; }

        /// <summary>
        /// 启用懒加载：填入URL后，展开树节点时会向该URL发送 ?id=&lt;nodeValue&gt; 请求，返回值格式与 ItemUrl 相同。
        /// 注意：LazyUrl 与 Items 互斥，启用时应不传 Items，让树从根节点开始懒加载。(#565)
        /// </summary>
        public string LazyUrl { get; set; }

        public TreeTagHelper(IOptionsMonitor<Configs> configs)
        {
            if (EmptyText == null)
            {
                EmptyText = THProgram._localizer["Sys.PleaseSelect"];
            }
            if (EnableSearch == null)
            {
                EnableSearch = configs.CurrentValue.UIOptions.ComboBox.DefaultEnableSearch;
            }
        }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            bool MultiSelect = false;
            var type = Field.Metadata.ModelType;
            if (Field.Name.Contains("[") || type.IsArray || type.IsList())// Array or List
            {
                MultiSelect = true;
            }

            output.TagName = "div";
            output.Attributes.Add("id", Id);
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Attributes.Add("wtm-ctype", "tree");
            output.Attributes.Add("wtm-name", Field.Name);
            output.Attributes.Add("wtm-multi", MultiSelect.ToString().ToLower());
            Id = string.IsNullOrEmpty(Id) ? Guid.NewGuid().ToNoSplitString() : Id;
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

            List<object> vals = [];
                if (Field?.Model != null)
                {
                    if (MultiSelect == true)
                    {
                        foreach (var item in Field.Model as dynamic)
                        {
                            vals.Add(item.ToString());
                        }
                    }
                    else
                    {
                        vals.Add(Field.Model.ToString());
                    }
                }
            if (vals.Count == 0)
            {
                if (string.IsNullOrEmpty(DefaultValue) == false)
                {
                    vals.AddRange(DefaultValue.Split(','));
                }
            }

            // Issue #470 Slice J (always-on, flag-independent): the SAME
            // data-wtm-defaults attribute CheckBoxTagHelper/RadioTagHelper/
            // ComboBoxTagHelper already emit (#632/Slice J) — ff._readFieldDefaults
            // (framework_layui.js) already prefers this attribute over the
            // window[Id+'defaultvalues'] global. Present at HTML-parse time on
            // every path, decoupled from the widget's own render timing. Value
            // equals the existing global's value — behavior-preserving.
            output.Attributes.Add("data-wtm-defaults", LayuiIslandJson.Serialize(vals.Select(v => v?.ToString()).ToList(), _islandJsonOptions));

            List<LayuiTreeItem> treeitems = [];

                if (string.IsNullOrEmpty(ItemUrl) == true && Items?.Model is List<TreeSelectListItem> mm)
                {
                    treeitems = GetLayuiTree(mm, vals);
                }

            if (string.IsNullOrEmpty(ItemUrl) == false)
            {
                foreach (var item in vals)
                {
                    treeitems.Add(new  LayuiTreeItem
                    {
                        Title = "",
                        Id = item?.ToString(),
                        Checked = true
                    });

                }
                // Issue #753 (Slice-G miss found in review of #753 itself):
                // unlike ComboBoxTagHelper/CheckBoxTagHelper/RadioTagHelper/
                // TransferTagHelper — whose loadComboItems island for their own
                // ItemUrl branch was ALREADY present in base 947ecbc9 (pre-#470,
                // accepted baseline behaviour) — TreeTagHelper's ItemUrl branch
                // in base emitted a legacy inline `<script>ff.LoadComboItems(...)`
                // call, not a JSON island. So this migration must stay gated on
                // UIConfig.UseSelectIslandRender (default OFF) for flag-off
                // output to stay byte-identical to base, exactly like every
                // other Slice-G/H/I emitter this issue exists to fix.
                if (UIConfig.UseSelectIslandRender)
                {
                    // Issue #470 Slice G (the #633 miss): reuse the SAME loadComboItems
                    // island ComboBoxTagHelper/CheckBoxTagHelper/RadioTagHelper/
                    // TransferTagHelper emit for their ItemUrl branch (#633/#470-F)
                    // instead of an inline <script> calling ff.LoadComboItems directly.
                    // The 'loadComboItems' DispatchAction case (framework_layui.js)
                    // already handles controlType 'tree' — see ff.LoadComboItems's
                    // `if (controltype === "tree")` branch — it was simply never wired
                    // up to a server emitter until now. `vals` (List<object>) is
                    // stringified to match LoadComboItemsIslandAction.SelectVal's
                    // List<string> shape, mirroring the other three emitters — matches
                    // ff.LoadComboItems's own `svals` parameter, which only ever does
                    // string comparisons/iteration on it, identical to what
                    // LayuiIslandJson.Serialize(vals) produced inline before. The
                    // legacy inline script always passed a no-op empty function as the
                    // 6th arg (cb); the 'loadComboItems' DispatchAction case hard-codes
                    // that same 6th positional arg to `undefined` for every caller —
                    // calling an empty function or not calling it at all is
                    // behaviourally identical, so this is byte-identical runtime
                    // behaviour.
                    var loadComboItemsAction = new LoadComboItemsIslandAction
                    {
                        ControlType = "tree",
                        Url = ItemUrl,
                        Id = Id,
                        Field = Field.Name,
                        SelectVal = vals.Select(v => v?.ToString()).ToList()
                    };
                    output.PostElement.AppendHtml($@"<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(loadComboItemsAction, _islandJsonOptions)}</script>");
                }
                else
                {
                    // Flag-off legacy path: verbatim from base 947ecbc9 — do not
                    // "clean up" this string, it must stay byte-identical.
                    output.PostElement.AppendHtml($@"<script>
ff.LoadComboItems('tree','{ItemUrl}','{Id}','{Field.Name}',{LayuiIslandJson.Serialize(vals)},function(){{
}})

</script>");
                }
            }

            // Issue #470 Slice J: opt-in (UIConfig.UseSelectIslandRender,
            // default OFF) eval-free 'renderSelect' island render — see
            // ComboBoxTagHelper's identical decision block for the full
            // rationale (same DTO, same JS consumer).
            string changeFuncName = string.IsNullOrEmpty(ChangeFunc) ? null : FormatFuncName(ChangeFunc, false);
            bool changeIsIdentifier = changeFuncName != null && _identifierRegex.IsMatch(changeFuncName);
            bool useSelectIsland = UIConfig.UseSelectIslandRender && (changeFuncName == null || changeIsIdentifier);

            // Issue #470 Slice J follow-up (2nd HIGH defect fix): see
            // ComboBoxTagHelper's identical assignment for the full
            // rationale — tells BaseFieldTag.Process (base.Process(...) at
            // the end of this method) whether this field's widget is being
            // rendered via the island.
            IsUsingSelectIslandRender = useSelectIsland;

            if (useSelectIsland)
            {
                var renderSelectAction = new RenderSelectIslandAction
                {
                    Widget = "tree",
                    Id = Id,
                    El = "#" + Id,
                    Name = Field.Name,
                    Tips = EmptyText,
                    Disabled = Disabled,
                    Language = THProgram._localizer["Sys.LayuiDateLan"] == "CN" ? "zn" : "en",
                    AutoRow = AutoRow,
                    Filterable = EnableSearch == true,
                    MultiSelect = MultiSelect,
                    ShowToolbar = ShowToolbar.GetValueOrDefault(true),
                    Height = "400px",
                    LazyUrl = string.IsNullOrEmpty(LazyUrl) ? null : LazyUrl,
                    Items = treeitems,
                    ChangeFunc = changeIsIdentifier ? changeFuncName : null,
                    LinkTo = linkto,
                    TriggerUrl = TriggerUrl,
                    ChainInitial = vals?.Count > 0 && linkto != null,
                    DefaultValues = vals.Select(v => v?.ToString()).ToList()
                };
                // Issue #470 Slice J follow-up (2nd HIGH defect fix): see
                // ComboBoxTagHelper's identical block for the full rationale
                // (same DTO, same JS consumer).
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
                // deprecation nudge in the browser console. See
                // ComboBoxTagHelper's identical deprecationWarn for the
                // byte-for-byte-when-empty rationale (the trailing "\n" is
                // embedded IN the string, only present when the warning
                // fires, so the flag-OFF path stays byte-identical to the
                // pre-Slice-J inline render).
                var deprecationWarn = (UIConfig.UseSelectIslandRender && changeFuncName != null && !changeIsIdentifier)
                    ? $"console.warn('[WTM] TreeTagHelper #{Id}: ChangeFunc \\'{JavaScriptEncoder.Default.Encode(ChangeFunc)}\\' is not a plain identifier — UseSelectIslandRender is ON but island render was skipped for this field; keeping the legacy inline script. See #470 Slice J.');\n"
                    : "";

                var script = $@"
<script>
{deprecationWarn}var {Id} = xmSelect.render({{
    el: '#{Id}',
    name:'{Field.Name}',
    tips:'{EmptyText}',
    disabled: {Disabled.ToString().ToLower()},
    {(THProgram._localizer["Sys.LayuiDateLan"] == "CN" ? "language:'zn'," : "language:'en',")}
	autoRow: {AutoRow.ToString().ToLower()},
	filterable: {EnableSearch.ToString().ToLower()},
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
        show: {ShowToolbar.Value.ToString().ToLower()},
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
	}},")}
    {(string.IsNullOrEmpty(LazyUrl) ? "" : $@"
    lazy: true,
    load: function(node, cb) {{
        $.get('{LazyUrl}', {{ id: node.value }}, function(data) {{
            cb(ff.getTreeItems(data.Data, []));
        }});
    }},
    ")}
    tree: {{
        strict: false,
		show: true,
		showFolderIcon: true,
		showLine: true,
		indent: 20
	}},
	height: '400px',
    on:function(data){{
        {((LinkField != null || string.IsNullOrEmpty(LinkId) == false) ? @$"
            if ({(string.IsNullOrEmpty(ChangeFunc) ? "true" : FormatFuncName(ChangeFunc))} != false) {{
                var u = ""{JavaScriptEncoder.Default.Encode(TriggerUrl ?? "")}"";
                if (u.indexOf(""?"") == -1) {{
                    u += ""?t="" + new Date().getTime();
                }}
                for (var i = 0; i < data.arr.length; i++) {{
                    u += ""&id="" + data.arr[i].value;
                }}
                ff.ChainChange(u, $('#{Id}')[0])
        }}" : FormatFuncName(ChangeFunc))}
   }},
	data:  {LayuiIslandJson.Serialize(treeitems)}
}});
     {Id}defaultvalues = {LayuiIslandJson.Serialize(vals)};
        {(vals?.Count > 0 && (LinkField != null || string.IsNullOrEmpty(LinkId) == false) ? @$"
                var {Id}u = ""{JavaScriptEncoder.Default.Encode(TriggerUrl ?? "")}"";
                if ({Id}u.indexOf(""?"") == -1) {{
                    {Id}u += ""?t="" + new Date().getTime();
                }}
                var {Id}data = {LayuiIslandJson.Serialize(vals)};
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
                string hidden = $"<p id='tree{Id}hidden'>";
                if (Field?.Model != null)
                {
                    if (MultiSelect == true)
                    {
                        foreach (var item in Field.Model as dynamic)
                        {
                            // Issue #108: model values are user-influenceable data — encode to prevent
                            // HTML-attribute injection via a crafted value containing ' or >.
                            hidden += $@"
<input type='hidden' name='{Field?.Name}' value='{WebUtility.HtmlEncode(item.ToString())}'/>";
                        }
                    }
                    else
                    {
                        // Issue #108: same as above for single-select value.
                        hidden += $"<input type='hidden' name='{Field?.Name}' value='{WebUtility.HtmlEncode(Field.Model?.ToString() ?? string.Empty)}'/>";
                    }
                    hidden += " </p>";
                }

                output.PostElement.AppendHtml(hidden);
            output.PostElement.AppendHtml($@"
<input type=""hidden"" name=""_DONOTUSE_{Field.Name}"" value=""1"" />
");

            base.Process(context, output);
        }

        private List<LayuiTreeItem> GetLayuiTree(IEnumerable<TreeSelectListItem> tree, List<object> values)
        {
            List<LayuiTreeItem> rv = [];
            foreach (var s in tree)
            {
                var news = new LayuiTreeItem
                {
                    Id = s.Value.ToString(),
                    Title = s.Text,
                    Url = s.Url,
                    Expand = s.Expended,
                    Disabled = s.Disabled,
                    Icon = s.Icon
                    //Children = new List<LayuiTreeItem>()
                };
                if (values.Contains(s.Value.ToString()))
                {
                    news.Checked = true;
                }
                if (s.Children != null && s.Children.Any())
                {
                    news.Children = GetLayuiTree(s.Children, values);
                    if(news.Children.Any(x=>x.Checked == true || x.Expand == true))
                    {
                        news.Expand = true;
                    }
                }
                rv.Add(news);
            }
            return rv;
        }


    }
}
