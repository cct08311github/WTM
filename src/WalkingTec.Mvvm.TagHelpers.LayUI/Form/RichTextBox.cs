using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI.Form
{
    [HtmlTargetElement("wt:richtextbox", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.NormalOrSelfClosing)]
    public class RichTextBoxTagHelper : BaseFieldTag
    {
        public string EmptyText { get; set; }
        public string UploadUrl { get; set; }
        public string UploadGroupName { get; set; }
        public string UploadSubdir { get; set; }
        public new int? Height { get; set; }
        public string ConnectionString { get; set; }
        public string ExtraQuery { get; set; }
        public string UploadMode { get; set; }

        // Issue #470 Slice G: see ComboBoxTagHelper's _islandJsonOptions for the
        // full rationale (same shared eval-free island pattern).
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            string placeHolder = EmptyText ?? "";
            output.TagName = "textarea";
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Attributes.Add("placeholder", placeHolder);
            output.Attributes.Add("style", "display:none");
            output.Attributes.Add("isrich", "1");

            if(string.IsNullOrEmpty(Field?.Model?.ToString()) == false)
            {
                DefaultValue = null;
            }

            if (DefaultValue != null)
            {
                output.Content.SetContent(DefaultValue.ToString());
            }
            else
            {
                output.Content.SetContent(Field?.Model?.ToString());
            }
            string url = UploadUrl;
            if (string.IsNullOrEmpty(url))
            {
                url = "/_framework/UploadForLayUIRichTextBox";
            }
            if(string.IsNullOrEmpty(UploadGroupName) == false)
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
            if (string.IsNullOrEmpty(ConnectionString) == true)
            {
                if (context.Items.ContainsKey("model") == true)
                {
                    var bvm = context.Items["model"] as BaseVM;
                    if (bvm?.CurrentCS != null)
                    {
                        url = url.AppendQuery($"_DONOT_USE_CS={bvm.CurrentCS}");
                    }
                }
            }
            else
            {
                url = url.AppendQuery($"_DONOT_USE_CS={ConnectionString}");
            }
            url = url.AppendQuery(ExtraQuery);

            // Issue #470 Slice G: eval-free JSON island — thin re-expression of
            //   layui.use('layedit', function(){
            //     var layedit = layui.layedit;
            //     layedit.set({ uploadImage: { url: uploadUrl } });
            //     var index = layedit.build(id[, {height:...}]);
            //     $('#'+id).attr('layeditindex', index);
            //   });
            // which the new 'layedit' DispatchAction case (framework_layui.js)
            // replays natively. RichTextBoxTagHelper exposes no developer-facing
            // callback attribute at all — the build/layeditindex write-back is
            // mandatory framework wiring, so this always safely migrates, no
            // legacy-fallback branch needed (same rationale as RateTagHelper,
            // #552). UploadUrl is server-composed (query-string builder above),
            // not raw user data, but this still routes through
            // LayuiIslandJson.Serialize (never raw JsonSerializer.Serialize) for
            // the '$' escaping that closes the #651/#652 sentinel-collision
            // stored-XSS class, matching every other island DTO in this project.
            var action = new LayeditIslandAction
            {
                Id = Id,
                UploadUrl = url,
                Height = Height
            };
            var json = LayuiIslandJson.Serialize(action, _islandJsonOptions);
            output.PostElement.AppendHtml(
                $"<script type=\"application/json\" class=\"wtm-dialog-init\">{json}</script>");

            base.Process(context, output);
        }
    }

    // Issue #470 Slice G: DTO for the bare (non-wrapped) layedit JSON island —
    // {"type":"layedit","id":"...","uploadUrl":"...","height":123}.
    // ff._normalizeIslandPayload (framework_layui.js) wraps this into the
    // {actions:[...]} shape ff.DispatchAction expects; not part of the public
    // API surface.
    internal sealed class LayeditIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "layedit";

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("uploadUrl")]
        public string UploadUrl { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }
    }
}
