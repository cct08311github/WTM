using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:colorpicker", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class ColorPickerTagHelper : BaseFieldTag
    {
        public string EmptyText { get; set; }

        /// <summary>
        /// 改变选择时触发的js函数，func(data)格式;
        /// <para>
        /// data为所选颜色;
        /// </para>
        /// </summary>
        public string ChangeFunc { get; set; }

        public bool EnableAlpha { get; set; }

        public string PredefinedColors { get; set; }
        public bool IsPassword { get; set; }

        // Issue #552 (#470-E): System.Text.Json's default encoder escapes '<', '>',
        // and '&', making the JSON payload safe to embed inside a <script> block
        // without risk of </script> injection — same pattern as
        // DateTimeTagHelper's _laydateJsonOptions (#556).
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Attributes.Add("id", $"cp_{Id}");

            string prec = "";
            var cs = PredefinedColors?.Split(",");
            if (cs != null)
            {
                foreach (var item in cs)
                {
                    if (item != "")
                    {
                        prec += $"'{item}',";
                    }
                }
            }
            if(prec.Length > 0)
            {
                prec = prec.Substring(0, prec.Length - 1);
            }
            string val = "";

            if (string.IsNullOrEmpty(Field?.Model?.ToString()) == false)
            {
                DefaultValue = null;
            }

            if (DefaultValue != null)
            {
                val = DefaultValue;
            }
            else
            {
                val = Field?.Model?.ToString();
            }
            var requiredtext = "";
            if (Field.Metadata.IsRequired)
            {
                requiredtext = $" lay-verify=\"required\" lay-reqText=\"{THProgram._localizer["Validate.{0}required", Field?.Metadata?.DisplayName ?? Field?.Metadata?.Name]}\"";
            }

            var encodedVal = WebUtility.HtmlEncode(val ?? "");
            var jsEncodedVal = JavaScriptEncoder.Default.Encode(val ?? "");

            // Issue #552 (#470-E): ChangeFunc is a developer-supplied JS function
            // name invoked with the live picked-color value — that can't be
            // JSON-expressed, so a non-empty ChangeFunc keeps this field on the
            // legacy inline <script> path below, unchanged from before. A
            // callback-free field migrates to the eval-free JSON island:
            // ff.OpenDialog / the page-ready consumer (framework_layui.js) parse
            // the island and call layui.colorpicker.render(action.opts) directly.
            // The mandatory 'done' write-back (persisting the picked color into
            // the bound hidden input) is framework wiring, not a developer
            // callback, and is reproduced natively in the JS action handler.
            bool hasCallback = !string.IsNullOrEmpty(ChangeFunc);

            if (!hasCallback)
            {
                var colorItems = (PredefinedColors ?? string.Empty)
                    .Split(",")
                    .Where(item => item != "")
                    .ToArray();

                var opts = new Dictionary<string, object>
                {
                    ["elem"] = "#cp_" + Id,
                    ["color"] = val ?? "",
                    ["alpha"] = EnableAlpha,
                    ["format"] = EnableAlpha ? "rgb" : "hex",
                    ["predefine"] = PredefinedColors != null
                };
                if (colorItems.Length > 0) { opts["colors"] = colorItems; }

                var action = new ColorPickerIslandAction
                {
                    Opts = opts,
                    ValueFieldId = Id
                };
                var json = JsonSerializer.Serialize(action, _islandJsonOptions);

                var islandContent = $@"
<input type='hidden' id='{Id}' name='{Field.Name}' value='{encodedVal}' {requiredtext}/>
<script type=""application/json"" class=""wtm-dialog-init"">{json}</script>
";
                output.PostElement.AppendHtml(islandContent);
            }
            else
            {
                var content = $@"
<input type='hidden' id='{Id}' name='{Field.Name}' value='{encodedVal}' {requiredtext}/>
<script>
layui.use('colorpicker', function(){{
  var colorpicker = layui.colorpicker;
  colorpicker.render({{
    elem: '#cp_{Id}'
    ,color:'{jsEncodedVal}'
    ,alpha : {EnableAlpha.ToString().ToLower()}
    ,format: '{(EnableAlpha==true? "rgb":"hex")}'
    ,predefine: {(PredefinedColors == null ? "false" : "true")}
    {(prec == "" ?"":$",colors: [{prec}]")}
    ,done: function(data){{
      $('#{Id}').val(data);
        {FormatFuncName(ChangeFunc)};
    }}
  }});
}});</script>
";
                output.PostElement.AppendHtml(content);
            }

            base.Process(context, output);
        }
    }

    // Issue #552 (#470-E): DTO for the bare (non-wrapped) colorpicker JSON
    // island — {"type":"colorpicker","opts":{...},"valueFieldId":"..."}.
    // ff._normalizeIslandPayload (framework_layui.js) wraps this into the
    // {actions:[...]} shape ff.DispatchAction expects; not part of the public
    // API surface.
    internal class ColorPickerIslandAction
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "colorpicker";

        [System.Text.Json.Serialization.JsonPropertyName("opts")]
        public Dictionary<string, object> Opts { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("valueFieldId")]
        public string ValueFieldId { get; set; }
    }
}
