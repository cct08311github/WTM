#nullable enable
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    /// <summary>
    /// Renders a Layui star-rating widget (layui.rate) bound to a numeric field.
    /// Opt-in: use &lt;wt:rate field="..." /&gt; in your Razor views.
    /// </summary>
    [HtmlTargetElement("wt:rate", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class RateTagHelper : BaseFieldTag
    {
        /// <summary>Number of stars (default 5).</summary>
        public int Length { get; set; } = 5;

        /// <summary>Whether to allow half-star selection (default false).</summary>
        public bool Half { get; set; }

        /// <summary>Whether the rating is read-only (default false).</summary>
        public bool ReadOnly { get; set; }

        /// <summary>Optional text displayed beside the stars.</summary>
        public string? Text { get; set; }

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
            var id = Id;
            output.Attributes.SetAttribute("id", id);
            output.Attributes.Add("class", "wtm-rate-placeholder");

            var currentVal = 0;
            if (Field?.Model != null && int.TryParse(Field.Model.ToString(), out var parsed))
            {
                currentVal = parsed;
            }

            var safeId = HtmlEncoder.Default.Encode(id);
            var safeName = HtmlEncoder.Default.Encode(string.IsNullOrEmpty(Name) ? (Field?.Name ?? "") : Name);
            var valueFieldId = $"{id}_val";

            output.PostElement.AppendHtml(
                $"<input type=\"hidden\" id=\"{safeId}_val\" name=\"{safeName}\" value=\"{HtmlEncoder.Default.Encode(currentVal.ToString())}\" />");

            // Issue #552 (#470-E): RateTagHelper exposes no developer-facing
            // callback attribute at all — the 'choose' handler in the legacy
            // inline <script> below only ever wrote the picked value back into
            // the widget's own hidden input, which is mandatory framework wiring,
            // not a developer callback. That write-back is therefore always safe
            // to reproduce natively in the JS action handler (framework_layui.js
            // DispatchAction 'rate' case), so this field unconditionally migrates
            // to the eval-free JSON island — there is no legacy-fallback branch
            // to take here (unlike DateTimeTagHelper/SliderTagHelper/
            // ColorPickerTagHelper, which each have a genuine developer callback
            // attribute that keeps emitting the inline <script> when set).
            // Issue #584: the selector MUST be the raw id, not a manually
            // JavaScriptEncoder-escaped copy. System.Text.Json already
            // Unicode-escapes this string when the island DTO is serialized
            // below (see _islandJsonOptions), so JavaScriptEncoder-escaping
            // it here first double-escapes non-BasicLatin characters (e.g. a
            // Chinese field id) — after JSON.parse on the client the selector
            // no longer matches the raw DOM id set above, and
            // layui.rate.render silently targets nothing. Matches the
            // sibling widgets (Slider/ColorPicker/TagInput), which all pass
            // the raw id through unescaped.
            var opts = new Dictionary<string, object>
            {
                ["elem"] = "#" + id,
                ["value"] = currentVal,
                ["length"] = Length
            };
            if (Half) { opts["half"] = true; }
            if (ReadOnly) { opts["readonly"] = true; }
            if (!string.IsNullOrEmpty(Text)) { opts["text"] = new[] { Text }; }

            // Issue #578: containment id for the client-side write-back gate
            // (mirrors #564's highlightErrors FormId). Sourced from the SAME
            // ambient context.Items["formid"] key FormTagHelper publishes for
            // descendant tag helpers — not a new resolution mechanism. Null
            // when the rate widget isn't nested inside a <wt:form>;
            // framework_layui.js treats an absent formId as back-compat
            // (write proceeds unguarded, never throws).
            var ownerFormId = context.Items.TryGetValue("formid", out var formIdObj)
                ? formIdObj as string
                : null;

            var action = new RateIslandAction
            {
                Opts = opts,
                ValueFieldId = valueFieldId,
                FormId = ownerFormId
            };
            var json = LayuiIslandJson.Serialize(action, _islandJsonOptions);
            output.PostElement.AppendHtml(
                $"<script type=\"application/json\" class=\"wtm-dialog-init\">{json}</script>");

            base.Process(context, output);
        }
    }

    // Issue #552 (#470-E): DTO for the bare (non-wrapped) rate JSON island —
    // {"type":"rate","opts":{...},"valueFieldId":"..."}. ff._normalizeIslandPayload
    // (framework_layui.js) wraps this into the {actions:[...]} shape
    // ff.DispatchAction expects; not part of the public API surface.
    internal class RateIslandAction
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "rate";

        [System.Text.Json.Serialization.JsonPropertyName("opts")]
        public Dictionary<string, object> Opts { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("valueFieldId")]
        public string? ValueFieldId { get; set; }

        // Issue #578: the owning <wt:form> id (from the ambient
        // context.Items["formid"] key), used by framework_layui.js's 'rate'
        // write-back handler to refuse writing into valueFieldId if it
        // resolves to an element outside this form — closing the id-spoofing
        // gap a smuggled island (#462/#552 threat model) could otherwise use.
        // Absent/null for back-compat with islands rendered outside a
        // <wt:form> — the client then applies no containment check at all.
        [System.Text.Json.Serialization.JsonPropertyName("formId")]
        public string? FormId { get; set; }
    }
}
