using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    public enum TabStyleEnum { Default, Simple }

    [HtmlTargetElement("wt:tab", TagStructure = TagStructure.NormalOrSelfClosing)]
    public class TabTagHelper : BaseElementTag
    {
        public bool AllowClose { get; set; }
        public int SelectedIndex { get; set; }

        public TabStyleEnum TabStyle { get; set; }

        // Issue #784 (#470 residual): island DTO omits null members, matching
        // every other #470 slice's _islandJsonOptions convention.
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            this.Id = Guid.NewGuid().ToString("N").ToLower();
            output.TagName = "div";
            if (TabStyle == TabStyleEnum.Default)
            {
                output.Attributes.SetAttribute("class", $"layui-tab layui-tab-card");
            }
            else
            {
                output.Attributes.SetAttribute("class", $"layui-tab layui-tab-brief");
            }
            if(AllowClose == true)
            {
                output.Attributes.SetAttribute("lay-allowclose", "true");
            }
            //context.Items.Add("tabselectedindex", 0);

            // Issue #784 (#470 residual): opt-in (WtmUIOptions.UseSelectIslandRender,
            // default OFF — the SAME flag #470 Slices G-O3 use) eval-free
            // 'tabInit' island — thin JSON wrapper over the fixed
            // tab-selection + chart-resize wiring in the else branch below. No
            // developer callback is involved at all (SelectedIndex is a plain
            // int, Id/filter are server-generated GUIDs) — every field is
            // trivially island-safe, so this is a plain flag check with no
            // per-field identifier decision (unlike ChangeFunc/ClickFunc-
            // bearing slices). Flag OFF keeps the EXACT legacy inline <script>
            // below, byte-identical to pre-#784 (the #754 gate).
            if (UIConfig.UseSelectIslandRender)
            {
                var action = new TabInitIslandAction
                {
                    Id = Id,
                    Filter = Id + "filter",
                    SelectedIndex = SelectedIndex
                };
                output.PostElement.AppendHtml($@"
<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(action, _islandJsonOptions)}</script>
");
            }
            else
            {
                output.PostElement.AppendHtml($@"
<script>
    $('#{Id} ul li').eq({SelectedIndex}).addClass('layui-this');
    $('#{Id} .layui-tab-item').eq({SelectedIndex}).addClass('layui-show');
    layui.element.on('tab({Id}filter)', function(data){{
        $('#{Id}').find(""div[ischart = '1']"").each(
            function (index) {{
                // Issue #789 Phase 1: replaced eval(id+'Chart.resize()') with safe window[] lookup
                var _chart = window[$(this).attr('id') + 'Chart'];
                if (_chart && typeof _chart.resize === 'function') _chart.resize();
            }}
        );
}});
</script>
");
            }
            base.Process(context, output);
        }
    }

    // Issue #784 (#470 residual): DTO for the bare 'tabInit' JSON island.
    // ff._normalizeIslandPayload (framework_layui.js) wraps this into the
    // {actions:[...]} shape ff.DispatchAction expects; not part of the public
    // API surface.
    internal sealed class TabInitIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "tabInit";

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("filter")]
        public string Filter { get; set; }

        [JsonPropertyName("selectedIndex")]
        public int SelectedIndex { get; set; }
    }

    [HtmlTargetElement("wt:tabheaders", TagStructure = TagStructure.NormalOrSelfClosing)]
    public class TabHeadersTagHelper : BaseElementTag
    {
        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "ul";
            output.Attributes.SetAttribute("class", "layui-tab-title");
            base.Process(context, output);
        }
    }

    [HtmlTargetElement("wt:tabheader", TagStructure = TagStructure.WithoutEndTag)]
    public class TabHeaderTagHelper : BaseElementTag
    {
        public string Title { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "li";
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Content.SetHtmlContent(Title);
            base.Process(context, output);
        }
    }

    [HtmlTargetElement("wt:tabcontents", TagStructure = TagStructure.NormalOrSelfClosing)]
    public class TabContentsTagHelper : BaseElementTag
    {

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.Attributes.SetAttribute("class", $"layui-tab-content");
            base.Process(context, output);
        }
    }

    [HtmlTargetElement("wt:tabcontent", TagStructure = TagStructure.NormalOrSelfClosing)]
    public class TabContentTagHelper : BaseElementTag
    {
        public string Url { get; set; }
        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.Attributes.SetAttribute("class", $"layui-tab-item");
            base.Process(context, output);
        }
    }
}
