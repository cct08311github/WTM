#nullable enable
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    /// <summary>
    /// Renders a native, dependency-free tag/chip input bound to a delimited
    /// string field. The underlying value is stored as a delimiter-joined
    /// hidden input, kept in sync client-side by a plain DOM widget — no
    /// layui module is required.
    /// Opt-in: use &lt;wt:taginput field="..." /&gt; in your Razor views.
    /// </summary>
    /// <remarks>
    /// Issue #571: the previous implementation targeted a <c>layui.tagInput</c>
    /// module that has never shipped in any bundled layui tree (neither the
    /// vendored 2.6.3 nor the opt-in 2.13.8 <c>layui-next</c> — see
    /// <c>test/manual/regression/README.md</c> #14, verified during #566), so
    /// <c>layui.use(['tagInput'], cb)</c> never resolved and the widget silently
    /// rendered nothing. This rewrite renders chips with plain DOM APIs
    /// (createElement/textContent — never innerHTML with tag data, since tag
    /// values are user data — see the #462/#552 XSS threat class) driven by
    /// <c>framework_layui.js</c>'s eval-free <c>wtm-dialog-init</c> JSON island
    /// (the #470/#552 pattern), so it now works identically on both layui
    /// trees.
    /// </remarks>
    [HtmlTargetElement("wt:taginput", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class TagInputTagHelper : BaseFieldTag
    {
        /// <summary>
        /// Delimiter used to join/split tag values (default ",").
        /// </summary>
        public string Delimiter { get; set; } = ",";

        /// <summary>
        /// Placeholder text shown in the tag-entry input when empty.
        /// </summary>
        public string? EmptyText { get; set; }

        /// <summary>
        /// Maximum number of tags allowed. <see langword="null"/> (default)
        /// means unlimited.
        /// </summary>
        public int? Max { get; set; }

        /// <summary>
        /// When <see langword="true"/>, tags are displayed but cannot be
        /// added or removed (the entry input and per-tag remove control are
        /// omitted). Default <see langword="false"/>.
        /// </summary>
        public bool ReadOnly { get; set; }

        // Issue #571 (#470-E pattern): System.Text.Json's default encoder escapes
        // '<', '>', and '&', making the JSON payload safe to embed inside a
        // <script> block without risk of </script> injection — same pattern as
        // DateTimeTagHelper's _laydateJsonOptions (#556) and
        // SliderTagHelper/RateTagHelper/ColorPickerTagHelper's _islandJsonOptions
        // (#552).
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
            output.Attributes.Add("class", "wtm-taginput layui-input-inline");

            var currentVal = Field?.Model?.ToString() ?? "";
            var safeName = string.IsNullOrEmpty(Name) ? (Field?.Name ?? "") : Name;
            var valueFieldId = $"{id}_val";
            var separator = string.IsNullOrEmpty(Delimiter) ? "," : Delimiter;

            output.PostElement.AppendHtml(
                $"<input type=\"hidden\" id=\"{WebUtility.HtmlEncode(valueFieldId)}\" name=\"{WebUtility.HtmlEncode(safeName)}\" value=\"{WebUtility.HtmlEncode(currentVal)}\" />");

            // Issue #571: TagInputTagHelper never exposed a developer-facing JS
            // callback attribute in the legacy implementation (its 'change'
            // handler only ever wrote the joined value back into the widget's
            // own hidden input — mandatory framework wiring, not a developer
            // callback). Exactly like RateTagHelper (#552), that means this
            // field unconditionally migrates to the eval-free JSON island —
            // there is no legacy-fallback branch to preserve here.
            var opts = new Dictionary<string, object>
            {
                ["elem"] = "#" + id,
                ["separator"] = separator
            };
            if (!string.IsNullOrEmpty(EmptyText)) { opts["placeholder"] = EmptyText; }
            if (Max is > 0) { opts["max"] = Max.Value; }
            if (ReadOnly) { opts["readonly"] = true; }
            if (Disabled) { opts["disabled"] = true; }

            var action = new TagInputIslandAction
            {
                Opts = opts,
                ValueFieldId = valueFieldId
            };
            var json = JsonSerializer.Serialize(action, _islandJsonOptions);
            output.PostElement.AppendHtml(
                $"<script type=\"application/json\" class=\"wtm-dialog-init\">{json}</script>");

            base.Process(context, output);
        }
    }

    // Issue #571 (#470-E pattern): DTO for the bare (non-wrapped) tagInput JSON
    // island — {"type":"tagInput","opts":{...},"valueFieldId":"..."}.
    // ff._normalizeIslandPayload (framework_layui.js) wraps this into the
    // {actions:[...]} shape ff.DispatchAction expects; not part of the public
    // API surface. opts carries only plain, whitelisted data (separator,
    // placeholder, max, readonly, disabled) — no callback, no arbitrary
    // properties.
    internal class TagInputIslandAction
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "tagInput";

        [System.Text.Json.Serialization.JsonPropertyName("opts")]
        public Dictionary<string, object> Opts { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("valueFieldId")]
        public string? ValueFieldId { get; set; }
    }
}
