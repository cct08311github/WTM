using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections.Generic;
using System.Net;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        public string EmptyText { get; set; }
        public ModelExpression Items { get; set; }
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
            if (LinkField != null || string.IsNullOrEmpty(LinkId) == false)
            {
                var linkto = "";
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

            var script = $@"
<script>
var {Id} = xmSelect.render({{
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
                var u = ""{(TriggerUrl ?? "")}"";
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
                var {Id}u = ""{(TriggerUrl ?? "")}"";
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
