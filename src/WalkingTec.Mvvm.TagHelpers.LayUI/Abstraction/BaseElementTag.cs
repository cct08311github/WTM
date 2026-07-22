using Microsoft.AspNetCore.Razor.TagHelpers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    public abstract class BaseElementTag : TagHelper
    {
        // Issue #470 Slice N1: same shared WtmUIOptionsHolder BaseFieldTag.UIConfig /
        // BaseButtonTag.UIConfig read — gives TreeContainerTagHelper/ChartTagHelper
        // (and any future BaseElementTag subclass that doesn't go through
        // BaseFieldTag/BaseButtonTag) access to WtmUIOptions.UseSelectIslandRender
        // without a second startup wiring call. Purely additive — no existing
        // BaseElementTag consumer reads this, so it changes nothing for them.
        protected WtmUIOptions UIConfig => WtmUIOptionsHolder.Options;

        // Issue #784 (#470 residual): identifier check for the checkbox/switch/
        // radio ChangeFunc and TextBox ChangeFunc island migrations below — the
        // SAME identifier class every other #470 slice's guarded
        // ff._resolveGuardedWindowFn resolver enforces: a bare JS identifier,
        // nothing else. Uses `\z` (not `$`) as the end anchor for the same
        // reason as SliderTagHelper's/TextBoxTagHelper's #470 Slice H/I
        // _identifierRegex — keeps .NET's and JS's differing `$` semantics in
        // agreement.
        private static readonly Regex _changeFuncIdentifierRegex = new(@"^[A-Za-z_$][\w$]*\z", RegexOptions.Compiled);

        // Issue #784 (#470 residual): island DTOs omit null members, matching
        // every other #470 slice's _islandJsonOptions convention.
        private static readonly JsonSerializerOptions _formChangeIslandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public int? Colspan { get; set; }
        public string Id { get; set; }

        public int? Height { get; set; }

        public int? Width { get; set; }

        public string Class { get; set; }

        public string Style { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            var preHtml = string.Empty;
            var postHtml = string.Empty;
            if (output.Attributes.ContainsName("id") == false && string.IsNullOrEmpty(Id) == false)
            {
                output.Attributes.SetAttribute("id", Id);
            }
            if (output.Attributes.ContainsName("lay-filter") == false && output.Attributes.ContainsName("id") == true)
            {
                output.Attributes.SetAttribute("lay-filter", $"{output.Attributes["id"].Value}filter");
            }
            if (string.IsNullOrEmpty(Class) == false)
            {
                output.Attributes.SetAttribute("class", Class);
            }
            if (Style == null)
            {
                Style = "";
            }
            if (Width.HasValue)
            {
                Style += $" width:{Width}px;";
            }
            if (Height.HasValue)
            {
                Style += $" min-height:{Height}px;";
            }
            if (string.IsNullOrEmpty(Style) == false)
            {
                if (this is TreeTagHelper )
                {
                    Style += " overflow:auto;";
                }
                TagHelperAttribute prestyle = null;
                if(output.Attributes.TryGetAttribute("style", out prestyle))
                {
                    string s = prestyle.Value.ToString();
                    if(s.EndsWith(";") == false)
                    {
                        s += ";";
                    }
                    Style = s+Style;
                }
                
                output.Attributes.SetAttribute("style",  Style);
            }

            if (context.Items.ContainsKey("ipr"))
            {
                int? ipr = (int?)context.Items["ipr"];
                if (ipr > 0)
                {
                    int col = 12 / ipr.Value;
                    if (Colspan != null)
                    {
                        col *= Colspan.Value;
                    }

                    // Build responsive column class starting with md breakpoint
                    var colClass = $"layui-col-md{col}";

                    if (context.Items.ContainsKey("ipr_sm"))
                    {
                        int? iprSm = (int?)context.Items["ipr_sm"];
                        if (iprSm > 0)
                        {
                            int colSm = 12 / iprSm.Value;
                            if (Colspan != null) colSm *= Colspan.Value;
                            colClass = $"layui-col-sm{colSm} " + colClass;
                        }
                    }

                    if (context.Items.ContainsKey("ipr_xs"))
                    {
                        int? iprXs = (int?)context.Items["ipr_xs"];
                        if (iprXs > 0)
                        {
                            int colXs = 12 / iprXs.Value;
                            if (Colspan != null) colXs *= Colspan.Value;
                            colClass = $"layui-col-xs{colXs} " + colClass;
                        }
                    }

                    preHtml = $@"
<div class=""{colClass}"">
" + preHtml;
                    postHtml += @"
</div>
";
                    output.PreElement.SetHtmlContent(preHtml + output.PreElement.GetContent());
                    output.PostElement.AppendHtml(postHtml);
                }
                if(this is CardTagHelper || this is FormTagHelper || this is ContainerTagHelper || this is TreeContainerTagHelper || this is SearchPanelTagHelper)
                {
                    context.Items.Remove("ipr");
                    context.Items.Remove("ipr_xs");
                    context.Items.Remove("ipr_sm");
                }
            }
            //输出事件
            switch (this)
            {
                case ComboBoxTagHelper item:
                    if(item.MultiSelect == true)
                    {
                        break;
                    }
//                    if (item.LinkField != null || item.LinkId != null)
//                    {
//                        if (!string.IsNullOrEmpty(item.TriggerUrl))
//                        {
//                            output.PostElement.AppendHtml($@"
//<script>
//layui.use(['form'],function(){{
//  var form = layui.form;
//  form.on('select({output.Attributes["lay-filter"].Value})', function(data){{
//    {FormatFuncName(item.ChangeFunc)};
//    ff.ChainChange('{item.TriggerUrl}/'+data.value,data.elem)
//    ff.changeComboIcon(data);
//  }});
//}})
//</script>
//");
//                        }
//                    }
//                    else
//                    {
//                        output.PostElement.AppendHtml($@"
//<script>
//layui.use(['form'],function(){{
//  var form = layui.form;
//  form.on('select({output.Attributes["lay-filter"].Value})', function(data){{
//    {FormatFuncName(item.ChangeFunc)};
//    ff.changeComboIcon(data);
//  }});
//}})
//</script>
//");
//                    }
                    break;
                case CheckBoxTagHelper item:
                    if (string.IsNullOrEmpty(item.ChangeFunc) == false)
                    {
                        output.PostContent.SetHtmlContent(output.PostContent.GetContent().Replace("type=\"checkbox\" ", $"type=\"checkbox\" lay-filter=\"{output.Attributes["lay-filter"].Value}\""));
                        EmitFormChangeWiring(output, "checkbox", output.Attributes["lay-filter"].Value.ToString(), item.ChangeFunc);
                    }
                    break;
                case SwitchTagHelper item:
                    if (string.IsNullOrEmpty(item.ChangeFunc) == false)
                    {
                        EmitFormChangeWiring(output, "switch", output.Attributes["lay-filter"].Value.ToString(), item.ChangeFunc);
                    }
                    break;
                case RadioTagHelper item:
                    if (string.IsNullOrEmpty(item.ChangeFunc) == false)
                    {
                        output.PostContent.SetHtmlContent(output.PostContent.GetContent().Replace("type=\"radio\" ", $"type=\"radio\" lay-filter=\"{output.Attributes["lay-filter"].Value}\""));
                        EmitFormChangeWiring(output, "radio", output.Attributes["lay-filter"].Value.ToString(), item.ChangeFunc);
                    }
                    break;
                case TextBoxTagHelper item:
                    if (string.IsNullOrEmpty(item.SearchUrl) == false)
                    {
                        EmitAutocompleteWiring(output, item.Id, item.SearchUrl, item.TriggerUrl, item.ChangeFunc);
                    }
                    break;
            }

            //如果是submitbutton，则在button前面加入一个区域用来定位输出后台返回的错误
            //if (output.TagName == "button" && output.Attributes.TryGetAttribute("lay-submit", out TagHelperAttribute ta) == true)
            //{
            //    output.PreElement.SetHtmlContent($"<p id='{Id}errorholder'></p>" + output.PreElement.GetContent());
            //}
        }

        public string FormatFuncName(string funcname,bool appendparameter = true)
        {
            if (funcname == null)
            {
                return null;
            }
            var rv = funcname;
            var ind = rv.IndexOf("(");
            if (ind > 0)
            {
                rv = rv.Substring(0, ind);
            }
            if (appendparameter == true)
            {
                rv += "(data)";
            }
            return rv;
        }

        // Issue #784 (#470 residual): shared checkbox/switch/radio ChangeFunc ->
        // layui.form.on(...) wiring — used by the CheckBoxTagHelper/
        // SwitchTagHelper/RadioTagHelper cases above (identical shape apart
        // from the layui form event "kind" name: checkbox/switch/radio). Flag
        // OFF keeps the EXACT legacy inline <script> below, byte-identical to
        // pre-#784 (the #754 gate); flag ON with a plain-identifier ChangeFunc
        // migrates to the eval-free 'formChange' JSON island
        // (ff._renderFormChangeAction, framework_layui.js), resolved
        // client-side through the SAME guarded ff._resolveGuardedWindowFn
        // every other #470 slice's named callback uses. A non-identifier
        // ChangeFunc (flag ON) keeps the legacy inline <script>, loudly
        // deprecated via console.warn — mirroring every other #470 slice's
        // 3-way decision.
        //
        // TRUST BOUNDARY: changeFunc is ALWAYS a compile-time,
        // developer-authored Razor literal (the ChangeFunc TagHelper
        // attribute value) — NEVER field/request/model data, the same trust
        // class as bindSubmit's beforeSubmit (#558) / Slice I's
        // Slider/ColorPicker ChangeFunc.
        private void EmitFormChangeWiring(TagHelperOutput output, string kind, string filter, string changeFunc)
        {
            var changeFuncName = FormatFuncName(changeFunc, false);
            bool isIdentifier = changeFuncName != null && _changeFuncIdentifierRegex.IsMatch(changeFuncName);
            bool useIsland = UIConfig.UseSelectIslandRender && isIdentifier;

            if (useIsland)
            {
                var action = new FormChangeIslandAction { Kind = kind, Filter = filter, ChangeFunc = changeFuncName };
                output.PostElement.AppendHtml($@"
<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(action, _formChangeIslandJsonOptions)}</script>
");
            }
            else
            {
                // Issue #784: only warn when the flag is actually ON and island
                // render was skipped for a genuine non-identifier ChangeFunc —
                // when the flag is OFF (or ChangeFunc is empty — unreachable
                // here, callers already guard on IsNullOrEmpty) this must
                // contribute ZERO characters to stay byte-identical to the
                // pre-#784 emission (the #754 gate).
                var warn = (UIConfig.UseSelectIslandRender && !isIdentifier)
                    ? $"console.warn('[WTM] {kind} ChangeFunc \\'{JavaScriptEncoder.Default.Encode(changeFunc)}\\' is not a plain identifier — UseSelectIslandRender is ON but island render was skipped for this field; keeping the legacy inline script. See #470/#784.');\n"
                    : "";
                output.PostElement.AppendHtml($@"
<script>
{warn}layui.use(['form'],function(){{
  var form = layui.form;
  form.on('{kind}({filter})', function(data){{
    {FormatFuncName(changeFunc)};
  }});
}})
</script>
");
            }
        }

        // Issue #784 (#470 residual): shared TextBoxTagHelper SearchUrl/
        // TriggerUrl -> layui.autocomplete.render(...) wiring. Flag OFF keeps
        // the EXACT legacy inline <script> below (either TriggerUrl variant),
        // byte-identical to pre-#784; flag ON with an empty or
        // plain-identifier ChangeFunc migrates to the eval-free
        // 'autocomplete' JSON island (ff._renderAutocompleteAction,
        // framework_layui.js). A non-identifier ChangeFunc (flag ON) keeps
        // the legacy inline <script>, loudly deprecated via console.warn.
        //
        // TRUST BOUNDARY: same class as EmitFormChangeWiring above — ChangeFunc
        // is ALWAYS a compile-time, developer-authored Razor literal, never
        // field/request/model data.
        private void EmitAutocompleteWiring(TagHelperOutput output, string id, string searchUrl, string triggerUrl, string changeFunc)
        {
            var changeFuncName = FormatFuncName(changeFunc, false);
            // A null changeFuncName (ChangeFunc never set) is trivially
            // island-safe — there is nothing to resolve, matching legacy's
            // FormatFuncName(null) => null => empty interpolation => no-op.
            bool isIdentifier = changeFuncName == null || _changeFuncIdentifierRegex.IsMatch(changeFuncName);
            bool useIsland = UIConfig.UseSelectIslandRender && isIdentifier;

            if (useIsland)
            {
                var action = new AutocompleteIslandAction
                {
                    Id = id,
                    Url = searchUrl,
                    TriggerUrl = string.IsNullOrEmpty(triggerUrl) ? null : triggerUrl,
                    ChangeFunc = changeFuncName
                };
                output.PostElement.AppendHtml($@"
<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(action, _formChangeIslandJsonOptions)}</script>
");
            }
            else
            {
                var warn = (UIConfig.UseSelectIslandRender && changeFuncName != null && !isIdentifier)
                    ? $"console.warn('[WTM] TextBox #{JavaScriptEncoder.Default.Encode(id ?? "")} ChangeFunc \\'{JavaScriptEncoder.Default.Encode(changeFunc)}\\' is not a plain identifier — UseSelectIslandRender is ON but island render was skipped for this field; keeping the legacy inline script. See #470/#784.');\n"
                    : "";
                if (!string.IsNullOrEmpty(triggerUrl))
                {
                    output.PostElement.AppendHtml($@"
<script>
{warn}layui.use(['autocomplete'],function(){{
  layui.autocomplete.render({{
    elem: $('#{id}')[0],
    url: '{searchUrl}',
    cache: false,
    template_val: '{{{{d.Value}}}}',
    template_txt: '{{{{d.Text}}}}',
    onselect: function (data) {{
      $('#{id}').val(data.Value);
     {FormatFuncName(changeFunc)};
     ff.ChainChange('{triggerUrl}/'+data.Value, data.elem);
    }}
  }});
}})
</script>
");
                }
                else
                {
                    output.PostElement.AppendHtml($@"
<script>
{warn}layui.use(['autocomplete'],function(){{
  layui.autocomplete.render({{
    elem: $('#{id}')[0],
    url: '{searchUrl}',
    cache: false,
    template_val: '{{{{d.Value}}}}',
    template_txt: '{{{{d.Text}}}}',
    onselect: function (data) {{
      $('#{id}').val(data.Value);
     {FormatFuncName(changeFunc)};
    }}
  }});
}})
</script>
");

                }
            }
        }
    }

    // Issue #784 (#470 residual): DTO for the bare 'formChange' JSON island —
    // checkbox/switch/radio ChangeFunc -> layui.form.on(kind(filter), ...)
    // wiring. kind is always one of "checkbox"/"switch"/"radio" (server-picked
    // from a closed C# switch in BaseElementTag.Process — never request/field
    // data). ff._normalizeIslandPayload (framework_layui.js) wraps this into
    // the {actions:[...]} shape ff.DispatchAction expects; not part of the
    // public API surface.
    internal sealed class FormChangeIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "formChange";

        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        [JsonPropertyName("filter")]
        public string Filter { get; set; }

        // Compile-time, developer-authored Razor literal (ChangeFunc
        // attribute value) — NEVER field/request/model data. Only ever
        // populated when it is already a plain identifier (see
        // EmitFormChangeWiring above); a non-identifier value keeps the
        // legacy inline <script> instead and this DTO is never constructed.
        [JsonPropertyName("changeFunc")]
        public string ChangeFunc { get; set; }
    }

    // Issue #784 (#470 residual): DTO for the bare 'autocomplete' JSON island
    // — TextBoxTagHelper's SearchUrl/TriggerUrl ->
    // layui.autocomplete.render(...) wiring.
    internal sealed class AutocompleteIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "autocomplete";

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("triggerUrl")]
        public string TriggerUrl { get; set; }

        [JsonPropertyName("changeFunc")]
        public string ChangeFunc { get; set; }
    }
}
