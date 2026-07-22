using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    public enum PanelType { Collapse, Card}

    [HtmlTargetElement("wt:panel", TagStructure = TagStructure.NormalOrSelfClosing)]
    public class PanelTagHelper : BaseElementTag
    {
        /// <summary>
        /// 是否合上，默认是展开状态
        /// </summary>
        public bool Collapsed { get; set; }

        public string Title { get; set; }

        public PanelType PanelType { get; set; }

        // Issue #784 (#470 residual): island DTO omits null members, matching
        // every other #470 slice's _islandJsonOptions convention.
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            string tid = Guid.NewGuid().ToString("N");
            output.Attributes.Add("lay-filter", tid);
            if (PanelType == PanelType.Collapse)
            {
                output.Attributes.SetAttribute("class", "layui-collapse");
                output.Attributes.SetAttribute("lay-accordion", "");
                var inside = await output.GetChildContentAsync();
                output.Content.SetHtmlContent($@"
<div class=""layui-colla-item"">
    <h2 class=""layui-colla-title"">{Title ?? ""}</h2>
    <div class=""layui-colla-content {(Collapsed == true ? "" : "layui-show")}"">
        {inside.GetContent()}
    </div>
</div>
");
            }
            if(PanelType == PanelType.Card)
            {
                output.Attributes.SetAttribute("class", "layui-card");
                var inside = await output.GetChildContentAsync();
                output.Content.SetHtmlContent($@"
<div class=""layui-card-header"">{Title ?? ""}</div>
<div class=""layui-card-body"">
    {inside.GetContent()}
</div>
");

            }

            // Issue #784 (#470 residual): opt-in (WtmUIOptions.UseSelectIslandRender,
            // default OFF — the SAME flag #470 Slices G-O3 use) eval-free
            // 'panelInit' island — thin JSON wrapper over the fixed
            // collapse-resize wiring in the else branch below. No developer
            // callback is involved at all (tid is a server-generated GUID) —
            // trivially island-safe, plain flag check. Flag OFF keeps the
            // EXACT legacy inline <script> below, byte-identical to pre-#784
            // (the #754 gate).
            if (UIConfig.UseSelectIslandRender)
            {
                var action = new PanelInitIslandAction { Filter = tid };
                output.PostElement.AppendHtml($@"
<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(action, _islandJsonOptions)}</script>
");
            }
            else
            {
                output.PostElement.AppendHtml($@"
<script>
layui.use(['element'],function(){{
  var element = layui.element;
  element.init();
  element.on('collapse({tid})', function(data){{
    setTimeout(function () {{
      if (typeof(Event) === 'function') {{
        window.dispatchEvent(new Event('resize'));
      }} else {{
        var evt = window.document.createEvent('UIEvents');
        evt.initUIEvent('resize', true, false, window, 0);
        window.dispatchEvent(evt);
      }}
    }}, 10);
  }});
}})
</script>
");
            }
            base.Process(context, output);
        }
    }

    // Issue #784 (#470 residual): DTO for the bare 'panelInit' JSON island.
    // ff._normalizeIslandPayload (framework_layui.js) wraps this into the
    // {actions:[...]} shape ff.DispatchAction expects; not part of the public
    // API surface.
    internal sealed class PanelInitIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "panelInit";

        [JsonPropertyName("filter")]
        public string Filter { get; set; }
    }
}
