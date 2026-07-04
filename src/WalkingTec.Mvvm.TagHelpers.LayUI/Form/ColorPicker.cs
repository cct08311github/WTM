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

            // Issue #552 adversarial-review fix (P0, pre-existing XSS): each
            // PredefinedColors token is validated against the shared color-token
            // grammar (BaseFieldTag.IsSafeColorToken) before it is ever concatenated
            // into this legacy inline-script JS-array-literal. Previously an item
            // reached here via raw string concatenation ($"'{item}',") — a token
            // containing a single quote could break out of the JS string literal
            // entirely. An unsafe token is dropped individually so one bad entry
            // doesn't poison the rest of the list.
            string prec = "";
            var cs = PredefinedColors?.Split(",");
            if (cs != null)
            {
                foreach (var item in cs)
                {
                    if (item != "" && IsSafeColorToken(item))
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

            // Issue #552 adversarial-review fix (P0, pre-existing XSS): the persisted
            // color value (val) is validated against the shared color-token grammar
            // (BaseFieldTag.IsSafeColorToken) before it is emitted as the LIVE color
            // opt on either path. jsEncodedVal above only makes the value safe as a
            // JS *string literal* in the generated <script> — once the browser
            // JS-decodes it back to the original string, layui.colorpicker.render
            // still splices that decoded string, unescaped, into an HTML string it
            // builds internally (style="...'+color+'..."), so a value that survives
            // JS-string-escaping can still carry a stored DOM-XSS payload. An unsafe
            // value is omitted entirely (the 'color' key/argument is dropped) rather
            // than emitted empty — layui falls back to its own default swatch, the
            // same as when no color was ever configured.
            bool hasSafeColorVal = IsSafeColorToken(val);

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
                // Issue #552 adversarial-review fix (P0): each predefined-color token
                // is validated independently — an invalid token is dropped, valid ones
                // are kept, exactly mirroring the legacy-path filtering above.
                var colorItems = (PredefinedColors ?? string.Empty)
                    .Split(",")
                    .Where(item => item != "" && IsSafeColorToken(item))
                    .ToArray();

                var opts = new Dictionary<string, object>
                {
                    ["elem"] = "#cp_" + Id,
                    ["alpha"] = EnableAlpha,
                    ["format"] = EnableAlpha ? "rgb" : "hex",
                    ["predefine"] = PredefinedColors != null
                };
                if (hasSafeColorVal) { opts["color"] = val; }
                if (colorItems.Length > 0) { opts["colors"] = colorItems; }

                // Issue #578: containment id for the client-side write-back
                // gate (mirrors #564's highlightErrors FormId). Sourced from
                // the SAME ambient context.Items["formid"] key FormTagHelper
                // publishes for descendant tag helpers — not a new resolution
                // mechanism. Null when the colorpicker isn't nested inside a
                // <wt:form>; framework_layui.js treats an absent formId as
                // back-compat (write proceeds unguarded, never throws).
                var ownerFormId = context.Items.TryGetValue("formid", out var formIdObj)
                    ? formIdObj as string
                    : null;

                var action = new ColorPickerIslandAction
                {
                    Opts = opts,
                    ValueFieldId = Id,
                    FormId = ownerFormId
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
    {(hasSafeColorVal ? $",color:'{jsEncodedVal}'" : string.Empty)}
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

        // Issue #578: the owning <wt:form> id (from the ambient
        // context.Items["formid"] key), used by framework_layui.js's
        // 'colorpicker' write-back handler to refuse writing into
        // valueFieldId if it resolves to an element outside this form —
        // closing the id-spoofing gap a smuggled island (#462/#552 threat
        // model) could otherwise use. Absent/null for back-compat with
        // islands rendered outside a <wt:form> — the client then applies no
        // containment check at all.
        [System.Text.Json.Serialization.JsonPropertyName("formId")]
        public string FormId { get; set; }
    }
}
