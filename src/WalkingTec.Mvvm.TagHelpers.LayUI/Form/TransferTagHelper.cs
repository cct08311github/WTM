using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    /// <summary>
    /// 穿梭框
    /// </summary>
    [HtmlTargetElement("wt:transfer", Attributes = REQUIRED_ATTR_NAMES, TagStructure = TagStructure.WithoutEndTag)]
    public class TransferTagHelper : BaseFieldTag
    {
        private const string REQUIRED_ATTR_NAMES = "field";

        private static readonly JsonSerializerOptions _camelCaseOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Issue #633 (#470-F): see ComboBoxTagHelper's _islandJsonOptions for the
        // full rationale (same shared LoadComboItemsIslandAction DTO, defined in
        // ComboBoxTagHelper.cs).
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Issue #470 Slice K: identifier check for the opt-in 'renderTransfer'
        // island decision below — the SAME identifier class framework_layui.js's
        // ff._resolveGuardedWindowFn enforces (a bare JS identifier, nothing
        // else). Duplicated (not shared) per-TagHelper, mirroring
        // ComboBoxTagHelper's/TreeTagHelper's #470 Slice J _identifierRegex
        // exactly, including the `\z` (not `$`) end anchor — see those files'
        // comments for why `$` would silently disagree with the client-side
        // /^[A-Za-z_$][\w$]*$/ regex on a value ending in '\n'.
        private static readonly Regex _identifierRegex = new(@"^[A-Za-z_$][\w$]*\z", RegexOptions.Compiled);

        /// <summary>
        /// 左侧穿梭框上方标题
        /// </summary>
        public string LeftTitle { get; set; }

        /// <summary>
        /// 右侧穿梭框上方标题
        /// </summary>
        public string RightTitle { get; set; }

        /// <summary>
        /// 左侧穿梭框数据源
        /// </summary>
        public ModelExpression Items { get; set; }

        /// <summary>
        /// 启用搜索
        /// 默认 false
        /// </summary>
        public bool EnableSearch { get; set; }

        /// <summary>
        /// 没有数据时的文案
        /// </summary>
        public string NonePlaceholder { get; set; } = THProgram._localizer["Sys.NoData"];

        /// <summary>
        /// 搜索无匹配数据时的文案
        /// </summary>
        public string SearchNonePlaceholder { get; set; } = THProgram._localizer["Sys.NoMatchingData"];

        /// <summary>
        /// 当数据在左右穿梭时触发，回调返回当前被穿梭的数据
        /// param0: 得到当前被穿梭的数据
        /// param1: 如果数据来自左边，index 为 0，否则为 1
        /// param2: 当前 transfer 实例
        /// </summary>
        public string ChangeFunc { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Attributes.Add("id", $"{Id}");
            output.Attributes.Add("wtm-ctype", "transfer");
            output.Attributes.Add("wtm-name", Field.Name);
            List<ComboSelectListItem> listItems = null;

            #region 添加下拉数据 并 设置默认选中

            var modeltype = Field.Metadata.ModelType;
            if (Items != null)
            {
                if (typeof(IEnumerable<ComboSelectListItem>).IsAssignableFrom(Items.Metadata.ModelType))
                {
                    if (typeof(IEnumerable<TreeSelectListItem>).IsAssignableFrom(Items.Metadata.ModelType))
                    {
                        listItems = (Items.Model as IEnumerable<TreeSelectListItem>)?.FlatTreeSelectList().Cast<ComboSelectListItem>().ToList();
                    }
                    else
                    {
                        listItems = (Items.Model as IEnumerable<ComboSelectListItem>)?.ToList();
                    }
                }

                if (listItems == null)
                {
                    listItems = [];
                    if (Items.Metadata.ModelType.IsList())
                    {
                        var exports = (Items.Model as IList);
                        if (exports != null)
                        {
                            foreach (var item in exports)
                            {
                                listItems.Add(new ComboSelectListItem
                                {
                                    Text = item?.ToString(),
                                    Value = item?.ToString()
                                });
                            }
                        }
                    }
                }
            }

            if(listItems == null)
            {
                listItems = [];
            }
            var data = listItems.Select(x => new
            {
                x.Value,
                Title = x.Text,
                x.Disabled,
                Checked = x.Selected
            }).ToArray();

            #endregion
            List<string> selectVal = [];
            if (Field.Name.Contains("["))
            {
                //默认多对多不必填
                if (Required == null)
                {
                    Required = false;
                }
                if (Field?.ModelExplorer?.Container?.Model != null)
                {
                    selectVal.AddRange(Field.ModelExplorer.Container.Model.GetPropertySiblingValues(Field.Name));
                }
            }

            // 赋默认值
            else if (Field.Model != null)
            {
                if (modeltype.IsArray || (modeltype.IsGenericType && typeof(List<>).IsAssignableFrom(modeltype.GetGenericTypeDefinition())))
                {
                    foreach (var item in Field.Model as dynamic)
                    {
                        selectVal.Add($"{item.ToString()}");
                    }
                }
                else
                {
                    selectVal.Add(Field.Model.ToString());
                }
            }
            // Issue #470 Slice K: capture the SAME list this block already
            // computes (previously only round-tripped through DefaultValue as
            // a serialized JSON string for the legacy inline script) so the
            // opt-in 'renderTransfer' island decision further down can reuse
            // it directly as the island's defaultValue array, instead of
            // re-parsing DefaultValue's JSON back out. Purely additive —
            // DefaultValue's own value/serialization is unchanged.
            List<string> effectiveDefaultValue;
            if(selectVal.Count > 0)
            {
                effectiveDefaultValue = selectVal;
                DefaultValue = LayuiIslandJson.Serialize(selectVal);
            }
            else
            {
                if(string.IsNullOrEmpty(DefaultValue) == false)
                {
                    effectiveDefaultValue = DefaultValue.Split(",").Select(x => x.Trim()).ToList();
                    DefaultValue = LayuiIslandJson.Serialize(effectiveDefaultValue);
                }
                else
                {
                    effectiveDefaultValue = [];
                }
            }
            if (string.IsNullOrEmpty(ItemUrl) == false)
            {
                foreach (var item in selectVal)
                {
                    listItems.Add(new ComboSelectListItem
                    {
                        Text = "",
                        Value = item?.ToString(),
                        Selected = true
                    });

                }

                data = listItems.Select(x => new
                {
                    x.Value,
                    Title = x.Text,
                    x.Disabled,
                    Checked = x.Selected
                }).ToArray();
                // Issue #633 (#470-F): eval-free JSON island — see ComboBoxTagHelper's
                // matching comment for the full rationale (shared action shape, JSON
                // escaping, and the async-ajax timing argument, which applies
                // identically here). This also incidentally fixes a pre-existing gap:
                // the legacy inline <script> this replaces interpolated ItemUrl RAW
                // (unlike the Combo/CheckBox/Radio siblings' #108 JavaScriptEncoder
                // wrap) — System.Text.Json now escapes it like every other field.
                var loadComboItemsAction = new LoadComboItemsIslandAction
                {
                    ControlType = "transfer",
                    Url = ItemUrl,
                    Id = Id,
                    Field = Field.Name,
                    SelectVal = selectVal
                };
                output.PostElement.AppendHtml($@"<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(loadComboItemsAction, _islandJsonOptions)}</script>");
            }

            var leftTitleText = string.IsNullOrEmpty(LeftTitle) ? THProgram._localizer["Sys.ForSelect"].ToString() : LeftTitle;
            var rightTitleText = string.IsNullOrEmpty(RightTitle) ? THProgram._localizer["Sys.Selected"].ToString() : RightTitle;
            var title = $"['{leftTitleText}','{rightTitleText}']";

            // Issue #470 Slice K: opt-in (UIConfig.UseSelectIslandRender,
            // default OFF — the SAME flag #470 Slice J's 'renderSelect' island
            // uses for <wt:combobox>/<wt:tree>) eval-free 'renderTransfer'
            // island render — see WtmUIOptions.UseSelectIslandRender and
            // ff._renderTransferAction (framework_layui.js) for the full
            // rationale. changeFuncName/changeIsIdentifier mirror ComboBox-
            // TagHelper's/TreeTagHelper's #470 Slice J 3-way decision exactly:
            // a plain-identifier ChangeFunc name (or no ChangeFunc at all) is
            // safe to carry as JSON island data (resolved client-side via the
            // SAME guarded window[name] lookup every other named-callback
            // action uses); a non-identifier name is NOT — it keeps the exact
            // legacy inline render below, so a developer's arbitrary callback
            // expression is never silently dropped.
            //
            // NOTE: unlike ComboBoxTagHelper/TreeTagHelper, TransferTagHelper
            // is excluded from BaseFieldTag's required-validation block (see
            // BaseFieldTag.Process's `!(this is ... || this is
            // TransferTagHelper)` guard — transfer never gets a
            // window[Id].update({{layVerify,...}}) script, island or legacy),
            // so there is no required-validation state to carry into the
            // island payload here.
            string changeFuncName = string.IsNullOrEmpty(ChangeFunc) ? null : FormatFuncName(ChangeFunc, false);
            bool changeIsIdentifier = changeFuncName != null && _identifierRegex.IsMatch(changeFuncName);
            bool useTransferIsland = UIConfig.UseSelectIslandRender && (changeFuncName == null || changeIsIdentifier);

            if (useTransferIsland)
            {
                var renderTransferAction = new RenderTransferIslandAction
                {
                    Id = Id,
                    El = "#" + Id,
                    Name = Field.Name,
                    Title = [leftTitleText, rightTitleText],
                    Data = data.Select(x => new TransferIslandItem
                    {
                        Value = x.Value,
                        Title = x.Title,
                        Disabled = x.Disabled,
                        Checked = x.Checked
                    }).ToList(),
                    DefaultValue = effectiveDefaultValue,
                    NonePlaceholder = NonePlaceholder,
                    SearchNonePlaceholder = SearchNonePlaceholder,
                    ShowSearch = EnableSearch,
                    Width = Width,
                    Height = Height,
                    Disabled = Disabled,
                    ChangeFunc = changeIsIdentifier ? changeFuncName : null
                };
                output.PostElement.AppendHtml($@"<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(renderTransferAction, _islandJsonOptions)}</script>");
            }
            else
            {
                // Issue #470 Slice K: when the flag is ON but ChangeFunc is a
                // non-identifier expression (island render skipped for this
                // one field — see useTransferIsland above), surface a
                // deprecation nudge in the browser console so the fallback is
                // visible during migration. Mirrors ComboBoxTagHelper's #470
                // Slice J deprecationWarn exactly, including the "contributes
                // ZERO characters when empty" invariant that keeps the
                // flag-OFF / identifier path byte-for-byte identical to the
                // pre-Slice-K inline render.
                var deprecationWarn = (UIConfig.UseSelectIslandRender && changeFuncName != null && !changeIsIdentifier)
                    ? $"console.warn('[WTM] TransferTagHelper #{Id}: ChangeFunc \\'{JavaScriptEncoder.Default.Encode(ChangeFunc)}\\' is not a plain identifier — UseSelectIslandRender is ON but island render was skipped for this field; keeping the legacy inline script. See #470 Slice K.');\n"
                    : "";

                var content = $@"
<script>
layui.use(['transfer'],function(){{
  {deprecationWarn}var $ = layui.$;
  var transfer = layui.transfer;
  var name = '{Field.Name}';
  var _id = '{Id}';
  var container = $('#'+_id+""div"");
  function defaultFunc(data,index,transferIns) {{
    var selectVals = transfer.getData('{Id}');
    /* remove old values */
    var inputs = $('#'+_id+'div input[name=""'+name+'""]')
    if(inputs!=null && inputs.length>0){{
      for (var i = 0; i < inputs.length; i++) {{
        inputs[i].remove();
      }}
    }}
    /* add new values */
    for (var i = 0; i < selectVals.length; i++) {{
      container.append('<input type=""hidden"" name=""'+name+'"" value=""'+selectVals[i].value+'""/>');
    }}
  }}
  var defaultVal = {(string.IsNullOrEmpty(DefaultValue) ? "[]" : DefaultValue)};
  var transferIns = transfer.render({{
    elem: '#'+_id
    ,title:{title}
    ,data:{LayuiIslandJson.Serialize(data,_camelCaseOptions)}
    {(string.IsNullOrEmpty(DefaultValue) ? string.Empty : $",value:defaultVal")}
    ,id:'{Id}'
    ,text:{{none:'{NonePlaceholder}',searchNone:'{SearchNonePlaceholder}'}}
    {(!EnableSearch ? string.Empty : ",showSearch:true")}
    {(!Width.HasValue ? string.Empty : $",width:{Width}")}
    {(!Height.HasValue ? string.Empty : $",height:{Height}")}
    ,onchange: function(data,index){{defaultFunc(data,index,transferIns);
    {(string.IsNullOrEmpty(ChangeFunc) ? string.Empty : $"{FormatFuncInvocation(ChangeFunc, "data, index,transferIns")};")}
    }}
  }});
  /* init default value */
  if(defaultVal!=null && defaultVal.length>0){{
    for (var i = 0; i < defaultVal.length; i++) {{
      container.append('<input type=""hidden"" name=""'+name+'"" value=""'+defaultVal[i]+'""/>');
    }}
  }}
  {(!Disabled?string.Empty: $@"
    $('#'+_id).find(':checkbox').prop('disabled',true)
    $('#'+_id).find(':input').prop('disabled',true)
    transfer.render();")}
}})
</script>
";
                output.PostElement.AppendHtml(content);
            }
            output.PostElement.AppendHtml($"<div id=\"{Id}div\"></div>");
            output.PostElement.AppendHtml($@"<input type=""hidden"" name=""_DONOTUSE_{Field.Name}"" value=""1"" />");

            base.Process(context, output);
        }
    }

    // Issue #470 Slice K: DTO for the bare (non-wrapped) 'renderTransfer' JSON
    // island — the opt-in (WtmUIOptions.UseSelectIslandRender, default OFF —
    // the SAME flag #470 Slice J's 'renderSelect' island uses) eval-free
    // replacement for the inline `layui.use(['transfer'], function(){
    // transfer.render(...) })` &lt;script&gt; TransferTagHelper otherwise emits.
    // ff._renderTransferAction (framework_layui.js) is the sole consumer;
    // ff._normalizeIslandPayload wraps this into the {actions:[...]} shape
    // ff.DispatchAction expects. Not part of the public API surface.
    //
    // TRUST BOUNDARY: ChangeFunc is ALWAYS a compile-time, developer-authored
    // Razor literal (the ChangeFunc TagHelper attribute value) — NEVER
    // field/request/model data, the same trust class as bindSubmit's
    // beforeSubmit (#558) / #470 Slice J's renderSelect ChangeFunc. The
    // emitter (TransferTagHelper.Process) only ever sets this when the
    // resolved name is already a plain identifier; a non-identifier name
    // keeps the legacy inline &lt;script&gt; instead and this field stays null.
    internal sealed class RenderTransferIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "renderTransfer";

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("el")]
        public string El { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        // Two-element [leftTitle, rightTitle] array — mirrors the legacy
        // inline render's `title:['...','...']` literal.
        [JsonPropertyName("title")]
        public string[] Title { get; set; }

        [JsonPropertyName("data")]
        public List<TransferIslandItem> Data { get; set; }

        [JsonPropertyName("defaultValue")]
        public List<string> DefaultValue { get; set; }

        [JsonPropertyName("nonePlaceholder")]
        public string NonePlaceholder { get; set; }

        [JsonPropertyName("searchNonePlaceholder")]
        public string SearchNonePlaceholder { get; set; }

        [JsonPropertyName("showSearch")]
        public bool ShowSearch { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; set; }

        [JsonPropertyName("changeFunc")]
        public string ChangeFunc { get; set; }
    }

    // Issue #470 Slice K: item shape for RenderTransferIslandAction.Data —
    // mirrors the legacy inline render's `data:` array item shape
    // ({{value, title, disabled, checked}}, camelCase) exactly.
    internal sealed class TransferIslandItem
    {
        [JsonPropertyName("value")]
        public string Value { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; set; }

        [JsonPropertyName("checked")]
        public bool Checked { get; set; }
    }
}
