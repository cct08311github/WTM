using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI.Form
{
    [HtmlTargetElement("wt:ueditor", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class UEditorTagHelper : BaseFieldTag
    {
        //文本框为空显示的PlaceHolder
        public string EmptyText { get; set; }

        //定义高度
        public new int? Height { get; set; }

        //定义宽度
        public new int? Width { get; set; }
        public string UploadGroupName { get; set; }
        public string UploadSubdir { get; set; }
        public string ConnectionString { get; set; }
        public string UploadMode { get; set; }

        // Issue #470 Slice G: System.Text.Json's default encoder escapes '<', '>',
        // and '&', making the JSON payload safe to embed inside a <script> block
        // without risk of </script> injection — same pattern as
        // ComboBoxTagHelper's _islandJsonOptions (#552/#633).
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            string placeHolder = EmptyText ?? string.Empty;
            output.TagName = "div";
            output.TagMode = TagMode.StartTagAndEndTag;
            string strWidth = Width == null ? "100%" : (Width + "px");
            string strHeight = Height == null ? "200px" : (Height + "px");
            output.Attributes.Add("style", $"width:{strWidth};height:{strHeight};");
            output.Attributes.Add("isrich", "1");
            var vm = context.Items["model"] as BaseVM;
            string url = "UploadForLayUIUEditor";
            if (string.IsNullOrEmpty(ConnectionString) == true)
            {
                if (vm != null)
                {
                    url = url.AppendQuery($"_DONOT_USE_CS={vm.CurrentCS}");
                }
            }
            else
            {
                url = url.AppendQuery($"_DONOT_USE_CS={ConnectionString}");
            }
            if (string.IsNullOrEmpty(UploadGroupName) == false)
            {
                url = url.AppendQuery($"groupName={UploadGroupName}");
            }
            if (string.IsNullOrEmpty(UploadSubdir) == false)
            {
                url = url.AppendQuery($"subdir={UploadSubdir}");
            }
            if (string.IsNullOrEmpty(UploadMode) == false)
            {
                url = url.AppendQuery($"sm={UploadMode}");
            }


            if (vm != null)
            {
                vm.ConfigInfo.UEditorOptions.FileActionName = url;
                vm.ConfigInfo.UEditorOptions.ImageActionName = url;
                vm.ConfigInfo.UEditorOptions.ScrawlActionName = url;
                vm.ConfigInfo.UEditorOptions.SnapscreenActionName = url;
                vm.ConfigInfo.UEditorOptions.VideoActionName = url;
            }
            var contentValue = string.IsNullOrEmpty(Field?.Model?.ToString())
                ? (DefaultValue ?? "")
                : Field.Model.ToString();

            // Issue #753: Slice G shipped BEFORE WtmUIOptions.UseSelectIslandRender
            // existed and migrated UNCONDITIONALLY (no legacy fallback branch at
            // all), breaking the flag-OFF byte-identical guarantee #470 Slice
            // J/K/L/M established for ComboBox/Tree/Transfer/Upload. Gate on the
            // SAME flag — flag-off restores the exact pre-Slice-G inline <script>.
            if (UIConfig.UseSelectIslandRender)
            {
                // Issue #470 Slice G: eval-free JSON island — thin re-expression of
                //   layui.use(['ueditorconfig'], function () {
                //     layui.ueditor.loadEditor(id).ready(function () { this.setContent(content); });
                //   });
                // which the new 'ueditor' DispatchAction case (framework_layui.js)
                // replays natively. UEditorTagHelper exposes no developer-facing
                // callback attribute at all — the ready/setContent call is mandatory
                // framework wiring (populates the editor with the field's current/
                // default value), so this always safely migrates once the flag is on
                // (same rationale as RateTagHelper, #552). Content is Field.Model /
                // DefaultValue — potentially stored, attacker-influenceable data, so
                // this MUST go through LayuiIslandJson.Serialize (never raw
                // JsonSerializer.Serialize) for the '$' escaping that closes the
                // #651/#652 sentinel-collision stored-XSS class, same as every other
                // island DTO in this project.
                var action = new UEditorIslandAction
                {
                    Id = Id,
                    Content = contentValue
                };
                var json = LayuiIslandJson.Serialize(action, _islandJsonOptions);
                output.PostElement.AppendHtml(
                    $"<script type=\"application/json\" class=\"wtm-dialog-init\">{json}</script>");
            }
            else
            {
                // Issue #753: pre-Slice-G legacy inline <script> — byte-identical to
                // base 947ecbc9 (the commit immediately before #470 Slice G shipped).
                var encodedContent = JavaScriptEncoder.Default.Encode(contentValue);
                output.PostElement.AppendHtml($@"
<script>
  layui.use(['ueditorconfig'], function () {{
    layui.ueditor.loadEditor('{Id}').ready(function(){{this.setContent('{encodedContent}')}});
  }});
</script>
");
            }

            base.Process(context, output);
        }
    }

    // Issue #470 Slice G: DTO for the bare (non-wrapped) ueditor JSON island —
    // {"type":"ueditor","id":"...","content":"..."}. ff._normalizeIslandPayload
    // (framework_layui.js) wraps this into the {actions:[...]} shape
    // ff.DispatchAction expects; not part of the public API surface.
    internal sealed class UEditorIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "ueditor";

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; }
    }
}
